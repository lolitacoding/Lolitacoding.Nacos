using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Locking;
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
/// Each module gets its own connection. Nacos routes a connection by the <c>module</c> label it
/// declares during the handshake, and the naming, lock and AI servers discard connections that did
/// not declare them, so the domains cannot share a single stream. Configuration and naming
/// therefore talk over separate connections that each carry their own label, ability table and
/// reconnect lifecycle; the lock connection is created by its client the first time a lock is used.
/// </para>
/// <para>
/// Assembling the graph is the only place that knows how the pieces fit together, which keeps the
/// clients themselves free of construction concerns and makes them testable in isolation.
/// </para>
/// </remarks>
internal sealed class NacosClientFactory : IAsyncDisposable
{
    private readonly NacosOptions _options;
    private readonly NacosGrpcConnection _configConnection;
    private readonly NacosGrpcConnection _namingConnection;
    private readonly NacosConfigClient _config;
    private readonly NacosNamingClient _naming;
    private readonly NacosLockClient _lock;
    private readonly NacosAiClient _ai;
    private readonly NacosEndpointServerListProvider? _endpointProvider;
    private readonly ILogger<NacosClientFactory> _logger;

    private bool _disposed;

    private NacosClientFactory(
        NacosOptions options,
        NacosGrpcConnection configConnection,
        NacosGrpcConnection namingConnection,
        NacosConfigClient config,
        NacosNamingClient naming,
        NacosLockClient @lock,
        NacosAiClient ai,
        NacosEndpointServerListProvider? endpointProvider,
        ILogger<NacosClientFactory> logger)
    {
        _options = options;
        _configConnection = configConnection;
        _namingConnection = namingConnection;
        _config = config;
        _naming = naming;
        _lock = @lock;
        _ai = ai;
        _endpointProvider = endpointProvider;
        _logger = logger;
    }

    /// <summary>Gets the configuration client.</summary>
    public INacosConfigClient Config => _config;

    /// <summary>Gets the naming client.</summary>
    public INacosNamingClient Naming => _naming;

    /// <summary>Gets the distributed lock client.</summary>
    /// <remarks>
    /// Unlike the other modules this one is not connected until it is first used, so returning it
    /// costs nothing to an application that never takes a lock.
    /// </remarks>
    public INacosLockClient Lock => _lock;

    /// <summary>Gets the AI registry client.</summary>
    /// <remarks>
    /// Like the lock client this one is not connected until it is first used, so an application
    /// that never asks the registry for anything pays nothing for it.
    /// </remarks>
    public INacosAiClient Ai => _ai;

    /// <summary>
    /// Creates the clients and their per-module connections.
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

        TimeProvider clock = timeProvider ?? TimeProvider.System;

        HttpClient client = httpClient ?? CreateHttpClient(options.Tls);

        // An address server replaces the configured addresses rather than adding to them: the
        // published list is the authority on which nodes exist, so a client that has one does not
        // also dial servers from configuration.
        NacosServerList servers;
        NacosEndpointServerListProvider? endpointProvider = null;

        if (options.HasEndpoint)
        {
            if (options.ServerAddresses.Count > 0)
            {
                NacosTransportLog.EndpointOverridesServers(
                    loggerFactory.CreateLogger("Lolitacoding.Nacos.AddressServer"),
                    options.Endpoint!,
                    options.ServerAddresses.Count);
            }

            servers = new NacosServerList(
                [],
                isDynamic: true,
                source: NacosEndpointServerListProvider.BuildAddressServerUri(options).AbsoluteUri);

            endpointProvider = new NacosEndpointServerListProvider(
                options,
                servers,
                client,
                clock,
                loggerFactory.CreateLogger<NacosEndpointServerListProvider>());

            // Seeding runs in the background so client creation never blocks on the address
            // server; the connections wait for the list for as long as their timeout allows.
            endpointProvider.Start();
        }
        else
        {
            servers = new NacosServerList(
                NacosServerAddress.ParseAll(options.ServerAddresses, options.ContextPath));
        }

        INacosAuthProvider authProvider = NacosAuthProviderFactory.Create(
            options, servers, client, clock, loggerFactory.CreateLogger("Lolitacoding.Nacos.Auth"), random);

        // One channel factory is shared so a test substitute sees every module, and one auth
        // provider is shared so all connections present the same access token.
        INacosChannelFactory channels =
            channelFactory ?? new GrpcNacosChannelFactory(options.RequestTimeout, options.Tls);

        var configConnection = new NacosGrpcConnection(
            options,
            NacosConnectionModule.Config,
            servers,
            channels,
            clock,
            loggerFactory.CreateLogger<NacosGrpcConnection>(),
            random,
            authProvider);

        var namingConnection = new NacosGrpcConnection(
            options,
            NacosConnectionModule.Naming,
            servers,
            channels,
            clock,
            loggerFactory.CreateLogger<NacosGrpcConnection>(),
            random,
            authProvider);

        NacosConfigSnapshotStore? configSnapshots = CreateConfigSnapshotStore(options);
        NacosServiceSnapshotStore? namingSnapshots = CreateServiceSnapshotStore(options);
        NacosAiSnapshotStore? aiSnapshots = CreateAiSnapshotStore(options);

        var config = new NacosConfigClient(
            options,
            configConnection,
            clock,
            loggerFactory.CreateLogger<NacosConfigClient>(),
            configSnapshots);

        var naming = new NacosNamingClient(
            options,
            namingConnection,
            clock,
            loggerFactory.CreateLogger<NacosNamingClient>(),
            namingSnapshots);

        var @lock = new NacosLockClient(
            options,
            servers,
            channels,
            clock,
            loggerFactory,
            random,
            authProvider);

        var ai = new NacosAiClient(
            options,
            servers,
            channels,
            client,
            clock,
            loggerFactory,
            random,
            authProvider,
            aiSnapshots);

        var factory = new NacosClientFactory(
            options, configConnection, namingConnection, config, naming, @lock, ai, endpointProvider,
            loggerFactory.CreateLogger<NacosClientFactory>());

        // Domain pushes arrive on the connection of the module that owns them; routing them here
        // is what turns a transport-level stream into feature-level notifications.
        configConnection.ServerRequestReceived += factory.OnConfigServerRequestAsync;
        namingConnection.ServerRequestReceived += factory.OnNamingServerRequestAsync;

        return factory;
    }

    /// <summary>
    /// Connects every module's transport.
    /// </summary>
    /// <param name="ct">A token that cancels the connection attempts.</param>
    /// <returns>A task that completes once every handshake finished.</returns>
    /// <remarks>
    /// The connections are attempted concurrently so one slow server cannot serialise startup
    /// behind the other. With <see cref="NacosOptions.Optional"/> set, an unreachable server is
    /// tolerated on every connection. A module whose connection is opened on first use — the
    /// distributed lock and the AI registry — is not connected here, because nothing has asked for
    /// it yet.
    /// </remarks>
    public Task ConnectAsync(CancellationToken ct = default) =>
        Task.WhenAll(_configConnection.ConnectAsync(ct), _namingConnection.ConnectAsync(ct));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _configConnection.ServerRequestReceived -= OnConfigServerRequestAsync;
        _namingConnection.ServerRequestReceived -= OnNamingServerRequestAsync;

        // The address server poll loop goes first: it can replace the list the connections are
        // still reading, and there is no reason to keep polling for a client that is shutting down.
        if (_endpointProvider is { } endpointProvider)
        {
            await endpointProvider.DisposeAsync().ConfigureAwait(false);
        }

        // The lock and AI clients dispose their own connection, which only exists if they were ever
        // used.
        await _ai.DisposeAsync().ConfigureAwait(false);
        await _lock.DisposeAsync().ConfigureAwait(false);
        await _naming.DisposeAsync().ConfigureAwait(false);
        await _config.DisposeAsync().ConfigureAwait(false);
        await _namingConnection.DisposeAsync().ConfigureAwait(false);
        await _configConnection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Routes a server push that arrived on the configuration connection.
    /// </summary>
    /// <param name="request">The pushed request.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>The response the server expects, or <see langword="null"/> when none is due.</returns>
    private Task<NacosResponse?> OnConfigServerRequestAsync(NacosRequest request, CancellationToken ct)
    {
        switch (request)
        {
            case ConfigChangeNotifyRequest notification:
                // The push is answered before the content is fetched. The server only needs to
                // know the client received the notification, and holding the acknowledgement until
                // after a round trip would delay every other message on the stream.
                _ = FetchChangedConfigAsync(notification, ct);
                return Task.FromResult<NacosResponse?>(
                    new ConfigChangeNotifyResponse { RequestId = notification.RequestId });

            case ConfigFuzzyWatchSyncRequest sync:
                // A diff only updates local state, so it is applied inline: it writes to channels
                // and never blocks, which keeps the acknowledgement immediate.
                _config.OnFuzzyWatchSync(sync);
                return Task.FromResult<NacosResponse?>(
                    new ConfigFuzzyWatchSyncResponse { RequestId = sync.RequestId });

            case ConfigFuzzyWatchChangeNotifyRequest change:
                _config.OnFuzzyWatchChange(change);
                return Task.FromResult<NacosResponse?>(
                    new ConfigFuzzyWatchChangeNotifyResponse { RequestId = change.RequestId });

            default:
                return Task.FromResult<NacosResponse?>(null);
        }
    }

    /// <summary>
    /// Routes a server push that arrived on the naming connection.
    /// </summary>
    /// <param name="request">The pushed request.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>The response the server expects, or <see langword="null"/> when none is due.</returns>
    private Task<NacosResponse?> OnNamingServerRequestAsync(NacosRequest request, CancellationToken ct)
    {
        switch (request)
        {
            case NotifySubscriberRequest notification:
                // Unlike a configuration change this push carries the complete instance list, so it
                // is applied directly and acknowledged.
                _ = _naming.OnServiceChangedAsync(notification, ct);
                return Task.FromResult<NacosResponse?>(
                    new NotifySubscriberResponse { RequestId = notification.RequestId });

            case NamingFuzzyWatchSyncRequest sync:
                // A diff only updates local state, so it is applied inline: it writes to channels
                // and never blocks, which keeps the acknowledgement immediate.
                _naming.OnFuzzyWatchSync(sync);
                return Task.FromResult<NacosResponse?>(
                    new NamingFuzzyWatchChangeNotifyResponse { RequestId = sync.RequestId });

            case NamingFuzzyWatchChangeNotifyRequest change:
                _naming.OnFuzzyWatchChange(change);
                return Task.FromResult<NacosResponse?>(
                    new NamingFuzzyWatchChangeNotifyResponse { RequestId = change.RequestId });

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
            NacosConfigLog.ChangeProcessingFailed(
                _logger, notification.DataId ?? "<unknown>", ex.Message);
        }
    }

    /// <summary>
    /// Creates the HTTP client used for authentication.
    /// </summary>
    /// <param name="tls">The TLS settings.</param>
    /// <returns>A client with the configured TLS behaviour, or a default client.</returns>
    private static HttpClient CreateHttpClient(NacosTlsOptions tls)
    {
        SocketsHttpHandler? handler = NacosTlsHandler.CreateHandler(tls);
        return handler is null ? new HttpClient() : new HttpClient(handler);
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
    /// Creates the AI snapshot store when local caching is enabled.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <returns>The store, or <see langword="null"/> when caching is disabled.</returns>
    private static NacosAiSnapshotStore? CreateAiSnapshotStore(NacosOptions options) =>
        options.Ai.EnableLocalCache
            ? new NacosAiSnapshotStore(ResolveCacheDirectory(options))
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
