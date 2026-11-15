using System.Collections.Concurrent;
using System.Globalization;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// Owns the MCP server endpoints this process has published, over either AI transport.
/// </summary>
/// <remarks>
/// <para>
/// An endpoint is registered through the AI module but stored as an instance of the Naming service
/// the server's <c>REF</c> endpoint names. That makes the two transports keep it differently: a
/// gRPC registration is owned by the connection that registered it and disappears with it, so it has
/// to be replayed after a reconnect; an HTTP registration is owned by a client identified by a
/// header, kept alive by a heartbeat and deleted by the server when it goes silent.
/// </para>
/// <para>
/// The owner is therefore sticky. The first registration of an endpoint picks a transport and every
/// later heartbeat or removal goes to the same one: publishing the same endpoint over both would
/// leave the other copy behind, because each transport removes only its own record.
/// </para>
/// <para>
/// A registration the server refused is not kept. The caller saw the failure and can decide whether
/// to try again, so silently publishing an endpoint later — after a reconnect the caller never
/// connected to the attempt — would report a state the caller did not ask for. The exception is a
/// retryable HTTP failure: there the intent is kept and the coordinator replays it, because the
/// server may simply have been restarting.
/// </para>
/// </remarks>
internal sealed class NacosMcpEndpointPublication :
    INacosAIHttpPublicationParticipant,
    IAsyncDisposable
{
    /// <summary>The client OpenAPI path that registers and removes an MCP endpoint.</summary>
    private const string EndpointPath = "/v3/client/ai/mcp/endpoints";

    /// <summary>The client OpenAPI path that heartbeats the shared HTTP publisher.</summary>
    private const string HeartbeatPath = "/v3/client/ai/mcp/endpoints/heartbeat";

    private readonly ConcurrentDictionary<string, Publication> _publications = new(StringComparer.Ordinal);
    private readonly Func<string, CancellationToken, Task<NacosGrpcConnection>> _grpc;
    private readonly NacosAIHttpClient _http;
    private readonly Func<NacosAIPublicationTransport> _selectOwner;
    private readonly NacosAIHttpPublicationCoordinator _coordinator;
    private readonly Action _grpcConnectionFailed;
    private readonly TimeSpan _timeout;
    private readonly string? _namespace;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _closed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosMcpEndpointPublication"/> class.
    /// </summary>
    /// <param name="grpc">
    /// Gets the AI registry connection for a server, checking that the server serves the registry.
    /// </param>
    /// <param name="http">The client the HTTP endpoint contract is called over.</param>
    /// <param name="selectOwner">Chooses the transport a new publication is owned by.</param>
    /// <param name="coordinator">The coordinator that heartbeats and replays HTTP publications.</param>
    /// <param name="grpcConnectionFailed">
    /// Reports that a gRPC publication could not reach the server, so the next publication may choose
    /// HTTP instead.
    /// </param>
    /// <param name="timeout">How long one endpoint request may take.</param>
    /// <param name="namespace">The namespace the endpoints are published in.</param>
    /// <param name="logger">The logger.</param>
    public NacosMcpEndpointPublication(
        Func<string, CancellationToken, Task<NacosGrpcConnection>> grpc,
        NacosAIHttpClient http,
        Func<NacosAIPublicationTransport> selectOwner,
        NacosAIHttpPublicationCoordinator coordinator,
        Action grpcConnectionFailed,
        TimeSpan timeout,
        string? @namespace,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(grpc);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(selectOwner);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(grpcConnectionFailed);
        ArgumentNullException.ThrowIfNull(logger);

        _grpc = grpc;
        _http = http;
        _selectOwner = selectOwner;
        _coordinator = coordinator;
        _grpcConnectionFailed = grpcConnectionFailed;
        _timeout = timeout;
        _namespace = @namespace;
        _logger = logger;

        coordinator.Register(this);
    }

    /// <inheritdoc />
    public string ModuleName => "MCP";

    /// <inheritdoc />
    public bool HasHttpPublication
    {
        get
        {
            foreach (Publication publication in _publications.Values)
            {
                if (publication.Owner == NacosAIPublicationTransport.Http)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <inheritdoc />
    public bool HasRegisteredHttpPublication
    {
        get
        {
            foreach (Publication publication in _publications.Values)
            {
                if (publication.Owner == NacosAIPublicationTransport.Http
                    && !publication.Deregister
                    && !publication.Dirty)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Publishes one endpoint of an MCP server.
    /// </summary>
    /// <param name="name">The MCP server name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    /// <param name="version">
    /// The version the endpoint serves, or <see langword="null"/> for every version of the server.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server accepted the endpoint.</returns>
    public async Task RegisterAsync(
        string name,
        string address,
        int port,
        string? version,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            string key = PortableKey(name, address, port);
            Publication? previous = _publications.GetValueOrDefault(key);
            var desired = new Publication(
                name,
                address,
                port,
                version,
                previous?.Owner ?? _selectOwner())
            {
                Dirty = true,
            };

            // Recorded before it is sent, so a reconnect that races the first attempt still replays
            // it and a failed HTTP attempt is still known to the coordinator.
            _publications[key] = desired;

            try
            {
                NacosClientLivenessInfo? liveness = await SendAsync(desired, ct).ConfigureAwait(false);

                desired.Dirty = false;
                desired.Rollback = null;

                Notify(desired.Owner == NacosAIPublicationTransport.Http ? liveness : null);
            }
            catch (NacosException ex)
            {
                NoteGrpcFailure(desired, ex);
                HandleFailure(key, previous, desired, ex);
                throw;
            }

            NacosAILog.McpEndpointRegistered(_logger, name, address, port);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes one endpoint of an MCP server.
    /// </summary>
    /// <param name="name">The MCP server name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the removal.</returns>
    /// <remarks>
    /// <para>
    /// The record is replaced before the request is sent, so a reconnect cannot replay an endpoint
    /// that is being taken away.
    /// </para>
    /// <para>
    /// The version the endpoint was published for travels with the removal, because Nacos resolves
    /// the service to remove the instance from by that version. The official client sends no version
    /// and lets the server resolve the one it currently labels latest, which removes nothing when the
    /// endpoint was pinned to a version that has since been superseded.
    /// </para>
    /// </remarks>
    public async Task DeregisterAsync(string name, string address, int port, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            string key = PortableKey(name, address, port);
            Publication? previous = _publications.GetValueOrDefault(key);
            var desired = new Publication(
                name,
                address,
                port,
                previous?.Version,
                previous?.Owner ?? _selectOwner())
            {
                Deregister = true,
                Dirty = true,
                Rollback = previous,
            };

            _publications[key] = desired;

            try
            {
                await SendAsync(desired, ct).ConfigureAwait(false);
                _publications.TryRemove(new KeyValuePair<string, Publication>(key, desired));

                Notify(null);
            }
            catch (NacosException ex)
            {
                NoteGrpcFailure(desired, ex);

                if (desired.Owner != NacosAIPublicationTransport.Http
                    || !NacosAIEndpointHttp.IsRetryable(ex))
                {
                    RestorePrevious(key, desired);
                }

                Notify(null);
                throw;
            }

            NacosAILog.McpEndpointDeregistered(_logger, name, address, port);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Republishes every gRPC endpoint this process still owns.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every endpoint was attempted.</returns>
    /// <remarks>
    /// Only gRPC publications are replayed here: an HTTP publication is not tied to the connection,
    /// so a reconnect says nothing about it and replaying it would register a second copy. One
    /// failing endpoint does not stop the others: it stays recorded so the next reconnect tries it
    /// again rather than losing it.
    /// </remarks>
    public async Task RestoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            int restored = 0;

            foreach (Publication publication in _publications.Values)
            {
                if (publication.Owner != NacosAIPublicationTransport.Grpc
                    || publication.Deregister
                    || publication.Dirty)
                {
                    continue;
                }

                try
                {
                    await SendGrpcAsync(publication, NacosAIRemote.RegisterEndpoint, ct)
                        .ConfigureAwait(false);
                    restored++;
                }
                catch (NacosException ex)
                {
                    NacosAILog.McpEndpointRestoreFailed(_logger, publication.Name, ex.Message);
                }
            }

            if (restored > 0)
            {
                NacosAILog.McpEndpointsRestored(_logger, restored);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<NacosClientLivenessInfo?> HeartbeatAsync(CancellationToken ct) =>
        HeartbeatCoreAsync(ct);

    /// <inheritdoc />
    public void MarkHttpPublicationsDirty()
    {
        foreach (Publication publication in _publications.Values)
        {
            if (publication.Owner == NacosAIPublicationTransport.Http)
            {
                publication.Dirty = true;
            }
        }
    }

    /// <inheritdoc />
    public async Task RedoDirtyHttpPublicationsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            NacosClientLivenessInfo? liveness = null;

            foreach (KeyValuePair<string, Publication> entry in _publications.ToArray())
            {
                Publication publication = entry.Value;

                if (!publication.Dirty || publication.Owner != NacosAIPublicationTransport.Http)
                {
                    continue;
                }

                try
                {
                    liveness = await SendAsync(publication, ct).ConfigureAwait(false);

                    if (publication.Deregister)
                    {
                        _publications.TryRemove(
                            new KeyValuePair<string, Publication>(entry.Key, publication));
                    }
                    else
                    {
                        publication.Dirty = false;
                        publication.Rollback = null;
                    }
                }
                catch (NacosException ex)
                {
                    if (!NacosAIEndpointHttp.IsRetryable(ex))
                    {
                        RestorePrevious(entry.Key, publication);
                    }

                    NacosAILog.McpEndpointRedoFailed(_logger, publication.Name, ex.Message);
                }
            }

            Notify(liveness);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_closed)
            {
                return;
            }

            _closed = true;

            // Best effort: the process is going away, and an endpoint that outlives it is expired by
            // the server anyway. Sending the removal is what makes the endpoint disappear at once
            // instead of after the timeout.
            foreach (Publication publication in _publications.Values)
            {
                try
                {
                    await SendDeregisterAsync(publication, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (NacosException ex)
                {
                    NacosAILog.McpEndpointDeregisterFailed(
                        _logger, publication.Name, publication.Address, publication.Port, ex.Message);
                }
            }

            _publications.Clear();

            Notify(null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends one publication operation over the transport that owns it.
    /// </summary>
    /// <param name="publication">The publication.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The liveness intervals for an HTTP registration, otherwise <see langword="null"/>.</returns>
    private async Task<NacosClientLivenessInfo?> SendAsync(Publication publication, CancellationToken ct)
    {
        if (publication.Deregister)
        {
            await SendDeregisterAsync(publication, ct).ConfigureAwait(false);

            return null;
        }

        if (publication.Owner == NacosAIPublicationTransport.Grpc)
        {
            await SendGrpcAsync(publication, NacosAIRemote.RegisterEndpoint, ct).ConfigureAwait(false);

            return null;
        }

        var form = new List<KeyValuePair<string, string>>(5)
        {
            new("namespaceId", _namespace ?? string.Empty),
            new("mcpName", publication.Name),
            new("address", publication.Address),
            new("port", publication.Port.ToString(CultureInfo.InvariantCulture)),
        };

        if (publication.Version is not null)
        {
            form.Add(new KeyValuePair<string, string>("version", publication.Version));
        }

        NacosAIHttpResponse response = await _http
            .PostFormAsync(EndpointPath, form, _timeout, headers: null, ct)
            .ConfigureAwait(false);

        return NacosAIEndpointHttp.ReadLiveness(
            response,
            string.Concat("registration of MCP endpoint '", publication.Name, "'"));
    }

    /// <summary>
    /// Sends the removal of one endpoint over the transport that owns it.
    /// </summary>
    /// <param name="publication">The publication.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the removal.</returns>
    private async Task SendDeregisterAsync(Publication publication, CancellationToken ct)
    {
        if (publication.Owner == NacosAIPublicationTransport.Grpc)
        {
            await SendGrpcAsync(publication, NacosAIRemote.DeregisterEndpoint, ct).ConfigureAwait(false);

            return;
        }

        var query = new List<KeyValuePair<string, string?>>(4)
        {
            new("namespaceId", _namespace ?? string.Empty),
            new("mcpName", publication.Name),
            new("address", publication.Address),
            new("port", publication.Port.ToString(CultureInfo.InvariantCulture)),
        };

        NacosAIHttpResponse response = await _http
            .DeleteAsync(EndpointPath, query, ct)
            .ConfigureAwait(false);

        NacosAIEndpointHttp.ReadAccepted(
            response,
            string.Concat("removal of MCP endpoint '", publication.Name, "'"));
    }

    /// <summary>
    /// Sends one endpoint operation over the AI registry connection.
    /// </summary>
    /// <param name="publication">The endpoint the operation is about.</param>
    /// <param name="type">The operation, one of the <see cref="NacosAIRemote"/> endpoint types.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server answered.</returns>
    private async Task SendGrpcAsync(Publication publication, string type, CancellationToken ct)
    {
        NacosGrpcConnection connection = await _grpc(publication.Name, ct).ConfigureAwait(false);

        var request = new McpServerEndpointRequest
        {
            NamespaceId = _namespace,
            McpName = publication.Name,
            Address = publication.Address,
            Port = publication.Port,
            Version = publication.Version,
            Type = type,
        };

        await connection.RequestAsync<McpServerEndpointResponse>(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Heartbeats the shared HTTP publisher through the MCP path.
    /// </summary>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The liveness intervals the server reported, or <see langword="null"/> for none.</returns>
    private async Task<NacosClientLivenessInfo?> HeartbeatCoreAsync(CancellationToken ct)
    {
        NacosAIHttpResponse response = await _http.PutAsync(HeartbeatPath, ct).ConfigureAwait(false);

        return NacosAIEndpointHttp.ReadLiveness(response, "MCP endpoint heartbeat");
    }

    /// <summary>
    /// Records that a gRPC publication could not reach the server.
    /// </summary>
    /// <param name="publication">The publication that was attempted.</param>
    /// <param name="exception">The failure.</param>
    /// <remarks>
    /// An <see cref="NacosAITransportMode.Auto"/> client decides the owner of the next publication
    /// from the transport window, and a publish that failed on an unreachable connection has to move
    /// that window: otherwise a process that only publishes endpoints would keep choosing gRPC it
    /// cannot reach, because nothing else in the client ever tries the connection.
    /// </remarks>
    private void NoteGrpcFailure(Publication publication, NacosException exception)
    {
        if (publication.Owner == NacosAIPublicationTransport.Grpc
            && exception is NacosConnectionException or NacosTimeoutException)
        {
            _grpcConnectionFailed();
        }
    }

    /// <summary>
    /// Handles a failed registration.
    /// </summary>
    /// <param name="key">The endpoint key.</param>
    /// <param name="previous">What the endpoint was before the attempt.</param>
    /// <param name="desired">What the attempt tried to publish.</param>
    /// <param name="exception">The failure.</param>
    private void HandleFailure(
        string key,
        Publication? previous,
        Publication desired,
        NacosException exception)
    {
        if (desired.Owner == NacosAIPublicationTransport.Http
            && NacosAIEndpointHttp.IsRetryable(exception))
        {
            desired.Dirty = true;
            desired.Rollback = previous;
        }
        else if (previous is null)
        {
            _publications.TryRemove(new KeyValuePair<string, Publication>(key, desired));
        }
        else
        {
            _publications[key] = previous;
        }

        Notify(null);
    }

    /// <summary>
    /// Puts back what the endpoint was before a failed operation.
    /// </summary>
    /// <param name="key">The endpoint key.</param>
    /// <param name="state">The failed state.</param>
    private void RestorePrevious(string key, Publication state)
    {
        if (state.Rollback is null)
        {
            _publications.TryRemove(new KeyValuePair<string, Publication>(key, state));
        }
        else
        {
            _publications[key] = state.Rollback;
        }
    }

    /// <summary>
    /// Tells the coordinator what this module now owns.
    /// </summary>
    /// <param name="liveness">The intervals the server reported, or <see langword="null"/> for none.</param>
    private void Notify(NacosClientLivenessInfo? liveness) =>
        _coordinator.StateChanged(this, liveness, HasHttpPublication);

    /// <summary>
    /// Builds the key of one endpoint from its parts.
    /// </summary>
    /// <param name="name">The MCP server name.</param>
    /// <param name="address">The endpoint address.</param>
    /// <param name="port">The endpoint port.</param>
    /// <returns>The key.</returns>
    /// <remarks>
    /// The address and port are part of the key so a server that publishes several endpoints keeps
    /// them all across a reconnect. The official client keys its replay record by server name alone,
    /// which leaves all but the last endpoint of a name unpublished after a reconnect.
    /// </remarks>
    private static string PortableKey(string name, string address, int port)
        => string.Concat(name, "::", address, ":", port.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// One published endpoint.
    /// </summary>
    private sealed class Publication
    {
        private int _dirty;

        /// <summary>
        /// Initializes a new instance of the <see cref="Publication"/> class.
        /// </summary>
        /// <param name="name">The MCP server name.</param>
        /// <param name="address">The endpoint address.</param>
        /// <param name="port">The endpoint port.</param>
        /// <param name="version">The version the endpoint serves, when it is pinned to one.</param>
        /// <param name="owner">The transport that owns the publication.</param>
        public Publication(
            string name,
            string address,
            int port,
            string? version,
            NacosAIPublicationTransport owner)
        {
            Name = name;
            Address = address;
            Port = port;
            Version = version;
            Owner = owner;
        }

        /// <summary>Gets the MCP server name.</summary>
        public string Name { get; }

        /// <summary>Gets the endpoint address.</summary>
        public string Address { get; }

        /// <summary>Gets the endpoint port.</summary>
        public int Port { get; }

        /// <summary>Gets the version the endpoint serves, when it is pinned to one.</summary>
        public string? Version { get; }

        /// <summary>Gets the transport that owns the publication.</summary>
        public NacosAIPublicationTransport Owner { get; }

        /// <summary>Gets a value indicating whether the record is a removal rather than a publish.</summary>
        public bool Deregister { get; init; }

        /// <summary>Gets or sets a value indicating whether the record still has to be sent.</summary>
        public bool Dirty
        {
            get => Volatile.Read(ref _dirty) != 0;
            set => Volatile.Write(ref _dirty, value ? 1 : 0);
        }

        /// <summary>Gets or sets what the endpoint was before a failed attempt.</summary>
        public Publication? Rollback { get; set; }
    }
}
