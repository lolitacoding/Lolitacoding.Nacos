using System.Runtime.CompilerServices;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// The MCP server registry, served over the AI registry connection.
/// </summary>
/// <remarks>
/// <para>
/// A read or a release is one request and the answer the server gives is the answer the caller sees:
/// Nacos owns the version manifest and the label that points at the latest version, so there is
/// nothing for the client to decide. A subscription is the exception — it is served by a cached copy
/// that is refreshed on an interval, because Nacos has no MCP change push to subscribe to.
/// </para>
/// <para>
/// Endpoints published through this client are instances of a Naming service, so they are tied to
/// the connection that registered them and are replayed after a reconnect by
/// <see cref="RestoreAsync"/>.
/// </para>
/// </remarks>
internal sealed class NacosMcpClient : INacosMcpRegistry, IAsyncDisposable
{
    /// <summary>The error code Nacos uses for a feature a server does not implement.</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The highest port a TCP endpoint can use.</summary>
    private const int MaxPort = 65535;

    private readonly NacosAiClient _owner;
    private readonly NacosOptions _options;
    private readonly ILogger<NacosMcpClient> _logger;
    private readonly NacosMcpServerCache _cache;
    private readonly NacosMcpEndpointPublication _endpoints;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosMcpClient"/> class.
    /// </summary>
    /// <param name="owner">The AI client that owns the connection.</param>
    /// <param name="options">The client options.</param>
    /// <param name="timeProvider">The clock the subscription refresh interval is measured with.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="snapshots">
    /// The disk failover store, or <see langword="null"/> when details are kept in memory only.
    /// </param>
    public NacosMcpClient(
        NacosAiClient owner,
        NacosOptions options,
        TimeProvider timeProvider,
        ILogger<NacosMcpClient> logger,
        NacosAiSnapshotStore? snapshots = null)
    {
        _owner = owner;
        _options = options;
        _logger = logger;

        _cache = new NacosMcpServerCache(
            QueryAsync,
            options.Ai.CacheRefreshInterval,
            timeProvider,
            logger,
            NacosAiSnapshot<NacosMcpServerDetail>.Create(
                snapshots, NacosAiSnapshotStore.McpKind, NacosJsonContext.Default.NacosMcpServerDetail));
        _endpoints = new NacosMcpEndpointPublication(owner.GetConnectionAsync, options.Namespace, logger);
    }

    /// <inheritdoc />
    public async Task<NacosMcpServerDetail> GetServerAsync(
        string name,
        string? version = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, name);

        return await QueryAsync(name, version, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> ReleaseServerAsync(
        NacosMcpServer server,
        NacosMcpToolSpecification? tools = null,
        NacosMcpResourceSpecification? resources = null,
        NacosMcpEndpointSpec? endpoint = null,
        bool createDraft = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(server.Name, nameof(server));

        string? version = VersionOf(server);

        if (version is null)
        {
            // The version is what the release creates; without one the server would have to invent
            // it, and a later release could not name the same version again.
            throw new ArgumentException(
                "An MCP server release must carry a version, either through VersionDetail or Version.",
                nameof(server));
        }

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, server.Name);
        EnsureDraftSupported(connection, server.Name, createDraft);

        var request = new ReleaseMcpServerRequest
        {
            NamespaceId = _options.Namespace,
            McpName = server.Name,
            ServerSpecification = server,
            ToolSpecification = tools,
            ResourceSpecification = resources,
            EndpointSpecification = endpoint,
            CreateDraft = createDraft,
        };

        ReleaseMcpServerResponse response =
            await connection.RequestAsync<ReleaseMcpServerResponse>(request, ct).ConfigureAwait(false);

        if (response.McpId is not null)
        {
            NacosAiLog.McpServerReleased(_logger, server.Name, version, response.McpId);
        }

        return response.McpId;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosMcpServerDetail> SubscribeServerAsync(
        string name,
        string? version = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The connection is opened here rather than at the call, because an async iterator runs on
        // the first move, which is also when a caller that never iterates should pay nothing. The
        // ability the server advertises is checked on the same connection, so a server that cannot
        // be reached cannot be gated on: when the version was cached on disk the subscription starts
        // from that copy and the loop reads the server, and its ability, when it comes back.
        try
        {
            NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
            EnsureSupported(connection, name);
        }
        catch (NacosException ex) when (ex is NacosConnectionException or NacosTimeoutException)
        {
            if (!await _cache.RestoreAsync(name, version, ct).ConfigureAwait(false))
            {
                throw;
            }
        }

        await foreach (NacosMcpServerDetail detail in _cache.SubscribeAsync(name, version, ct).ConfigureAwait(false))
        {
            yield return detail;
        }
    }

    /// <inheritdoc />
    public async Task RegisterServerEndpointAsync(
        string name,
        string address,
        int port,
        string? version = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, MaxPort);

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, name);

        await _endpoints.RegisterAsync(name, address, port, version, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeregisterServerEndpointAsync(
        string name,
        string address,
        int port,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, MaxPort);

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, name);

        await _endpoints.DeregisterAsync(name, address, port, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Republishes every endpoint this process still owns.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every endpoint was attempted.</returns>
    public Task RestoreAsync(CancellationToken ct) => _endpoints.RestoreAsync(ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _cache.DisposeAsync();

    /// <summary>
    /// Reads one version of a server from the registry.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved server detail.</returns>
    /// <remarks>
    /// This is the raw read that both <see cref="GetServerAsync"/> and the subscription refresh loop
    /// use. It deliberately does not check the server's ability table: a subscription's refresh must
    /// not fail for a reason a caller cannot act on, and the check belongs on the paths a caller
    /// enters.
    /// </remarks>
    private async Task<NacosMcpServerDetail> QueryAsync(string name, string? version, CancellationToken ct)
    {
        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);

        var request = new QueryMcpServerRequest
        {
            NamespaceId = _options.Namespace,
            McpName = name,
            Version = version,
        };

        QueryMcpServerResponse response =
            await connection.RequestAsync<QueryMcpServerResponse>(request, ct).ConfigureAwait(false);

        NacosMcpServerDetail? detail = response.McpServerDetailInfo;

        if (detail is null)
        {
            // A missing server or version is reported as a failure result code, which the transport
            // has already turned into an exception. Reaching here therefore means the reply did not
            // match the contract, and reporting it as a missing server would hide that.
            NacosAiLog.McpServerDetailMissing(_logger, name, version);

            throw new NacosServerException(
                string.Concat("The Nacos server returned no detail for MCP server '", name, "'."));
        }

        return detail;
    }

    /// <summary>
    /// Reads the version a release will create.
    /// </summary>
    /// <param name="server">The server specification.</param>
    /// <returns>The version, or <see langword="null"/> when the specification carries none.</returns>
    /// <remarks>
    /// Nacos moved the version into the version detail object so it could carry a release date and
    /// a latest flag, and kept the plain string for servers that predate the move. A specification
    /// may legitimately use either, so both are read, with the structured form winning.
    /// </remarks>
    private static string? VersionOf(NacosMcpServer server)
    {
        string? version = server.VersionDetail?.Version;

        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        return string.IsNullOrWhiteSpace(server.Version) ? null : server.Version;
    }

    /// <summary>
    /// Fails the operation when the server does not implement the MCP registry.
    /// </summary>
    /// <param name="connection">The AI registry connection.</param>
    /// <param name="name">The MCP server the operation is about.</param>
    /// <exception cref="NacosServerException">The server advertises that it cannot serve the registry.</exception>
    /// <remarks>
    /// Only an explicit rejection is treated as one. A server that says nothing about the ability
    /// may still serve the request, and letting the request fail on its own reports the truth more
    /// precisely than guessing from the absence of a field.
    /// </remarks>
    private static void EnsureSupported(NacosGrpcConnection connection, string name)
    {
        if (connection.ServerAbilities.TryGetValue(NacosAiServerAbility.Mcp, out bool supported)
            && !supported)
        {
            throw new NacosServerException(
                NotImplementedErrorCode,
                string.Concat(
                    "The connected Nacos server does not implement the MCP registry, so MCP server '",
                    name,
                    "' cannot be used."));
        }
    }

    /// <summary>
    /// Fails a draft release when the server does not implement lifecycle drafts.
    /// </summary>
    /// <param name="connection">The AI registry connection.</param>
    /// <param name="name">The MCP server being released.</param>
    /// <param name="createDraft">Whether a draft was requested.</param>
    /// <exception cref="NacosServerException">The server advertises that it cannot store drafts.</exception>
    /// <remarks>
    /// Failing here rather than at the server matters: an older server would read
    /// <c>createDraft</c> as an unknown field and publish the version directly, which is the
    /// opposite of what a caller asking for a draft wants.
    /// </remarks>
    private static void EnsureDraftSupported(
        NacosGrpcConnection connection,
        string name,
        bool createDraft)
    {
        if (!createDraft)
        {
            return;
        }

        if (connection.ServerAbilities.TryGetValue(NacosAiServerAbility.McpDraftRelease, out bool supported)
            && !supported)
        {
            throw new NacosServerException(
                NotImplementedErrorCode,
                string.Concat(
                    "The connected Nacos server does not implement MCP lifecycle drafts, so a draft of '",
                    name,
                    "' cannot be created."));
        }
    }
}
