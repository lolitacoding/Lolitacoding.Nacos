using System.Collections.Concurrent;
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
internal sealed class NacosConfigClient : INacosConfigClient
{
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

        // The server forgets every listener when the connection drops, so they are re-registered
        // here. Without this, a reconnect would silently stop delivering changes.
        _connection.Reconnected += OnReconnectedAsync;
    }

    /// <inheritdoc />
    public async Task<NacosConfigResult> GetConfigAsync(
        NacosConfigKey key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosConfigKey resolved = key.WithDefaultNamespace(_options.Namespace);

        try
        {
            NacosConfigResult result = await QueryAsync(resolved, localMd5: null, ct).ConfigureAwait(false);

            await UpdateSnapshotAsync(resolved, result, ct).ConfigureAwait(false);

            return result;
        }
        catch (NacosException ex) when (ex is NacosConnectionException or NacosTimeoutException)
        {
            // The control plane is unreachable. A snapshot is the difference between a process
            // that survives the outage and one that cannot start, so it is used here.
            NacosConfigResult? cached = await ReadSnapshotAsync(resolved, ct).ConfigureAwait(false);

            if (cached is not null)
            {
                NacosConfigLog.ServedFromSnapshot(_logger, resolved.ToString(), ex.Message);
                return cached;
            }

            throw;
        }
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

        var request = new ConfigPublishRequest
        {
            DataId = resolved.DataId,
            Group = resolved.Group,
            Tenant = resolved.Namespace,
            Content = content,
            CasMd5 = casMd5,
            // The content type is not a field of its own on the wire: the server reads it out of
            // the free-form addition map under the key "type".
            AdditionMap = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = resolvedContentType,
            },
        };

        await _connection.RequestAsync<ConfigPublishResponse>(request, ct).ConfigureAwait(false);

        // The published value is the newest known one, so the snapshot is updated immediately
        // rather than waiting for the next read.
        await UpdateSnapshotAsync(
            resolved,
            new NacosConfigResult(
                resolved,
                content,
                NacosMd5.Compute(content),
                resolvedContentType,
                _timeProvider.GetUtcNow(),
                fromLocalCache: false),
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
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        _connection.Reconnected -= OnReconnectedAsync;

        // Registrations hold only a list of callbacks, so there is nothing to dispose; clearing
        // the collection releases them.
        _listeners.Clear();

        return ValueTask.CompletedTask;
    }

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
                ToTimestamp(response.LastModified), fromLocalCache: false);
        }

        VerifyMd5(key, response.Content, response.Md5);

        return new NacosConfigResult(
            key,
            response.Content,
            response.Md5 ?? NacosMd5.Compute(response.Content),
            response.ContentType,
            ToTimestamp(response.LastModified),
            fromLocalCache: false);
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

        await InvokeSafelyAsync(
            registration,
            new NacosConfigChange(key, result) { PreviousMd5 = previous },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-registers every listener after the transport reconnected.
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

        List<NacosConfigKey> keys = [.. _listeners.Values.Select(registration => registration.Key)];
        if (keys.Count == 0)
        {
            return;
        }

        try
        {
            await SendListenRequestAsync(keys, listen: true, ct).ConfigureAwait(false);
            NacosConfigLog.ListenersRestored(_logger, keys.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reconnect itself succeeded, so this is recoverable: the next reconnect retries
            // the replay. Failing here must not take down the connection that was just restored.
            NacosConfigLog.ListenerRestoreFailed(_logger, ex.Message);
        }
    }

    /// <summary>
    /// Sends a batch listen request that adds or removes registrations.
    /// </summary>
    /// <param name="keys">The configurations to register or unregister.</param>
    /// <param name="listen">Whether to register or unregister.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the change.</returns>
    private async Task SendListenRequestAsync(
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
        await _connection
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
            await _snapshots.SaveAsync(key, result.Content, result.ContentType, ct).ConfigureAwait(false);
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
            (string Content, string? ContentType)? snapshot =
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
                fromLocalCache: true);
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
}
