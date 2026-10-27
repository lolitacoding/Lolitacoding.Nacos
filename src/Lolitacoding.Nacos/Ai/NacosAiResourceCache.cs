using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// The on-disk failover cache one resource type writes to and reads from.
/// </summary>
/// <typeparam name="T">The resource the cache holds.</typeparam>
/// <remarks>
/// The store is shared by every resource type, so each cache says which kind it is and how to turn
/// its resource into the JSON the store writes. Both are required together with the store: a cache
/// without a policy simply has no disk failover.
/// </remarks>
internal sealed class NacosAiSnapshot<T>
    where T : class
{
    /// <summary>Gets the store the snapshots are written to.</summary>
    public required NacosAiSnapshotStore Store { get; init; }

    /// <summary>Gets the resource kind, which keeps two resources of the same name apart.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets the contract the resource is serialized with.</summary>
    public required JsonTypeInfo<T> Type { get; init; }

    /// <summary>
    /// Builds the policy of a resource type, when the client was given a store.
    /// </summary>
    /// <param name="store">The store, or <see langword="null"/> when resources are kept in memory only.</param>
    /// <param name="kind">The resource kind.</param>
    /// <param name="type">The contract the resource is serialized with.</param>
    /// <returns>The policy, or <see langword="null"/> when there is no store to write to.</returns>
    public static NacosAiSnapshot<T>? Create(
        NacosAiSnapshotStore? store,
        string kind,
        JsonTypeInfo<T> type)
        => store is null ? null : new NacosAiSnapshot<T> { Store = store, Kind = kind, Type = type };
}

/// <summary>
/// The inputs a <see cref="NacosAiResourceCache{T}"/> is built from.
/// </summary>
/// <typeparam name="T">The resource the cache holds.</typeparam>
/// <remarks>
/// The cache owns the machinery every AI subscription shares — one refresh loop per selector, one
/// cached copy per selector, one queue per consumer — and takes the parts that differ per resource
/// as delegates. Prompts are compared by their serialized form and skills by their archive, and the
/// two log through different event ids, so none of that belongs in the loop.
/// </remarks>
internal sealed class NacosAiResourceCacheOptions<T>
    where T : class
{
    /// <summary>
    /// Gets the read that fetches one selector.
    /// </summary>
    /// <remarks>
    /// The arguments are the resource name, the version, the label and the hash of the copy that is
    /// already cached (or <see langword="null"/> when there is none). The read is expected to report
    /// a resource that does not exist as a <see cref="NacosServerException"/> with the not-found code
    /// and an unchanged copy with the not-modified code.
    /// </remarks>
    public required Func<string, string?, string?, string?, CancellationToken, Task<T>> Query { get; init; }

    /// <summary>
    /// Gets the comparison that decides whether a read changed anything.
    /// </summary>
    /// <remarks>
    /// Both sides came from the server, so the representation is stable and a faithful comparison is
    /// a faithful "did anything change". Two absent states are the same state, which is what keeps a
    /// resource that is still missing from being announced again on every refresh.
    /// </remarks>
    public required Func<T?, T?, bool> IsSame { get; init; }

    /// <summary>
    /// Gets the content hash a conditional read carries.
    /// </summary>
    /// <remarks>
    /// The hash of the cached copy is what makes an unchanged resource cost a round trip rather than
    /// a body: the server answers "not modified" when it still applies. A resource type that carries
    /// no hash returns <see langword="null"/>, which turns every refresh into an unconditional read.
    /// </remarks>
    public required Func<T, string?> Hash { get; init; }

    /// <summary>Gets the log call for a changed resource.</summary>
    /// <remarks>
    /// The arguments are the resource and its name.
    /// </remarks>
    public required Action<T, string> Changed { get; init; }

    /// <summary>Gets the log call for a resource that stopped being served.</summary>
    public required Action<string> Withdrawn { get; init; }

    /// <summary>Gets the log call for a refresh that failed.</summary>
    public required Action<string, string?, string?, string> RefreshFailed { get; init; }

    /// <summary>Gets the interval between two refreshes of a subscribed selector.</summary>
    public required TimeSpan RefreshInterval { get; init; }

    /// <summary>
    /// Gets the disk failover cache, or <see langword="null"/> when resources are kept in memory
    /// only.
    /// </summary>
    public NacosAiSnapshot<T>? Snapshot { get; init; }

    /// <summary>Gets the clock the refresh interval is measured with.</summary>
    public required TimeProvider TimeProvider { get; init; }

    /// <summary>Gets the logger.</summary>
    public required ILogger Logger { get; init; }
}

/// <summary>
/// Holds the AI resources a process is subscribed to, and keeps them current.
/// </summary>
/// <typeparam name="T">The resource the cache holds.</typeparam>
/// <remarks>
/// <para>
/// Nacos does not push AI resource changes. Its own client discovers one by re-reading the resource
/// on an interval and comparing the answer with the copy that was cached, and there is no
/// notification request on the wire to subscribe to, so a subscription here is a cached copy kept
/// current by a loop rather than a stream of server-initiated events. The read is conditional: the
/// hash of the cached copy is sent with it, and the server answers "not modified" when it still
/// applies, so an unchanged resource costs a round trip rather than a body.
/// </para>
/// <para>
/// A selector is a name with an optional version and an optional label, and only one of the two is
/// resolved: the server prefers the version, falls back to the label, and takes the version labelled
/// <c>latest</c> when neither is named. The cache keys the copy by the selector that asked for it
/// rather than by the version that came back, so <c>{name}::latest</c>,
/// <c>{name}::version:1.0.0</c> and <c>{name}::label:stable</c> are three independent subscriptions
/// that may be answered with the same content.
/// </para>
/// <para>
/// A resource that stops being served is reported: the server answers a missing resource and a
/// missing version with the same not-found code, so there is no "not published yet" to confuse a
/// withdrawal with, and a subscriber that never learned of one would keep using content that no
/// longer exists. The withdrawal is delivered as a change whose resource is <see langword="null"/>.
/// </para>
/// </remarks>
internal sealed class NacosAiResourceCache<T> : IAsyncDisposable
    where T : class
{
    /// <summary>The label a subscription without a version or a label resolves to.</summary>
    public const string LatestVersion = "latest";

    /// <summary>The prefix that marks the part of a key that came from a label.</summary>
    private const string LabelPrefix = "label:";

    /// <summary>The prefix that marks the part of a key that came from a version.</summary>
    private const string VersionPrefix = "version:";

    /// <summary>The error code Nacos uses for a resource or version that does not exist.</summary>
    private const int NotFoundErrorCode = 404;

    /// <summary>The error code Nacos uses for a conditional read whose hash still matches.</summary>
    private const int NotModifiedErrorCode = 304;

    private readonly NacosAiResourceCacheOptions<T> _options;

    private readonly ConcurrentDictionary<string, T> _resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _refreshes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);

    /// <summary>
    /// Serializes storing a state, writing its snapshot and announcing it.
    /// </summary>
    /// <remarks>
    /// A subscription's opening read takes its first element from the cache, and a change is
    /// announced to the consumers that are already past that first element. Both sides take this
    /// gate, so a change that is being stored while an opening read is in flight is either already
    /// part of the state that read takes or is announced after the consumer is past its first
    /// element — never both, which would hand the subscriber the same state twice.
    /// </remarks>
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAiResourceCache{T}"/> class.
    /// </summary>
    /// <param name="options">The query, comparison, logging and timing the cache runs on.</param>
    public NacosAiResourceCache(NacosAiResourceCacheOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <summary>
    /// Subscribes to one selector.
    /// </summary>
    /// <param name="name">The resource name.</param>
    /// <param name="version">The version to watch, or <see langword="null"/> for none.</param>
    /// <param name="label">The label to watch, or <see langword="null"/> for none.</param>
    /// <param name="ct">A token that ends the subscription.</param>
    /// <returns>
    /// The current resource as the first element — or <see langword="null"/> when it is not
    /// published — then the resource again on every change, with <see langword="null"/> reporting
    /// that it stopped being served.
    /// </returns>
    /// <remarks>
    /// Leaving the enumeration ends the subscription, and the last subscriber of a selector stops
    /// its refresh loop. A first read that reports the resource as missing is not an error here: it
    /// is the state the subscription starts from, and the loop announces the resource when it
    /// appears.
    /// </remarks>
    public async IAsyncEnumerable<T?> SubscribeAsync(
        string name,
        string? version,
        string? label,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string key = Key(name, version, label);

        var queue = Channel.CreateBounded<T?>(
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
            EnsureRefresh(key, name, version, label);

            // The cached copy answers a subscription that is already current, so the first read only
            // goes to the server when nothing was cached for this selector yet.
            if (Lookup(key) is null)
            {
                await TryQueryAsync(name, version, label, key, ct).ConfigureAwait(false);
            }

            // The first element is taken from the cache under the gate that stores and announces a
            // change. A change that is being stored while this read is in flight is therefore either
            // already part of the state taken here or announced after this consumer is past its
            // first element, never both.
            T? seed;

            await _publishGate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                consumer.MarkSeeded();
                seed = Lookup(key);
            }
            finally
            {
                _publishGate.Release();
            }

            yield return seed;

            await foreach (T? change in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
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
    /// Reads a cached resource.
    /// </summary>
    /// <param name="name">The resource name.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <returns>The cached resource, or <see langword="null"/> when this selector was never served.</returns>
    public T? Lookup(string name, string? version, string? label)
        => Lookup(Key(name, version, label));

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
    /// Seeds a selector from its local snapshot, when the client caches on disk.
    /// </summary>
    /// <param name="name">The resource name.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns><see langword="true"/> when the selector has a copy, either already or now.</returns>
    /// <remarks>
    /// Called before a subscription opens when the server could not be reached. With the copy in
    /// place the subscription starts from it, and the read that would otherwise need the connection
    /// is not made at all; the refresh loop reads the server when it comes back and announces a
    /// change if the copy it holds is no longer what the server serves.
    /// </remarks>
    public async Task<bool> RestoreAsync(
        string name,
        string? version,
        string? label,
        CancellationToken ct)
    {
        string key = Key(name, version, label);

        if (Lookup(key) is not null)
        {
            return true;
        }

        T? restored = await LoadSnapshotAsync(key, ct).ConfigureAwait(false);

        return restored is not null && _resources.TryAdd(key, restored);
    }

    /// <summary>
    /// Builds the key that identifies one selector.
    /// </summary>
    /// <param name="name">The resource name.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <returns>The cache key.</returns>
    /// <remarks>
    /// The version and the label are marked rather than used bare, because a version and a label can
    /// legitimately be the same string and would then share a cache entry. The label wins when both
    /// are named, matching the key the official client builds — which resolves the version first on
    /// the wire, so the two selectors address different versions notwithstanding the shared key.
    /// </remarks>
    private static string Key(string name, string? version, string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            return string.Concat(name, "::", LabelPrefix, label);
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            return string.Concat(name, "::", VersionPrefix, version);
        }

        return string.Concat(name, "::", LatestVersion);
    }

    /// <summary>
    /// Reads a cached resource by key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>The cached resource, or <see langword="null"/>.</returns>
    private T? Lookup(string key)
        => _resources.TryGetValue(key, out T? resource) ? resource : null;

    /// <summary>
    /// Reads one selector from the registry, storing and announcing what it returns.
    /// </summary>
    /// <param name="name">The resource name.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="key">The cache key the answer belongs to.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>The resource, or <see langword="null"/> when the server serves none for the selector.</returns>
    /// <remarks>
    /// This is the seeding read, and it is unconditional: there is nothing to compare against yet, so
    /// asking for the content is the only useful question. It is also the read that falls back to the
    /// local cache: a subscription that restarts while the control plane is unreachable starts from
    /// the copy that was last read from the server instead of failing.
    /// </remarks>
    private async Task<T?> TryQueryAsync(
        string name,
        string? version,
        string? label,
        string key,
        CancellationToken ct)
    {
        try
        {
            T resource = await _options.Query(name, version, label, null, ct).ConfigureAwait(false);
            await ProcessAsync(key, resource, ct).ConfigureAwait(false);
            return resource;
        }
        catch (NacosServerException ex) when (ex.ErrorCode == NotFoundErrorCode)
        {
            // Nothing is served for this selector right now. The subscription is meant to survive
            // that, so the refresh loop keeps looking instead of the subscription failing.
            await ProcessAsync(key, null, ct).ConfigureAwait(false);
            return null;
        }
        catch (NacosException ex) when (ex is NacosConnectionException or NacosTimeoutException)
        {
            T? restored = await LoadSnapshotAsync(key, ct).ConfigureAwait(false);

            if (restored is null)
            {
                // Nothing was cached either, so there is no answer to give and the caller is told
                // the read failed.
                throw;
            }

            // Seeded into the cache rather than processed: this copy was not read just now, so it
            // is not a change and is not announced to a subscriber that already holds it.
            _resources.TryAdd(key, restored);
            NacosAiLog.SnapshotRestored(_options.Logger, _options.Snapshot!.Kind, name, ex.Message);

            return restored;
        }
    }

    /// <summary>
    /// Reads a selector's snapshot, when the resource type caches on disk.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>The cached resource, or <see langword="null"/> when there is none to fall back to.</returns>
    private async Task<T?> LoadSnapshotAsync(string key, CancellationToken ct)
    {
        NacosAiSnapshot<T>? snapshot = _options.Snapshot;

        if (snapshot is null)
        {
            return null;
        }

        return await snapshot.Store.LoadAsync(snapshot.Kind, key, snapshot.Type, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a selector's snapshot, when the resource type caches on disk.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="resource">The resource the server returned.</param>
    /// <param name="ct">A token that cancels the write.</param>
    /// <returns>A task that completes once the snapshot is written, or its failure was reported.</returns>
    private async Task SaveSnapshotAsync(string key, T resource, CancellationToken ct)
    {
        NacosAiSnapshot<T>? snapshot = _options.Snapshot;

        if (snapshot is null)
        {
            return;
        }

        try
        {
            await snapshot.Store
                .SaveAsync(snapshot.Kind, key, resource, snapshot.Type, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The read succeeded, so a snapshot that could not be written is reported rather than
            // raised: losing the failover copy must not fail a caller.
            NacosAiLog.SnapshotWriteFailed(_options.Logger, snapshot.Kind, NameOf(key), ex.Message);
        }
    }

    /// <summary>
    /// Removes a selector's snapshot, when the resource type caches on disk.
    /// </summary>
    /// <param name="key">The cache key.</param>
    private void RemoveSnapshot(string key)
    {
        NacosAiSnapshot<T>? snapshot = _options.Snapshot;

        if (snapshot is not null)
        {
            snapshot.Store.Remove(snapshot.Kind, key);
        }
    }

    /// <summary>
    /// Stores a resource and announces it when it differs from the copy that was cached.
    /// </summary>
    /// <param name="key">The cache key the resource belongs to.</param>
    /// <param name="resource">The resource the server returned, or <see langword="null"/> when it serves none.</param>
    /// <param name="ct">A token that cancels the snapshot write.</param>
    /// <returns>A task that completes once the resource, its snapshot and its subscribers are updated.</returns>
    /// <remarks>
    /// A resource that is not served removes the cached copy and announces the removal, which is how
    /// a subscriber learns that what it was using is gone. Nothing is announced when the state did
    /// not change, so a resource that was never there does not produce a stream of empty updates.
    /// The store, the snapshot and the announcement are one step: a subscriber that is handed a
    /// state, or told that it is gone, has the matching failover copy on disk by then.
    /// </remarks>
    private async Task ProcessAsync(string key, T? resource, CancellationToken ct)
    {
        await _publishGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            bool changed = Store(key, resource);

            if (resource is null)
            {
                // Removed whether or not the absence is news: a snapshot left behind by an earlier
                // process would otherwise be served during the next outage for a resource the
                // server says is gone.
                RemoveSnapshot(key);
            }
            else if (changed)
            {
                await SaveSnapshotAsync(key, resource, ct).ConfigureAwait(false);
            }

            if (changed)
            {
                Publish(key, resource);
            }
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>
    /// Stores a read result, reporting whether it differs from what the cache held.
    /// </summary>
    /// <param name="key">The cache key the answer belongs to.</param>
    /// <param name="resource">The resource, or <see langword="null"/> when the server serves none.</param>
    /// <returns>
    /// <see langword="true"/> when the cache changed, so the state is news to a subscriber.
    /// </returns>
    /// <remarks>
    /// The comparison and the store are one step. Restoring a snapshot seeds the cache without
    /// going through <see cref="ProcessAsync"/>, so it can add a copy between the lookup and the
    /// store here; the loser of that race retries against what the winner stored, so the cache and
    /// the reported change stay in agreement.
    /// </remarks>
    private bool Store(string key, T? resource)
    {
        while (true)
        {
            T? previous = Lookup(key);

            if (_options.IsSame(previous, resource))
            {
                return false;
            }

            bool stored = resource is null
                ? _resources.TryRemove(new KeyValuePair<string, T>(key, previous!))
                : previous is null
                    ? _resources.TryAdd(key, resource)
                    : _resources.TryUpdate(key, resource, previous);

            if (stored)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Delivers a changed resource to every subscriber of one key.
    /// </summary>
    /// <param name="key">The cache key the change belongs to.</param>
    /// <param name="resource">The changed resource, or <see langword="null"/> when it was removed.</param>
    private void Publish(string key, T? resource)
    {
        if (_subscriptions.TryGetValue(key, out Subscription? subscription))
        {
            subscription.Publish(resource);
        }

        string name = NameOf(key);

        if (resource is null)
        {
            _options.Withdrawn(name);
        }
        else
        {
            _options.Changed(resource, name);
        }
    }

    /// <summary>
    /// Reads the resource name back out of a cache key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>The part of the key before the selector.</returns>
    /// <remarks>
    /// Only used for logging, and only for a removal, where there is no resource to read the name
    /// from.
    /// </remarks>
    private static string NameOf(string key)
    {
        int separator = key.LastIndexOf("::", StringComparison.Ordinal);

        return separator < 0 ? key : key[..separator];
    }

    /// <summary>
    /// Starts a refresh loop for a subscribed selector.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    private void EnsureRefresh(string key, string name, string? version, string? label)
    {
        CancellationTokenSource source = _refreshes.GetOrAdd(key, _ => StartRefresh(name, version, label, key));

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
    /// <param name="name">The resource name.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="key">The cache key the loop keeps current.</param>
    /// <returns>The source the loop is cancelled with.</returns>
    private CancellationTokenSource StartRefresh(string name, string? version, string? label, string key)
    {
        var source = new CancellationTokenSource();

        _ = Task.Run(() => RefreshAsync(name, version, label, key, source), CancellationToken.None);

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
    /// <param name="name">The resource name.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="key">The cache key the loop keeps current.</param>
    /// <param name="source">The source that ends the loop.</param>
    /// <returns>A task that completes once the loop ends.</returns>
    /// <remarks>
    /// The loop is the only thing that makes a subscription fire, because Nacos has no AI change
    /// push. A read that fails leaves the cached copy in place — a subscription outlives an outage
    /// rather than failing with it — while a resource the server no longer serves is a change and is
    /// announced as one.
    /// </remarks>
    private async Task RefreshAsync(
        string name,
        string? version,
        string? label,
        string key,
        CancellationTokenSource source)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_options.RefreshInterval, _options.TimeProvider, source.Token)
                    .ConfigureAwait(false);

                try
                {
                    // The read itself is not cancelled: it is already in flight when the last
                    // subscriber leaves, and letting it finish keeps the cache consistent.
                    T resource = await _options.Query(
                        name,
                        version,
                        label,
                        Lookup(key) is { } cached ? _options.Hash(cached) : null,
                        CancellationToken.None).ConfigureAwait(false);

                    await ProcessAsync(key, resource, CancellationToken.None).ConfigureAwait(false);
                }
                catch (NacosServerException ex) when (ex.ErrorCode == NotFoundErrorCode)
                {
                    // Withdrawn, or a version that is no longer served. A subscriber is told, because
                    // a resource that is gone is not indistinguishable from one that has not
                    // arrived yet.
                    await ProcessAsync(key, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (NacosServerException ex) when (ex.ErrorCode == NotModifiedErrorCode)
                {
                    // The hash still matches, so the cached copy is current and nothing is reported.
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                    {
                        _options.RefreshFailed(name, version, label, ex.Message);
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
        public Consumer Attach(ChannelWriter<T?> writer)
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
        /// <param name="resource">The changed resource, or <see langword="null"/> when it was removed.</param>
        public void Publish(T? resource)
        {
            Consumer[] consumers;

            lock (_consumers)
            {
                consumers = [.. _consumers];
            }

            foreach (Consumer consumer in consumers)
            {
                consumer.Deliver(resource);
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
        private int _seeded;

        /// <summary>
        /// Initializes a new instance of the <see cref="Consumer"/> class.
        /// </summary>
        /// <param name="writer">The queue this consumer reads.</param>
        public Consumer(ChannelWriter<T?> writer) => Writer = writer;

        /// <summary>Gets the queue this consumer reads.</summary>
        public ChannelWriter<T?> Writer { get; }

        /// <summary>
        /// Records that the consumer's first element was taken from the cache.
        /// </summary>
        public void MarkSeeded() => Volatile.Write(ref _seeded, 1);

        /// <summary>
        /// Queues a changed state for a consumer that is past its first element.
        /// </summary>
        /// <param name="resource">The changed resource, or <see langword="null"/> when it was removed.</param>
        /// <remarks>
        /// Called under the cache's publish gate. A consumer that has not taken its first element
        /// yet is skipped: its opening read takes the state from the cache instead, so the same
        /// change would otherwise arrive twice.
        /// </remarks>
        public void Deliver(T? resource)
        {
            if (Volatile.Read(ref _seeded) != 0)
            {
                Writer.TryWrite(resource);
            }
        }
    }
}
