using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// Owns the AI registry connection and the per-resource clients that use it.
/// </summary>
/// <remarks>
/// <para>
/// Every AI resource is served by the same server-side module, and Nacos routes a connection to
/// that module by the <c>module</c> label it declares during the handshake, so the whole registry
/// shares one connection rather than opening one per resource type.
/// </para>
/// <para>
/// The connection is created the first time a resource is used, which is why this type exists
/// separately from the clients it hands out: the clients are cheap and can be created up front,
/// while the connection cannot.
/// </para>
/// </remarks>
internal sealed class NacosAiClient : INacosAiClient, IDisposable
{
    private readonly NacosLazyModuleConnection _connection;
    private readonly NacosMcpClient _mcp;
    private readonly NacosPromptClient _prompt;
    private readonly NacosSkillClient _skill;
    private readonly NacosAgentSpecClient _agentSpec;
    private readonly NacosAgentCardClient _agentCard;
    private readonly NacosAgentRegistryClient _agent;

    /// <summary>
    /// This process's AI publisher identity.
    /// </summary>
    /// <remarks>
    /// Nacos records the identifier with every MCP or agent endpoint a connection registers, so an
    /// endpoint can be attributed to the process that owns it — which is what lets a stale endpoint
    /// be cleaned up after a restart. It is minted once per client rather than per connection so a
    /// reconnect does not orphan what the previous connection registered.
    /// </remarks>
    private readonly string _clientUuid = Guid.NewGuid().ToString();

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAiClient"/> class.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <param name="servers">The parsed server addresses.</param>
    /// <param name="channelFactory">The channel factory that opens the gRPC channels.</param>
    /// <param name="httpClient">The client the AI module's HTTP-only resources are read over.</param>
    /// <param name="timeProvider">The clock used for reconnect timing.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="random">The random source used for reconnect jitter.</param>
    /// <param name="authProvider">Supplies the access token, or <see langword="null"/> for none.</param>
    public NacosAiClient(
        NacosOptions options,
        IReadOnlyList<NacosServerAddress> servers,
        INacosChannelFactory channelFactory,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        Random? random = null,
        INacosAuthProvider? authProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(channelFactory);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        ILogger<NacosGrpcConnection> connectionLogger = loggerFactory.CreateLogger<NacosGrpcConnection>();

        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NacosAiRemote.ClientUuidLabel] = _clientUuid,
        };

        _connection = new NacosLazyModuleConnection(async ct =>
        {
            var connection = new NacosGrpcConnection(
                options,
                NacosConnectionModule.Ai,
                servers,
                channelFactory,
                timeProvider,
                connectionLogger,
                random,
                authProvider,
                labels);

            // An endpoint published through the AI module is an instance owned by the connection
            // that registered it, so the server drops it with the connection. Replaying is what
            // keeps it published across a reconnect.
            connection.Reconnected += OnReconnectedAsync;

            // A watch hint arrives as a pushed request rather than as a reply, and only the AI
            // module pushes one. The manager that owns the watches is what answers it, because the
            // watch key only means something to the client that installed the watch.
            connection.ServerRequestReceived += OnServerRequestAsync;

            await connection.ConnectAsync(ct).ConfigureAwait(false);
            return connection;
        });

        _mcp = new NacosMcpClient(this, options, timeProvider, loggerFactory.CreateLogger<NacosMcpClient>());
        _prompt = new NacosPromptClient(this, options, timeProvider, loggerFactory.CreateLogger<NacosPromptClient>());

        var http = new NacosAiHttpClient(
            servers,
            httpClient,
            options.RequestTimeout,
            loggerFactory.CreateLogger<NacosAiHttpClient>(),
            authProvider);

        _skill = new NacosSkillClient(http, options, timeProvider, loggerFactory.CreateLogger<NacosSkillClient>());
        _agentSpec = new NacosAgentSpecClient(
            http, options, timeProvider, loggerFactory.CreateLogger<NacosAgentSpecClient>());

        _agentCard = new NacosAgentCardClient(
            this, options, timeProvider, loggerFactory.CreateLogger<NacosAgentCardClient>());

        _agent = new NacosAgentRegistryClient(
            this, options, timeProvider, loggerFactory, random);
    }

    /// <inheritdoc />
    public INacosMcpRegistry Mcp => _mcp;

    /// <inheritdoc />
    public INacosPromptRegistry Prompt => _prompt;

    /// <inheritdoc />
    public INacosSkillRegistry Skill => _skill;

    /// <inheritdoc />
    public INacosAgentSpecRegistry AgentSpec => _agentSpec;

    /// <inheritdoc />
    public INacosAgentCardRegistry AgentCard => _agentCard;

    /// <inheritdoc />
    public INacosAgentRegistry Agent => _agent;

    /// <summary>
    /// Gets the AI registry connection, creating it if this is the first use.
    /// </summary>
    /// <param name="ct">A token that cancels the creation.</param>
    /// <returns>The connected connection.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    public Task<NacosGrpcConnection> GetConnectionAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _connection.GetAsync(ct);
    }

    /// <summary>
    /// Replays every endpoint this process published.
    /// </summary>
    /// <param name="state">The state the transport reached.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once state was restored.</returns>
    private async Task OnReconnectedAsync(NacosTransportState state, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        // The connection the watches were installed on is gone, so the server forgot them: they are
        // re-installed and re-read before the endpoints are replayed, because a caller following an
        // agent is waiting on a state that the outage may have changed.
        await _agent.Watch.RestoreAsync(ct).ConfigureAwait(false);
        await _mcp.RestoreAsync(ct).ConfigureAwait(false);
        await _agentCard.RestoreAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a message the server pushed to this connection.
    /// </summary>
    /// <param name="request">The pushed message.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>
    /// The reply, or <see langword="null"/> when no handler recognises the message.
    /// </returns>
    private Task<NacosResponse?> OnServerRequestAsync(NacosRequest request, CancellationToken ct) =>
        _agent.Watch.OnServerRequestAsync(request, ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // The subscriptions are stopped first: their refresh loops talk over the connection, and
        // completing them ends the enumerations a caller may still be reading. The watches are
        // closed for the same reason, and before the connection so a removal can still be sent.
        await _agent.DisposeAsync().ConfigureAwait(false);
        await _agentCard.DisposeAsync().ConfigureAwait(false);
        await _agentSpec.DisposeAsync().ConfigureAwait(false);
        await _skill.DisposeAsync().ConfigureAwait(false);
        await _prompt.DisposeAsync().ConfigureAwait(false);
        await _mcp.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the client synchronously.
    /// </summary>
    /// <remarks>
    /// A dependency injection container disposes its singletons synchronously by default, and a
    /// service that implements only <see cref="IAsyncDisposable"/> makes the container throw when it
    /// is disposed that way. Like the shared client, configuration and naming, the AI client is
    /// therefore also synchronously disposable; the disposal closes in-process resources rather than
    /// waiting on the server, so blocking briefly here is safe.
    /// </remarks>
    void IDisposable.Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
