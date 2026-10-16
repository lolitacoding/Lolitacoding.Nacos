using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Naming;

/// <summary>
/// The default <see cref="INacosNamingClient"/> over the Nacos gRPC transport.
/// </summary>
/// <remarks>
/// <para>
/// This client deliberately sends no heartbeats. For an ephemeral instance registered over gRPC
/// the server treats the connection itself as the liveness signal: it associates the instance with
/// the connection and removes it when that connection closes, and its expiry check explicitly
/// exempts connection-owned clients. Sending heartbeats would therefore be redundant work, and the
/// official client does not do it either. The correctness requirement this creates is the one this
/// class does implement: every registration must be replayed after a reconnect, because the server
/// discarded it along with the old connection.
/// </para>
/// <para>
/// Registrations and subscriptions are held here rather than on the connection because they
/// outlive it, and the connection raises a single event that this client uses to replay them.
/// </para>
/// </remarks>
internal sealed class NacosNamingClient : INacosNamingClient, IDisposable
{
    private readonly NacosOptions _options;
    private readonly NacosGrpcConnection _connection;
    private readonly NacosServiceSnapshotStore? _snapshots;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NacosNamingClient> _logger;

    /// <summary>Instances this client registered, keyed by service and endpoint.</summary>
    private readonly ConcurrentDictionary<string, Registration> _registrations = new(StringComparer.Ordinal);

    /// <summary>Subscriptions this client holds, keyed by service and cluster filter.</summary>
    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);

    /// <summary>
    /// Retains the last known instance list per service, so a control-plane outage can still be
    /// served from local state.
    /// </summary>
    private readonly ConcurrentDictionary<string, NacosServiceInfo> _cache = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosNamingClient"/> class.
    /// </summary>
    /// <param name="options">The client options.</param>
    /// <param name="connection">The transport to use.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="snapshots">
    /// The service snapshot store, or <see langword="null"/> when local caching is disabled.
    /// </param>
    public NacosNamingClient(
        NacosOptions options,
        NacosGrpcConnection connection,
        TimeProvider timeProvider,
        ILogger<NacosNamingClient> logger,
        NacosServiceSnapshotStore? snapshots)
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

        // The server discards every registration and subscription when the connection drops, so
        // they must be replayed or the service silently stops being discoverable.
        _connection.Reconnected += OnReconnectedAsync;
    }

    /// <inheritdoc />
    public async Task RegisterInstanceAsync(
        NacosServiceKey service,
        NacosInstance instance,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(instance);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosServiceKey resolved = service.WithDefaultNamespace(_options.Namespace);
        NacosInstance resolvedInstance = ApplyDefaultCluster(instance);

        // The registration is recorded before it is sent, so a reconnect that races the first
        // attempt still finds it and replays it.
        string id = RegistrationId(resolved, resolvedInstance);
        var registration = new Registration(resolved, resolvedInstance);
        _registrations[id] = registration;

        try
        {
            await SendRegistrationAsync(resolved, resolvedInstance, isReplay: false, ct).ConfigureAwait(false);
            registration.Confirmed = true;
        }
        catch
        {
            // The registration never reached the server, so it must not be replayed later as
            // though it had been accepted.
            _registrations.TryRemove(id, out _);
            throw;
        }

        NacosNamingLog.InstanceRegistered(
            _logger, resolved.ToString(), resolvedInstance.Endpoint, resolvedInstance.Ephemeral);
    }

    /// <inheritdoc />
    public async Task DeregisterInstanceAsync(
        NacosServiceKey service,
        NacosInstance instance,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(instance);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosServiceKey resolved = service.WithDefaultNamespace(_options.Namespace);
        NacosInstance resolvedInstance = ApplyDefaultCluster(instance);

        // Removed first so a concurrent reconnect cannot re-register what is being removed.
        _registrations.TryRemove(RegistrationId(resolved, resolvedInstance), out _);

        var request = new InstanceRequest
        {
            Namespace = resolved.Namespace,
            ServiceName = resolved.ServiceName,
            GroupName = resolved.GroupName,
            Type = InstanceRequest.DeregisterType,
            Instance = ToWireInstance(resolved, resolvedInstance),
        };

        await _connection.RequestAsync<InstanceResponse>(request, ct).ConfigureAwait(false);

        NacosNamingLog.InstanceDeregistered(
            _logger, resolved.ToString(), resolvedInstance.Endpoint);
    }

    /// <inheritdoc />
    public async Task<NacosServiceInfo> GetInstancesAsync(
        NacosServiceKey service,
        string? cluster = null,
        bool healthyOnly = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosServiceKey resolved = service.WithDefaultNamespace(_options.Namespace);

        try
        {
            return await QueryAsync(resolved, cluster, healthyOnly, ct).ConfigureAwait(false);
        }
        catch (NacosException ex) when (ex is NacosConnectionException or NacosTimeoutException)
        {
            // Falling back to the last known list keeps the application routing during a control
            // plane outage; it is flagged so a caller that must not use stale data can tell.
            if (_cache.TryGetValue(resolved.ToString(), out NacosServiceInfo? cached))
            {
                NacosNamingLog.ServedFromCache(_logger, resolved.ToString(), ex.Message);
                return cached with { FromLocalCache = true };
            }

            // The in-process cache is empty (typically a fresh process, or a service that was
            // never queried). The disk snapshot is what makes failover survive a restart.
            NacosServiceInfo? snapshot = await ReadSnapshotAsync(resolved, ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                NacosNamingLog.ServedFromSnapshot(_logger, resolved.ToString(), ex.Message);
                return snapshot;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosServiceChange> SubscribeAsync(
        NacosServiceKey service,
        string? clusters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosServiceKey resolved = service.WithDefaultNamespace(_options.Namespace);

        // The current state is published first so a consumer can initialise from the same path
        // that handles later changes.
        NacosServiceInfo current = await GetInstancesAsync(resolved, clusters, healthyOnly: false, ct)
            .ConfigureAwait(false);
        yield return new NacosServiceChange(current, null);

        var queue = Channel.CreateBounded<NacosServiceChange>(
            new BoundedChannelOptions(16)
            {
                // The newest instance list is the one that matters, so an overwhelmed consumer
                // loses intermediate states rather than stalling the shared read loop.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        string id = SubscriptionId(resolved, clusters);

        Subscription subscription = _subscriptions.GetOrAdd(
            id, _ => new Subscription(resolved, clusters));

        subscription.Attach(queue.Writer);
        subscription.Delivered = current;

        try
        {
            await SendSubscribeRequestAsync(resolved, clusters, subscribe: true, ct).ConfigureAwait(false);

            await foreach (NacosServiceChange change in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            subscription.Detach(queue.Writer);

            if (subscription.IsEmpty)
            {
                _subscriptions.TryRemove(id, out _);
                await UnsubscribeQuietlyAsync(resolved, clusters).ConfigureAwait(false);
            }
        }
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

        foreach (Subscription subscription in _subscriptions.Values)
        {
            subscription.Complete();
        }

        _subscriptions.Clear();
        _registrations.Clear();
        _cache.Clear();

        return ValueTask.CompletedTask;
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
    /// Handles a service change pushed by the server.
    /// </summary>
    /// <param name="notification">The push, which carries the full instance list.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once subscribers were notified.</returns>
    /// <remarks>
    /// Unlike a configuration change, this push carries the complete state, so no follow-up query
    /// is needed. The request's own namespace, service and group fields are left unset by the
    /// server, which builds it from a service info alone, so the identity is read back out of that
    /// service info — the same way the Java handler does, by handing
    /// <c>notifyRequest.getServiceInfo()</c> to the client's service info holder rather than
    /// looking at the request's fields.
    /// </remarks>
    internal async Task OnServiceChangedAsync(NotifySubscriberRequest notification, CancellationToken ct = default)
    {
        ServiceInfo? pushed = notification.ServiceInfo;

        string? serviceName = notification.ServiceName ?? pushed?.Name;
        string? groupName = notification.GroupName ?? pushed?.GroupName;

        if (pushed is null || string.IsNullOrEmpty(serviceName) || string.IsNullOrEmpty(groupName))
        {
            return;
        }

        var key = new NacosServiceKey(
            serviceName,
            groupName,
            string.IsNullOrEmpty(notification.Namespace) ? _options.Namespace : notification.Namespace);

        NacosServiceInfo info = Materialize(key, pushed, fromLocalCache: false);

        // The cache is refreshed on every push, so a later outage has the freshest possible list
        // even if no query was ever issued.
        _cache[key.ToString()] = info;

        // Persisting on the push keeps the on-disk snapshot current without waiting for a read,
        // so a restart after a push still fails over to the pushed state.
        await UpdateSnapshotAsync(key, info, ct).ConfigureAwait(false);

        foreach (Subscription subscription in _subscriptions.Values)
        {
            if (!subscription.Matches(key))
            {
                // A push for a service nobody subscribed to is possible after a subscription was
                // removed while the push was already in flight.
                continue;
            }

            if (pushed.LastRefTime < subscription.LastRefTime)
            {
                // Pushes arrive in order on one stream, but each one is applied on its own task, so
                // an older state can finish after a newer one. Applying it would roll the consumer
                // back to an instance list the server has already replaced.
                NacosNamingLog.StaleChangeIgnored(
                    _logger, key.ToString(), pushed.LastRefTime, subscription.LastRefTime);

                continue;
            }

            // Duplicate notifications are normal, and the server leaves the checksum out of the
            // pushes it sends, so an unchanged state is detected by comparing the instance lists.
            // That is the comparison the Java client makes: InstancesDiffer.doDiff keys the old and
            // new lists by address and compares the instance descriptions.
            if (SameInstances(subscription.Delivered, info))
            {
                NacosNamingLog.ChangeIgnored(_logger, key.ToString());
                continue;
            }

            NacosServiceInfo? previous = subscription.Delivered;
            subscription.Delivered = info;
            subscription.LastRefTime = pushed.LastRefTime;

            NacosNamingLog.ServiceChanged(
                _logger, key.ToString(), info.Instances.Count, previous?.Instances.Count ?? 0);

            subscription.Publish(new NacosServiceChange(info, previous?.Checksum));
        }
    }

    /// <summary>
    /// Reports whether two service states describe the same instances.
    /// </summary>
    /// <param name="delivered">The state most recently delivered, or <see langword="null"/>.</param>
    /// <param name="current">The state the server just supplied.</param>
    /// <returns><see langword="true"/> when a consumer would see no difference.</returns>
    /// <remarks>
    /// The instances are compared by address and by their description, in any order, which is what
    /// the Java client compares when it decides whether a push is a change.
    /// </remarks>
    private static bool SameInstances(NacosServiceInfo? delivered, NacosServiceInfo current)
    {
        if (delivered is null || delivered.Instances.Count != current.Instances.Count)
        {
            return false;
        }

        var byAddress = new Dictionary<string, NacosInstance>(delivered.Instances.Count, StringComparer.Ordinal);

        foreach (NacosInstance instance in delivered.Instances)
        {
            byAddress[instance.Endpoint] = instance;
        }

        return current.Instances.All(
            instance => byAddress.TryGetValue(instance.Endpoint, out NacosInstance? known)
                && SameInstance(known, instance));
    }

    /// <summary>
    /// Reports whether two instances of the same service describe the same state.
    /// </summary>
    /// <param name="delivered">The instance most recently delivered.</param>
    /// <param name="current">The instance the server just supplied.</param>
    /// <returns><see langword="true"/> when a consumer would see no difference.</returns>
    private static bool SameInstance(NacosInstance delivered, NacosInstance current) =>
        delivered.Weight == current.Weight
        && string.Equals(delivered.ClusterName, current.ClusterName, StringComparison.Ordinal)
        && delivered.Healthy == current.Healthy
        && delivered.Enabled == current.Enabled
        && delivered.Ephemeral == current.Ephemeral
        && string.Equals(delivered.InstanceId, current.InstanceId, StringComparison.Ordinal)
        && SameMetadata(delivered.Metadata, current.Metadata);

    /// <summary>
    /// Reports whether two metadata maps hold the same entries.
    /// </summary>
    /// <param name="delivered">The metadata most recently delivered.</param>
    /// <param name="current">The metadata the server just supplied.</param>
    /// <returns><see langword="true"/> when the entries match.</returns>
    private static bool SameMetadata(
        IReadOnlyDictionary<string, string>? delivered,
        IReadOnlyDictionary<string, string>? current)
    {
        if (delivered is null || current is null)
        {
            return delivered is null && current is null;
        }

        return delivered.Count == current.Count
            && delivered.All(
                entry => current.TryGetValue(entry.Key, out string? value)
                    && string.Equals(value, entry.Value, StringComparison.Ordinal));
    }

    /// <summary>
    /// Sends a service query and interprets the response.
    /// </summary>
    /// <param name="service">The resolved service key.</param>
    /// <param name="cluster">The cluster filter.</param>
    /// <param name="healthyOnly">Whether to exclude unhealthy instances.</param>
    /// <param name="ct">A token that cancels the query.</param>
    /// <returns>The service info.</returns>
    private async Task<NacosServiceInfo> QueryAsync(
        NacosServiceKey service,
        string? cluster,
        bool healthyOnly,
        CancellationToken ct)
    {
        var request = new ServiceQueryRequest
        {
            Namespace = service.Namespace,
            ServiceName = service.ServiceName,
            GroupName = service.GroupName,
            Cluster = cluster,

            // The server filters when asked, which keeps an unhealthy instance from crossing the
            // wire at all.
            HealthyOnly = healthyOnly,
        };

        QueryServiceResponse response = await _connection
            .RequestAsync<QueryServiceResponse>(request, ct)
            .ConfigureAwait(false);

        NacosServiceInfo info = Materialize(service, response.ServiceInfo, fromLocalCache: false);

        // The answer is retained so a later outage can be served from the last known state, both
        // in memory for this process and on disk for the next one.
        _cache[service.ToString()] = info;

        await UpdateSnapshotAsync(service, info, ct).ConfigureAwait(false);

        return info;
    }

    /// <summary>
    /// Replays every registration and subscription after a reconnect.
    /// </summary>
    /// <param name="state">The state the transport reached.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once state was restored.</returns>
    private async Task OnReconnectedAsync(NacosTransportState state, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        int restored = 0;

        foreach (Registration registration in _registrations.Values)
        {
            try
            {
                await SendRegistrationAsync(
                    registration.Service, registration.Instance, isReplay: true, ct).ConfigureAwait(false);

                registration.Confirmed = true;
                restored++;
            }
            catch (NacosException ex)
            {
                // The connection is back but this registration failed. It stays in the map, so the
                // next reconnect retries it rather than losing the instance forever.
                NacosNamingLog.RestoreFailed(_logger, ex.Message);
            }
        }

        foreach (Subscription subscription in _subscriptions.Values)
        {
            try
            {
                await SendSubscribeRequestAsync(
                    subscription.Service, subscription.Clusters, subscribe: true, ct).ConfigureAwait(false);

                restored++;
            }
            catch (NacosException ex)
            {
                NacosNamingLog.RestoreFailed(_logger, ex.Message);
            }
        }

        if (restored > 0)
        {
            NacosNamingLog.RestoredAfterReconnect(_logger, restored);
        }
    }

    /// <summary>
    /// Sends a single instance registration.
    /// </summary>
    /// <param name="service">The resolved service key.</param>
    /// <param name="instance">The instance to register.</param>
    /// <param name="isReplay">Whether this is a reconnect replay, which changes the request type.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the registration.</returns>
    /// <remarks>
    /// The request type differs by instance kind: the server routes a persistent instance to a
    /// different handler, which is what makes it survive the connection. Sending the wrong type
    /// would silently store the instance as ephemeral.
    /// </remarks>
    private async Task SendRegistrationAsync(
        NacosServiceKey service,
        NacosInstance instance,
        bool isReplay,
        CancellationToken ct)
    {
        var wireInstance = ToWireInstance(service, instance);

        // A registration replayed after a reconnect must not be coalesced with a heartbeat, so it
        // is always sent as a full registration.
        InstanceRequest request = instance.Ephemeral
            ? new InstanceRequest
            {
                Namespace = service.Namespace,
                ServiceName = service.ServiceName,
                GroupName = service.GroupName,
                Type = InstanceRequest.RegisterType,
                Instance = wireInstance,
            }
            : new PersistentInstanceRequest
            {
                Namespace = service.Namespace,
                ServiceName = service.ServiceName,
                GroupName = service.GroupName,
                Type = InstanceRequest.RegisterType,
                Instance = wireInstance,
            };

        if (isReplay)
        {
            // A replay is an idempotent re-assertion of intent; the header makes the distinction
            // visible in a server-side trace without changing the payload.
            request.SetHeader("replay", "true");
        }

        await _connection.RequestAsync<InstanceResponse>(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a subscribe or unsubscribe request.
    /// </summary>
    /// <param name="service">The resolved service key.</param>
    /// <param name="clusters">The cluster filter.</param>
    /// <param name="subscribe">Whether to subscribe or unsubscribe.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the change.</returns>
    private async Task SendSubscribeRequestAsync(
        NacosServiceKey service,
        string? clusters,
        bool subscribe,
        CancellationToken ct)
    {
        var request = new SubscribeServiceRequest
        {
            Namespace = service.Namespace,
            ServiceName = service.ServiceName,
            GroupName = service.GroupName,
            Subscribe = subscribe,
            Clusters = clusters,
        };

        await _connection.RequestAsync<SubscribeServiceResponse>(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Unsubscribes without letting a failure escape.
    /// </summary>
    /// <param name="service">The resolved service key.</param>
    /// <param name="clusters">The cluster filter.</param>
    /// <returns>A task that completes once the attempt finished.</returns>
    private async Task UnsubscribeQuietlyAsync(NacosServiceKey service, string? clusters)
    {
        if (_disposed || _connection.State != NacosTransportState.Connected)
        {
            // Nothing to tell the server: the subscription is already gone locally, and the
            // server cleans up when the connection closes.
            return;
        }

        try
        {
            await SendSubscribeRequestAsync(service, clusters, subscribe: false, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (NacosException)
        {
            // The local state is what matters; a failed unsubscribe is corrected on reconnect.
        }
    }

    /// <summary>
    /// Converts a server-reported service into the public model.
    /// </summary>
    /// <param name="key">The resolved service key.</param>
    /// <param name="info">The server-reported service, or <see langword="null"/>.</param>
    /// <param name="fromLocalCache">Whether the value came from a local cache.</param>
    /// <returns>The service info.</returns>
    private static NacosServiceInfo Materialize(
        NacosServiceKey key,
        ServiceInfo? info,
        bool fromLocalCache) =>
        new(
            key,
            info?.Hosts is null ? [] : [.. info.Hosts.Select(ToInstance)],
            info?.Clusters,
            info?.Checksum,
            fromLocalCache);

    /// <summary>
    /// Converts a wire instance into the public model.
    /// </summary>
    /// <param name="instance">The wire instance.</param>
    /// <returns>The public instance.</returns>
    private static NacosInstance ToInstance(Instance instance) =>
        new(
            instance.Ip ?? string.Empty,
            instance.Port,
            // A server that reports a non-positive weight is treated as unweighted rather than
            // rejected, since a malformed entry must not fail the whole lookup.
            instance.Weight > 0 ? instance.Weight : 1.0,
            instance.ClusterName,
            instance.Healthy,
            instance.Enabled,
            instance.Ephemeral,
            instance.Metadata)
        {
            InstanceId = instance.InstanceId,
        };

    /// <summary>
    /// Converts the public instance into the wire form.
    /// </summary>
    /// <param name="service">The resolved service key.</param>
    /// <param name="instance">The public instance.</param>
    /// <returns>The wire instance.</returns>
    private static Instance ToWireInstance(NacosServiceKey service, NacosInstance instance)
    {
        var wire = new Instance
        {
            Ip = instance.IpAddress,
            Port = instance.Port,
            Weight = instance.Weight,
            ClusterName = instance.ClusterName,
            Healthy = instance.Healthy,
            Enabled = instance.Enabled,
            Ephemeral = instance.Ephemeral,
            ServiceName = service.ServiceName,
            InstanceId = instance.InstanceId,
            Metadata = instance.Metadata is null
                ? []
                : new Dictionary<string, string>(instance.Metadata, StringComparer.Ordinal),
        };

        // The server derives the instance identity from the reserved metadata keys, so honouring
        // the metadata form keeps compatibility with callers that set heartbeat values that way.
        wire.ApplyReservedMetadata();

        return wire;
    }

    /// <summary>
    /// Applies the configured default cluster when the caller did not name one.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <returns>An instance with a concrete cluster.</returns>
    private NacosInstance ApplyDefaultCluster(NacosInstance instance) =>
        string.IsNullOrEmpty(instance.ClusterName)
            ? instance with { ClusterName = _options.Naming.DefaultCluster }
            : instance;

    /// <summary>
    /// Writes the current service state to the failover store.
    /// </summary>
    /// <param name="service">The resolved service key.</param>
    /// <param name="info">The state to persist.</param>
    /// <param name="ct">A token that cancels the write.</param>
    /// <returns>A task that completes once the attempt finished.</returns>
    private async Task UpdateSnapshotAsync(
        NacosServiceKey service,
        NacosServiceInfo info,
        CancellationToken ct)
    {
        if (_snapshots is null)
        {
            return;
        }

        try
        {
            await _snapshots.SaveAsync(service, info, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed snapshot write must not fail a lookup that already succeeded.
            NacosNamingLog.SnapshotWriteFailed(_logger, service.ToString(), ex.Message);
        }
    }

    /// <summary>
    /// Reads the last known service state from the failover store.
    /// </summary>
    /// <param name="service">The resolved service key.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>The stored state, or <see langword="null"/> when there is none.</returns>
    private async Task<NacosServiceInfo?> ReadSnapshotAsync(NacosServiceKey service, CancellationToken ct)
    {
        if (_snapshots is null)
        {
            return null;
        }

        try
        {
            return await _snapshots.LoadAsync(service, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NacosNamingLog.SnapshotReadFailed(_logger, service.ToString(), ex.Message);
            return null;
        }
    }

    private static string RegistrationId(NacosServiceKey service, NacosInstance instance) =>
        string.Concat(
            service.ToString(), "|", instance.IpAddress, ":",
            instance.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string SubscriptionId(NacosServiceKey service, string? clusters) =>
        string.Concat(service.ToString(), "|", clusters ?? string.Empty);

    /// <summary>A registration this client owns and must replay after a reconnect.</summary>
    private sealed class Registration
    {
        public Registration(NacosServiceKey service, NacosInstance instance)
        {
            Service = service;
            Instance = instance;
        }

        public NacosServiceKey Service { get; }

        public NacosInstance Instance { get; }

        /// <summary>
        /// Gets or sets a value indicating whether the server acknowledged the registration.
        /// </summary>
        public bool Confirmed { get; set; }
    }

    /// <summary>A subscription this client owns and must replay after a reconnect.</summary>
    private sealed class Subscription
    {
        private readonly List<ChannelWriter<NacosServiceChange>> _writers = [];
        private readonly Lock _sync = new();

        public Subscription(NacosServiceKey service, string? clusters)
        {
            Service = service;
            Clusters = clusters;
        }

        public NacosServiceKey Service { get; }

        public string? Clusters { get; }

        /// <summary>
        /// Gets or sets the state most recently delivered, used to suppress duplicate
        /// notifications.
        /// </summary>
        public NacosServiceInfo? Delivered { get; set; }

        /// <summary>
        /// Gets or sets the time the delivered state was built at, used to drop a push that
        /// describes an older state than the one already applied.
        /// </summary>
        public long LastRefTime { get; set; }

        /// <summary>Gets a value indicating whether the subscription has no live consumers.</summary>
        public bool IsEmpty
        {
            get
            {
                lock (_sync)
                {
                    return _writers.Count == 0;
                }
            }
        }

        /// <summary>Reports whether this subscription observes the given service.</summary>
        /// <param name="key">The service to check.</param>
        /// <returns><see langword="true"/> when the subscription observes it.</returns>
        public bool Matches(NacosServiceKey key) =>
            string.Equals(Service.ToString(), key.ToString(), StringComparison.Ordinal);

        /// <summary>Adds a consumer.</summary>
        /// <param name="writer">The channel to publish to.</param>
        public void Attach(ChannelWriter<NacosServiceChange> writer)
        {
            lock (_sync)
            {
                _writers.Add(writer);
            }
        }

        /// <summary>Removes a consumer.</summary>
        /// <param name="writer">The channel to stop publishing to.</param>
        public void Detach(ChannelWriter<NacosServiceChange> writer)
        {
            lock (_sync)
            {
                _writers.Remove(writer);
            }
        }

        /// <summary>Publishes a change to every consumer.</summary>
        /// <param name="change">The change.</param>
        public void Publish(NacosServiceChange change)
        {
            ChannelWriter<NacosServiceChange>[] writers;
            lock (_sync)
            {
                writers = [.. _writers];
            }

            foreach (ChannelWriter<NacosServiceChange> writer in writers)
            {
                // A bounded channel that drops the oldest entry can still refuse a write if it is
                // momentarily full, which is acceptable: the next push carries the full state.
                writer.TryWrite(change);
            }
        }

        /// <summary>Ends every consumer's enumeration.</summary>
        public void Complete()
        {
            ChannelWriter<NacosServiceChange>[] writers;
            lock (_sync)
            {
                writers = [.. _writers];
                _writers.Clear();
            }

            foreach (ChannelWriter<NacosServiceChange> writer in writers)
            {
                writer.TryComplete();
            }
        }
    }
}
