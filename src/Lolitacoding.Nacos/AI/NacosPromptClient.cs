using System.Runtime.CompilerServices;
using System.Text.Json;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// The prompt registry, served over the AI registry connection or the client OpenAPI.
/// </summary>
/// <remarks>
/// <para>
/// A read is one request and the answer the server gives is the answer the caller sees: Nacos owns
/// the version manifest and the labels that point into it, so there is nothing for the client to
/// decide. A subscription is the exception — it is served by a cached copy that is refreshed on an
/// interval, because Nacos has no prompt change push to subscribe to.
/// </para>
/// <para>
/// Which transport answers a read is <see cref="NacosAIOptions.TransportMode"/>, with
/// <see cref="NacosAIOptions.PromptTransportMode"/> overriding it for prompts. The gRPC path shares
/// the AI connection with the MCP registry rather than opening one of its own, because Nacos routes
/// a connection to the AI module as a whole rather than to one resource type within it. The HTTP
/// path is the same client endpoint the other OpenAPI resources use and does not open that
/// connection at all.
/// </para>
/// </remarks>
internal sealed class NacosPromptClient : INacosPromptRegistry, IAsyncDisposable
{
    /// <summary>The client OpenAPI path that serves one prompt.</summary>
    private const string PromptPath = "/v3/client/ai/prompt";

    /// <summary>The status a conditional HTTP read gets when the hash still matches.</summary>
    private const int NotModifiedErrorCode = 304;

    /// <summary>The status an HTTP read gets when the prompt or version is not served.</summary>
    private const int NotFoundErrorCode = 404;

    /// <summary>
    /// How long the client stays on HTTP after a gRPC connection failure before trying gRPC again.
    /// </summary>
    /// <remarks>
    /// A failed gRPC attempt costs the connection timeout, so retrying it on every read would make
    /// <see cref="NacosAITransportMode.Auto"/> slower than pinning HTTP. The window is short enough
    /// that a recovered connection is picked up again without a restart.
    /// </remarks>
    private static readonly TimeSpan GrpcRetryInterval = TimeSpan.FromSeconds(30);

    private readonly NacosAIClient _owner;
    private readonly NacosOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly NacosAIHttpClient _http;
    private readonly ILogger<NacosPromptClient> _logger;
    private readonly NacosAIResourceCache<NacosPrompt> _cache;

    /// <summary>The time at which gRPC becomes worth trying again, as UTC ticks; zero for now.</summary>
    private long _grpcRetryAtTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosPromptClient"/> class.
    /// </summary>
    /// <param name="owner">The AI client that owns the connection.</param>
    /// <param name="options">The client options.</param>
    /// <param name="timeProvider">The clock the subscription refresh interval is measured with.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="http">The HTTP client the prompt endpoint is read over.</param>
    /// <param name="snapshots">
    /// The disk failover store, or <see langword="null"/> when resources are kept in memory only.
    /// </param>
    public NacosPromptClient(
        NacosAIClient owner,
        NacosOptions options,
        TimeProvider timeProvider,
        ILogger<NacosPromptClient> logger,
        NacosAIHttpClient http,
        NacosAISnapshotStore? snapshots = null)
    {
        _owner = owner;
        _options = options;
        _timeProvider = timeProvider;
        _http = http;
        _logger = logger;

        _cache = new NacosAIResourceCache<NacosPrompt>(new NacosAIResourceCacheOptions<NacosPrompt>
        {
            Query = QueryAsync,
            IsSame = IsSame,
            Hash = static prompt => prompt.Md5,
            Changed = (prompt, name) =>
            {
                if (prompt.Version is not null)
                {
                    NacosAILog.PromptChanged(_logger, prompt.PromptKey ?? name, prompt.Version);
                }
            },
            Withdrawn = name => NacosAILog.PromptWithdrawn(_logger, name),
            RefreshFailed = (name, version, label, reason) =>
                NacosAILog.PromptRefreshFailed(_logger, name, version, reason),
            RefreshInterval = options.AI.CacheRefreshInterval,
            Snapshot = NacosAISnapshot<NacosPrompt>.Create(
                snapshots, NacosAISnapshotStore.PromptKind, NacosJsonContext.Default.NacosPrompt),
            TimeProvider = timeProvider,
            Logger = logger,
        });
    }

    /// <inheritdoc />
    public async Task<NacosPrompt> GetPromptAsync(
        string promptKey,
        string? version = null,
        string? label = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptKey);

        // The read is unconditional: a caller that asked for the prompt wants its content, so there
        // is nothing to be answered with "not modified".
        return await QueryAsync(promptKey, version, label, md5: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosPrompt?> SubscribePromptAsync(
        string promptKey,
        string? version = null,
        string? label = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptKey);

        // The connection is opened here rather than at the call, because an async iterator runs on
        // the first move, which is also when a caller that never iterates should pay nothing. Only a
        // gRPC-only client has a connection to open: an HTTP or Auto client leaves the seeding read
        // to the cache, which falls back to the disk copy when both transports are unreachable.
        if (EffectiveTransport == NacosAITransportMode.Grpc)
        {
            try
            {
                await _owner.GetConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (NacosException ex) when (ex is NacosConnectionException or NacosTimeoutException)
            {
                // A server that cannot be reached is not fatal when the prompt was cached on disk: the
                // subscription starts from that copy, and the refresh loop reads the server when it
                // comes back. With nothing cached there is nothing to start from, and the failure is
                // the caller's exactly as it was before.
                if (!await _cache.RestoreAsync(promptKey, version, label, ct).ConfigureAwait(false))
                {
                    throw;
                }
            }
        }

        await foreach (NacosPrompt? prompt in _cache.SubscribeAsync(promptKey, version, label, ct).ConfigureAwait(false))
        {
            yield return prompt;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _cache.DisposeAsync();

    /// <summary>
    /// Reads one prompt selector from the registry.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="md5">The hash of the copy the caller already holds, or <see langword="null"/>.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved prompt.</returns>
    /// <exception cref="NacosServerException">
    /// The prompt or version does not exist (the not-found code), the hash still matches (the
    /// not-modified code), or the read failed for any other reason.
    /// </exception>
    /// <remarks>
    /// This is the raw read that both <see cref="GetPromptAsync"/> and the subscription refresh loop
    /// use. A prompt that is not served and a copy that is still current are both reported as result
    /// codes rather than as a missing field, so the transport turns them into exceptions and both
    /// callers decide what they mean: a refresh treats the first as a withdrawal and the second as
    /// nothing to do, while a read surfaces both.
    /// </remarks>
    private async Task<NacosPrompt> QueryAsync(
        string promptKey,
        string? version,
        string? label,
        string? md5,
        CancellationToken ct)
    {
        NacosAITransportMode mode = EffectiveTransport;

        if (mode == NacosAITransportMode.Http)
        {
            return await QueryOverHttpAsync(promptKey, version, label, md5, ct).ConfigureAwait(false);
        }

        if (mode == NacosAITransportMode.Auto && !GrpcIsWorthTrying())
        {
            return await QueryOverHttpAsync(promptKey, version, label, md5, ct).ConfigureAwait(false);
        }

        try
        {
            return await QueryOverGrpcAsync(promptKey, version, label, md5, ct).ConfigureAwait(false);
        }
        catch (NacosException ex) when (
            mode == NacosAITransportMode.Auto
            && ex is NacosConnectionException or NacosTimeoutException)
        {
            // The transport is what failed, not the registry, so the same read is retried over HTTP.
            // The window keeps a caller from paying a failed gRPC attempt on every read while the
            // connection is down; once it has passed, gRPC is tried again.
            Volatile.Write(
                ref _grpcRetryAtTicks,
                _timeProvider.GetUtcNow().Add(GrpcRetryInterval).UtcTicks);

            NacosAILog.AiTransportFallback(_logger, "prompt", ex.Message);

            return await QueryOverHttpAsync(promptKey, version, label, md5, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the transport a prompt read uses.
    /// </summary>
    private NacosAITransportMode EffectiveTransport =>
        _options.AI.ResolveTransport(_options.AI.PromptTransportMode);

    /// <summary>
    /// Decides whether gRPC is worth trying while the client is in
    /// <see cref="NacosAITransportMode.Auto"/>.
    /// </summary>
    /// <returns><see langword="true"/> when gRPC may be tried.</returns>
    private bool GrpcIsWorthTrying()
    {
        long retryAt = Volatile.Read(ref _grpcRetryAtTicks);

        return retryAt == 0 || _timeProvider.GetUtcNow().UtcTicks >= retryAt;
    }

    /// <summary>
    /// Reads one prompt selector over the AI gRPC connection.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="md5">The hash of the copy the caller already holds, or <see langword="null"/>.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved prompt.</returns>
    private async Task<NacosPrompt> QueryOverGrpcAsync(
        string promptKey,
        string? version,
        string? label,
        string? md5,
        CancellationToken ct)
    {
        NacosGrpcConnection connection = await _owner.GetConnectionAsync(ct).ConfigureAwait(false);

        var request = new QueryPromptRequest
        {
            NamespaceId = _options.Namespace,
            PromptKey = promptKey,
            Version = version,
            Label = label,
            Md5 = md5,
        };

        QueryPromptResponse response =
            await connection.RequestAsync<QueryPromptResponse>(request, ct).ConfigureAwait(false);

        NacosPrompt? prompt = response.PromptInfo;

        if (prompt is null)
        {
            // A missing prompt, a version that is not published and an unchanged hash are all
            // reported as result codes, which the transport has already turned into exceptions.
            // Reaching here therefore means the reply did not match the contract, and reporting it
            // as a missing prompt would hide that.
            NacosAILog.PromptDetailMissing(_logger, promptKey, version, label);

            throw new NacosServerException(
                string.Concat("The Nacos server returned no prompt for prompt key '", promptKey, "'."));
        }

        return prompt;
    }

    /// <summary>
    /// Reads one prompt selector over the client OpenAPI.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="md5">The hash of the copy the caller already holds, or <see langword="null"/>.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved prompt.</returns>
    /// <exception cref="NacosServerException">
    /// The prompt or version does not exist, the hash still matches, or the read failed.
    /// </exception>
    /// <remarks>
    /// The endpoint answers with the same result envelope the other client OpenAPI resources use, so
    /// the prompt is read out of <c>data</c>. A missing prompt and an unchanged hash arrive as status
    /// codes, which are turned into the same error codes the gRPC transport reports; the cache and a
    /// caller then treat both transports identically.
    /// </remarks>
    private async Task<NacosPrompt> QueryOverHttpAsync(
        string promptKey,
        string? version,
        string? label,
        string? md5,
        CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string?>>(5)
        {
            new("namespaceId", _options.Namespace),
            new("promptKey", promptKey),
        };

        AddIfPresent(query, "version", version);
        AddIfPresent(query, "label", label);
        AddIfPresent(query, "md5", md5);

        NacosAIHttpResponse response = await _http.GetAsync(PromptPath, query, ct).ConfigureAwait(false);

        switch ((int)response.StatusCode)
        {
            case 200:
                break;

            case NotModifiedErrorCode:
                throw new NacosServerException(
                    NotModifiedErrorCode,
                    string.Concat("The prompt '", promptKey, "' has not changed."));

            case NotFoundErrorCode:
                throw new NacosServerException(
                    NotFoundErrorCode,
                    response.ErrorMessage
                        ?? string.Concat("The Nacos server does not serve prompt '", promptKey, "'."));

            case 403:
                throw new NacosAuthException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat("The Nacos server refused the read of prompt '", promptKey, "'."));

            default:
                throw new NacosServerException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat(
                            "The Nacos server answered the read of prompt '",
                            promptKey,
                            "' with HTTP ",
                            ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "."));
        }

        NacosPromptResult? result;

        try
        {
            result = JsonSerializer.Deserialize(
                response.Body, NacosJsonContext.Default.NacosPromptResult);
        }
        catch (JsonException ex)
        {
            // A 200 carrying something that is not a result — an intermediary's error page, say —
            // would otherwise surface as a JSON exception from inside the serializer, which says
            // nothing about what the server did.
            NacosAILog.PromptDetailMissing(_logger, promptKey, version, label);

            throw new NacosServerException(
                string.Concat(
                    "The Nacos server returned a body for prompt '",
                    promptKey,
                    "' that is not a result."),
                ex);
        }

        if (result is null || result.Code != 0 || result.Data is null)
        {
            // The endpoint answers with its own result envelope, so a body that is not one, or one
            // that reports a failure, did not produce the prompt the caller asked for.
            NacosAILog.PromptDetailMissing(_logger, promptKey, version, label);

            throw new NacosServerException(
                result?.Code ?? 0,
                result?.Message
                    ?? string.Concat("The Nacos server returned no prompt for prompt key '", promptKey, "'."));
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
    /// Decides whether two reads are the same state.
    /// </summary>
    /// <param name="left">The cached prompt.</param>
    /// <param name="right">The prompt just read.</param>
    /// <returns><see langword="true"/> when the two are the same state.</returns>
    /// <remarks>
    /// The comparison is made over the serialized prompt rather than over a field list, so a change
    /// to any part of it — the template, the hash, or a variable's metadata — is seen. Both sides of
    /// the comparison came from the server, so the representation is stable and a byte comparison is
    /// a faithful "did anything change".
    /// </remarks>
    private static bool IsSame(NacosPrompt? left, NacosPrompt? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return string.Equals(
            JsonSerializer.Serialize(left, NacosJsonContext.Default.NacosPrompt),
            JsonSerializer.Serialize(right, NacosJsonContext.Default.NacosPrompt),
            StringComparison.Ordinal);
    }
}
