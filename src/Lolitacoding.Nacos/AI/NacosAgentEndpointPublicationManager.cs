using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// Owns the RAD agent endpoint publications this process has made, over either AI transport.
/// </summary>
/// <remarks>
/// <para>
/// An agent endpoint publication is one batch per agent and protocol, and registering a batch
/// replaces whatever the same publisher published for that pair. The manager therefore keeps the
/// batch, not the individual endpoints: a replay has to send the whole set, because the server reads
/// it as a replacement rather than as an addition.
/// </para>
/// <para>
/// The transport that owns a publication is chosen once and kept, for the same reason as the MCP
/// publication: a gRPC registration belongs to the connection and an HTTP one to a heartbeating
/// client, so a removal sent over the other transport removes nothing. On top of that the manager
/// keeps the intent across a retryable HTTP failure so the coordinator can replay it, and restores
/// the previous batch when a failure will not resolve by itself.
/// </para>
/// <para>
/// A2A card endpoints are a different registry with a different contract and are not published
/// here; this manager covers the RAD endpoints only.
/// </para>
/// </remarks>
internal sealed class NacosAgentEndpointPublicationManager :
    INacosAIHttpPublicationParticipant,
    IAsyncDisposable
{
    /// <summary>The client OpenAPI path that registers and removes agent endpoints.</summary>
    private const string EndpointPath = "/v3/client/ai/agents/endpoints";

    /// <summary>The client OpenAPI path that heartbeats the shared HTTP publisher.</summary>
    private const string HeartbeatPath = "/v3/client/ai/agents/endpoints/heartbeat";

    /// <summary>
    /// How many endpoints one AI client may keep published.
    /// </summary>
    /// <remarks>
    /// The server enforces the same limit and answers with code 50103 when it is reached. The client
    /// checks it first so a batch that cannot fit fails before it is sent, with the same code.
    /// </remarks>
    private const int MaxPublications = 100;

    private readonly ConcurrentDictionary<string, Publication> _publications = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task<NacosGrpcConnection>> _grpc;
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
    /// Initializes a new instance of the <see cref="NacosAgentEndpointPublicationManager"/> class.
    /// </summary>
    /// <param name="grpc">
    /// Gets the AI registry connection, checking that the server serves the RAD registry.
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
    public NacosAgentEndpointPublicationManager(
        Func<CancellationToken, Task<NacosGrpcConnection>> grpc,
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
    public string ModuleName => "Agent";

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
                    && publication.Batch is not null
                    && !publication.Dirty)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Replaces what this publisher has published for one agent and protocol.
    /// </summary>
    /// <param name="batch">The complete batch.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server accepted the batch.</returns>
    public async Task RegisterAsync(NacosAgentEndpointBatch batch, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            string key = Key(batch.AgentName!, batch.Protocol!);
            Publication? previous = _publications.GetValueOrDefault(key);

            int nextCount = batch.Endpoints?.Count ?? 0;

            if (CountEntries() >= MaxPublications && nextCount > CountEntries(previous))
            {
                throw new NacosServerException(
                    NacosAIEndpointHttp.EndpointPublicationOverLimitErrorCode,
                    string.Concat(
                        "The agent endpoint publication limit of ",
                        MaxPublications.ToString(CultureInfo.InvariantCulture),
                        " was reached for this AI client."));
            }

            var desired = new Publication(
                Copy(batch),
                batch.AgentName!,
                batch.Protocol!,
                previous?.Owner ?? _selectOwner())
            {
                Dirty = true,
            };

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
                HandleWriteFailure(key, previous, desired, ex);
                throw;
            }

            NacosAILog.AgentEndpointsRegistered(
                _logger, batch.AgentName!, batch.Protocol!, nextCount);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes everything this publisher published for one agent and protocol.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="protocol">The protocol.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the removal.</returns>
    public async Task DeregisterAsync(string agentName, string protocol, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);

            string key = Key(agentName, protocol);
            Publication? previous = _publications.GetValueOrDefault(key);

            if (previous?.Batch is null)
            {
                return;
            }

            var desired = new Publication(null, agentName, protocol, previous.Owner)
            {
                Dirty = true,
                Rollback = previous,
            };

            _publications[key] = desired;

            try
            {
                await SendAsync(desired, ct).ConfigureAwait(false);
                _publications.Remove(key, out _);

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

            NacosAILog.AgentEndpointsDeregistered(_logger, agentName, protocol);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Republishes every gRPC publication this process still owns.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every publication was attempted.</returns>
    /// <remarks>
    /// Only gRPC publications are replayed: the server drops them with the connection, while an HTTP
    /// publication is kept alive by its heartbeat and a reconnect says nothing about it.
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
                    || publication.Batch is null
                    || publication.Dirty)
                {
                    continue;
                }

                try
                {
                    await SendGrpcAsync(publication, ct).ConfigureAwait(false);
                    restored++;
                }
                catch (NacosException ex)
                {
                    NoteGrpcFailure(publication, ex);
                    NacosAILog.AgentEndpointsRestoreFailed(
                        _logger, publication.AgentName, publication.Protocol, ex.Message);
                }
            }

            if (restored > 0)
            {
                NacosAILog.AgentEndpointPublicationsRestored(_logger, restored);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<NacosClientLivenessInfo?> HeartbeatAsync(CancellationToken ct)
    {
        NacosAIHttpResponse response = await _http.PutAsync(HeartbeatPath, ct).ConfigureAwait(false);

        return NacosAIEndpointHttp.ReadLiveness(response, "agent endpoint heartbeat");
    }

    /// <inheritdoc />
    public void MarkHttpPublicationsDirty()
    {
        lock (_gate)
        {
            foreach (Publication publication in _publications.Values)
            {
                if (publication.Owner == NacosAIPublicationTransport.Http
                    && publication.Batch is not null)
                {
                    publication.Dirty = true;
                }
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

                    if (publication.Batch is null)
                    {
                        _publications.Remove(entry.Key, out _);
                    }
                    else
                    {
                        publication.Dirty = false;
                        publication.Rollback = null;
                    }
                }
                catch (NacosException ex)
                {
                    if (NacosAIEndpointHttp.IsCapacityRejected(ex))
                    {
                        _publications.Remove(entry.Key, out _);
                    }
                    else if (!NacosAIEndpointHttp.IsRetryable(ex))
                    {
                        RestorePrevious(entry.Key, publication);
                    }

                    NacosAILog.AgentEndpointsRedoFailed(
                        _logger, publication.AgentName, publication.Protocol, ex.Message);
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

            foreach (Publication publication in _publications.Values)
            {
                try
                {
                    await SendDeregisterAsync(publication, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (NacosException ex)
                {
                    NacosAILog.AgentEndpointsDeregisterFailed(
                        _logger, publication.AgentName, publication.Protocol, ex.Message);
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
        if (publication.Batch is null)
        {
            await SendDeregisterAsync(publication, ct).ConfigureAwait(false);

            return null;
        }

        if (publication.Owner == NacosAIPublicationTransport.Grpc)
        {
            await SendGrpcAsync(publication, ct).ConfigureAwait(false);

            return null;
        }

        NacosAgentEndpointBatch batch = publication.Batch;
        var form = new List<KeyValuePair<string, string>>(6)
        {
            new("namespaceId", _namespace ?? string.Empty),
            new("agentName", batch.AgentName!),
        };

        if (batch.RuntimeVersion is not null)
        {
            form.Add(new KeyValuePair<string, string>("runtimeVersion", batch.RuntimeVersion));
        }

        if (batch.VersionRange is not null)
        {
            form.Add(new KeyValuePair<string, string>("versionRange", batch.VersionRange));
        }

        form.Add(new KeyValuePair<string, string>("protocol", batch.Protocol!));
        form.Add(new KeyValuePair<string, string>(
            "endpoints",
            JsonSerializer.Serialize(
                batch.Endpoints ?? [],
                NacosJsonContext.Default.ListNacosAgentRuntimeEndpoint)));

        NacosAIHttpResponse response = await _http
            .PostFormAsync(EndpointPath, form, _timeout, headers: null, ct)
            .ConfigureAwait(false);

        return NacosAIEndpointHttp.ReadLiveness(
            response,
            string.Concat("registration of endpoints for agent '", batch.AgentName, "'"));
    }

    /// <summary>
    /// Sends the removal of one publication over the transport that owns it.
    /// </summary>
    /// <param name="publication">The publication.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the removal.</returns>
    private async Task SendDeregisterAsync(Publication publication, CancellationToken ct)
    {
        if (publication.Owner == NacosAIPublicationTransport.Grpc)
        {
            var request = new AgentEndpointDeregisterRpcRequest
            {
                NamespaceId = _namespace,
                AgentName = publication.AgentName,
                Protocol = publication.Protocol,
            };

            NacosGrpcConnection connection = await _grpc(ct).ConfigureAwait(false);

            await connection
                .RequestAsync<AgentEndpointOperationResponse>(request, ct)
                .ConfigureAwait(false);

            return;
        }

        var query = new List<KeyValuePair<string, string?>>(3)
        {
            new("namespaceId", _namespace ?? string.Empty),
            new("agentName", publication.AgentName),
            new("protocol", publication.Protocol),
        };

        NacosAIHttpResponse response = await _http
            .DeleteAsync(EndpointPath, query, ct)
            .ConfigureAwait(false);

        NacosAIEndpointHttp.ReadAccepted(
            response,
            string.Concat("removal of endpoints for agent '", publication.AgentName, "'"));
    }

    /// <summary>
    /// Sends one publication over the AI registry connection.
    /// </summary>
    /// <param name="publication">The publication.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server answered.</returns>
    private async Task SendGrpcAsync(Publication publication, CancellationToken ct)
    {
        var request = new AgentEndpointRegisterRpcRequest
        {
            NamespaceId = _namespace,
            RegistrationBatch = publication.Batch!,
        };

        NacosGrpcConnection connection = await _grpc(ct).ConfigureAwait(false);

        await connection
            .RequestAsync<AgentEndpointOperationResponse>(request, ct)
            .ConfigureAwait(false);
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
    /// <param name="key">The publication key.</param>
    /// <param name="previous">What was published before the attempt.</param>
    /// <param name="desired">What the attempt tried to publish.</param>
    /// <param name="exception">The failure.</param>
    private void HandleWriteFailure(
        string key,
        Publication? previous,
        Publication desired,
        NacosException exception)
    {
        if (NacosAIEndpointHttp.IsCapacityRejected(exception))
        {
            _publications.Remove(key, out _);
        }
        else if (desired.Owner == NacosAIPublicationTransport.Http
            && NacosAIEndpointHttp.IsRetryable(exception))
        {
            desired.Dirty = true;
            desired.Rollback = previous;
        }
        else
        {
            RestorePrevious(key, desired);
        }

        Notify(null);
    }

    /// <summary>
    /// Puts back what was published before a failed operation.
    /// </summary>
    /// <param name="key">The publication key.</param>
    /// <param name="state">The failed state.</param>
    private void RestorePrevious(string key, Publication state)
    {
        if (state.Rollback is null)
        {
            _publications.Remove(key, out _);
        }
        else
        {
            _publications[key] = state.Rollback;
        }
    }

    /// <summary>
    /// Counts the endpoints every publication currently carries.
    /// </summary>
    /// <returns>The number of endpoints.</returns>
    private int CountEntries()
    {
        int count = 0;

        foreach (Publication publication in _publications.Values)
        {
            count += CountEntries(publication);
        }

        return count;
    }

    /// <summary>
    /// Counts the endpoints one publication carries.
    /// </summary>
    /// <param name="publication">The publication, or <see langword="null"/> for none.</param>
    /// <returns>The number of endpoints.</returns>
    private static int CountEntries(Publication? publication) =>
        publication?.Batch?.Endpoints?.Count ?? 0;

    /// <summary>
    /// Tells the coordinator what this module now owns.
    /// </summary>
    /// <param name="liveness">The intervals the server reported, or <see langword="null"/> for none.</param>
    private void Notify(NacosClientLivenessInfo? liveness) =>
        _coordinator.StateChanged(this, liveness, HasHttpPublication);

    /// <summary>
    /// Copies a batch so a caller cannot change what was published by mutating its argument.
    /// </summary>
    /// <param name="batch">The batch.</param>
    /// <returns>The copy.</returns>
    private static NacosAgentEndpointBatch Copy(NacosAgentEndpointBatch batch) => new()
    {
        AgentName = batch.AgentName,
        Protocol = batch.Protocol,
        RuntimeVersion = batch.RuntimeVersion,
        VersionRange = batch.VersionRange,
        Endpoints = batch.Endpoints is null ? null : [.. batch.Endpoints],
    };

    /// <summary>
    /// Builds the key of one publication.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="protocol">The protocol.</param>
    /// <returns>The key.</returns>
    /// <remarks>
    /// The namespace is fixed per client, so the agent and protocol are what identify a publication:
    /// a publisher replaces its own batch for a pair and nothing else.
    /// </remarks>
    private static string Key(string agentName, string protocol) =>
        string.Concat(agentName, "::", protocol);

    /// <summary>
    /// One agent endpoint publication.
    /// </summary>
    private sealed class Publication
    {
        private int _dirty;

        /// <summary>
        /// Initializes a new instance of the <see cref="Publication"/> class.
        /// </summary>
        /// <param name="batch">The batch, or <see langword="null"/> for a removal.</param>
        /// <param name="agentName">The agent name.</param>
        /// <param name="protocol">The protocol.</param>
        /// <param name="owner">The transport that owns the publication.</param>
        public Publication(
            NacosAgentEndpointBatch? batch,
            string agentName,
            string protocol,
            NacosAIPublicationTransport owner)
        {
            Batch = batch;
            AgentName = agentName;
            Protocol = protocol;
            Owner = owner;
        }

        /// <summary>Gets the batch, or <see langword="null"/> when the record is a removal.</summary>
        public NacosAgentEndpointBatch? Batch { get; }

        /// <summary>Gets the agent name.</summary>
        public string AgentName { get; }

        /// <summary>Gets the protocol.</summary>
        public string Protocol { get; }

        /// <summary>Gets the transport that owns the publication.</summary>
        public NacosAIPublicationTransport Owner { get; }

        /// <summary>Gets or sets a value indicating whether the record still has to be sent.</summary>
        public bool Dirty
        {
            get => Volatile.Read(ref _dirty) != 0;
            set => Volatile.Write(ref _dirty, value ? 1 : 0);
        }

        /// <summary>Gets or sets what was published before a failed attempt.</summary>
        public Publication? Rollback { get; set; }
    }
}
