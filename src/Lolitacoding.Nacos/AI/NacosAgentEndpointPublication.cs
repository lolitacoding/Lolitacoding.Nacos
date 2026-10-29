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
    /// The record is dropped before the request is sent, so a reconnect cannot replay an endpoint
    /// that is being taken away. The endpoint travels whole rather than by address alone, because
    /// the version and protocol it was registered for are what the server resolves the instance by.
    /// </remarks>
    public async Task DeregisterAsync(
        string agentName,
        NacosAgentEndpoint endpoint,
        CancellationToken ct)
    {
        var publication = new Publication(agentName, endpoint);
        _publications.TryRemove(Key(publication), out _);

        await SendAsync(publication, NacosAIRemote.DeregisterEndpoint, ct).ConfigureAwait(false);

        NacosAILog.AgentEndpointDeregistered(
            _logger, agentName, endpoint.Address ?? string.Empty, endpoint.Port);
    }

    /// <summary>
    /// Republishes every endpoint this process still owns.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every endpoint was attempted.</returns>
    /// <remarks>
    /// One failing endpoint does not stop the others: it stays recorded so the next reconnect tries
    /// it again rather than losing it.
    /// </remarks>
    public async Task RestoreAsync(CancellationToken ct)
    {
        int restored = 0;

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
    /// Builds the key of one endpoint.
    /// </summary>
    /// <param name="publication">The endpoint.</param>
    /// <returns>The key.</returns>
    private static string Key(Publication publication)
        => string.Concat(
            publication.AgentName,
            "::",
            publication.Endpoint.Address,
            ":",
            publication.Endpoint.Port.ToString(CultureInfo.InvariantCulture),
            "/",
            publication.Endpoint.Path);

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
