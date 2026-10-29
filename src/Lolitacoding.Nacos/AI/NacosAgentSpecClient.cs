using System.Runtime.CompilerServices;
using System.Text.Json;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// The agent-specification registry, served over the AI module's HTTP endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Agent specifications are served only over HTTP, like skills: the official gRPC client answers the
/// request with <c>SERVER_NOT_IMPLEMENTED</c>, and the server exposes it as an OpenAPI endpoint whose
/// answer is an ordinary v3 result envelope. Unlike a skill the body is JSON rather than an archive,
/// so what the client parses is the envelope around the specification.
/// </para>
/// <para>
/// A read names a version, or a label, or neither. This resource resolves a label <em>before</em> a
/// version, which is the opposite of a prompt, and an unknown label falls through to the version and
/// then to <c>latest</c> rather than failing — so a read that names both gets the label's version, and
/// a read that names only an unknown label gets the current one.
/// </para>
/// <para>
/// A subscription is a cached copy refreshed on an interval, because Nacos pushes no changes to this
/// resource either. The refresh reads the specification in full rather than conditionally, because a
/// conditional read cannot report a withdrawal here; the reason is on the cache's configuration.
/// </para>
/// </remarks>
internal sealed class NacosAgentSpecClient : INacosAgentSpecRegistry, IAsyncDisposable
{
    /// <summary>The path of the agent-specification endpoint.</summary>
    private const string AgentSpecPath = "/v3/client/ai/agentspecs";

    /// <summary>The header that carries the hash of the published content.</summary>
    private const string Md5Header = "X-Nacos-AgentSpec-Md5";

    /// <summary>The header that carries the version the server resolved.</summary>
    private const string ResolvedVersionHeader = "X-Nacos-AgentSpec-Resolved-Version";

    /// <summary>The error code Nacos uses for a resource or version that does not exist.</summary>
    private const int NotFoundErrorCode = 404;

    /// <summary>The error code Nacos uses for a conditional read whose hash still matches.</summary>
    private const int NotModifiedErrorCode = 304;

    private readonly NacosAIHttpClient _http;
    private readonly NacosOptions _options;
    private readonly ILogger<NacosAgentSpecClient> _logger;
    private readonly NacosAIResourceCache<NacosAgentSpec> _cache;

    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAgentSpecClient"/> class.
    /// </summary>
    /// <param name="http">The HTTP endpoint the registry is served over.</param>
    /// <param name="options">The client options.</param>
    /// <param name="timeProvider">The clock the subscription refresh interval is measured with.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="snapshots">
    /// The disk failover store, or <see langword="null"/> when specifications are kept in memory only.
    /// </param>
    public NacosAgentSpecClient(
        NacosAIHttpClient http,
        NacosOptions options,
        TimeProvider timeProvider,
        ILogger<NacosAgentSpecClient> logger,
        NacosAISnapshotStore? snapshots = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _options = options;
        _logger = logger;

        _cache = new NacosAIResourceCache<NacosAgentSpec>(new NacosAIResourceCacheOptions<NacosAgentSpec>
        {
            Query = QueryAsync,
            IsSame = IsSame,

            // No hash is sent, so every refresh is an unconditional read. A conditional read cannot
            // report a withdrawal on this resource: the server resolves the selector, compares the
            // stored hash of that version, and answers 304 before it checks whether the version is
            // still online — so a version taken offline, whose hash has not changed, is answered
            // "not modified" forever. A skill escapes this because its selector resolves through an
            // index manifest that an offline removes the version from, which leaves no hash to
            // compare; an agent specification's labels are untouched by an offline, so the hash is
            // still there to match. Reading the body every interval is the price of noticing.
            Hash = static _ => null,
            Changed = (spec, name) => NacosAILog.AgentSpecChanged(_logger, name, spec.Version),
            Withdrawn = name => NacosAILog.AgentSpecWithdrawn(_logger, name),
            RefreshFailed = (name, version, label, reason) =>
                NacosAILog.AgentSpecRefreshFailed(_logger, name, version, label, reason),
            RefreshInterval = options.AI.CacheRefreshInterval,
            Snapshot = NacosAISnapshot<NacosAgentSpec>.Create(
                snapshots, NacosAISnapshotStore.AgentSpecKind, NacosJsonContext.Default.NacosAgentSpec),
            TimeProvider = timeProvider,
            Logger = logger,
        });
    }

    /// <inheritdoc />
    public async Task<NacosAgentSpec> GetAgentSpecAsync(
        string agentSpecName,
        string? version = null,
        string? label = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSpecName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The read is unconditional: a caller that asked for the specification wants its content, so
        // there is nothing to be answered with "not modified".
        return await QueryAsync(agentSpecName, version, label, md5: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosAgentSpec?> SubscribeAgentSpecAsync(
        string agentSpecName,
        string? version = null,
        string? label = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentSpecName);

        // Served over HTTP only, so a subscription does not open the AI connection either.
        await foreach (NacosAgentSpec? spec in
            _cache.SubscribeAsync(agentSpecName, version, label, ct).ConfigureAwait(false))
        {
            yield return spec;
        }
    }

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
    /// Reads one agent-specification selector from the registry.
    /// </summary>
    /// <param name="agentSpecName">The specification name.</param>
    /// <param name="version">The version, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="md5">The hash of the copy the caller already holds, or <see langword="null"/>.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved specification.</returns>
    /// <exception cref="NacosServerException">
    /// The specification or version does not exist (the not-found code), the hash still matches (the
    /// not-modified code), the reply did not carry a specification, or the request failed for any
    /// other reason.
    /// </exception>
    private async Task<NacosAgentSpec> QueryAsync(
        string agentSpecName,
        string? version,
        string? label,
        string? md5,
        CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string?>>(5)
        {
            new("namespaceId", _options.Namespace),
            new("name", agentSpecName),
        };

        AddIfPresent(query, "version", version);
        AddIfPresent(query, "label", label);
        AddIfPresent(query, "md5", md5);

        NacosAIHttpResponse response =
            await _http.GetAsync(AgentSpecPath, query, ct).ConfigureAwait(false);

        switch ((int)response.StatusCode)
        {
            case 200:
                break;

            case NotModifiedErrorCode:
                throw new NacosServerException(
                    NotModifiedErrorCode,
                    string.Concat("The agent specification '", agentSpecName, "' has not changed."));

            case NotFoundErrorCode:
                throw new NacosServerException(
                    NotFoundErrorCode,
                    response.ErrorMessage
                        ?? string.Concat(
                            "The Nacos server does not serve agent specification '", agentSpecName, "'."));

            case 403:
                throw new NacosAuthException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat(
                            "The Nacos server refused the read of agent specification '",
                            agentSpecName,
                            "'."));

            default:
                throw new NacosServerException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat(
                            "The Nacos server answered the read of agent specification '",
                            agentSpecName,
                            "' with HTTP ",
                            ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "."));
        }

        NacosAgentSpecResult? result;

        try
        {
            result = JsonSerializer.Deserialize(
                response.Body, NacosJsonContext.Default.NacosAgentSpecResult);
        }
        catch (JsonException ex)
        {
            // A 200 carrying something that is not a result — an intermediary's error page, say —
            // would otherwise surface as a JSON exception from inside the serializer, which says
            // nothing about what the server did.
            NacosAILog.AgentSpecDetailMissing(_logger, agentSpecName, version, label, 0);

            throw new NacosServerException(
                string.Concat(
                    "The Nacos server returned a body for agent specification '",
                    agentSpecName,
                    "' that is not a result."),
                ex);
        }

        if (result is null || result.Code != 0 || result.Data is null)
        {
            // The endpoint answers with its own result envelope, so a body that is not one, or one
            // that reports a failure, did not produce the specification the caller asked for.
            NacosAILog.AgentSpecDetailMissing(_logger, agentSpecName, version, label, result?.Code ?? 0);

            throw new NacosServerException(
                result?.Code ?? 0,
                result?.Message
                    ?? string.Concat(
                        "The Nacos server returned no agent specification for '", agentSpecName, "'."));
        }

        NacosAgentSpec spec = result.Data;

        // The payload does not carry either of these: they are reported in the response headers.
        spec.Version = response.Header(ResolvedVersionHeader);
        spec.Md5 = response.Header(Md5Header);

        return spec;
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
    /// Decides whether two reads are the same state.
    /// </summary>
    /// <param name="left">The cached specification.</param>
    /// <param name="right">The specification just read.</param>
    /// <returns><see langword="true"/> when the two are the same state.</returns>
    /// <remarks>
    /// The specification is compared by its serialized form, like a prompt: the payload is a JSON
    /// document whose resources carry free-form metadata, so comparing the document is faithful to
    /// "did anything the server serves change" without a field list that could miss one. Both sides
    /// came from the server, so the representation is stable.
    /// </remarks>
    private static bool IsSame(NacosAgentSpec? left, NacosAgentSpec? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(
            JsonSerializer.Serialize(left, NacosJsonContext.Default.NacosAgentSpec),
            JsonSerializer.Serialize(right, NacosJsonContext.Default.NacosAgentSpec),
            StringComparison.Ordinal);
    }
}
