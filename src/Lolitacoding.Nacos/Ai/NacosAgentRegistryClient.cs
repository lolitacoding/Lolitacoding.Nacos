using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// The RAD agent registry, served over the AI registry connection.
/// </summary>
/// <remarks>
/// <para>
/// This is the agent contract Nacos 3.3 adds: an agent is a versioned resource with metadata, call
/// interfaces and runtime endpoints, published and searched through the registry's own model rather
/// than as an A2A document. It is a different surface from the card registry, not a newer version of
/// it — a card still advertises an agent to other agents, and this is how an agent is managed.
/// </para>
/// <para>
/// Every operation here is gated on the server advertising <c>radV1</c>. Unlike the older abilities,
/// the client does not treat an absent value as "try it and see": a server that states neither
/// <c>radV1</c> nor the older <c>agent</c> ability has not told the client enough to know which
/// contract it speaks, and guessing would send a payload the server may not route.
/// </para>
/// </remarks>
internal sealed class NacosAgentRegistryClient : INacosAgentRegistry
{
    /// <summary>The error code Nacos uses for a feature a server does not implement.</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The error code Nacos uses for a request the client could not determine how to send.</summary>
    private const int ServerErrorCode = 500;

    private readonly NacosAiClient _owner;
    private readonly NacosOptions _options;
    private readonly ILogger<NacosAgentRegistryClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAgentRegistryClient"/> class.
    /// </summary>
    /// <param name="owner">The AI client that owns the connection.</param>
    /// <param name="options">The client options.</param>
    /// <param name="logger">The logger.</param>
    public NacosAgentRegistryClient(
        NacosAiClient owner,
        NacosOptions options,
        ILogger<NacosAgentRegistryClient> logger)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _owner = owner;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<NacosAgentVersionDetail> PublishAgentAsync(
        NacosAgentPublishRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentName, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Version, nameof(request));

        NacosGrpcConnection connection = await ConnectAsync(ct).ConfigureAwait(false);

        var rpcRequest = new AgentPublishRpcRequest
        {
            NamespaceId = _options.Namespace,
            PublishRequest = request,
        };

        AgentPublishRpcResponse response =
            await connection.RequestAsync<AgentPublishRpcResponse>(rpcRequest, ct).ConfigureAwait(false);

        NacosAgentVersionDetail? detail = response.VersionDetail;

        if (detail is null)
        {
            // A refused publish is a failure result code, which the transport has already turned into
            // an exception; reaching here means the reply did not match the contract.
            NacosAiLog.AgentPublishDetailMissing(_logger, request.AgentName!, request.Version);

            throw new NacosServerException(
                string.Concat(
                    "The Nacos server published agent '",
                    request.AgentName,
                    "' but returned no version detail."));
        }

        NacosAiLog.AgentPublished(_logger, request.AgentName!, detail.Version, detail.Status);

        return detail;
    }

    /// <inheritdoc />
    public async Task<NacosAgentPage<NacosAgentSummary>> SearchAgentsAsync(
        NacosAgentSearchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        NacosGrpcConnection connection = await ConnectAsync(ct).ConfigureAwait(false);

        var rpcRequest = new AgentSearchRpcRequest
        {
            NamespaceId = _options.Namespace,
            SearchRequest = request,
        };

        AgentSearchResponse response =
            await connection.RequestAsync<AgentSearchResponse>(rpcRequest, ct).ConfigureAwait(false);

        NacosAgentPage<NacosAgentSummary>? page = response.Page;

        if (page is null)
        {
            throw new NacosServerException(
                "The Nacos server answered a search but returned no page.");
        }

        return page;
    }

    /// <inheritdoc />
    public async Task<NacosAgentDiscoveryResult> DiscoverAgentAsync(
        NacosAgentReference reference,
        NacosAgentDiscoveryFilter? filter = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.AgentName, nameof(reference));

        NacosGrpcConnection connection = await ConnectAsync(ct).ConfigureAwait(false);

        var discovery = new NacosAgentDiscoveryRequest
        {
            NamespaceId = _options.Namespace,
            Reference = reference,
            Filter = filter,
        };

        var rpcRequest = new AgentDiscoveryRpcRequest { DiscoveryRequest = discovery };

        AgentDiscoveryResponse response =
            await connection.RequestAsync<AgentDiscoveryResponse>(rpcRequest, ct).ConfigureAwait(false);

        NacosAgentDiscoveryResult? result = response.DiscoveryResult;

        if (result is null)
        {
            NacosAiLog.AgentDiscoveryDetailMissing(
                _logger, reference.AgentName!, reference.Version, reference.Label);

            throw new NacosServerException(
                string.Concat(
                    "The Nacos server returned no discovery result for agent '",
                    reference.AgentName,
                    "'."));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task RegisterEndpointsAsync(
        NacosAgentEndpointBatch batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.AgentName, nameof(batch));
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.Protocol, nameof(batch));

        if (batch.Endpoints is not { Count: > 0 })
        {
            // A batch replaces what the same publisher registered before, so an empty one would
            // withdraw the endpoints instead of publishing any.
            throw new ArgumentException(
                "An agent endpoint batch must carry at least one endpoint.",
                nameof(batch));
        }

        NacosGrpcConnection connection = await ConnectAsync(ct).ConfigureAwait(false);

        var rpcRequest = new AgentEndpointRegisterRpcRequest
        {
            NamespaceId = _options.Namespace,
            RegistrationBatch = batch,
        };

        await connection
            .RequestAsync<AgentEndpointOperationResponse>(rpcRequest, ct)
            .ConfigureAwait(false);

        NacosAiLog.AgentEndpointsRegistered(_logger, batch.AgentName!, batch.Protocol!, batch.Endpoints.Count);
    }

    /// <inheritdoc />
    public async Task DeregisterEndpointsAsync(
        string agentName,
        string protocol,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);

        NacosGrpcConnection connection = await ConnectAsync(ct).ConfigureAwait(false);

        var rpcRequest = new AgentEndpointDeregisterRpcRequest
        {
            NamespaceId = _options.Namespace,
            AgentName = agentName,
            Protocol = protocol,
        };

        await connection
            .RequestAsync<AgentEndpointOperationResponse>(rpcRequest, ct)
            .ConfigureAwait(false);

        NacosAiLog.AgentEndpointsDeregistered(_logger, agentName, protocol);
    }

    /// <summary>
    /// Gets the connection, failing when the server does not speak the RAD contract.
    /// </summary>
    /// <param name="ct">A token that cancels the connection attempt.</param>
    /// <returns>The connected connection.</returns>
    /// <exception cref="NacosServerException">
    /// The server does not implement the RAD contract, or stated nothing that says which agent
    /// contract it speaks.
    /// </exception>
    private async Task<NacosGrpcConnection> ConnectAsync(CancellationToken ct)
    {
        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureRadSupported(connection);
        return connection;
    }

    /// <summary>
    /// Fails a call when the server does not speak the RAD contract.
    /// </summary>
    /// <param name="connection">The AI registry connection.</param>
    /// <exception cref="NacosServerException">
    /// The server does not implement the RAD contract, or stated nothing that says which agent
    /// contract it speaks.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A server that states <c>radV1</c> is the only one this surface can talk to. A server that
    /// states the older <c>agent</c> ability instead is a 3.1 or 3.2 one: it has the A2A card
    /// contract and nothing else, which is a refusal rather than an unknown.
    /// </para>
    /// <para>
    /// A server that states neither has told the client nothing, and the two possibilities need
    /// different payloads. Sending one and hoping is what this avoids.
    /// </para>
    /// </remarks>
    private static void EnsureRadSupported(NacosGrpcConnection connection)
    {
        if (connection.ServerAbilities.TryGetValue(NacosAiServerAbility.RadV1, out bool supported))
        {
            if (supported)
            {
                return;
            }

            throw new NacosServerException(
                NotImplementedErrorCode,
                "The connected Nacos server states that it does not implement the RAD agent registry.");
        }

        if (connection.ServerAbilities.TryGetValue(NacosAiServerAbility.Agent, out bool agent) && agent)
        {
            throw new NacosServerException(
                NotImplementedErrorCode,
                string.Concat(
                    "The connected Nacos server implements the A2A agent card registry but not the RAD ",
                    "agent registry, which needs Nacos 3.3 or newer."));
        }

        throw new NacosServerException(
            ServerErrorCode,
            "The connected Nacos server stated neither the RAD agent ability nor the A2A agent "
            + "ability, so the client cannot tell which agent contract it speaks.");
    }
}
