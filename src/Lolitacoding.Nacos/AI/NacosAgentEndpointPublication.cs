using System.Collections.Concurrent;
using System.Globalization;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// Owns the A2A agent endpoints this process has published, and replays them after a reconnect.
/// </summary>
/// <remarks>
/// <para>
/// An agent endpoint is registered through the AI module but stored as a Naming instance, so the
/// server ties it to the connection that registered it and removes it when that connection closes.
/// The registration therefore has to be replayed after a reconnect, which is why this type keeps a
/// record of what it published — the same contract the MCP endpoint publication follows.
/// </para>
/// <para>
/// A registration the server refused is not kept: the caller saw the failure, so publishing it later
/// would report a state the caller never asked for.
/// </para>
/// </remarks>
internal sealed class NacosAgentEndpointPublication
{
    private readonly ConcurrentDictionary<string, Publication> _publications = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BatchPublication> _batches = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task<NacosGrpcConnection>> _connection;
    private readonly string? _namespace;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAgentEndpointPublication"/> class.
    /// </summary>
    /// <param name="connection">Gets the AI registry connection, creating it if needed.</param>
    /// <param name="namespace">The namespace the endpoints are published in.</param>
    /// <param name="logger">The logger.</param>
    public NacosAgentEndpointPublication(
        Func<CancellationToken, Task<NacosGrpcConnection>> connection,
        string? @namespace,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _namespace = @namespace;
        _logger = logger;
    }

    /// <summary>
    /// Publishes one endpoint of an agent.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server accepted the endpoint.</returns>
    public async Task RegisterAsync(string agentName, NacosAgentEndpoint endpoint, CancellationToken ct)
    {
        var publication = new Publication(agentName, endpoint);
        string key = Key(publication);

        // Recorded before it is sent, so a reconnect that races the first attempt still replays it.
        _publications[key] = publication;

        try
        {
            await SendAsync(publication, NacosAIRemote.RegisterEndpoint, ct).ConfigureAwait(false);
        }
        catch
        {
            _publications.TryRemove(new KeyValuePair<string, Publication>(key, publication));
            throw;
        }

        NacosAILog.AgentEndpointRegistered(
            _logger, agentName, endpoint.Address ?? string.Empty, endpoint.Port);
    }

    /// <summary>
    /// Removes one endpoint of an agent.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the removal.</returns>
    /// <remarks>
    /// <para>
    /// The record is dropped before the request is sent, so a reconnect cannot replay an endpoint
    /// that is being taken away. The endpoint travels whole rather than by address alone, because
    /// the version and protocol it was registered for are what the server resolves the instance by.
    /// </para>
    /// <para>
    /// A batch this endpoint is part of loses it too, or the replay would put back what the caller
    /// just removed.
    /// </para>
    /// </remarks>
    public async Task DeregisterAsync(
        string agentName,
        NacosAgentEndpoint endpoint,
        CancellationToken ct)
    {
        var publication = new Publication(agentName, endpoint);
        _publications.TryRemove(Key(publication), out _);

        DropFromBatch(agentName, endpoint);

        await SendAsync(publication, NacosAIRemote.DeregisterEndpoint, ct).ConfigureAwait(false);

        NacosAILog.AgentEndpointDeregistered(
            _logger, agentName, endpoint.Address ?? string.Empty, endpoint.Port);
    }

    /// <summary>
    /// Publishes the complete endpoint set of one agent version.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="endpoints">The endpoints, all serving one version.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server accepted the batch.</returns>
    /// <remarks>
    /// <para>
    /// The server replaces this client's whole instance set for the agent version, so the batch is
    /// recorded as the current state of that version and the single registrations it replaces are
    /// dropped. Keeping them would make a replay publish endpoints the caller withdrew with the
    /// batch.
    /// </para>
    /// <para>
    /// The batch is recorded before it is sent, so a reconnect that races the first attempt still
    /// replays it. A refused batch is removed again and the endpoints it replaced are restored,
    /// because the server still holds them.
    /// </para>
    /// </remarks>
    public async Task RegisterBatchAsync(
        string agentName,
        IReadOnlyList<NacosAgentEndpoint> endpoints,
        CancellationToken ct)
    {
        var batch = new BatchPublication(agentName, [.. endpoints]);
        string key = BatchKey(agentName, batch.Version);

        List<KeyValuePair<string, Publication>> replaced = [];
        foreach (KeyValuePair<string, Publication> entry in _publications)
        {
            if (string.Equals(entry.Value.AgentName, agentName, StringComparison.Ordinal)
                && string.Equals(
                    entry.Value.Endpoint.Version, batch.Version, StringComparison.Ordinal))
            {
                replaced.Add(entry);
            }
        }

        _batches.TryGetValue(key, out BatchPublication? previous);

        // Recorded before it is sent, so a reconnect that races the first attempt still replays it.
        _batches[key] = batch;

        foreach (KeyValuePair<string, Publication> entry in replaced)
        {
            _publications.TryRemove(entry);
        }

        try
        {
            await SendBatchAsync(batch, ct).ConfigureAwait(false);
        }
        catch
        {
            // The caller saw the failure, so the batch is not replayed later; what it replaced is
            // put back, because the server never withdrew it.
            _batches.TryRemove(new KeyValuePair<string, BatchPublication>(key, batch));

            if (previous is not null)
            {
                _batches[key] = previous;
            }

            foreach (KeyValuePair<string, Publication> entry in replaced)
            {
                _publications[entry.Key] = entry.Value;
            }

            throw;
        }

        NacosAILog.AgentEndpointBatchRegistered(
            _logger, agentName, batch.Version, batch.Endpoints.Count);
    }

    /// <summary>
    /// Republishes every endpoint this process still owns.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every endpoint was attempted.</returns>
    /// <remarks>
    /// One failing endpoint does not stop the others: it stays recorded so the next reconnect tries
    /// it again rather than losing it. A batch is replayed as one request and before the single
    /// registrations, because it replaces the publisher's set and a single registration made after
    /// it has to be added on top.
    /// </remarks>
    public async Task RestoreAsync(CancellationToken ct)
    {
        int restored = 0;

        foreach (BatchPublication batch in _batches.Values)
        {
            try
            {
                await SendBatchAsync(batch, ct).ConfigureAwait(false);
                restored += batch.Endpoints.Count;
            }
            catch (NacosException ex)
            {
                NacosAILog.AgentEndpointRestoreFailed(_logger, batch.AgentName, ex.Message);
            }
        }

        foreach (Publication publication in _publications.Values)
        {
            try
            {
                await SendAsync(publication, NacosAIRemote.RegisterEndpoint, ct).ConfigureAwait(false);
                restored++;
            }
            catch (NacosException ex)
            {
                NacosAILog.AgentEndpointRestoreFailed(_logger, publication.AgentName, ex.Message);
            }
        }

        if (restored > 0)
        {
            NacosAILog.AgentEndpointsRestored(_logger, restored);
        }
    }

    /// <summary>
    /// Sends one endpoint operation.
    /// </summary>
    /// <param name="publication">The endpoint the operation is about.</param>
    /// <param name="type">The operation, one of the <see cref="NacosAIRemote"/> endpoint types.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server answered.</returns>
    private async Task SendAsync(Publication publication, string type, CancellationToken ct)
    {
        NacosGrpcConnection connection = await _connection(ct).ConfigureAwait(false);

        var request = new AgentEndpointRequest
        {
            NamespaceId = _namespace,
            AgentName = publication.AgentName,
            Endpoint = publication.Endpoint,
            Type = type,
        };

        await connection.RequestAsync<AgentEndpointResponse>(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the complete endpoint set of one agent version.
    /// </summary>
    /// <param name="batch">The batch to publish.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server answered.</returns>
    private async Task SendBatchAsync(BatchPublication batch, CancellationToken ct)
    {
        NacosGrpcConnection connection = await _connection(ct).ConfigureAwait(false);

        var request = new BatchAgentEndpointRequest
        {
            NamespaceId = _namespace,
            AgentName = batch.AgentName,
            Endpoints = batch.Endpoints,
            Type = NacosAIRemote.BatchRegisterEndpoint,
        };

        await connection.RequestAsync<AgentEndpointResponse>(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes one endpoint out of the recorded batch it belongs to.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="endpoint">The endpoint being removed.</param>
    /// <remarks>
    /// A batch is replayed as one request, so an endpoint removed on its own has to leave the batch
    /// too, or the next reconnect publishes it again. The last endpoint of a batch removes the batch
    /// itself, because an empty one would be refused and there is nothing left to publish.
    /// </remarks>
    private void DropFromBatch(string agentName, NacosAgentEndpoint endpoint)
    {
        string key = BatchKey(agentName, endpoint.Version);
        string removed = EndpointKey(endpoint);

        while (_batches.TryGetValue(key, out BatchPublication? batch))
        {
            List<NacosAgentEndpoint> remaining = [.. batch.Endpoints.Where(
                candidate => !string.Equals(EndpointKey(candidate), removed, StringComparison.Ordinal))];

            if (remaining.Count == batch.Endpoints.Count)
            {
                return;
            }

            if (remaining.Count == 0)
            {
                if (_batches.TryRemove(new KeyValuePair<string, BatchPublication>(key, batch)))
                {
                    return;
                }

                continue;
            }

            if (_batches.TryUpdate(key, new BatchPublication(agentName, remaining), batch))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Builds the key of one endpoint.
    /// </summary>
    /// <param name="publication">The endpoint.</param>
    /// <returns>The key.</returns>
    private static string Key(Publication publication)
        => string.Concat(publication.AgentName, "::", EndpointKey(publication.Endpoint));

    /// <summary>
    /// Builds the identity of one endpoint within its agent.
    /// </summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <returns>The identity.</returns>
    /// <remarks>
    /// The address, port and path are what the server turns into a Naming instance, so two endpoints
    /// that differ only in transport or protocol version are one registration and the later one
    /// replaces the earlier. The key mirrors that rather than pretending they are separate.
    /// </remarks>
    private static string EndpointKey(NacosAgentEndpoint endpoint)
        => string.Concat(
            endpoint.Address,
            ":",
            endpoint.Port.ToString(CultureInfo.InvariantCulture),
            "/",
            endpoint.Path);

    /// <summary>
    /// Builds the key of one published batch.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The card version the batch serves.</param>
    /// <returns>The key.</returns>
    /// <remarks>
    /// A batch is one publication per agent version, because the server replaces the publisher's
    /// whole set for that version rather than merging into it.
    /// </remarks>
    private static string BatchKey(string? agentName, string? version)
        => string.Concat(agentName, "::", version);

    /// <summary>
    /// One published endpoint set.
    /// </summary>
    private sealed class BatchPublication
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BatchPublication"/> class.
        /// </summary>
        /// <param name="agentName">The agent name.</param>
        /// <param name="endpoints">The endpoints, all serving one version.</param>
        public BatchPublication(string agentName, IReadOnlyList<NacosAgentEndpoint> endpoints)
        {
            AgentName = agentName;
            Endpoints = endpoints;
            Version = endpoints[0].Version;
        }

        /// <summary>Gets the agent name.</summary>
        public string AgentName { get; }

        /// <summary>Gets the card version the batch serves.</summary>
        public string? Version { get; }

        /// <summary>Gets the endpoints the batch publishes.</summary>
        public IReadOnlyList<NacosAgentEndpoint> Endpoints { get; }
    }

    /// <summary>
    /// One published endpoint.
    /// </summary>
    private sealed class Publication
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Publication"/> class.
        /// </summary>
        /// <param name="agentName">The agent name.</param>
        /// <param name="endpoint">The endpoint.</param>
        public Publication(string agentName, NacosAgentEndpoint endpoint)
        {
            AgentName = agentName;
            Endpoint = endpoint;
        }

        /// <summary>Gets the agent name.</summary>
        public string AgentName { get; }

        /// <summary>Gets the endpoint.</summary>
        public NacosAgentEndpoint Endpoint { get; }
    }
}
