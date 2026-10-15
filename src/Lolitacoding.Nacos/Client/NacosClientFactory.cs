using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Naming;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Client;

/// <summary>
/// Builds the connected Nacos clients from options.
/// </summary>
/// <remarks>
/// <para>
/// One connection is shared by every feature because that is what the protocol requires: Nacos
/// multiplexes configuration, naming and every other concern over a single bi-directional stream,
/// and a second stream would double the heartbeat and handshake load against the server for no
/// benefit.
/// </para>
/// <para>
/// Assembling the graph is the only place that knows how the pieces fit together, which keeps the
/// clients themselves free of construction concerns and makes them testable in isolation.
/// </para>
/// </remarks>
internal sealed class NacosClientFactory : IAsyncDisposable
{
    private readonly NacosOptions _options;
    private readonly NacosGrpcConnection _connection;
    private readonly NacosConfigClient _config;
    private readonly NacosNamingClient _naming;
    private readonly ILogger<NacosClientFactory> _logger;

    private bool _disposed;

    private NacosClientFactory(
        NacosOptions options,
        NacosGrpcConnection connection,
        NacosConfigClient config,
        NacosNamingClient naming,
        ILogger<NacosClientFactory> logger)
    {
        _options = options;
        _connection = connection;
        _config = config;
        _naming = naming;
        _logger = logger;
    }

    /// <summary>Gets the configuration client.</summary>
    public INacosConfigClient Config => _config;

    /// <summary>Gets the naming client.</summary>
    public INacosNamingClient Naming => _naming;

    /// <summary>
    /// Creates the clients and their shared connection.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <param name="httpClient">
    /// The HTTP client used for authentication, or <see langword="null"/> to create one.
    /// </param>
    /// <param name="channelFactory">
    /// The channel factory, or <see langword="null"/> to use real gRPC channels.
    /// </param>
    /// <param name="timeProvider">The clock, or <see langword="null"/> for the system clock.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="random">The random source, for deterministic tests.</param>
    /// <returns>The assembled clients.</returns>
    public static NacosClientFactory Create(
        NacosOptions options,
        HttpClient? httpClient,
        INacosChannelFactory? channelFactory,
        TimeProvider? timeProvider,
        ILoggerFactory loggerFactory,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        options.Validate();

        IReadOnlyList<NacosServerAddress> servers =
            NacosServerAddress.ParseAll(options.ServerAddresses);

        TimeProvider clock = timeProvider ?? TimeProvider.System;

        HttpClient client = httpClient ?? new HttpClient();

        INacosAuthProvider authProvider = NacosAuthProviderFactory.Create(
            options, servers, client, clock, loggerFactory.CreateLogger("Lolitacoding.Nacos.Auth"), random);

        var connection = new NacosGrpcConnection(
            options,
            servers,
            channelFactory ?? new GrpcNacosChannelFactory(options.RequestTimeout),
            clock,
            loggerFactory.CreateLogger<NacosGrpcConnection>(),
            random,
            authProvider);

        NacosConfigSnapshotStore? configSnapshots = CreateConfigSnapshotStore(options);
        NacosServiceSnapshotStore? namingSnapshots = CreateServiceSnapshotStore(options);

        var config = new NacosConfigClient(
            options,
            connection,
            clock,
            loggerFactory.CreateLogger<NacosConfigClient>(),
            configSnapshots);

        var naming = new NacosNamingClient(
            options,
            connection,
            clock,
            loggerFactory.CreateLogger<NacosNamingClient>(),
            namingSnapshots);

        var factory = new NacosClientFactory(
            options, connection, config, naming, loggerFactory.CreateLogger<NacosClientFactory>());

        // Domain pushes arrive on the shared read loop; routing them here is what turns a
        // transport-level stream into feature-level notifications.
        connection.ServerRequestReceived += factory.OnServerRequestAsync;

        return factory;
    }

    /// <summary>
    /// Connects the shared transport.
    /// </summary>
    /// <param name="ct">A token that cancels the connection attempt.</param>
    /// <returns>A task that completes once the handshake finished.</returns>
    public Task ConnectAsync(CancellationToken ct = default) => _connection.ConnectAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _connection.ServerRequestReceived -= OnServerRequestAsync;

        await _naming.DisposeAsync().ConfigureAwait(false);
        await _config.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Routes a server push to the feature that owns it.
    /// </summary>
    /// <param name="request">The pushed request.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>The response the server expects, or <see langword="null"/> when none is due.</returns>
    private Task<NacosResponse?> OnServerRequestAsync(NacosRequest request, CancellationToken ct)
    {
        switch (request)
        {
            case ConfigChangeNotifyRequest notification:
                // The push is answered before the content is fetched. The server only needs to
                // know the client received the notification, and holding the acknowledgement
                // until after a round trip would delay every other message on the stream.
                _ = FetchChangedConfigAsync(notification, ct);
                return Task.FromResult<NacosResponse?>(
                    new ConfigChangeNotifyResponse { RequestId = notification.RequestId });

            case NotifySubscriberRequest notification:
                // Unlike a configuration change this push carries the complete instance list, so
                // it is applied directly and acknowledged.
                _ = _naming.OnServiceChangedAsync(notification, ct);
                return Task.FromResult<NacosResponse?>(
                    new NotifySubscriberResponse { RequestId = notification.RequestId });

            default:
                return Task.FromResult<NacosResponse?>(null);
        }
    }

    /// <summary>
    /// Fetches and delivers the content for a change notification.
    /// </summary>
    /// <param name="notification">The notification.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once listeners were notified.</returns>
    private async Task FetchChangedConfigAsync(
        ConfigChangeNotifyRequest notification,
        CancellationToken ct)
    {
        try
        {
            // Deliberately not awaited by the caller: the read loop must keep flowing while the
            // new content is fetched.
            await _config.OnConfigChangedAsync(notification, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown; nothing to report.
        }
        catch (Exception ex)
        {
            NacosConfigLog.ListenerFailed(
                _logger, notification.DataId ?? "<unknown>", ex.Message);
        }
    }

    /// <summary>
    /// Creates the configuration snapshot store when local caching is enabled.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <returns>The store, or <see langword="null"/> when caching is disabled.</returns>
    private static NacosConfigSnapshotStore? CreateConfigSnapshotStore(NacosOptions options) =>
        options.Config.EnableLocalCache
            ? new NacosConfigSnapshotStore(ResolveCacheDirectory(options))
            : null;

    /// <summary>
    /// Creates the service snapshot store when local caching is enabled.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <returns>The store, or <see langword="null"/> when caching is disabled.</returns>
    private static NacosServiceSnapshotStore? CreateServiceSnapshotStore(NacosOptions options) =>
        options.Naming.EnableLocalCache
            ? new NacosServiceSnapshotStore(ResolveCacheDirectory(options))
            : null;

    /// <summary>
    /// Resolves the directory that holds local failover files.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <returns>The directory.</returns>
    /// <remarks>
    /// The user profile is the only location that is reliably writable across platforms, and
    /// per-user rather than per-machine so one user's snapshots cannot leak values into another's
    /// application. The namespace is a path segment so two namespaces never overwrite each other.
    /// </remarks>
    private static string ResolveCacheDirectory(NacosOptions options)
    {
        string directory = options.LocalCacheDirectory ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(directory))
        {
            return directory;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lolitacoding.Nacos",
            options.Namespace);
    }
}
