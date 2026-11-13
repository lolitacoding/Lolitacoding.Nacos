using System.Runtime.CompilerServices;
using System.Text.Json;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// The A2A agent card registry, served over the AI registry connection.
/// </summary>
/// <remarks>
/// <para>
/// This is the older of the two agent contracts: a card is published as a document, read back by
/// name and version, and made callable by registering runtime endpoints against it. Nacos 3.3 adds a
/// second, richer contract — the RAD registry — which is a different client surface; a card remains
/// the way an A2A agent advertises itself to other agents.
/// </para>
/// <para>
/// A card carries no content hash, so a subscription reads it in full every interval and compares the
/// document. Nacos pushes no card changes, which is why a subscription is a poll at all.
/// </para>
/// <para>
/// Endpoints published through this client are Naming instances, so they are tied to the connection
/// that registered them and are replayed after a reconnect by <see cref="RestoreAsync"/>.
/// </para>
/// </remarks>
internal sealed class NacosAgentCardClient : INacosAgentCardRegistry, IAsyncDisposable
{
    /// <summary>The error code Nacos uses for a feature a server does not implement.</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The highest port a TCP endpoint can use.</summary>
    private const int MaxPort = 65535;

    private readonly NacosAIClient _owner;
    private readonly NacosOptions _options;
    private readonly ILogger<NacosAgentCardClient> _logger;
    private readonly NacosAIResourceCache<NacosAgentCardDetail> _cache;
    private readonly NacosAgentEndpointPublication _endpoints;

    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAgentCardClient"/> class.
    /// </summary>
    /// <param name="owner">The AI client that owns the connection.</param>
    /// <param name="options">The client options.</param>
    /// <param name="timeProvider">The clock the subscription refresh interval is measured with.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="snapshots">
    /// The disk failover store, or <see langword="null"/> when cards are kept in memory only.
    /// </param>
    public NacosAgentCardClient(
        NacosAIClient owner,
        NacosOptions options,
        TimeProvider timeProvider,
        ILogger<NacosAgentCardClient> logger,
        NacosAISnapshotStore? snapshots = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _owner = owner;
        _options = options;
        _logger = logger;

        _cache = new NacosAIResourceCache<NacosAgentCardDetail>(new NacosAIResourceCacheOptions<NacosAgentCardDetail>
        {
            Query = QueryAsync,
            IsSame = IsSame,

            // A card has no content hash, so every refresh reads the document in full.
            Hash = static _ => null,
            Changed = (card, name) => NacosAILog.AgentCardChanged(_logger, name, card.Version),
            Withdrawn = name => NacosAILog.AgentCardWithdrawn(_logger, name),
            RefreshFailed = (name, version, registrationType, reason) =>
                NacosAILog.AgentCardRefreshFailed(_logger, name, version, registrationType, reason),
            RefreshInterval = options.AI.CacheRefreshInterval,
            Snapshot = NacosAISnapshot<NacosAgentCardDetail>.Create(
                snapshots, NacosAISnapshotStore.AgentCardKind, NacosJsonContext.Default.NacosAgentCardDetail),
            TimeProvider = timeProvider,
            Logger = logger,
        });

        _endpoints = new NacosAgentEndpointPublication(owner.GetConnectionAsync, options.Namespace, logger);
    }

    /// <inheritdoc />
    public async Task<NacosAgentCardDetail> GetAgentCardAsync(
        string agentName,
        string? version = null,
        string? registrationType = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, agentName);

        return await QueryAsync(agentName, version, registrationType, md5: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReleaseAgentCardAsync(
        NacosAgentCard card,
        string? registrationType = null,
        bool setAsLatest = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentException.ThrowIfNullOrWhiteSpace(card.Name, nameof(card));
        ArgumentException.ThrowIfNullOrWhiteSpace(card.Version, nameof(card));
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, card.Name!);

        var request = new ReleaseAgentCardRequest
        {
            NamespaceId = _options.Namespace,
            AgentName = card.Name,
            AgentCard = card,
            RegistrationType = registrationType ?? NacosA2a.EndpointTypeService,
            SetAsLatest = setAsLatest,
        };

        await connection.RequestAsync<ReleaseAgentCardResponse>(request, ct).ConfigureAwait(false);

        NacosAILog.AgentCardReleased(_logger, card.Name!, card.Version!, request.RegistrationType!);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosAgentCardDetail?> SubscribeAgentCardAsync(
        string agentName,
        string? version = null,
        string? registrationType = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        // The cache's third selector is a label for the prompt, skill and agent-specification
        // registries; a card has no label, and the registration type plays the same role here: it
        // names which copy of the resource is meant.
        await foreach (NacosAgentCardDetail? card in
            _cache.SubscribeAsync(agentName, version, registrationType, ct).ConfigureAwait(false))
        {
            yield return card;
        }
    }

    /// <inheritdoc />
    public async Task RegisterAgentEndpointAsync(
        string agentName,
        NacosAgentEndpoint endpoint,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateEndpoint(endpoint);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, agentName);

        await _endpoints.RegisterAsync(agentName, endpoint, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RegisterAgentEndpointsAsync(
        string agentName,
        IReadOnlyList<NacosAgentEndpoint> endpoints,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(endpoints);
        ValidateEndpoints(endpoints);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, agentName);

        await _endpoints.RegisterBatchAsync(agentName, endpoints, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeregisterAgentEndpointAsync(
        string agentName,
        NacosAgentEndpoint endpoint,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateEndpoint(endpoint);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
        EnsureSupported(connection, agentName);

        await _endpoints.DeregisterAsync(agentName, endpoint, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Republishes every agent endpoint this process published.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every endpoint was attempted.</returns>
    public Task RestoreAsync(CancellationToken ct) => _endpoints.RestoreAsync(ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _cache.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one card selector from the registry.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The card version, or <see langword="null"/> for the latest one.</param>
    /// <param name="registrationType">The registration type, or <see langword="null"/> for any.</param>
    /// <param name="md5">
    /// The hash the cache would send. A card carries none, so this is always
    /// <see langword="null"/>; the parameter exists because the shared cache's query signature
    /// carries it.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved card.</returns>
    /// <exception cref="NacosServerException">
    /// The agent or version does not exist, or the reply carried no card.
    /// </exception>
    private async Task<NacosAgentCardDetail> QueryAsync(
        string agentName,
        string? version,
        string? registrationType,
        string? md5,
        CancellationToken ct)
    {
        _ = md5;

        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);

        var request = new QueryAgentCardRequest
        {
            NamespaceId = _options.Namespace,
            AgentName = agentName,
            Version = version,
            RegistrationType = registrationType,
        };

        QueryAgentCardResponse response =
            await connection.RequestAsync<QueryAgentCardResponse>(request, ct).ConfigureAwait(false);

        NacosAgentCardDetail? card = response.AgentCardDetailInfo;

        if (card is null)
        {
            // A missing agent or version is reported as a failure result code, which the transport
            // has already turned into an exception. Reaching here therefore means the reply did not
            // match the contract, and reporting it as a missing card would hide that.
            NacosAILog.AgentCardDetailMissing(_logger, agentName, version, registrationType);

            throw new NacosServerException(
                string.Concat("The Nacos server returned no card for agent '", agentName, "'."));
        }

        return card;
    }

    /// <summary>
    /// Decides whether two reads are the same state.
    /// </summary>
    /// <param name="left">The cached card.</param>
    /// <param name="right">The card just read.</param>
    /// <returns><see langword="true"/> when the two are the same state.</returns>
    /// <remarks>
    /// The card is compared by its serialized form. It is an open document — security schemes,
    /// signatures and extension parameters are kept as JSON precisely because their shape is not
    /// fixed — so comparing the document is faithful to "did anything the server serves change"
    /// without a field list that could miss one.
    /// </remarks>
    private static bool IsSame(NacosAgentCardDetail? left, NacosAgentCardDetail? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(
            JsonSerializer.Serialize(left, NacosJsonContext.Default.NacosAgentCardDetail),
            JsonSerializer.Serialize(right, NacosJsonContext.Default.NacosAgentCardDetail),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Fails a registration or a removal whose endpoint the server would refuse.
    /// </summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <exception cref="ArgumentException">
    /// The endpoint names no version, no address, or a port outside the TCP range.
    /// </exception>
    /// <remarks>
    /// These are the fields the server turns into a Naming instance, so it refuses the request
    /// without them and reports the reason as a missing parameter. Checking them here says which
    /// one is missing without a round trip, which is what the official client does too.
    /// </remarks>
    private static void ValidateEndpoint(NacosAgentEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Version))
        {
            // The service a registration becomes an instance of is named after the card's name and
            // version, so an endpoint without one has no service to register into.
            throw new ArgumentException(
                "An agent endpoint must name the card version it serves.",
                nameof(endpoint));
        }

        if (string.IsNullOrWhiteSpace(endpoint.Address))
        {
            throw new ArgumentException(
                "An agent endpoint must name an address.",
                nameof(endpoint));
        }

        if (endpoint.Port is < 1 or > MaxPort)
        {
            throw new ArgumentException(
                "An agent endpoint must name a port between 1 and 65535.",
                nameof(endpoint));
        }
    }

    /// <summary>
    /// Fails a batch whose endpoints the server would refuse.
    /// </summary>
    /// <param name="endpoints">The endpoints to check.</param>
    /// <exception cref="ArgumentException">
    /// The batch is empty, holds a null endpoint, holds an endpoint that is invalid, or mixes
    /// versions.
    /// </exception>
    /// <remarks>
    /// The server checks the same three things — a non-empty batch, valid endpoints, and one shared
    /// version — and reports them as missing or invalid parameters. Checking here names the endpoint
    /// and the version that disagree without a round trip.
    /// </remarks>
    private static void ValidateEndpoints(IReadOnlyList<NacosAgentEndpoint> endpoints)
    {
        if (endpoints.Count == 0)
        {
            // "Register nothing" is a removal, and the server says so; it has its own call here.
            throw new ArgumentException(
                "An agent endpoint batch must carry at least one endpoint; remove endpoints one at a time instead.",
                nameof(endpoints));
        }

        string? version = null;

        foreach (NacosAgentEndpoint endpoint in endpoints)
        {
            if (endpoint is null)
            {
                throw new ArgumentException(
                    "An agent endpoint batch cannot contain a null endpoint.",
                    nameof(endpoints));
            }

            ValidateEndpoint(endpoint);

            if (version is null)
            {
                version = endpoint.Version;
            }
            else if (!string.Equals(version, endpoint.Version, StringComparison.Ordinal))
            {
                // The endpoints become instances of one service, named after the agent and the
                // version, so a batch that mixed versions would have no single service to register
                // into.
                throw new ArgumentException(
                    "Every endpoint in a batch must serve the same agent card version.",
                    nameof(endpoints));
            }
        }
    }

    /// <summary>
    /// Fails a call when the server advertises that it cannot serve the agent registry.
    /// </summary>
    /// <param name="connection">The AI registry connection.</param>
    /// <param name="agentName">The agent the operation is about.</param>
    /// <exception cref="NacosServerException">The server advertises that it cannot serve agents.</exception>
    /// <remarks>
    /// Only an explicit rejection is treated as one. A server that says nothing about the ability may
    /// still serve the request, and letting the request fail on its own reports the truth more
    /// precisely than guessing from the absence of a field.
    /// </remarks>
    private static void EnsureSupported(NacosGrpcConnection connection, string agentName)
    {
        if (connection.ServerAbilities.TryGetValue(NacosAIServerAbility.Agent, out bool supported)
            && !supported)
        {
            throw new NacosServerException(
                NotImplementedErrorCode,
                string.Concat(
                    "The connected Nacos server does not implement the agent registry, so agent '",
                    agentName,
                    "' cannot be used."));
        }
    }
}
