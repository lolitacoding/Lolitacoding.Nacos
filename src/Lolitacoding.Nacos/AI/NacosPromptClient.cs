using System.Runtime.CompilerServices;
using System.Text.Json;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// The prompt registry, served over the AI registry connection.
/// </summary>
/// <remarks>
/// <para>
/// A read is one request and the answer the server gives is the answer the caller sees: Nacos owns
/// the version manifest and the labels that point into it, so there is nothing for the client to
/// decide. A subscription is the exception — it is served by a cached copy that is refreshed on an
/// interval, because Nacos has no prompt change push to subscribe to.
/// </para>
/// <para>
/// The registry shares the AI connection with the MCP registry rather than opening one of its own,
/// because Nacos routes a connection to the AI module as a whole rather than to one resource type
/// within it.
/// </para>
/// </remarks>
internal sealed class NacosPromptClient : INacosPromptRegistry, IAsyncDisposable
{
    private readonly NacosAIClient _owner;
    private readonly NacosOptions _options;
    private readonly ILogger<NacosPromptClient> _logger;
    private readonly NacosAIResourceCache<NacosPrompt> _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosPromptClient"/> class.
    /// </summary>
    /// <param name="owner">The AI client that owns the connection.</param>
    /// <param name="options">The client options.</param>
    /// <param name="timeProvider">The clock the subscription refresh interval is measured with.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="snapshots">
    /// The disk failover store, or <see langword="null"/> when resources are kept in memory only.
    /// </param>
    public NacosPromptClient(
        NacosAIClient owner,
        NacosOptions options,
        TimeProvider timeProvider,
        ILogger<NacosPromptClient> logger,
        NacosAISnapshotStore? snapshots = null)
    {
        _owner = owner;
        _options = options;
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
        // the first move, which is also when a caller that never iterates should pay nothing.
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
