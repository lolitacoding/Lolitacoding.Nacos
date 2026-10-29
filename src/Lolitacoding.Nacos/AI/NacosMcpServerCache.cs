using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// Holds the MCP server details a process is subscribed to, and keeps them current.
/// </summary>
/// <remarks>
/// <para>
/// Nacos does not push MCP changes. A change is discovered by re-reading the server on an interval
/// and comparing the answer with the copy that was cached, which is why a subscription is served by
/// a refresh loop per subscribed version rather than by a stream of server-initiated events. The
/// loop runs only while something is subscribed, and every copy it takes is also what a later read
/// of the same version answers with.
/// </para>
/// <para>
/// A version is keyed as <c>{name}::{version}</c>, and a subscription that names no version is keyed
/// as <c>{name}::latest</c>. The latest version is additionally stored under the <c>latest</c> key
/// whenever the server flags it as latest, which is what lets a subscription without a version be
/// answered from the cache instead of by a round trip.
/// </para>
/// <para>
/// The comparison is made over the serialized detail rather than over a field list, so a change to
/// any part of the published server — including the nested tool and resource specifications that
/// Nacos stores verbatim — is seen. Both sides of the comparison came from the server, so the
/// representation is stable and a byte comparison is a faithful "did anything change".
/// </para>
/// </remarks>
internal sealed class NacosMcpServerCache : IAsyncDisposable
{
    /// <summary>The version label a subscription without a version resolves to.</summary>
    public const string LatestVersion = "latest";

    /// <summary>The error code Nacos uses for a server or version that does not exist.</summary>
    private const int NotFoundErrorCode = 404;

    private readonly Func<string, string?, CancellationToken, Task<NacosMcpServerDetail>> _query;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly NacosAISnapshot<NacosMcpServerDetail>? _snapshot;

    private readonly ConcurrentDictionary<string, NacosMcpServerDetail> _servers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _refreshes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);

    /// <summary>
    /// Serializes storing a detail, writing its snapshot and announcing it.
    /// </summary>
    /// <remarks>
    /// A subscription's opening read takes its first element from the cache, and a change is
    /// announced to the consumers that are already past that first element. Both sides take this
    /// gate, so a change that is being stored while an opening read is in flight is either already
    /// part of the state this read takes or is announced after the consumer is past its first
    /// element — never both, which would hand the subscriber the same detail twice.
    /// </remarks>
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosMcpServerCache"/> class.
    /// </summary>
    /// <param name="query">Reads one version of a server from the registry.</param>
    /// <param name="refreshInterval">How long to wait between refreshes of a subscribed version.</param>
    /// <param name="timeProvider">The clock the refresh interval is measured with.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="snapshot">
    /// The disk failover cache, or <see langword="null"/> when details are kept in memory only.
    /// </param>
    public NacosMcpServerCache(
        Func<string, string?, CancellationToken, Task<NacosMcpServerDetail>> query,
        TimeSpan refreshInterval,
        TimeProvider timeProvider,
        ILogger logger,
        NacosAISnapshot<NacosMcpServerDetail>? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _query = query;
        _refreshInterval = refreshInterval;
        _timeProvider = timeProvider;
        _logger = logger;
        _snapshot = snapshot;
    }

    /// <summary>
    /// Subscribes to one version of an MCP server.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <param name="ct">A token that ends the subscription.</param>
    /// <returns>
    /// The current detail as the first element, then the detail again on every change. A server that
    /// has not been released yet yields nothing until it appears.
    /// </returns>
    /// <remarks>
    /// Leaving the enumeration ends the subscription, and the last subscriber of a version stops its
    /// refresh loop. A first read that reports the server as missing is not an error here: the
    /// subscription stays open and announces the server when it is released.
    /// </remarks>
    public async IAsyncEnumerable<NacosMcpServerDetail> SubscribeAsync(
        string name,
        string? version,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string key = Key(name, version);

        var queue = Channel.CreateBounded<NacosMcpServerDetail>(
            new BoundedChannelOptions(16)
            {
                // The newest detail is the one that matters, so an overwhelmed consumer loses
                // intermediate states rather than stalling the shared refresh loop.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        Subscription subscription = _subscriptions.GetOrAdd(key, _ => new Subscription());
        Consumer consumer = subscription.Attach(queue.Writer);

        try
        {
            EnsureRefresh(key, name, version);

            // The cached copy answers a subscription that is already current, so the first read only
            // goes to the server when nothing was cached for this version yet.
            NacosMcpServerDetail? current = Lookup(key);

            if (current is null)
            {
                current = await TryQueryAsync(name, version, ct).ConfigureAwait(false);
            }

            // The first element is taken from the cache under the gate that stores and announces a
            // change. A change that is being stored while this read is in flight is therefore either
            // already part of the state taken here or announced after this consumer is past its
            // first element, never both.
            NacosMcpServerDetail? seed;

            await _publishGate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                consumer.MarkSeeded();
                seed = Lookup(key) ?? current;
            }
            finally
            {
                _publishGate.Release();
            }

            if (seed is not null)
            {
                yield return seed;
            }

            await foreach (NacosMcpServerDetail change in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
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
    /// Reads a cached server version.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <returns>The cached detail, or <see langword="null"/> when this version was never read.</returns>
    public NacosMcpServerDetail? Lookup(string name, string? version) => Lookup(Key(name, version));

    /// <summary>
    /// Seeds a version from its local snapshot, when the client caches on disk.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns><see langword="true"/> when the version has a copy, either already or now.</returns>
    /// <remarks>
    /// Called before a subscription opens when the server could not be reached, so the subscription
    /// starts from the copy it last had instead of failing and reads the server when it comes back.
    /// </remarks>
    public async Task<bool> RestoreAsync(string name, string? version, CancellationToken ct)
    {
        string key = Key(name, version);

        if (Lookup(key) is not null)
        {
            return true;
        }

        NacosMcpServerDetail? restored = await LoadSnapshotAsync(key, ct).ConfigureAwait(false);

        return restored is not null && _servers.TryAdd(key, restored);
    }

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
    /// Builds the key that identifies one version of a server.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <returns>The cache key.</returns>
    private static string Key(string name, string? version)
        => string.Concat(name, "::", string.IsNullOrWhiteSpace(version) ? LatestVersion : version);

    /// <summary>
    /// Reads a cached detail by key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <returns>The cached detail, or <see langword="null"/>.</returns>
    private NacosMcpServerDetail? Lookup(string key)
        => _servers.TryGetValue(key, out NacosMcpServerDetail? detail) ? detail : null;

    /// <summary>
    /// Reads one version from the registry, storing and announcing what it returns.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>The detail, or <see langword="null"/> when the server does not know it yet.</returns>
    /// <remarks>
    /// This is also the read that falls back to the local cache: a subscription that restarts while
    /// the control plane is unreachable starts from the copy that was last read from the server
    /// instead of failing.
    /// </remarks>
    private async Task<NacosMcpServerDetail?> TryQueryAsync(string name, string? version, CancellationToken ct)
    {
        try
        {
            NacosMcpServerDetail detail = await _query(name, version, ct).ConfigureAwait(false);
            await ProcessAsync(detail, ct).ConfigureAwait(false);
            return detail;
        }
        catch (NacosServerException ex) when (ex.ErrorCode == NotFoundErrorCode)
        {
            // The server exists but this version does not (yet). A subscription is meant to survive
            // that, so the refresh loop keeps looking instead of the subscription failing.
            return null;
        }
        catch (NacosException ex) when (ex is NacosConnectionException or NacosTimeoutException)
        {
            string key = Key(name, version);
            NacosMcpServerDetail? restored = await LoadSnapshotAsync(key, ct).ConfigureAwait(false);

            if (restored is null)
            {
                // Nothing was cached either, so there is no answer to give and the caller is told
                // the read failed.
                throw;
            }

            // Seeded into the cache rather than processed: this copy was not read just now, so it
            // is not a change and is not announced to a subscriber that already holds it.
            _servers.TryAdd(key, restored);
            NacosAILog.SnapshotRestored(_logger, NacosAISnapshotStore.McpKind, name, ex.Message);

            return restored;
        }
    }

    /// <summary>
    /// Reads a version's snapshot, when the cache is backed by disk.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>The cached detail, or <see langword="null"/> when there is none to fall back to.</returns>
    private async Task<NacosMcpServerDetail?> LoadSnapshotAsync(string key, CancellationToken ct)
    {
        NacosAISnapshot<NacosMcpServerDetail>? snapshot = _snapshot;

        if (snapshot is null)
        {
            return null;
        }

        return await snapshot.Store.LoadAsync(snapshot.Kind, key, snapshot.Type, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a version's snapshot, when the cache is backed by disk.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="detail">The detail the server returned.</param>
    /// <param name="ct">A token that cancels the write.</param>
    /// <returns>A task that completes once the snapshot is written, or its failure was reported.</returns>
    private async Task SaveSnapshotAsync(string key, NacosMcpServerDetail detail, CancellationToken ct)
    {
        NacosAISnapshot<NacosMcpServerDetail>? snapshot = _snapshot;

        if (snapshot is null)
        {
            return;
        }

        // The store reports a failed write itself; a cache is an optimisation and losing it must
        // not fail a caller.
        await snapshot.Store
            .SaveAsync(snapshot.Kind, key, detail, snapshot.Type, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Stores a detail and announces it when it differs from the copy that was cached.
    /// </summary>
    /// <param name="detail">The detail the server returned.</param>
    /// <param name="ct">A token that cancels the snapshot write.</param>
    /// <returns>A task that completes once the detail, its snapshot and its subscribers are updated.</returns>
    /// <remarks>
    /// A detail that carries no name or version cannot be keyed, so it is dropped rather than cached
    /// under a key nothing can look up. The store, the snapshot and the announcement are one step:
    /// a subscriber that is handed a detail has the matching failover copy on disk by then.
    /// </remarks>
    private async Task ProcessAsync(NacosMcpServerDetail detail, CancellationToken ct)
    {
        string? name = detail.Name;
        string? version = detail.VersionDetail?.Version ?? detail.Version;

        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
        {
            return;
        }

        string versionedKey = Key(name, version);
        string latestKey = Key(name, null);
        bool isLatest = detail.VersionDetail?.IsLatest == true;

        await _publishGate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (await CompareAndStoreAsync(versionedKey, detail, ct).ConfigureAwait(false))
            {
                Publish(versionedKey, detail);
            }

            if (isLatest && await CompareAndStoreAsync(latestKey, detail, ct).ConfigureAwait(false))
            {
                Publish(latestKey, detail);
            }
        }
        finally
        {
            _publishGate.Release();
        }
    }

    /// <summary>
    /// Stores a detail under one key, reporting whether it differs from what was there.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="detail">The detail to store.</param>
    /// <param name="ct">A token that cancels the snapshot write.</param>
    /// <returns><see langword="true"/> when the stored detail changed.</returns>
    private async Task<bool> CompareAndStoreAsync(string key, NacosMcpServerDetail detail, CancellationToken ct)
    {
        bool changed = !_servers.TryGetValue(key, out NacosMcpServerDetail? previous) || !IsSame(previous, detail);

        _servers[key] = detail;

        if (changed)
        {
            await SaveSnapshotAsync(key, detail, ct).ConfigureAwait(false);
        }

        return changed;
    }

    /// <summary>
    /// Compares two details by their serialized form.
    /// </summary>
    /// <param name="left">The cached detail.</param>
    /// <param name="right">The detail just read.</param>
    /// <returns><see langword="true"/> when the two describe the same server version.</returns>
    private static bool IsSame(NacosMcpServerDetail left, NacosMcpServerDetail right)
        => string.Equals(
            JsonSerializer.Serialize(left, NacosJsonContext.Default.NacosMcpServerDetail),
            JsonSerializer.Serialize(right, NacosJsonContext.Default.NacosMcpServerDetail),
            StringComparison.Ordinal);

    /// <summary>
    /// Delivers a changed detail to every subscriber of one key.
    /// </summary>
    /// <param name="key">The cache key the change belongs to.</param>
    /// <param name="detail">The changed detail.</param>
    private void Publish(string key, NacosMcpServerDetail detail)
    {
        if (!_subscriptions.TryGetValue(key, out Subscription? subscription))
        {
            return;
        }

        subscription.Publish(detail);

        NacosAILog.McpServerChanged(
            _logger,
            detail.Name ?? string.Empty,
            detail.VersionDetail?.Version ?? detail.Version ?? string.Empty);
    }

    /// <summary>
    /// Starts a refresh loop for a subscribed version.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    private void EnsureRefresh(string key, string name, string? version)
    {
        CancellationTokenSource source = _refreshes.GetOrAdd(key, _ => StartRefresh(name, version));

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
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <returns>The source the loop is cancelled with.</returns>
    private CancellationTokenSource StartRefresh(string name, string? version)
    {
        var source = new CancellationTokenSource();

        _ = Task.Run(() => RefreshAsync(name, version, source), CancellationToken.None);

        return source;
    }

    /// <summary>
    /// Stops the refresh loop of a version.
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
    /// Re-reads a subscribed version on an interval until it is cancelled.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <param name="source">The source that ends the loop.</param>
    /// <returns>A task that completes once the loop ends.</returns>
    /// <remarks>
    /// The loop is the only thing that makes a subscription fire, because Nacos has no MCP change
    /// push. A read that fails leaves the cached copy in place — a subscription outlives an outage
    /// rather than failing with it — and a server that is not there yet is not a failure.
    /// </remarks>
    private async Task RefreshAsync(string name, string? version, CancellationTokenSource source)
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
                    NacosMcpServerDetail detail =
                        await _query(name, version, CancellationToken.None).ConfigureAwait(false);

                    await ProcessAsync(detail, CancellationToken.None).ConfigureAwait(false);
                }
                catch (NacosServerException ex) when (ex.ErrorCode == NotFoundErrorCode)
                {
                    // Not released yet, or withdrawn. The loop keeps looking.
                }
                catch (Exception ex)
                {
                    if (!_disposed)
                    {
                        NacosAILog.McpServerRefreshFailed(_logger, name, version, ex.Message);
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
    /// One version of a server as a set of live consumers.
    /// </summary>
    /// <remarks>
    /// Several consumers can subscribe to the same version; they share one cache entry and one
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
        public Consumer Attach(ChannelWriter<NacosMcpServerDetail> writer)
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
        /// Queues a changed detail for every consumer.
        /// </summary>
        /// <param name="detail">The changed detail.</param>
        public void Publish(NacosMcpServerDetail detail)
        {
            Consumer[] consumers;

            lock (_consumers)
            {
                consumers = [.. _consumers];
            }

            foreach (Consumer consumer in consumers)
            {
                consumer.Deliver(detail);
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
    /// One reader of a subscribed version.
    /// </summary>
    /// <remarks>
    /// The delivery state belongs to the reader rather than to the version, because a consumer that
    /// subscribes to an already-cached version must still be handed that first detail even though an
    /// earlier consumer of the same version already was.
    /// </remarks>
    private sealed class Consumer
    {
        private int _seeded;

        /// <summary>
        /// Initializes a new instance of the <see cref="Consumer"/> class.
        /// </summary>
        /// <param name="writer">The queue this consumer reads.</param>
        public Consumer(ChannelWriter<NacosMcpServerDetail> writer) => Writer = writer;

        /// <summary>Gets the queue this consumer reads.</summary>
        public ChannelWriter<NacosMcpServerDetail> Writer { get; }

        /// <summary>
        /// Records that the consumer's first element was taken from the cache.
        /// </summary>
        public void MarkSeeded() => Volatile.Write(ref _seeded, 1);

        /// <summary>
        /// Queues a changed detail for a consumer that is past its first element.
        /// </summary>
        /// <param name="detail">The changed detail.</param>
        /// <remarks>
        /// Called under the cache's publish gate. A consumer that has not taken its first element
        /// yet is skipped: its opening read takes the detail from the cache instead, so the same
        /// change would otherwise arrive twice.
        /// </remarks>
        public void Deliver(NacosMcpServerDetail detail)
        {
            if (Volatile.Read(ref _seeded) != 0)
            {
                Writer.TryWrite(detail);
            }
        }
    }
}
