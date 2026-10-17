using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Lolitacoding.Nacos.Protocol;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// Holds the prompt versions a process is subscribed to, and keeps them current.
/// </summary>
/// <remarks>
/// <para>
/// Nacos does not push prompt changes either. A change is discovered by re-reading the prompt on an
/// interval and comparing the answer with the copy that was cached, which is why a subscription is
/// served by a refresh loop per subscribed selector rather than by a stream of server-initiated
/// events. The read is conditional: the hash of the cached copy is sent with it, and the server
/// answers "not modified" when it still applies, so an unchanged prompt costs a round trip rather
/// than a body.
/// </para>
/// <para>
/// A selector is a key with an optional version and an optional label, and only one of the two is
/// resolved: the server prefers the version, falls back to the label, and takes the version labelled
/// <c>latest</c> when neither is named. The cache keys the copy by the selector that asked for it
/// rather than by the version that came back, so <c>{key}::latest</c>, <c>{key}::version:1.0.0</c>
/// and <c>{key}::label:stable</c> are three independent subscriptions that may be answered with the
/// same content.
/// </para>
/// <para>
/// The comparison is made over the serialized prompt rather than over a field list, so a change to
/// any part of it — the template, the hash, or a variable's metadata — is seen. Both sides of the
/// comparison came from the server, so the representation is stable and a byte comparison is a
/// faithful "did anything change".
/// </para>
/// <para>
/// A prompt that stops being served is reported, unlike an MCP server: the server answers a missing
/// prompt and a missing version with the same not-found code, so there is no "not published yet" to
/// confuse a withdrawal with, and a subscriber that never learned of one would keep rendering a
/// prompt that no longer exists. The withdrawal is delivered as a change whose prompt is
/// <see langword="null"/>.
/// </para>
/// </remarks>
internal sealed class NacosPromptCache : IAsyncDisposable
{
    /// <summary>The label a subscription without a version or a label resolves to.</summary>
    public const string LatestVersion = "latest";

    /// <summary>The prefix that marks the part of a key that came from a label.</summary>
    private const string LabelPrefix = "label:";

    /// <summary>The prefix that marks the part of a key that came from a version.</summary>
    private const string VersionPrefix = "version:";

    /// <summary>The error code Nacos uses for a prompt or version that does not exist.</summary>
    private const int NotFoundErrorCode = 404;

    /// <summary>The error code Nacos uses for a conditional read whose hash still matches.</summary>
    private const int NotModifiedErrorCode = 304;

    private readonly Func<string, string?, string?, string?, CancellationToken, Task<NacosPrompt>> _query;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, NacosPrompt> _prompts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _refreshes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);

    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosPromptCache"/> class.
    /// </summary>
    /// <param name="query">Reads one prompt selector from the registry.</param>
    /// <param name="refreshInterval">How long to wait between refreshes of a subscribed selector.</param>
    /// <param name="timeProvider">The clock the refresh interval is measured with.</param>
    /// <param name="logger">The logger.</param>
    /// <remarks>
    /// The read is passed the hash of the copy that is already cached, or <see langword="null"/> when
    /// there is none, and is expected to report a prompt that does not exist as a
    /// <see cref="NacosServerException"/> with the not-found code and an unchanged one with the
    /// not-modified code.
    /// </remarks>
    public NacosPromptCache(
        Func<string, string?, string?, string?, CancellationToken, Task<NacosPrompt>> query,
        TimeSpan refreshInterval,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _query = query;
        _refreshInterval = refreshInterval;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Subscribes to one prompt selector.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version to watch, or <see langword="null"/> for none.</param>
    /// <param name="label">The label to watch, or <see langword="null"/> for none.</param>
    /// <param name="ct">A token that ends the subscription.</param>
    /// <returns>
    /// The current prompt as the first element — or <see langword="null"/> when it is not published —
    /// then the prompt again on every change, with <see langword="null"/> reporting that it stopped
    /// being served.
    /// </returns>
    /// <remarks>
    /// Leaving the enumeration ends the subscription, and the last subscriber of a selector stops
    /// its refresh loop. A first read that reports the prompt as missing is not an error here: it is
    /// the state the subscription starts from, and the loop announces the prompt when it appears.
    /// </remarks>
    public async IAsyncEnumerable<NacosPrompt?> SubscribeAsync(
        string promptKey,
        string? version,
        string? label,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string key = Key(promptKey, version, label);

        var queue = Channel.CreateBounded<NacosPrompt?>(
            new BoundedChannelOptions(16)
            {
                // The newest state is the one that matters, so an overwhelmed consumer loses
                // intermediate states rather than stalling the shared refresh loop.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        Subscription subscription = _subscriptions.GetOrAdd(key, _ => new Subscription());
        Consumer consumer = subscription.Attach(queue.Writer);

        try
        {
            EnsureRefresh(key, promptKey, version, label);

            // The cached copy answers a subscription that is already current, so the first read only
            // goes to the server when nothing was cached for this selector yet.
            NacosPrompt? current = Lookup(key);

            if (current is null)
            {
                current = await TryQueryAsync(promptKey, version, label, key, ct).ConfigureAwait(false);
            }

            // The read above publishes through the same path a refresh does, so it may already have
            // delivered this state to this consumer; the flag keeps it from seeing it twice.
            if (consumer.TryMarkDelivered())
            {
                yield return current;
            }

            await foreach (NacosPrompt? change in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            subscription.Detach(consumer);

            if (subscription.IsEmpty)
            {
                _subscriptions.TryRemove(new KeyValuePair<string, Subscription>(key, subscription));
                StopRefresh(key);
            }
        }
    }

    /// <summary>
    /// Reads a cached prompt.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <returns>The cached prompt, or <see langword="null"/> when this selector was never served.</returns>
    public NacosPrompt? Lookup(string promptKey, string? version, string? label)
        => Lookup(Key(promptKey, version, label));

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        // Stopping the loops first means no further server traffic; completing the channels then
        // ends the enumerations a caller may still be reading instead of leaving them hanging.
        foreach (CancellationTokenSource source in _refreshes.Values)
        {
            source.Cancel();
        }

        _refreshes.Clear();

        foreach (Subscription subscription in _subscriptions.Values)
        {
            subscription.Complete();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Builds the key that identifies one prompt selector.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <returns>The cache key.</returns>
    /// <remarks>
    /// The version and the label are marked rather than used bare, because a version and a label can
    /// legitimately be the same string and would then share a cache entry. The label wins when both
    /// are named, matching the key the official client builds — which resolves the version first on
    /// the wire, so the two selectors address different versions notwithstanding the shared key.
    /// </remarks>
    private static string Key(string promptKey, string? version, string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            return string.Concat(promptKey, "::", LabelPrefix, label);
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            return string.Concat(promptKey, "::", VersionPrefix, version);
        }

        return string.Concat(promptKey, "::", LatestVersion);
    }

    /// <summary>
    /// Reads a cached prompt by key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>The cached prompt, or <see langword="null"/>.</returns>
    private NacosPrompt? Lookup(string key)
        => _prompts.TryGetValue(key, out NacosPrompt? prompt) ? prompt : null;

    /// <summary>
    /// Reads one selector from the registry, storing and announcing what it returns.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="key">The cache key the answer belongs to.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>The prompt, or <see langword="null"/> when the server serves none for the selector.</returns>
    /// <remarks>
    /// This is the seeding read, and it is unconditional: there is nothing to compare against yet, so
    /// asking for the content is the only useful question.
    /// </remarks>
    private async Task<NacosPrompt?> TryQueryAsync(
        string promptKey,
        string? version,
        string? label,
        string key,
        CancellationToken ct)
    {
        try
        {
            NacosPrompt prompt = await _query(promptKey, version, label, null, ct).ConfigureAwait(false);
            Process(key, prompt);
            return prompt;
        }
        catch (NacosServerException ex) when (ex.ErrorCode == NotFoundErrorCode)
        {
            // Nothing is served for this selector right now. The subscription is meant to survive
            // that, so the refresh loop keeps looking instead of the subscription failing.
            Process(key, null);
            return null;
        }
    }

    /// <summary>
    /// Stores a prompt and announces it when it differs from the copy that was cached.
    /// </summary>
    /// <param name="key">The cache key the prompt belongs to.</param>
    /// <param name="prompt">The prompt the server returned, or <see langword="null"/> when it serves none.</param>
    /// <remarks>
    /// A prompt that is not served removes the cached copy and announces the removal, which is how a
    /// subscriber learns that what it was rendering is gone. Nothing is announced when the state did
    /// not change, so a prompt that was never there does not produce a stream of empty updates.
    /// </remarks>
    private void Process(string key, NacosPrompt? prompt)
    {
        NacosPrompt? previous = Lookup(key);
        bool changed = !IsSame(previous, prompt);

        if (prompt is null)
        {
            _prompts.TryRemove(key, out _);
        }
        else
        {
            _prompts[key] = prompt;
        }

        if (changed)
        {
            Publish(key, prompt);
        }
    }

    /// <summary>
    /// Compares two prompts by their serialized form.
    /// </summary>
    /// <param name="left">The cached prompt.</param>
    /// <param name="right">The prompt just read.</param>
    /// <returns><see langword="true"/> when the two are the same state.</returns>
    /// <remarks>
    /// Two absent states are the same state, which is what keeps a prompt that is still missing from
    /// being announced again on every refresh.
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

    /// <summary>
    /// Delivers a changed prompt to every subscriber of one key.
    /// </summary>
    /// <param name="key">The cache key the change belongs to.</param>
    /// <param name="prompt">The changed prompt, or <see langword="null"/> when it was removed.</param>
    private void Publish(string key, NacosPrompt? prompt)
    {
        if (_subscriptions.TryGetValue(key, out Subscription? subscription))
        {
            subscription.Publish(prompt);
        }

        if (prompt is null)
        {
            NacosAiLog.PromptWithdrawn(_logger, PromptKeyOf(key));
        }
        else if (prompt.Version is not null)
        {
            NacosAiLog.PromptChanged(_logger, prompt.PromptKey ?? PromptKeyOf(key), prompt.Version);
        }
    }

    /// <summary>
    /// Reads the prompt key back out of a cache key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>The part of the key before the selector.</returns>
    /// <remarks>
    /// Only used for logging, and only for a removal, where there is no prompt to read the key from.
    /// </remarks>
    private static string PromptKeyOf(string key)
    {
        int separator = key.LastIndexOf("::", StringComparison.Ordinal);

        return separator < 0 ? key : key[..separator];
    }

    /// <summary>
    /// Starts a refresh loop for a subscribed selector.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    private void EnsureRefresh(string key, string promptKey, string? version, string? label)
    {
        CancellationTokenSource source = _refreshes.GetOrAdd(key, _ => StartRefresh(promptKey, version, label, key));

        // Disposal can race the first subscription and cancel the loops that already exist; the one
        // created above would otherwise run on after the client was closed.
        if (_disposed)
        {
            source.Cancel();
        }
    }

    /// <summary>
    /// Creates the cancellation source and task of a refresh loop.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="key">The cache key the loop keeps current.</param>
    /// <returns>The source the loop is cancelled with.</returns>
    private CancellationTokenSource StartRefresh(string promptKey, string? version, string? label, string key)
    {
        var source = new CancellationTokenSource();

        _ = Task.Run(() => RefreshAsync(promptKey, version, label, key, source), CancellationToken.None);

        return source;
    }

    /// <summary>
    /// Stops the refresh loop of a selector.
    /// </summary>
    /// <param name="key">The cache key.</param>
    private void StopRefresh(string key)
    {
        if (_refreshes.TryRemove(key, out CancellationTokenSource? source))
        {
            source.Cancel();
        }
    }

    /// <summary>
    /// Re-reads a subscribed selector on an interval until it is cancelled.
    /// </summary>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="key">The cache key the loop keeps current.</param>
    /// <param name="source">The source that ends the loop.</param>
    /// <returns>A task that completes once the loop ends.</returns>
    /// <remarks>
    /// The loop is the only thing that makes a subscription fire, because Nacos has no prompt change
    /// push. A read that fails leaves the cached copy in place — a subscription outlives an outage
    /// rather than failing with it — while a prompt the server no longer serves is a change and is
    /// announced as one.
    /// </remarks>
    private async Task RefreshAsync(
        string promptKey,
        string? version,
        string? label,
        string key,
        CancellationTokenSource source)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_refreshInterval, _timeProvider, source.Token).ConfigureAwait(false);

                try
                {
                    // The read itself is not cancelled: it is already in flight when the last
                    // subscriber leaves, and letting it finish keeps the cache consistent.
                    NacosPrompt prompt = await _query(
                        promptKey,
                        version,
                        label,
                        Lookup(key)?.Md5,
                        CancellationToken.None).ConfigureAwait(false);

                    Process(key, prompt);
                }
                catch (NacosServerException ex) when (ex.ErrorCode == NotFoundErrorCode)
                {
                    // Withdrawn, or a version that is no longer served. A subscriber is told, because
                    // unlike an MCP server a prompt that is gone is not indistinguishable from one
                    // that has not arrived yet.
                    Process(key, null);
                }
                catch (NacosServerException ex) when (ex.ErrorCode == NotModifiedErrorCode)
                {
                    // The hash still matches, so the cached copy is current and nothing is reported.
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                    {
                        NacosAiLog.PromptRefreshFailed(_logger, promptKey, version, ex.Message);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The last subscriber left, or the client was disposed.
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>
    /// One selector as a set of live consumers.
    /// </summary>
    /// <remarks>
    /// Several consumers can subscribe to the same selector; they share one cache entry and one
    /// refresh loop, and each gets its own queue and its own delivery state so a slow reader does
    /// not hold up the others and one consumer's first element is not mistaken for another's.
    /// </remarks>
    private sealed class Subscription
    {
        private readonly HashSet<Consumer> _consumers = [];

        /// <summary>Gets a value indicating whether no consumer is attached any more.</summary>
        public bool IsEmpty
        {
            get
            {
                lock (_consumers)
                {
                    return _consumers.Count == 0;
                }
            }
        }

        /// <summary>
        /// Adds a consumer.
        /// </summary>
        /// <param name="writer">The queue to publish to.</param>
        /// <returns>The consumer handle the caller keeps.</returns>
        public Consumer Attach(ChannelWriter<NacosPrompt?> writer)
        {
            var consumer = new Consumer(writer);

            lock (_consumers)
            {
                _consumers.Add(consumer);
            }

            return consumer;
        }

        /// <summary>
        /// Removes a consumer.
        /// </summary>
        /// <param name="consumer">The consumer that is going away.</param>
        public void Detach(Consumer consumer)
        {
            lock (_consumers)
            {
                _consumers.Remove(consumer);
            }
        }

        /// <summary>
        /// Queues a changed state for every consumer.
        /// </summary>
        /// <param name="prompt">The changed prompt, or <see langword="null"/> when it was removed.</param>
        public void Publish(NacosPrompt? prompt)
        {
            Consumer[] consumers;

            lock (_consumers)
            {
                consumers = [.. _consumers];
            }

            foreach (Consumer consumer in consumers)
            {
                consumer.Deliver(prompt);
            }
        }

        /// <summary>
        /// Ends every consumer's enumeration.
        /// </summary>
        public void Complete()
        {
            Consumer[] consumers;

            lock (_consumers)
            {
                consumers = [.. _consumers];
            }

            foreach (Consumer consumer in consumers)
            {
                consumer.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// One reader of a subscribed selector.
    /// </summary>
    /// <remarks>
    /// The delivery state belongs to the reader rather than to the selector, because a consumer that
    /// subscribes to an already-cached selector must still be handed that first state even though an
    /// earlier consumer of the same selector already was.
    /// </remarks>
    private sealed class Consumer
    {
        private int _delivered;

        /// <summary>
        /// Initializes a new instance of the <see cref="Consumer"/> class.
        /// </summary>
        /// <param name="writer">The queue this consumer reads.</param>
        public Consumer(ChannelWriter<NacosPrompt?> writer) => Writer = writer;

        /// <summary>Gets the queue this consumer reads.</summary>
        public ChannelWriter<NacosPrompt?> Writer { get; }

        /// <summary>
        /// Claims the right to hand the first state to the consumer.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when nothing was delivered yet; <see langword="false"/> when a
        /// change was already published and will arrive through the queue.
        /// </returns>
        public bool TryMarkDelivered() => Interlocked.Exchange(ref _delivered, 1) == 0;

        /// <summary>
        /// Queues a changed state and records that this consumer is now past its first element.
        /// </summary>
        /// <param name="prompt">The changed prompt, or <see langword="null"/> when it was removed.</param>
        public void Deliver(NacosPrompt? prompt)
        {
            Interlocked.Exchange(ref _delivered, 1);
            Writer.TryWrite(prompt);
        }
    }
}
