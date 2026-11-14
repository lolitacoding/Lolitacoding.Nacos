using System.Runtime.CompilerServices;
using System.Text.Json;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// The MCP server registry, served over the AI registry connection or the client OpenAPI.
/// </summary>
/// <remarks>
/// <para>
/// A read or a release is one request and the answer the server gives is the answer the caller sees:
/// Nacos owns the version manifest and the label that points at the latest version, so there is
/// nothing for the client to decide. A subscription is the exception — it is served by a cached copy
/// that is refreshed on an interval, because Nacos has no MCP change push to subscribe to.
/// </para>
/// <para>
/// Which transport answers is <see cref="NacosAIOptions.TransportMode"/>, with
/// <see cref="NacosAIOptions.McpTransportMode"/> overriding it for this registry. A read may fall
/// back to the other transport; a release may not, because the attempt that failed may already have
/// created the version.
/// </para>
/// <para>
/// Endpoints published through this client are instances of a Naming service, so they are tied to
/// the connection that registered them and are replayed after a reconnect by
/// <see cref="RestoreAsync"/>. They stay on gRPC whatever the transport says: the HTTP endpoint
/// contract gives them a different owner and lifetime, and that is a separate piece of work.
/// </para>
/// </remarks>
internal sealed class NacosMcpClient : INacosMcpRegistry, IAsyncDisposable
{
    /// <summary>The error code Nacos uses for a feature a server does not implement.</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The status an HTTP read gets when the server or version is not served.</summary>
    private const int NotFoundErrorCode = 404;

    /// <summary>The client OpenAPI path that serves and releases one MCP server.</summary>
    private const string McpPath = "/v3/client/ai/mcp";

    /// <summary>The highest port a TCP endpoint can use.</summary>
    private const int MaxPort = 65535;

    private readonly NacosAIClient _owner;
    private readonly NacosOptions _options;
    private readonly NacosAIHttpClient _http;
    private readonly ILogger<NacosMcpClient> _logger;
    private readonly NacosMcpServerCache _cache;
    private readonly NacosMcpEndpointPublication _endpoints;
    private readonly NacosAITransportFallback _fallback;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosMcpClient"/> class.
    /// </summary>
    /// <param name="owner">The AI client that owns the connection.</param>
    /// <param name="options">The client options.</param>
    /// <param name="timeProvider">The clock the subscription refresh interval is measured with.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="http">The HTTP client the MCP endpoint is read and released over.</param>
    /// <param name="snapshots">
    /// The disk failover store, or <see langword="null"/> when details are kept in memory only.
    /// </param>
    public NacosMcpClient(
        NacosAIClient owner,
        NacosOptions options,
        TimeProvider timeProvider,
        ILogger<NacosMcpClient> logger,
        NacosAIHttpClient http,
        NacosAISnapshotStore? snapshots = null)
    {
        _owner = owner;
        _options = options;
        _http = http;
        _logger = logger;
        _fallback = new NacosAITransportFallback(timeProvider);

        _cache = new NacosMcpServerCache(
            QueryAsync,
            options.AI.CacheRefreshInterval,
            timeProvider,
            logger,
            NacosAISnapshot<NacosMcpServerDetail>.Create(
                snapshots, NacosAISnapshotStore.McpKind, NacosJsonContext.Default.NacosMcpServerDetail));
        _endpoints = new NacosMcpEndpointPublication(owner.GetConnectionAsync, options.Namespace, logger);
    }

    /// <inheritdoc />
    public async Task<NacosMcpServerDetail> GetServerAsync(
        string name,
        string? version = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // A gRPC-only client opens the connection anyway, so the ability is checked before the
        // request. Http has no connection to check against, and Auto may not have one either: there
        // the check is left to the transport that answers, which reports a server that cannot serve
        // the registry with the same code the local check would have thrown.
        if (EffectiveTransport == NacosAITransportMode.Grpc)
        {
            NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
            EnsureSupported(connection, name);
        }

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

        NacosAITransportMode mode = EffectiveTransport;

        if (mode == NacosAITransportMode.Http
            || (mode == NacosAITransportMode.Auto && !_fallback.GrpcIsWorthTrying()))
        {
            return await ReleaseOverHttpAsync(server, version, tools, resources, endpoint, createDraft, ct)
                .ConfigureAwait(false);
        }

        try
        {
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
                NacosAILog.McpServerReleased(_logger, server.Name, version, response.McpId);
            }

            return response.McpId;
        }
        catch (NacosException ex) when (
            mode == NacosAITransportMode.Auto
            && ex is NacosConnectionException or NacosTimeoutException)
        {
            // Deliberately not retried over HTTP: the attempt may have reached the server and created
            // the version, and a second release would then either conflict with it or publish twice.
            // The window is still recorded, so the next operation does not pay the same failed
            // connect before choosing HTTP.
            _fallback.RecordGrpcFailure();

            NacosAILog.AiReleaseTransportUnavailable(_logger, "MCP server", ex.Message);

            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosMcpServerDetail> SubscribeServerAsync(
        string name,
        string? version = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // The connection is opened here rather than at the call, because an async iterator runs on
        // the first move, which is also when a caller that never iterates should pay nothing. Only a
        // gRPC-only client has a connection to open: an HTTP or Auto client leaves the seeding read
        // to the cache, which reads over whichever transport answers and restores the disk copy when
        // neither does. The ability is checked on the same connection, so a server that cannot be
        // reached cannot be gated on either.
        if (EffectiveTransport == NacosAITransportMode.Grpc)
        {
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
        NacosAITransportMode mode = EffectiveTransport;

        if (mode == NacosAITransportMode.Http
            || (mode == NacosAITransportMode.Auto && !_fallback.GrpcIsWorthTrying()))
        {
            return await QueryOverHttpAsync(name, version, ct).ConfigureAwait(false);
        }

        try
        {
            return await QueryOverGrpcAsync(name, version, ct).ConfigureAwait(false);
        }
        catch (NacosException ex) when (
            mode == NacosAITransportMode.Auto
            && ex is NacosConnectionException or NacosTimeoutException)
        {
            // The transport is what failed, not the registry, so the same read is retried over HTTP.
            // A read is safe to repeat: it changes nothing, so the second attempt cannot duplicate an
            // effect the first one had.
            _fallback.RecordGrpcFailure();

            NacosAILog.AiTransportFallback(_logger, "MCP server", ex.Message);

            return await QueryOverHttpAsync(name, version, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the transport an MCP registry operation uses.
    /// </summary>
    private NacosAITransportMode EffectiveTransport =>
        _options.AI.ResolveTransport(_options.AI.McpTransportMode);

    /// <summary>
    /// Reads one version of a server over the AI gRPC connection.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved server detail.</returns>
    private async Task<NacosMcpServerDetail> QueryOverGrpcAsync(
        string name,
        string? version,
        CancellationToken ct)
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
            NacosAILog.McpServerDetailMissing(_logger, name, version);

            throw new NacosServerException(
                string.Concat("The Nacos server returned no detail for MCP server '", name, "'."));
        }

        return detail;
    }

    /// <summary>
    /// Reads one version of a server over the client OpenAPI.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved server detail.</returns>
    /// <exception cref="NacosServerException">
    /// The server or version does not exist (the not-found code), or the read failed.
    /// </exception>
    /// <remarks>
    /// The endpoint answers with the client OpenAPI's result envelope, so the detail is read out of
    /// <c>data</c>. A missing server arrives as HTTP 404, which is turned into the same code the gRPC
    /// transport reports — the subscription cache decides what a missing version means by the code,
    /// not by the transport.
    /// </remarks>
    private async Task<NacosMcpServerDetail> QueryOverHttpAsync(
        string name,
        string? version,
        CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string?>>(3)
        {
            new("namespaceId", _options.Namespace),
            new("mcpName", name),
        };

        AddIfPresent(query, "version", version);

        NacosAIHttpResponse response = await _http.GetAsync(McpPath, query, ct).ConfigureAwait(false);

        switch ((int)response.StatusCode)
        {
            case 200:
                break;

            case NotFoundErrorCode:
                throw new NacosServerException(
                    NotFoundErrorCode,
                    response.ErrorMessage
                        ?? string.Concat("The Nacos server does not serve MCP server '", name, "'."));

            case 403:
                throw new NacosAuthException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat("The Nacos server refused the read of MCP server '", name, "'."));

            default:
                throw new NacosServerException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat(
                            "The Nacos server answered the read of MCP server '",
                            name,
                            "' with HTTP ",
                            ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "."));
        }

        NacosMcpServerResult? result;

        try
        {
            result = JsonSerializer.Deserialize(response.Body, NacosJsonContext.Default.NacosMcpServerResult);
        }
        catch (JsonException ex)
        {
            // A 200 carrying something that is not a result — an intermediary's error page, say —
            // would otherwise surface as a JSON exception from inside the serializer, which says
            // nothing about what the server did.
            NacosAILog.McpServerDetailMissing(_logger, name, version);

            throw new NacosServerException(
                string.Concat(
                    "The Nacos server returned a body for MCP server '",
                    name,
                    "' that is not a result."),
                ex);
        }

        if (result is null || result.Code != 0 || result.Data is null)
        {
            // The endpoint answers with its own result envelope, so a body that is not one, or one
            // that reports a failure, did not produce the detail the caller asked for.
            NacosAILog.McpServerDetailMissing(_logger, name, version);

            throw new NacosServerException(
                result?.Code ?? 0,
                result?.Message
                    ?? string.Concat("The Nacos server returned no detail for MCP server '", name, "'."));
        }

        return result.Data;
    }

    /// <summary>
    /// Releases one version of a server over the client OpenAPI.
    /// </summary>
    /// <param name="server">The server specification.</param>
    /// <param name="version">The version the release creates.</param>
    /// <param name="tools">The tool specification, or <see langword="null"/> for none.</param>
    /// <param name="resources">The resource specification, or <see langword="null"/> for none.</param>
    /// <param name="endpoint">How the backend is reached, or <see langword="null"/> to let Nacos register one.</param>
    /// <param name="createDraft">Whether to store a draft instead of publishing.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The internal identifier Nacos assigned, or <see langword="null"/> when it reported none.</returns>
    /// <exception cref="NacosServerException">The release was refused or failed.</exception>
    /// <remarks>
    /// The endpoint is a form POST whose complex members are JSON in a field of their own, rather than
    /// one JSON body. The fields are the ones the server binds literally, so a specification is
    /// serialized with the same contract the gRPC message uses and then sent as a value.
    /// </remarks>
    private async Task<string?> ReleaseOverHttpAsync(
        NacosMcpServer server,
        string version,
        NacosMcpToolSpecification? tools,
        NacosMcpResourceSpecification? resources,
        NacosMcpEndpointSpec? endpoint,
        bool createDraft,
        CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>(7)
        {
            new("namespaceId", _options.Namespace),
            new("mcpName", server.Name!),
            new("serverSpecification", JsonSerializer.Serialize(server, NacosJsonContext.Default.NacosMcpServer)),
        };

        if (tools is not null)
        {
            form.Add(new KeyValuePair<string, string>(
                "toolSpecification",
                JsonSerializer.Serialize(tools, NacosJsonContext.Default.NacosMcpToolSpecification)));
        }

        if (resources is not null)
        {
            form.Add(new KeyValuePair<string, string>(
                "resourceSpecification",
                JsonSerializer.Serialize(resources, NacosJsonContext.Default.NacosMcpResourceSpecification)));
        }

        if (endpoint is not null)
        {
            form.Add(new KeyValuePair<string, string>(
                "endpointSpecification",
                JsonSerializer.Serialize(endpoint, NacosJsonContext.Default.NacosMcpEndpointSpec)));
        }

        form.Add(new KeyValuePair<string, string>("createDraft", createDraft ? "true" : "false"));

        NacosAIHttpResponse response = await _http.PostFormAsync(
            McpPath,
            form,
            _options.RequestTimeout,
            headers: null,
            ct).ConfigureAwait(false);

        switch ((int)response.StatusCode)
        {
            case 200:
                break;

            case 403:
                throw new NacosAuthException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat("The Nacos server refused the release of MCP server '", server.Name, "'."));

            default:
                throw new NacosServerException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat(
                            "The Nacos server answered the release of MCP server '",
                            server.Name,
                            "' with HTTP ",
                            ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "."));
        }

        NacosMcpReleaseResult? result;

        try
        {
            result = JsonSerializer.Deserialize(response.Body, NacosJsonContext.Default.NacosMcpReleaseResult);
        }
        catch (JsonException ex)
        {
            throw new NacosServerException(
                string.Concat(
                    "The Nacos server returned a body for the release of MCP server '",
                    server.Name,
                    "' that is not a result."),
                ex);
        }

        if (result is null || result.Code != 0)
        {
            throw new NacosServerException(
                result?.Code ?? 0,
                result?.Message
                    ?? string.Concat(
                        "The Nacos server returned no result for the release of MCP server '",
                        server.Name,
                        "'."));
        }

        if (result.Data is not null)
        {
            NacosAILog.McpServerReleased(_logger, server.Name!, version, result.Data);
        }

        return result.Data;
    }

    /// <summary>
    /// Adds a query parameter when it carries a value.
    /// </summary>
    /// <param name="query">The parameters collected so far.</param>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The value, or <see langword="null"/> to omit the parameter.</param>
    private static void AddIfPresent(
        List<KeyValuePair<string, string?>> query,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add(new KeyValuePair<string, string?>(name, value));
        }
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
        if (connection.ServerAbilities.TryGetValue(NacosAIServerAbility.Mcp, out bool supported)
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

        if (connection.ServerAbilities.TryGetValue(NacosAIServerAbility.McpDraftRelease, out bool supported)
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
