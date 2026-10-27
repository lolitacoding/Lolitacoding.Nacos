using System.Collections.Concurrent;
using System.Threading.Channels;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Config;

/// <summary>
/// The default <see cref="INacosConfigClient"/> over the Nacos gRPC transport.
/// </summary>
/// <remarks>
/// <para>
/// Reads go through the transport, which makes them subject to reconnect; writes do too. Local
/// snapshots are written on every successful read so that a later outage can be served from disk.
/// </para>
/// <para>
/// Listener registrations are held here rather than on the connection because they are
/// configuration-level state: the connection is torn down and rebuilt across reconnects, and the
/// server discards the registrations with it, so this client replays them on the connection's
/// reconnect event.
/// </para>
/// </remarks>
internal sealed class NacosConfigClient : INacosConfigClient, IDisposable
{
    /// <summary>The code reported when the server cannot answer a request at all.</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The code the server reports when its pattern limit is reached.</summary>
    private const int PatternOverLimitErrorCode = 50310;

    /// <summary>The code the server reports when a pattern matches too many configurations.</summary>
    private const int MatchCountOverLimitErrorCode = 50311;

    private readonly NacosOptions _options;
    private readonly NacosGrpcConnection _connection;
    private readonly NacosConfigSnapshotStore? _snapshots;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NacosConfigClient> _logger;

    /// <summary>
    /// Registrations keyed by configuration, so a second listener for the same configuration
    /// shares one server-side registration rather than adding a duplicate.
    /// </summary>
    private readonly ConcurrentDictionary<string, ListenerRegistration> _listeners = new(StringComparer.Ordinal);

    /// <summary>
    /// Fuzzy watch registrations keyed by pattern, so every subscription to the same pattern shares
    /// one server-side watch and therefore one diff stream.
    /// </summary>
    private readonly ConcurrentDictionary<string, FuzzyWatchRegistration> _fuzzyWatches =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Serialises fuzzy watch creation, subscription changes and cancellation.
    /// </summary>
    /// <remarks>
    /// These three mutate the same registration set, and a cancellation that raced an addition
    /// could leave a server-side watch with no client-side owner — a registration the server keeps
    /// pushing to and nobody can cancel. It is a semaphore rather than a lock because registering
    /// a watch is an awaited round trip and must happen inside the critical section to close that
    /// race.
    /// </remarks>
    private readonly SemaphoreSlim _fuzzyWatchSync = new(1, 1);

    /// <summary>The timer that periodically re-sends every watch, or <see langword="null"/>.</summary>
    private ITimer? _fuzzyWatchResyncTimer;

    /// <summary>
    /// The filter chain in execution order.
    /// </summary>
    /// <remarks>
    /// Replaced as a whole under <see cref="_filterSync"/> rather than mutated in place, so a
    /// publish or read that is already running keeps a stable view while a filter is being added.
    /// </remarks>
    private volatile INacosConfigFilter[] _filters = [];

    /// <summary>Serialises additions to the filter chain.</summary>
    private readonly object _filterSync = new();

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigClient"/> class.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <param name="connection">The transport to use.</param>
    /// <param name="timeProvider">The clock used for timestamps.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="snapshots">
    /// The snapshot store, or <see langword="null"/> when local caching is disabled.
    /// </param>
    public NacosConfigClient(
        NacosOptions options,
        NacosGrpcConnection connection,
        TimeProvider timeProvider,
        ILogger<NacosConfigClient> logger,
        NacosConfigSnapshotStore? snapshots)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _connection = connection;
        _timeProvider = timeProvider;
        _logger = logger;
        _snapshots = snapshots;

        // The official client registers its encryption filter through a service loader. .NET has
        // no equivalent for arbitrary assemblies, so configuring providers is what seeds the
        // chain; AddConfigFilter remains available for everything else.
        if (options.Config.EncryptionProviders.Count > 0)
        {
            _filters =
            [
                new NacosConfigEncryptionFilter(
                    [.. options.Config.EncryptionProviders], logger),
            ];
        }

        // The server forgets every listener when the connection drops, so they are re-registered
        // here. Without this, a reconnect would silently stop delivering changes.
        _connection.Reconnected += OnReconnectedAsync;
    }

    /// <inheritdoc />
    public NacosServerStatus ServerStatus =>
        _connection.State == NacosTransportState.Connected
            ? NacosServerStatus.Up
            : NacosServerStatus.Down;

    /// <inheritdoc />
    public void AddConfigFilter(INacosConfigFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(filter.Name))
        {
            throw new ArgumentException(
                "A configuration filter must expose a non-empty Name.", nameof(filter));
        }

        lock (_filterSync)
        {
            INacosConfigFilter[] current = _filters;

            foreach (INacosConfigFilter existing in current)
            {
                if (string.Equals(existing.Name, filter.Name, StringComparison.Ordinal))
                {
                    NacosConfigLog.ConfigFilterDuplicateIgnored(_logger, filter.Name);
                    return;
                }
            }

            // The new filter goes before the first one with a strictly greater order, so filters
            // with equal orders keep their insertion order — the same rule the Java chain applies.
            int index = 0;
            while (index < current.Length && current[index].Order <= filter.Order)
            {
                index++;
            }

            var ordered = new INacosConfigFilter[current.Length + 1];
            Array.Copy(current, ordered, index);
            ordered[index] = filter;
            Array.Copy(current, index, ordered, index + 1, current.Length - index);

            _filters = ordered;
        }
    }

    /// <inheritdoc />
    public async Task<NacosConfigResult> GetConfigAsync(
        NacosConfigKey key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosConfigKey resolved = key.WithDefaultNamespace(_options.Namespace);

        NacosConfigResult result;

        try
        {
            result = await QueryAsync(resolved, localMd5: null, ct).ConfigureAwait(false);

            await UpdateSnapshotAsync(resolved, result, ct).ConfigureAwait(false);
        }
        catch (NacosException ex) when (ex is NacosConnectionException or NacosTimeoutException)
        {
            // The control plane is unreachable. A snapshot is the difference between a process
            // that survives the outage and one that cannot start, so it is used here.
            NacosConfigResult? cached = await ReadSnapshotAsync(resolved, ct).ConfigureAwait(false);

            if (cached is null)
            {
                throw;
            }

            NacosConfigLog.ServedFromSnapshot(_logger, resolved.ToString(), ex.Message);
            result = cached;
        }

        // Filtering happens after the snapshot is written: the snapshot must keep the value as the
        // server stores it, key included, so a later failover can run the same filters over it.
        return await FilterReadAsync(result, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PublishConfigAsync(
        NacosConfigKey key,
        string content,
        string? contentType = null,
        string? casMd5 = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosConfigKey resolved = key.WithDefaultNamespace(_options.Namespace);
        string resolvedContentType = contentType ?? _options.Config.DefaultContentType;

        var context = new NacosConfigFilterContext(
            resolved,
            NacosConfigFilterStage.Publish,
            content,
            resolvedContentType);

        await ApplyFiltersAsync(context, ct).ConfigureAwait(false);

        if (context.Content is null)
        {
            // A filter that removes the content would otherwise publish an empty document, which
            // is virtually never what the caller meant.
            NacosConfigLog.FilterRemovedPublishedContent(_logger, resolved.ToString());

            throw new InvalidOperationException(string.Concat(
                "A configuration filter removed the content of '", resolved.ToString(),
                "' while publishing; a filter must not publish an empty value."));
        }

        string publishedContent = context.Content;
        string publishedContentType = context.ContentType ?? resolvedContentType;

        var request = new ConfigPublishRequest
        {
            DataId = resolved.DataId,
            Group = resolved.Group,
            Tenant = resolved.Namespace,
            Content = publishedContent,
            CasMd5 = casMd5,
            // The content type and the encrypted data key are not fields of their own on the wire:
            // the server reads them out of the free-form addition map.
            AdditionMap = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = publishedContentType,
                ["encryptedDataKey"] = context.EncryptedDataKey ?? string.Empty,
            },
        };

        await _connection.RequestAsync<ConfigPublishResponse>(request, ct).ConfigureAwait(false);

        // The published value is the newest known one, so the snapshot is updated immediately
        // rather than waiting for the next read. It keeps the filtered (for an encrypted
        // configuration, ciphertext) content and the key, because that is the pair the server
        // stored and therefore the pair a later failover has to filter again.
        await UpdateSnapshotAsync(
            resolved,
            new NacosConfigResult(
                resolved,
                publishedContent,
                NacosMd5.Compute(publishedContent),
                publishedContentType,
                _timeProvider.GetUtcNow(),
                fromLocalCache: false,
                context.EncryptedDataKey),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveConfigAsync(NacosConfigKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosConfigKey resolved = key.WithDefaultNamespace(_options.Namespace);

        var request = new ConfigRemoveRequest
        {
            DataId = resolved.DataId,
            Group = resolved.Group,
            Tenant = resolved.Namespace,
        };

        await _connection.RequestAsync<ConfigRemoveResponse>(request, ct).ConfigureAwait(false);

        // A deleted value must not survive on disk, or the next outage would resurrect it.
        _snapshots?.Remove(resolved);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosConfigChange> ListenAsync(
        NacosConfigKey key,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosConfigKey resolved = key.WithDefaultNamespace(_options.Namespace);

        // The current value is published first so a consumer can initialise from the same code
        // path that handles later changes.
        NacosConfigResult initial = await GetConfigAsync(resolved, ct).ConfigureAwait(false);
        yield return new NacosConfigChange(resolved, initial);

        // A bounded channel decouples the server's pushes from the consumer's pace. Without it, a
        // slow listener would stall the shared read loop and delay every other listener.
        var queue = System.Threading.Channels.Channel.CreateBounded<NacosConfigChange>(
            new System.Threading.Channels.BoundedChannelOptions(16)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        await using ListenerHandle handle = await RegisterAsync(
            resolved,
            (change, _) =>
            {
                // Dropping the oldest change is preferable to blocking: the newest value is the
                // one that matters, and the consumer re-reads on any notification anyway.
                queue.Writer.TryWrite(change);
                return Task.CompletedTask;
            },
            ct).ConfigureAwait(false);

        await foreach (NacosConfigChange change in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return change;
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AddListenerAsync(
        NacosConfigKey key,
        Func<NacosConfigChange, CancellationToken, Task> onChange,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(onChange);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosConfigKey resolved = key.WithDefaultNamespace(_options.Namespace);

        return await RegisterAsync(resolved, onChange, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosConfigWatchEvent> WatchAsync(
        string dataIdPattern,
        string groupPattern,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Building the pattern validates both arguments; the namespace is what the client is
        // configured for, because one fuzzy watch cannot span namespaces.
        string pattern = NacosFuzzyPattern.Generate(_options.Namespace, groupPattern, dataIdPattern);

        FuzzyWatchSubscription subscription =
            await SubscribeFuzzyWatchAsync(pattern, ct).ConfigureAwait(false);

        try
        {
            await foreach (NacosConfigWatchEvent item in
                subscription.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            await UnsubscribeFuzzyWatchAsync(pattern, subscription).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<NacosConfigWatchEvent> WatchAsync(
        string groupPattern,
        CancellationToken ct) =>
        WatchAsync("*", groupPattern, ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _connection.Reconnected -= OnReconnectedAsync;

        // Registrations hold only a list of callbacks, so there is nothing to dispose; clearing
        // the collection releases them.
        _listeners.Clear();

        // A subscription is ended by completing its channel rather than by dropping it: a consumer
        // awaiting the next event would otherwise stay parked until its own token was cancelled.
        // Taking the gate first keeps a registration from being created behind the sweep.
        await _fuzzyWatchSync.WaitAsync().ConfigureAwait(false);
        try
        {
            _fuzzyWatchResyncTimer?.Dispose();
            _fuzzyWatchResyncTimer = null;

            foreach (FuzzyWatchRegistration registration in _fuzzyWatches.Values)
            {
                lock (registration.Sync)
                {
                    foreach (FuzzyWatchSubscription subscription in registration.Subscriptions)
                    {
                        subscription.Queue.Writer.TryComplete();
                    }

                    registration.Subscriptions.Clear();
                }
            }

            _fuzzyWatches.Clear();
        }
        finally
        {
            _fuzzyWatchSync.Release();
        }
    }

    /// <summary>
    /// Releases the client synchronously, for a container that disposes synchronously.
    /// </summary>
    /// <remarks>
    /// The client is normally disposed by the shared <c>NacosClient</c> it belongs to; this is an
    /// idempotent bridge so the container's own synchronous disposal does not fail.
    /// </remarks>
    void IDisposable.Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Sends a configuration query and interprets the response.
    /// </summary>
    /// <param name="key">The resolved configuration coordinates.</param>
    /// <param name="localMd5">
    /// The hash the caller already holds, enabling the server's "not modified" fast path.
    /// </param>
    /// <param name="ct">A token that cancels the query.</param>
    /// <returns>The configuration result.</returns>
    /// <exception cref="NacosServerException">The content failed its integrity check.</exception>
    private async Task<NacosConfigResult> QueryAsync(
        NacosConfigKey key,
        string? localMd5,
        CancellationToken ct)
    {
        var request = new ConfigQueryRequest
        {
            DataId = key.DataId,
            Group = key.Group,
            Tenant = key.Namespace,
        };

        if (!string.IsNullOrEmpty(localMd5))
        {
            request.LocalMd5 = localMd5;
        }

        ConfigQueryResponse response;

        try
        {
            response = await _connection
                .RequestAsync<ConfigQueryResponse>(request, ct)
                .ConfigureAwait(false);
        }
        catch (NacosServerException ex) when (ex.ErrorCode == ConfigQueryResponse.ConfigNotFound)
        {
            // "Not found" is an ordinary answer for a configuration that has not been created yet,
            // not a transport failure, so it must not surface as an exception.
            NacosConfigLog.NotFound(_logger, key.ToString());
            return new NacosConfigResult(
                key, content: null, md5: null, contentType: null,
                DateTimeOffset.MinValue, fromLocalCache: false);
        }

        if (string.IsNullOrEmpty(response.Content))
        {
            return new NacosConfigResult(
                key, content: null, md5: response.Md5, contentType: response.ContentType,
                ToTimestamp(response.LastModified), fromLocalCache: false,
                encryptedDataKey: response.EncryptedDataKey);
        }

        VerifyMd5(key, response.Content, response.Md5);

        return new NacosConfigResult(
            key,
            response.Content,
            response.Md5 ?? NacosMd5.Compute(response.Content),
            response.ContentType,
            ToTimestamp(response.LastModified),
            fromLocalCache: false,
            encryptedDataKey: response.EncryptedDataKey);
    }

    /// <summary>
    /// Verifies that content matches the hash the server reported.
    /// </summary>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="content">The content that arrived.</param>
    /// <param name="md5">The hash the server reported.</param>
    /// <exception cref="NacosServerException">
    /// The content does not hash to the reported value.
    /// </exception>
    /// <remarks>
    /// A mismatch means the content was corrupted or truncated in transit. Applying it would
    /// silently configure the application with wrong values, which is far worse than failing
    /// loudly, so this is an error rather than a warning.
    /// </remarks>
    private void VerifyMd5(NacosConfigKey key, string content, string? md5)
    {
        if (!_options.Config.VerifyMd5 || string.IsNullOrEmpty(md5))
        {
            return;
        }

        string actual = NacosMd5.Compute(content);

        if (!NacosMd5.AreEqual(md5, actual))
        {
            NacosConfigLog.Md5Mismatch(_logger, key.ToString(), md5, actual);

            throw new NacosServerException(
                0,
                string.Concat(
                    "The content of configuration '", key.ToString(),
                    "' failed MD5 verification, so it was rejected rather than applied."));
        }
    }

    /// <summary>
    /// Runs the filter chain over a value.
    /// </summary>
    /// <param name="context">The mutable value.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes once every filter ran.</returns>
    private async ValueTask ApplyFiltersAsync(
        NacosConfigFilterContext context,
        CancellationToken ct)
    {
        foreach (INacosConfigFilter filter in _filters)
        {
            await filter.InvokeAsync(context, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the read side of the filter chain over a raw value.
    /// </summary>
    /// <param name="raw">The value as the server or the snapshot stored it.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>The value a caller should see.</returns>
    /// <remarks>
    /// The hash stays the one of the stored value: it is what the client compares to decide whether
    /// a notification is new, and a filter that changed the content would otherwise make every
    /// notification look like a change. A missing value is not filtered, because there is nothing
    /// to transform and a filter is not the place to invent content for a configuration that does
    /// not exist.
    /// </remarks>
    private async ValueTask<NacosConfigResult> FilterReadAsync(
        NacosConfigResult raw,
        CancellationToken ct)
    {
        INacosConfigFilter[] filters = _filters;

        if (filters.Length == 0 || raw.Content is null)
        {
            return raw;
        }

        var context = new NacosConfigFilterContext(
            raw.Key,
            NacosConfigFilterStage.Read,
            raw.Content,
            raw.ContentType,
            raw.EncryptedDataKey,
            raw.Md5,
            raw.FromLocalCache);

        await ApplyFiltersAsync(context, ct).ConfigureAwait(false);

        if (ReferenceEquals(context.Content, raw.Content)
            && string.Equals(context.ContentType, raw.ContentType, StringComparison.Ordinal)
            && string.Equals(context.EncryptedDataKey, raw.EncryptedDataKey, StringComparison.Ordinal))
        {
            // A filter that only observes the value should not force a copy on every read.
            return raw;
        }

        return new NacosConfigResult(
            raw.Key,
            context.Content,
            raw.Md5,
            context.ContentType,
            raw.LastModified,
            raw.FromLocalCache,
            context.EncryptedDataKey);
    }

    /// <summary>
    /// Registers a listener, sharing the server-side registration when one already exists.
    /// </summary>
    /// <param name="key">The resolved configuration coordinates.</param>
    /// <param name="onChange">The callback to invoke.</param>
    /// <param name="ct">A token that cancels registration.</param>
    /// <returns>A handle that removes the callback when disposed.</returns>
    private async Task<ListenerHandle> RegisterAsync(
        NacosConfigKey key,
        Func<NacosConfigChange, CancellationToken, Task> onChange,
        CancellationToken ct)
    {
        string id = key.ToString();

        ListenerRegistration registration = _listeners.GetOrAdd(
            id,
            _ => new ListenerRegistration(key));

        bool isFirst;
        lock (registration.Sync)
        {
            isFirst = registration.Count == 0;
            registration.Add(onChange);
        }

        if (isFirst)
        {
            try
            {
                await SendListenRequestAsync([key], listen: true, ct).ConfigureAwait(false);
            }
            catch
            {
                // Registration failed, so the entry must not be left behind claiming a
                // subscription the server never accepted.
                lock (registration.Sync)
                {
                    registration.Remove(onChange);
                }

                if (registration.Count == 0)
                {
                    _listeners.TryRemove(id, out _);
                }

                throw;
            }
        }

        // The current value is delivered immediately so a subscriber does not have to issue a
        // separate read to learn where it is starting from.
        NacosConfigResult current = await GetConfigAsync(key, ct).ConfigureAwait(false);

        // Recording the hash that was just read is what lets the server answer "nothing changed for
        // you" instead of listing this configuration at the next reconnect, and what keeps a
        // spurious or repeated notification from being delivered as a change.
        registration.LastMd5 = current.Md5;

        await InvokeSafelyAsync(registration, new NacosConfigChange(key, current), ct).ConfigureAwait(false);

        return new ListenerHandle(this, id, registration, onChange);
    }

    /// <summary>
    /// Invokes every callback registered for a configuration.
    /// </summary>
    /// <param name="registration">The registration to notify.</param>
    /// <param name="change">The change to deliver.</param>
    /// <param name="ct">A token that cancels the notification.</param>
    /// <returns>A task that completes once every callback ran.</returns>
    private async Task InvokeSafelyAsync(
        ListenerRegistration registration,
        NacosConfigChange change,
        CancellationToken ct)
    {
        Func<NacosConfigChange, CancellationToken, Task>[] callbacks;
        lock (registration.Sync)
        {
            callbacks = [.. registration.Callbacks];
        }

        foreach (Func<NacosConfigChange, CancellationToken, Task> callback in callbacks)
        {
            try
            {
                await callback(change, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One faulty listener must not stop the others, and must never tear down the
                // shared connection.
                NacosConfigLog.ListenerFailed(_logger, change.Key.ToString(), ex.Message);
            }
        }
    }

    /// <summary>
    /// Handles a change notification pushed by the server.
    /// </summary>
    /// <param name="notification">The notification, which carries only coordinates.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once listeners have been notified.</returns>
    internal async Task OnConfigChangedAsync(
        ConfigChangeNotifyRequest notification,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(notification.DataId) || string.IsNullOrEmpty(notification.Group))
        {
            return;
        }

        var key = new NacosConfigKey(
            notification.DataId,
            notification.Group,
            string.IsNullOrEmpty(notification.Tenant) ? _options.Namespace : notification.Tenant);

        if (!_listeners.TryGetValue(key.ToString(), out ListenerRegistration? registration))
        {
            // A notification for something nobody is listening to: possible after a listener was
            // removed while a push was already in flight.
            return;
        }

        NacosConfigResult result;
        try
        {
            // The push carries no content, so the value must be fetched. Passing the known hash
            // lets the server answer "not modified" when the notification is spurious.
            result = await QueryAsync(key, registration.LastMd5, ct).ConfigureAwait(false);
        }
        catch (NacosException ex)
        {
            NacosConfigLog.ListenerRestoreFailed(_logger, ex.Message);
            return;
        }

        // Duplicate and spurious notifications are normal — the server may notify on a change
        // that the client already observed — so an unchanged hash is dropped here rather than
        // reaching user code.
        if (NacosMd5.AreEqual(result.Md5, registration.LastMd5))
        {
            NacosConfigLog.ChangeIgnored(_logger, key.ToString(), result.Md5);
            return;
        }

        string? previous = registration.LastMd5;
        registration.LastMd5 = result.Md5;

        await UpdateSnapshotAsync(key, result, ct).ConfigureAwait(false);

        NacosConfigLog.ConfigChanged(_logger, key.ToString(), previous, result.Md5);

        NacosConfigResult delivered;

        try
        {
            delivered = await FilterReadAsync(result, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The change itself is already recorded, so a filter that fails here must not take down
            // the shared read loop or be retried forever. The consumer keeps the value it had and
            // the next genuine change runs the chain again.
            NacosConfigLog.ConfigFilterFailedOnPush(_logger, key.ToString(), ex.Message);
            return;
        }

        await InvokeSafelyAsync(
            registration,
            new NacosConfigChange(key, delivered) { PreviousMd5 = previous },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-registers every listener and fuzzy watch after the transport reconnected.
    /// </summary>
    /// <param name="state">The state the transport reached.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once registrations are replayed.</returns>
    private async Task OnReconnectedAsync(NacosTransportState state, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        await RestoreListenersAsync(ct).ConfigureAwait(false);
        await RestoreFuzzyWatchesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-registers every exact listener after the transport reconnected.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once registrations are replayed.</returns>
    private async Task RestoreListenersAsync(CancellationToken ct)
    {
        List<NacosConfigKey> keys = [.. _listeners.Values.Select(registration => registration.Key)];
        if (keys.Count == 0)
        {
            return;
        }

        try
        {
            ConfigChangeBatchListenResponse response = await SendListenRequestAsync(keys, listen: true, ct)
                .ConfigureAwait(false);
            NacosConfigLog.ListenersRestored(_logger, keys.Count);

            await ApplyMissedChangesAsync(response, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reconnect itself succeeded, so this is recoverable: the next reconnect retries
            // the replay. Failing here must not take down the connection that was just restored.
            NacosConfigLog.ListenerRestoreFailed(_logger, ex.Message);
        }
    }

    /// <summary>
    /// Delivers the changes the server reported while the client was disconnected.
    /// </summary>
    /// <param name="response">The acknowledgement of the listener replay.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once every reported change was handled.</returns>
    /// <remarks>
    /// A configuration that changed during an outage has nobody to be pushed to: the listener died
    /// with the connection, so the change would sit unnoticed until something else changed the same
    /// configuration. The replay tells the server the hash held before the outage, and its answer
    /// names every configuration that no longer matches it; each one is then handled exactly the
    /// way the corresponding push would have been.
    /// </remarks>
    private async Task ApplyMissedChangesAsync(
        ConfigChangeBatchListenResponse response,
        CancellationToken ct)
    {
        if (response.ChangedConfigs is not { Count: > 0 } changed)
        {
            return;
        }

        NacosConfigLog.ChangesMissedWhileAway(_logger, changed.Count);

        foreach (ConfigContext context in changed)
        {
            if (string.IsNullOrEmpty(context.DataId) || string.IsNullOrEmpty(context.Group))
            {
                continue;
            }

            var key = new NacosConfigKey(
                context.DataId,
                context.Group,
                string.IsNullOrEmpty(context.Tenant) ? _options.Namespace : context.Tenant);

            try
            {
                // The same path a push takes: re-read with the hash the client holds, and deliver
                // only when the value really differs from it.
                await OnConfigChangedAsync(
                    new ConfigChangeNotifyRequest
                    {
                        DataId = key.DataId,
                        Group = key.Group,
                        Tenant = key.Namespace,
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One configuration that cannot be read must not hide the changes to the others.
                NacosConfigLog.ListenerFailed(_logger, key.ToString(), ex.Message);
            }
        }
    }

    /// <summary>
    /// Registers a subscription to a fuzzy watch, creating the shared server-side watch when it is
    /// the first one for the pattern.
    /// </summary>
    /// <param name="pattern">The resolved pattern.</param>
    /// <param name="ct">A token that cancels registration.</param>
    /// <returns>The subscription, whose channel starts receiving events immediately.</returns>
    /// <exception cref="NacosServerException">
    /// The server states that it does not implement fuzzy watch, or refused the pattern.
    /// </exception>
    private async Task<FuzzyWatchSubscription> SubscribeFuzzyWatchAsync(
        string pattern,
        CancellationToken ct)
    {
        // The ability table only exists once the handshake has happened, so a client that has not
        // connected yet would otherwise skip the refusal below and send the watch anyway. The watch
        // request would establish the connection a moment later, so this costs no extra round trip.
        await _connection.ConnectAsync(ct).ConfigureAwait(false);

        EnsureFuzzyWatchSupported();

        var subscription = new FuzzyWatchSubscription();

        await _fuzzyWatchSync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            FuzzyWatchRegistration registration =
                _fuzzyWatches.GetOrAdd(pattern, static value => new FuzzyWatchRegistration(value));

            bool isFirst;
            lock (registration.Sync)
            {
                isFirst = registration.Subscriptions.Count == 0;
                registration.Subscriptions.Add(subscription);
                ReplayKnownKeysLocked(registration, subscription);
            }

            if (!isFirst)
            {
                // The watch is already installed and synchronised, so the replay above is all this
                // subscriber needs; a second registration would only duplicate the diff stream.
                return subscription;
            }

            StartFuzzyWatchResyncTimer();

            try
            {
                await SendFuzzyWatchRequestAsync(registration, watch: true, ct).ConfigureAwait(false);
            }
            catch
            {
                // The watch was never installed, so it must not stay behind claiming one.
                lock (registration.Sync)
                {
                    registration.Subscriptions.Remove(subscription);
                }

                _fuzzyWatches.TryRemove(pattern, out _);
                StopFuzzyWatchResyncTimerIfIdle();
                throw;
            }

            return subscription;
        }
        finally
        {
            _fuzzyWatchSync.Release();
        }
    }

    /// <summary>
    /// Removes a subscription, cancelling the server-side watch when it was the last one.
    /// </summary>
    /// <param name="pattern">The pattern the subscription belongs to.</param>
    /// <param name="subscription">The subscription to remove.</param>
    /// <returns>A task that completes once the watch is cancelled.</returns>
    private async Task UnsubscribeFuzzyWatchAsync(string pattern, FuzzyWatchSubscription subscription)
    {
        await _fuzzyWatchSync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_fuzzyWatches.TryGetValue(pattern, out FuzzyWatchRegistration? registration))
            {
                return;
            }

            bool isLast;
            lock (registration.Sync)
            {
                registration.Subscriptions.Remove(subscription);
                isLast = registration.Subscriptions.Count == 0;
            }

            if (!isLast)
            {
                return;
            }

            _fuzzyWatches.TryRemove(pattern, out _);
            StopFuzzyWatchResyncTimerIfIdle();

            if (_disposed || _connection.State != NacosTransportState.Connected)
            {
                // Nothing to tell the server: either the client is gone, or the watch already died
                // with the connection.
                return;
            }

            try
            {
                await SendFuzzyWatchRequestAsync(registration, watch: false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (NacosException)
            {
                // The watch is gone locally, which is what matters; the server drops the
                // registration when the connection closes.
            }
        }
        finally
        {
            _fuzzyWatchSync.Release();
        }
    }

    /// <summary>
    /// Fails a fuzzy watch when the server explicitly says it has none.
    /// </summary>
    /// <exception cref="NacosServerException">
    /// The connected server advertises <c>fuzzyWatch = false</c>.
    /// </exception>
    /// <remarks>
    /// An absent key is not a refusal: it means the server did not state the ability, which is what
    /// a handshake without an acknowledgement looks like. Only a stated <see langword="false"/> is
    /// treated as "this server cannot do it".
    /// </remarks>
    private void EnsureFuzzyWatchSupported()
    {
        if (_connection.ServerAbilities.TryGetValue(NacosServerAbility.FuzzyWatch, out bool supported)
            && !supported)
        {
            throw new NacosServerException(
                NotImplementedErrorCode,
                "The connected Nacos server states that it does not implement fuzzy watch, so a "
                + "pattern subscription cannot be installed.");
        }
    }

    /// <summary>
    /// Starts the periodic resynchronisation of every watch.
    /// </summary>
    private void StartFuzzyWatchResyncTimer()
    {
        TimeSpan interval = _options.Config.FuzzyWatchResyncInterval;

        _fuzzyWatchResyncTimer ??= _timeProvider.CreateTimer(
            static state => ((NacosConfigClient)state!).OnFuzzyWatchResyncTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _fuzzyWatchResyncTimer.Change(interval, interval);
    }

    /// <summary>
    /// Stops the periodic resynchronisation when no watch is left.
    /// </summary>
    private void StopFuzzyWatchResyncTimerIfIdle()
    {
        if (_fuzzyWatches.IsEmpty)
        {
            _fuzzyWatchResyncTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Runs one periodic resynchronisation.
    /// </summary>
    private void OnFuzzyWatchResyncTimer() => _ = ResyncFuzzyWatchesAsync();

    /// <summary>
    /// Re-sends every watch so a push lost while the connection looked healthy is repaired.
    /// </summary>
    /// <returns>A task that completes once every watch was re-sent.</returns>
    /// <remarks>
    /// The server only pushes a change once. A push dropped between the server and this client
    /// would otherwise leave the matched set wrong until the next reconnect, so the full diff is
    /// requested on a timer as well.
    /// </remarks>
    private async Task ResyncFuzzyWatchesAsync()
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            await _fuzzyWatchSync.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                foreach (FuzzyWatchRegistration registration in _fuzzyWatches.Values)
                {
                    try
                    {
                        await SendFuzzyWatchRequestAsync(registration, watch: true, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (NacosException ex)
                    {
                        // One pattern that cannot be resynchronised must not stop the others; the
                        // next tick tries again.
                        NacosConfigLog.FuzzyWatchSyncFailed(_logger, registration.Pattern, ex.Message);
                    }
                }
            }
            finally
            {
                _fuzzyWatchSync.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A timer callback has no caller to report to; an escaping exception would be
            // unobserved and could take the process down on finalisation.
            NacosConfigLog.FuzzyWatchSyncFailed(_logger, "<all>", ex.Message);
        }
    }

    /// <summary>
    /// Re-registers every fuzzy watch after the transport reconnected.
    /// </summary>
    /// <param name="ct">A token that cancels the replay.</param>
    /// <returns>A task that completes once every watch was re-sent.</returns>
    private async Task RestoreFuzzyWatchesAsync(CancellationToken ct)
    {
        if (_fuzzyWatches.IsEmpty)
        {
            return;
        }

        await _fuzzyWatchSync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            int count = 0;
            foreach (FuzzyWatchRegistration registration in _fuzzyWatches.Values)
            {
                await SendFuzzyWatchRequestAsync(registration, watch: true, ct).ConfigureAwait(false);
                count++;
            }

            NacosConfigLog.FuzzyWatchesRestored(_logger, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The connection itself is healthy; the next reconnect or the periodic resync repairs
            // whatever did not get re-registered here.
            NacosConfigLog.FuzzyWatchSyncFailed(_logger, "<all>", ex.Message);
        }
        finally
        {
            _fuzzyWatchSync.Release();
        }
    }

    /// <summary>
    /// Sends a watch registration or cancellation.
    /// </summary>
    /// <param name="registration">The watch to send.</param>
    /// <param name="watch">Whether to register or cancel.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the request.</returns>
    /// <exception cref="NacosServerException">
    /// The server refused the pattern because a limit was reached.
    /// </exception>
    private async Task SendFuzzyWatchRequestAsync(
        FuzzyWatchRegistration registration,
        bool watch,
        CancellationToken ct)
    {
        List<string> knownKeys;
        bool initializing;

        lock (registration.Sync)
        {
            knownKeys = [.. registration.KnownKeys];
            initializing = !registration.Initialized;
        }

        var request = new ConfigFuzzyWatchRequest
        {
            GroupKeyPattern = registration.Pattern,

            // The set the client already holds is what makes the answer a diff. It is sent on
            // every request, including a cancellation, exactly as the official client does.
            ReceivedGroupKeys = knownKeys,
            WatchType = watch ? FuzzyWatchProtocol.Watch : FuzzyWatchProtocol.CancelWatch,
            Initializing = initializing,
        };

        try
        {
            await _connection.RequestAsync<ConfigFuzzyWatchResponse>(request, ct).ConfigureAwait(false);
        }
        catch (NacosServerException ex)
            when (ex.ErrorCode is PatternOverLimitErrorCode or MatchCountOverLimitErrorCode)
        {
            NacosConfigLog.FuzzyWatchRefused(_logger, registration.Pattern, ex.ErrorCode);
            throw;
        }
    }

    /// <summary>
    /// Applies a diff pushed by the server.
    /// </summary>
    /// <param name="request">The pushed diff.</param>
    /// <remarks>
    /// Runs on the read loop, so it must not block: every event is written to an unbounded channel
    /// and delivered by the consumer's own enumeration.
    /// </remarks>
    internal void OnFuzzyWatchSync(ConfigFuzzyWatchSyncRequest request)
    {
        if (request.GroupKeyPattern is not { Length: > 0 } pattern
            || !_fuzzyWatches.TryGetValue(pattern, out FuzzyWatchRegistration? registration))
        {
            // A diff for a watch this client does not hold: the server may still have it registered
            // until the cancellation reaches it, so it is ignored rather than treated as an error.
            return;
        }

        lock (registration.Sync)
        {
            if (string.Equals(
                request.SyncType,
                FuzzyWatchProtocol.FinishInitNotify,
                StringComparison.Ordinal))
            {
                MarkInitializedLocked(registration);
                return;
            }

            if (request.Contexts is { Count: > 0 } contexts)
            {
                foreach (ConfigFuzzyWatchContext context in contexts)
                {
                    if (context.GroupKey is not { Length: > 0 } groupKey
                        || !NacosFuzzyPattern.TryParseGroupKey(
                            groupKey,
                            out string dataId,
                            out string group,
                            out string? tenant))
                    {
                        NacosConfigLog.FuzzyWatchMalformedGroupKey(_logger, pattern, context.GroupKey);
                        continue;
                    }

                    var key = new NacosConfigKey(dataId, group, tenant);

                    if (string.Equals(
                        context.ChangedType,
                        ConfigFuzzyWatchChangeType.AddConfig,
                        StringComparison.Ordinal))
                    {
                        if (registration.KnownKeys.Add(groupKey))
                        {
                            DeliverLocked(registration, NacosConfigWatchKind.Added, key, groupKey);
                        }
                    }
                    else if (string.Equals(
                        context.ChangedType,
                        ConfigFuzzyWatchChangeType.DeleteConfig,
                        StringComparison.Ordinal))
                    {
                        if (registration.KnownKeys.Remove(groupKey))
                        {
                            DeliverLocked(registration, NacosConfigWatchKind.Removed, key, groupKey);
                        }
                    }
                    else
                    {
                        NacosConfigLog.FuzzyWatchUnknownChangeType(_logger, pattern, context.ChangedType);
                    }
                }
            }

            // The server ends the initial phase with FINISH_FUZZY_WATCH_INIT_NOTIFY: immediately
            // when the diff is empty, and after the last batch has been acknowledged otherwise. The
            // phase is also ended here at the last batch, so a consumer does not wait for that extra
            // round trip and a lost marker cannot leave it hanging. A marker that arrives afterwards
            // finds every subscription initialised and changes nothing. The one thing this must not
            // do is end before the last batch: a batch is only pushed after the previous one, so
            // receiving the last one means the whole diff has been applied.
            if (string.Equals(request.SyncType, FuzzyWatchProtocol.InitNotify, StringComparison.Ordinal)
                && request.TotalBatch > 0
                && request.CurrentBatch >= request.TotalBatch)
            {
                MarkInitializedLocked(registration);
            }
        }
    }

    /// <summary>
    /// Applies a single-configuration change pushed by the server.
    /// </summary>
    /// <param name="notification">The pushed change.</param>
    internal void OnFuzzyWatchChange(ConfigFuzzyWatchChangeNotifyRequest notification)
    {
        if (notification.GroupKey is not { Length: > 0 } groupKey
            || !NacosFuzzyPattern.TryParseGroupKey(
                groupKey,
                out string dataId,
                out string group,
                out string? tenant))
        {
            NacosConfigLog.FuzzyWatchMalformedGroupKey(_logger, "<push>", notification.GroupKey);
            return;
        }

        var key = new NacosConfigKey(dataId, group, tenant);

        foreach (FuzzyWatchRegistration registration in _fuzzyWatches.Values)
        {
            // The server pushes to every pattern it matched, but routing by the local registrations
            // keeps a push for an unrelated watch from being delivered.
            if (!NacosFuzzyPattern.Matches(registration.Pattern, key.Namespace, key.Group, key.DataId))
            {
                continue;
            }

            lock (registration.Sync)
            {
                if (string.Equals(
                    notification.ChangeType,
                    ConfigFuzzyWatchChangeType.AddConfig,
                    StringComparison.Ordinal))
                {
                    if (registration.KnownKeys.Add(groupKey))
                    {
                        DeliverLocked(registration, NacosConfigWatchKind.Added, key, groupKey);
                    }
                }
                else if (string.Equals(
                    notification.ChangeType,
                    ConfigFuzzyWatchChangeType.DeleteConfig,
                    StringComparison.Ordinal))
                {
                    if (registration.KnownKeys.Remove(groupKey))
                    {
                        DeliverLocked(registration, NacosConfigWatchKind.Removed, key, groupKey);
                    }
                }
                else if (string.Equals(
                    notification.ChangeType,
                    ConfigFuzzyWatchChangeType.ConfigChanged,
                    StringComparison.Ordinal))
                {
                    // The server cannot tell a creation from an update — it reports CONFIG_CHANGED
                    // for both — so the distinction is derived from the set this watch holds. Without
                    // that, a configuration created after the initial sync would never be reported as
                    // added, and its later deletion would be suppressed as a removal of a key the
                    // client never learned about.
                    NacosConfigWatchKind kind = registration.KnownKeys.Add(groupKey)
                        ? NacosConfigWatchKind.Added
                        : NacosConfigWatchKind.Modified;

                    DeliverLocked(registration, kind, key, groupKey);
                }
                else
                {
                    NacosConfigLog.FuzzyWatchUnknownChangeType(
                        _logger, registration.Pattern, notification.ChangeType);
                }
            }
        }
    }

    /// <summary>
    /// Delivers one event to every subscription, suppressing what a subscription already knows.
    /// </summary>
    /// <param name="registration">The watch that produced the event.</param>
    /// <param name="kind">The kind of change.</param>
    /// <param name="key">The configuration.</param>
    /// <param name="groupKey">The wire group key, which is the deduplication identity.</param>
    private void DeliverLocked(
        FuzzyWatchRegistration registration,
        NacosConfigWatchKind kind,
        NacosConfigKey key,
        string groupKey)
    {
        NacosConfigLog.FuzzyWatchEvent(_logger, registration.Pattern, kind, key.ToString());

        foreach (FuzzyWatchSubscription subscription in registration.Subscriptions)
        {
            bool deliver = kind switch
            {
                NacosConfigWatchKind.Added => subscription.KnownKeys.Add(groupKey),
                NacosConfigWatchKind.Removed => subscription.KnownKeys.Remove(groupKey),
                _ => true,
            };

            if (deliver)
            {
                // Unbounded on purpose: dropping an add or a remove would silently corrupt the
                // consumer's view of the matched set, and no later event repairs it.
                subscription.Queue.Writer.TryWrite(
                    new NacosConfigWatchEvent(kind, registration.Pattern, key));
            }
        }
    }

    /// <summary>
    /// Ends the initial phase and reports it to every subscription that has not seen it.
    /// </summary>
    /// <param name="registration">The watch that finished its initial catch-up.</param>
    private void MarkInitializedLocked(FuzzyWatchRegistration registration)
    {
        bool first = !registration.Initialized;
        registration.Initialized = true;

        foreach (FuzzyWatchSubscription subscription in registration.Subscriptions)
        {
            if (subscription.Initialized)
            {
                continue;
            }

            subscription.Initialized = true;
            subscription.Queue.Writer.TryWrite(new NacosConfigWatchEvent(
                NacosConfigWatchKind.InitialSyncCompleted,
                registration.Pattern,
                key: null));
        }

        if (first)
        {
            NacosConfigLog.FuzzyWatchInitialized(
                _logger, registration.Pattern, registration.KnownKeys.Count);
        }
    }

    /// <summary>
    /// Seeds a new subscription with the keys the watch already holds.
    /// </summary>
    /// <param name="registration">The watch being joined.</param>
    /// <param name="subscription">The subscription to seed.</param>
    private static void ReplayKnownKeysLocked(
        FuzzyWatchRegistration registration,
        FuzzyWatchSubscription subscription)
    {
        foreach (string groupKey in registration.KnownKeys)
        {
            if (!NacosFuzzyPattern.TryParseGroupKey(
                groupKey,
                out string dataId,
                out string group,
                out string? tenant))
            {
                continue;
            }

            subscription.KnownKeys.Add(groupKey);
            subscription.Queue.Writer.TryWrite(new NacosConfigWatchEvent(
                NacosConfigWatchKind.Added,
                registration.Pattern,
                new NacosConfigKey(dataId, group, tenant)));
        }

        if (!registration.Initialized)
        {
            return;
        }

        subscription.Initialized = true;
        subscription.Queue.Writer.TryWrite(new NacosConfigWatchEvent(
            NacosConfigWatchKind.InitialSyncCompleted,
            registration.Pattern,
            key: null));
    }

    /// <summary>
    /// Sends a batch listen request that adds or removes registrations.
    /// </summary>
    /// <param name="keys">The configurations to register or unregister.</param>
    /// <param name="listen">Whether to register or unregister.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>
    /// The acknowledgement, which lists the configurations the server holds a different value for
    /// than the client said it had.
    /// </returns>
    private async Task<ConfigChangeBatchListenResponse> SendListenRequestAsync(
        IReadOnlyList<NacosConfigKey> keys,
        bool listen,
        CancellationToken ct)
    {
        var request = new ConfigBatchListenRequest { Listen = listen };

        foreach (NacosConfigKey key in keys)
        {
            // The hash the client already holds is sent so the server can skip notifying about a
            // change the client has already applied.
            string? knownMd5 = null;

            if (_listeners.TryGetValue(key.ToString(), out ListenerRegistration? registration))
            {
                knownMd5 = registration.LastMd5;
            }

            request.ConfigListenContexts.Add(new ConfigListenContext
            {
                DataId = key.DataId,
                Group = key.Group,
                Tenant = key.Namespace,
                Md5 = knownMd5,
            });
        }

        // This goes over the unary RPC, not the stream, despite being listener-related.
        return await _connection
            .RequestAsync<ConfigChangeBatchListenResponse>(request, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a callback, unregistering from the server when the last one goes away.
    /// </summary>
    /// <param name="id">The configuration identity.</param>
    /// <param name="registration">The registration the callback belonged to.</param>
    /// <param name="onChange">The callback to remove.</param>
    /// <returns>A task that completes once the registration is removed.</returns>
    private async Task UnregisterAsync(
        string id,
        ListenerRegistration registration,
        Func<NacosConfigChange, CancellationToken, Task> onChange)
    {
        bool isLast;
        lock (registration.Sync)
        {
            registration.Remove(onChange);
            isLast = registration.Count == 0;
        }

        if (!isLast || !_listeners.TryRemove(id, out _))
        {
            return;
        }

        if (_disposed || _connection.State != NacosTransportState.Connected)
        {
            // Nothing to tell the server: either the client is gone, or the subscription already
            // died with the connection.
            return;
        }

        try
        {
            await SendListenRequestAsync([registration.Key], listen: false, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (NacosException)
        {
            // The subscription is gone locally, which is what matters; the server cleans up the
            // registration when the connection closes.
        }
    }

    private async Task UpdateSnapshotAsync(
        NacosConfigKey key,
        NacosConfigResult result,
        CancellationToken ct)
    {
        if (_snapshots is null)
        {
            return;
        }

        if (result.Content is null)
        {
            // A configuration that no longer exists must not be served from disk later.
            _snapshots.Remove(key);
            return;
        }

        try
        {
            await _snapshots.SaveAsync(
                key, result.Content, result.ContentType, result.EncryptedDataKey, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NacosConfigLog.SnapshotWriteFailed(_logger, key.ToString(), ex.Message);
        }
    }

    private async Task<NacosConfigResult?> ReadSnapshotAsync(NacosConfigKey key, CancellationToken ct)
    {
        if (_snapshots is null)
        {
            return null;
        }

        try
        {
            (string Content, string? ContentType, string? EncryptedDataKey)? snapshot =
                await _snapshots.LoadAsync(key, ct).ConfigureAwait(false);

            if (snapshot is null)
            {
                return null;
            }

            return new NacosConfigResult(
                key,
                snapshot.Value.Content,
                NacosMd5.Compute(snapshot.Value.Content),
                snapshot.Value.ContentType,
                DateTimeOffset.MinValue,
                fromLocalCache: true,
                snapshot.Value.EncryptedDataKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NacosConfigLog.SnapshotReadFailed(_logger, key.ToString(), ex.Message);
            return null;
        }
    }

    private static DateTimeOffset ToTimestamp(long lastModified) =>
        lastModified <= 0
            ? DateTimeOffset.MinValue
            : DateTimeOffset.FromUnixTimeMilliseconds(lastModified);

    /// <summary>
    /// The per-configuration listener state.
    /// </summary>
    private sealed class ListenerRegistration
    {
        public ListenerRegistration(NacosConfigKey key) => Key = key;

        /// <summary>Gets the configuration this registration observes.</summary>
        public NacosConfigKey Key { get; }

        /// <summary>Gets an object used to guard <see cref="Callbacks"/> and <see cref="LastMd5"/>.</summary>
        public object Sync { get; } = new();

        /// <summary>Gets the registered callbacks.</summary>
        public List<Func<NacosConfigChange, CancellationToken, Task>> Callbacks { get; } = [];

        /// <summary>
        /// Gets or sets the hash of the value most recently delivered, used to suppress duplicate
        /// notifications and to enable the server's "not modified" fast path.
        /// </summary>
        public string? LastMd5 { get; set; }

        /// <summary>Gets the number of registered callbacks.</summary>
        public int Count => Callbacks.Count;

        public void Add(Func<NacosConfigChange, CancellationToken, Task> callback) =>
            Callbacks.Add(callback);

        public void Remove(Func<NacosConfigChange, CancellationToken, Task> callback) =>
            Callbacks.Remove(callback);
    }

    /// <summary>
    /// A handle that removes one callback when disposed.
    /// </summary>
    private sealed class ListenerHandle : IAsyncDisposable
    {
        private readonly NacosConfigClient _owner;
        private readonly string _id;
        private readonly ListenerRegistration _registration;
        private readonly Func<NacosConfigChange, CancellationToken, Task> _onChange;
        private bool _disposed;

        public ListenerHandle(
            NacosConfigClient owner,
            string id,
            ListenerRegistration registration,
            Func<NacosConfigChange, CancellationToken, Task> onChange)
        {
            _owner = owner;
            _id = id;
            _registration = registration;
            _onChange = onChange;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _owner.UnregisterAsync(_id, _registration, _onChange).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The shared state of one fuzzy watch pattern.
    /// </summary>
    private sealed class FuzzyWatchRegistration
    {
        public FuzzyWatchRegistration(string pattern) => Pattern = pattern;

        /// <summary>Gets the pattern the server watches.</summary>
        public string Pattern { get; }

        /// <summary>
        /// Gets an object that guards <see cref="KnownKeys"/>, <see cref="Initialized"/> and
        /// <see cref="Subscriptions"/>.
        /// </summary>
        public object Sync { get; } = new();

        /// <summary>
        /// Gets the group keys the server last reported as matching, which is the set a new
        /// subscriber is seeded from and what the next diff is computed against.
        /// </summary>
        public HashSet<string> KnownKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets a value indicating whether the initial diff has been applied, so a later
        /// request asks for a diff rather than a fresh initialisation.
        /// </summary>
        public bool Initialized { get; set; }

        /// <summary>Gets the subscriptions that receive this watch's events.</summary>
        public List<FuzzyWatchSubscription> Subscriptions { get; } = [];
    }

    /// <summary>
    /// One consumer's view of a fuzzy watch.
    /// </summary>
    private sealed class FuzzyWatchSubscription
    {
        /// <summary>
        /// Gets the queue of events. It is unbounded because dropping an add or a remove would leave
        /// the consumer's set wrong with no way to notice, unlike a value stream where only the
        /// newest value matters.
        /// </summary>
        public Channel<NacosConfigWatchEvent> Queue { get; } =
            Channel.CreateUnbounded<NacosConfigWatchEvent>(
                new UnboundedChannelOptions { SingleReader = true });

        /// <summary>Gets the group keys already delivered to this subscription.</summary>
        public HashSet<string> KnownKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets a value indicating whether this subscription has received its one
        /// <see cref="NacosConfigWatchKind.InitialSyncCompleted"/> marker.
        /// </summary>
        public bool Initialized { get; set; }
    }
}
