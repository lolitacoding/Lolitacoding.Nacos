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
    /// <summary>The error code a server uses to report a capability it does not implement.</summary>
    private const int NotImplementedErrorCode = 501;

    /// <summary>The error code a server uses when the connection watches too many patterns.</summary>
    private const int PatternOverLimitErrorCode = 50310;

    /// <summary>The error code a server uses when one pattern matches too many services.</summary>
    private const int MatchCountOverLimitErrorCode = 50311;

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

    /// <summary>
    /// Fuzzy watch registrations keyed by pattern, so every subscription to the same pattern shares
    /// one server-side watch.
    /// </summary>
    private readonly ConcurrentDictionary<string, ServiceWatchRegistration> _serviceWatches =
        new(StringComparer.Ordinal);

    /// <summary>Serialises fuzzy watch creation, subscription changes and cancellation.</summary>
    private readonly SemaphoreSlim _serviceWatchSync = new(1, 1);

    /// <summary>
    /// Re-sends every watch periodically, or <see langword="null"/> until the first watch is opened.
    /// </summary>
    private ITimer? _serviceWatchResyncTimer;

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
    public NacosServerStatus ServerStatus =>
        _connection.State == NacosTransportState.Connected
            ? NacosServerStatus.Up
            : NacosServerStatus.Down;

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
        // attempt still finds it and replays it. The key is the service, not the endpoint: the
        // server keeps one published instance set per service per connection, so a new
        // registration supersedes the previous one for replay as well.
        _registrations.TryGetValue(resolved.ToString(), out Registration? previous);
        var registration = new Registration(resolved, [resolvedInstance], isBatch: false);
        _registrations[resolved.ToString()] = registration;

        try
        {
            await SendRegistrationAsync(registration, isReplay: false, ct).ConfigureAwait(false);
            registration.Confirmed = true;
        }
        catch
        {
            // The registration never reached the server, so it must not be replayed later as
            // though it had been accepted; the set the server still holds is the one to keep.
            Revert(resolved, registration, previous);
            throw;
        }

        NacosNamingLog.InstanceRegistered(
            _logger, resolved.ToString(), resolvedInstance.Endpoint, resolvedInstance.Ephemeral);
    }

    /// <inheritdoc />
    public async Task RegisterInstancesAsync(
        NacosServiceKey service,
        IReadOnlyList<NacosInstance> instances,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(instances);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (instances.Count == 0)
        {
            // An empty batch is not a no-op on the wire: it replaces the service's published set
            // with nothing, which is a deregistration spelled as a registration.
            throw new ArgumentException(
                "A batch registration must contain at least one instance.", nameof(instances));
        }

        NacosServiceKey resolved = service.WithDefaultNamespace(_options.Namespace);
        var resolvedInstances = new NacosInstance[instances.Count];

        for (int i = 0; i < instances.Count; i++)
        {
            NacosInstance instance = instances[i]
                ?? throw new ArgumentException(
                    "A batch registration cannot contain a null instance.", nameof(instances));

            if (!instance.Ephemeral)
            {
                // The server's batch handler is the ephemeral one; the persistent store has no
                // batch path and would silently drop the registration.
                throw new ArgumentException(
                    "Batch registration only supports ephemeral instances; register a persistent "
                    + "instance with RegisterInstanceAsync.",
                    nameof(instances));
            }

            resolvedInstances[i] = ApplyDefaultCluster(instance);
        }

        var registration = new Registration(resolved, resolvedInstances, isBatch: true);
        _registrations.TryGetValue(resolved.ToString(), out Registration? previous);
        _registrations[resolved.ToString()] = registration;

        try
        {
            await SendRegistrationAsync(registration, isReplay: false, ct).ConfigureAwait(false);
            registration.Confirmed = true;
        }
        catch
        {
            Revert(resolved, registration, previous);
            throw;
        }

        NacosNamingLog.InstancesRegistered(_logger, resolved.ToString(), resolvedInstances.Length);
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

        // Removed first so a concurrent reconnect cannot re-register what is being removed. The
        // server removes the service's whole published set, so the entry is the service, not the
        // endpoint.
        _registrations.TryRemove(resolved.ToString(), out _);

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
    public async Task DeregisterInstancesAsync(
        NacosServiceKey service,
        IReadOnlyList<NacosInstance> instances,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(instances);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (instances.Count == 0)
        {
            throw new ArgumentException(
                "A batch deregistration must name at least one instance.", nameof(instances));
        }

        NacosServiceKey resolved = service.WithDefaultNamespace(_options.Namespace);

        var removed = new HashSet<(string Ip, int Port)>();
        foreach (NacosInstance instance in instances)
        {
            if (instance is null)
            {
                throw new ArgumentException(
                    "A batch deregistration cannot contain a null instance.", nameof(instances));
            }

            removed.Add((instance.IpAddress, instance.Port));
        }

        if (!_registrations.TryGetValue(resolved.ToString(), out Registration? registration)
            || !registration.IsBatch)
        {
            // There is no batch to subtract from: the official client refuses this too, rather than
            // guessing that the caller meant a plain deregistration.
            throw new InvalidOperationException(
                $"The service {resolved} has no batch registration from this client to remove "
                + "instances from; use DeregisterInstanceAsync for a single registration.");
        }

        // Matched by address and port only, as the official client does: a caller constructs the
        // instance locally and cannot know the server-assigned id.
        NacosInstance[] remainder =
            [.. registration.Instances.Where(instance => !removed.Contains((instance.IpAddress, instance.Port)))];

        if (remainder.Length == 0)
        {
            // The official client publishes an empty batch here. An ordinary deregistration leaves
            // the same server state without a lingering empty published set, so it is preferred.
            _registrations.TryRemove(resolved.ToString(), out _);

            var request = new InstanceRequest
            {
                Namespace = resolved.Namespace,
                ServiceName = resolved.ServiceName,
                GroupName = resolved.GroupName,
                Type = InstanceRequest.DeregisterType,
                Instance = ToWireInstance(resolved, ApplyDefaultCluster(instances[0])),
            };

            await _connection.RequestAsync<InstanceResponse>(request, ct).ConfigureAwait(false);

            NacosNamingLog.InstanceDeregistered(_logger, resolved.ToString(), instances[0].Endpoint);
            return;
        }

        // The remainder replaces the tracked batch before it is sent, so a reconnect that races the
        // attempt replays the set the caller asked for rather than the one being replaced.
        _registrations.TryGetValue(resolved.ToString(), out Registration? previous);
        var replacement = new Registration(resolved, remainder, isBatch: true);
        _registrations[resolved.ToString()] = replacement;

        try
        {
            await SendRegistrationAsync(replacement, isReplay: false, ct).ConfigureAwait(false);
            replacement.Confirmed = true;
        }
        catch
        {
            Revert(resolved, replacement, previous);
            throw;
        }

        NacosNamingLog.InstancesRegistered(_logger, resolved.ToString(), remainder.Length);
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
    public async Task<NacosServiceListPage> GetServicesAsync(
        int pageNo = 1,
        int pageSize = 10,
        string? groupName = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNo, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var request = new ServiceListRequest
        {
            Namespace = _options.Namespace,
            GroupName = groupName ?? NacosConstants.DefaultGroup,
            PageNo = pageNo,
            PageSize = pageSize,
        };

        ServiceListResponse response = await _connection
            .RequestAsync<ServiceListResponse>(request, ct)
            .ConfigureAwait(false);

        // The server reports the group total before paging, so Count stays the group's size even
        // when the requested page is past the end. The names arrive without their group prefix.
        return new NacosServiceListPage(response.Count, response.ServiceNames ?? []);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<NacosServiceChange> SubscribeAsync(
        NacosServiceKey service,
        string? clusters = null,
        CancellationToken ct = default) =>
        SubscribeCoreAsync(service, clusters, selector: null, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<NacosServiceChange> SubscribeAsync(
        NacosServiceKey service,
        INacosInstanceSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        // The server is asked for every cluster: the narrowing is the selector's job, exactly as in
        // the Java SDK, where the selector overload subscribes with an empty cluster filter.
        //
        // The token is not a parameter of this overload; it reaches the iterator through
        // WithCancellation, which is why None is passed here: the compiler-generated
        // GetAsyncEnumerator combines the token given at enumeration time with this one.
        return SubscribeCoreAsync(service, clusters: null, selector, CancellationToken.None);
    }

    /// <summary>
    /// Implements both subscription overloads.
    /// </summary>
    /// <param name="service">The service to observe.</param>
    /// <param name="clusters">The cluster filter, or <see langword="null"/> for every cluster.</param>
    /// <param name="selector">The filter to apply, or <see langword="null"/> to deliver everything.</param>
    /// <param name="ct">A token that ends the enumeration.</param>
    /// <returns>The stream of changes.</returns>
    private async IAsyncEnumerable<NacosServiceChange> SubscribeCoreAsync(
        NacosServiceKey service,
        string? clusters,
        INacosInstanceSelector? selector,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(service);
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosServiceKey resolved = service.WithDefaultNamespace(_options.Namespace);

        // The current state is published first so a consumer can initialise from the same path
        // that handles later changes. The selector is applied here too, so the first element has
        // exactly the shape every later one will have.
        NacosServiceInfo current = await GetInstancesAsync(resolved, clusters, healthyOnly: false, ct)
            .ConfigureAwait(false);

        // A selector that throws here fails the caller's own call, which is where its bug belongs;
        // on the push path the same failure is caught and logged instead.
        NacosServiceInfo initial = Select(selector, resolved, clusters, current);

        yield return new NacosServiceChange(initial, null);

        var queue = Channel.CreateBounded<NacosServiceChange>(
            new BoundedChannelOptions(16)
            {
                // The newest instance list is the one that matters, so an overwhelmed consumer
                // loses intermediate states rather than stalling the shared read loop.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        var consumer = new SubscriptionConsumer(queue.Writer, selector) { Delivered = initial };

        string id = SubscriptionId(resolved, clusters);

        Subscription subscription = _subscriptions.GetOrAdd(
            id, _ => new Subscription(resolved, clusters, _logger));

        subscription.Attach(consumer);

        try
        {
            SubscribeServiceResponse response = await SendSubscribeRequestAsync(
                resolved, clusters, subscribe: true, ct).ConfigureAwait(false);

            // The subscription takes effect on the server at an instant the client cannot observe:
            // the read above happened before the request was sent, so a change landing in between
            // was never pushed to anybody and would be lost. The response carries the service state
            // as it was when the subscription became live, so applying it here closes that window —
            // it is exactly what a push would have delivered.
            if (response.ServiceInfo is { } atSubscription)
            {
                await ApplyServiceStateAsync(resolved, atSubscription, ct).ConfigureAwait(false);
            }

            await foreach (NacosServiceChange change in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            subscription.Detach(consumer);

            if (subscription.IsEmpty)
            {
                _subscriptions.TryRemove(id, out _);
                await UnsubscribeQuietlyAsync(resolved, clusters).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosServiceWatchEvent> WatchAsync(
        string serviceNamePattern,
        string groupPattern,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Building the pattern validates both arguments; the namespace is what the client is
        // configured for, because one fuzzy watch cannot span namespaces.
        string pattern = NacosFuzzyPattern.Generate(
            _options.Namespace, groupPattern, serviceNamePattern);

        ServiceWatchSubscription subscription =
            await SubscribeServiceWatchAsync(pattern, ct).ConfigureAwait(false);

        try
        {
            await foreach (NacosServiceWatchEvent item in
                subscription.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            await UnsubscribeServiceWatchAsync(pattern, subscription).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<NacosServiceWatchEvent> WatchAsync(
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

        foreach (Subscription subscription in _subscriptions.Values)
        {
            subscription.Complete();
        }

        _subscriptions.Clear();
        _registrations.Clear();
        _cache.Clear();

        // A fuzzy watch subscription is ended by completing its channel rather than by dropping it:
        // a consumer awaiting the next event would otherwise stay parked until its own token was
        // cancelled. Taking the gate first keeps a registration from being created behind the sweep.
        await _serviceWatchSync.WaitAsync().ConfigureAwait(false);
        try
        {
            _serviceWatchResyncTimer?.Dispose();
            _serviceWatchResyncTimer = null;

            foreach (ServiceWatchRegistration registration in _serviceWatches.Values)
            {
                lock (registration.Sync)
                {
                    foreach (ServiceWatchSubscription subscription in registration.Subscriptions)
                    {
                        subscription.Queue.Writer.TryComplete();
                    }

                    registration.Subscriptions.Clear();
                }
            }

            _serviceWatches.Clear();
        }
        finally
        {
            _serviceWatchSync.Release();
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

        await ApplyServiceStateAsync(key, pushed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a service state the server supplied, whether as a push or as the answer to a
    /// subscribe request.
    /// </summary>
    /// <param name="key">The resolved service key.</param>
    /// <param name="pushed">The service state the server reported.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>A task that completes once subscribers were notified.</returns>
    /// <remarks>
    /// Both sources go through here because they mean the same thing: the state of the service at
    /// the moment the server produced it. The comparison below is what keeps the second delivery of
    /// an unchanged state — a re-subscription after a reconnect, say — from reaching a consumer.
    /// </remarks>
    private async Task ApplyServiceStateAsync(
        NacosServiceKey key,
        ServiceInfo pushed,
        CancellationToken ct)
    {
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
            // new lists by address and compares the instance descriptions. The comparison is made
            // per consumer, against the selected view it was last given, so a push that only
            // touches instances a selector filtered out reaches nobody.
            if (!subscription.Publish(info))
            {
                NacosNamingLog.ChangeIgnored(_logger, key.ToString());
                continue;
            }

            subscription.LastRefTime = pushed.LastRefTime;
        }
    }

    /// <summary>
    /// Applies a selector to a service state, producing the view a subscription should see.
    /// </summary>
    /// <param name="selector">The selector, or <see langword="null"/> to keep everything.</param>
    /// <param name="service">The service the state belongs to.</param>
    /// <param name="clusters">The cluster filter the subscription was opened with.</param>
    /// <param name="info">The state to narrow.</param>
    /// <returns>
    /// The narrowed state, or <paramref name="info"/> itself when there is no selector, so the
    /// common case allocates nothing.
    /// </returns>
    private static NacosServiceInfo Select(
        INacosInstanceSelector? selector,
        NacosServiceKey service,
        string? clusters,
        NacosServiceInfo info)
    {
        if (selector is null)
        {
            return info;
        }

        var context = new NacosInstanceSelectorContext(service, clusters, info.Instances);

        return info with { Instances = selector.Select(context) };
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
                await SendRegistrationAsync(registration, isReplay: true, ct).ConfigureAwait(false);

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
                SubscribeServiceResponse response = await SendSubscribeRequestAsync(
                    subscription.Service, subscription.Clusters, subscribe: true, ct).ConfigureAwait(false);

                // The server discarded the subscription with the old connection, so the client was
                // blind to everything that changed while it was away — no push could reach it. The
                // response reports the state at the instant the subscription was restored, which is
                // exactly what was missed, and the comparison inside drops it when nothing changed.
                if (response.ServiceInfo is { } restoredState)
                {
                    await ApplyServiceStateAsync(subscription.Service, restoredState, ct).ConfigureAwait(false);
                }

                restored++;
            }
            catch (NacosException ex)
            {
                NacosNamingLog.RestoreFailed(_logger, ex.Message);
            }
        }

        await RestoreServiceWatchesAsync(ct).ConfigureAwait(false);

        if (restored > 0)
        {
            NacosNamingLog.RestoredAfterReconnect(_logger, restored);
        }
    }

    /// <summary>
    /// Sends the registration this client holds for one service.
    /// </summary>
    /// <param name="registration">The registration to send.</param>
    /// <param name="isReplay">Whether this is a reconnect replay.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the registration.</returns>
    /// <remarks>
    /// <para>
    /// A single instance is sent as <c>InstanceRequest</c> or <c>PersistentInstanceRequest</c>: the
    /// server routes a persistent instance to a different handler, which is what makes it survive
    /// the connection. A batch is always ephemeral and goes out as one <c>BatchInstanceRequest</c>.
    /// </para>
    /// <para>
    /// A replay is an idempotent re-assertion of intent; the header makes the distinction visible in
    /// a server-side trace without changing the payload.
    /// </para>
    /// </remarks>
    private async Task SendRegistrationAsync(
        Registration registration,
        bool isReplay,
        CancellationToken ct)
    {
        NacosServiceKey service = registration.Service;

        if (registration.IsBatch)
        {
            var batch = new BatchInstanceRequest
            {
                Namespace = service.Namespace,
                ServiceName = service.ServiceName,
                GroupName = service.GroupName,
                Type = BatchInstanceRequest.RegisterType,
                Instances = [.. registration.Instances.Select(instance => ToWireInstance(service, instance))],
            };

            if (isReplay)
            {
                batch.SetHeader("replay", "true");
            }

            await _connection.RequestAsync<BatchInstanceResponse>(batch, ct).ConfigureAwait(false);
            return;
        }

        NacosInstance instance = registration.Instances[0];
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
    /// <returns>
    /// The acknowledgement, whose service info is the state of the service at the instant the
    /// subscription took effect on the server.
    /// </returns>
    private async Task<SubscribeServiceResponse> SendSubscribeRequestAsync(
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

        return await _connection.RequestAsync<SubscribeServiceResponse>(request, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Registers a subscription to a naming fuzzy watch, creating the shared server-side watch when
    /// it is the first one for the pattern.
    /// </summary>
    /// <param name="pattern">The resolved pattern.</param>
    /// <param name="ct">A token that cancels registration.</param>
    /// <returns>The subscription, whose channel starts receiving events immediately.</returns>
    /// <exception cref="NacosServerException">
    /// The server states that it does not implement fuzzy watch, or refused the pattern.
    /// </exception>
    private async Task<ServiceWatchSubscription> SubscribeServiceWatchAsync(
        string pattern,
        CancellationToken ct)
    {
        // The ability table only exists once the handshake has happened, so a client that has not
        // connected yet would otherwise skip the refusal below and send the watch anyway. The watch
        // request would establish the connection a moment later, so this costs no extra round trip.
        await _connection.ConnectAsync(ct).ConfigureAwait(false);

        EnsureFuzzyWatchSupported();

        var subscription = new ServiceWatchSubscription();

        await _serviceWatchSync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            ServiceWatchRegistration registration = _serviceWatches.GetOrAdd(
                pattern, value => new ServiceWatchRegistration(value, _options.Namespace));

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

            StartServiceWatchResyncTimer();

            try
            {
                await SendServiceWatchRequestAsync(registration, watch: true, ct).ConfigureAwait(false);
            }
            catch
            {
                // The watch was never installed, so it must not stay behind claiming one.
                lock (registration.Sync)
                {
                    registration.Subscriptions.Remove(subscription);
                }

                _serviceWatches.TryRemove(pattern, out _);
                StopServiceWatchResyncTimerIfIdle();
                throw;
            }

            return subscription;
        }
        finally
        {
            _serviceWatchSync.Release();
        }
    }

    /// <summary>
    /// Removes a subscription, cancelling the server-side watch when it was the last one.
    /// </summary>
    /// <param name="pattern">The pattern the subscription belongs to.</param>
    /// <param name="subscription">The subscription to remove.</param>
    /// <returns>A task that completes once the watch is cancelled.</returns>
    private async Task UnsubscribeServiceWatchAsync(
        string pattern,
        ServiceWatchSubscription subscription)
    {
        await _serviceWatchSync.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_serviceWatches.TryGetValue(pattern, out ServiceWatchRegistration? registration))
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

            _serviceWatches.TryRemove(pattern, out _);
            StopServiceWatchResyncTimerIfIdle();

            if (_disposed || _connection.State != NacosTransportState.Connected)
            {
                // Nothing to tell the server: either the client is gone, or the watch already died
                // with the connection.
                return;
            }

            try
            {
                await SendServiceWatchRequestAsync(registration, watch: false, CancellationToken.None)
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
            _serviceWatchSync.Release();
        }
    }

    /// <summary>
    /// Fails a naming fuzzy watch when the server explicitly says it has none.
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
    /// Starts the periodic resynchronisation of every naming watch.
    /// </summary>
    private void StartServiceWatchResyncTimer()
    {
        TimeSpan interval = _options.Naming.FuzzyWatchResyncInterval;

        _serviceWatchResyncTimer ??= _timeProvider.CreateTimer(
            static state => ((NacosNamingClient)state!).OnServiceWatchResyncTimer(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _serviceWatchResyncTimer.Change(interval, interval);
    }

    /// <summary>
    /// Stops the periodic resynchronisation when no watch is left.
    /// </summary>
    private void StopServiceWatchResyncTimerIfIdle()
    {
        if (_serviceWatches.IsEmpty)
        {
            _serviceWatchResyncTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Runs one periodic resynchronisation.
    /// </summary>
    private void OnServiceWatchResyncTimer() => _ = ResyncServiceWatchesAsync();

    /// <summary>
    /// Re-sends every naming watch so a push lost while the connection looked healthy is repaired.
    /// </summary>
    /// <returns>A task that completes once every watch was re-sent.</returns>
    /// <remarks>
    /// The server only pushes a change once. A push dropped between the server and this client
    /// would otherwise leave the matched set wrong until the next reconnect, so the full diff is
    /// requested on a timer as well.
    /// </remarks>
    private async Task ResyncServiceWatchesAsync()
    {
        try
        {
            if (_disposed)
            {
                return;
            }

            await _serviceWatchSync.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                foreach (ServiceWatchRegistration registration in _serviceWatches.Values)
                {
                    try
                    {
                        await SendServiceWatchRequestAsync(
                            registration, watch: true, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (NacosException ex)
                    {
                        // One pattern that cannot be resynchronised must not stop the others; the
                        // next tick tries again.
                        NacosNamingLog.FuzzyWatchSyncFailed(_logger, registration.Pattern, ex.Message);
                    }
                }
            }
            finally
            {
                _serviceWatchSync.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A timer callback has no caller to report to; an escaping exception would be
            // unobserved and could take the process down on finalisation.
            NacosNamingLog.FuzzyWatchSyncFailed(_logger, "<all>", ex.Message);
        }
    }

    /// <summary>
    /// Re-registers every naming fuzzy watch after the transport reconnected.
    /// </summary>
    /// <param name="ct">A token that cancels the replay.</param>
    /// <returns>A task that completes once every watch was re-sent.</returns>
    private async Task RestoreServiceWatchesAsync(CancellationToken ct)
    {
        if (_serviceWatches.IsEmpty)
        {
            return;
        }

        await _serviceWatchSync.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            int count = 0;
            foreach (ServiceWatchRegistration registration in _serviceWatches.Values)
            {
                await SendServiceWatchRequestAsync(registration, watch: true, ct).ConfigureAwait(false);
                count++;
            }

            NacosNamingLog.FuzzyWatchesRestored(_logger, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The connection itself is healthy; the next reconnect or the periodic resync repairs
            // whatever did not get re-registered here.
            NacosNamingLog.FuzzyWatchSyncFailed(_logger, "<all>", ex.Message);
        }
        finally
        {
            _serviceWatchSync.Release();
        }
    }

    /// <summary>
    /// Sends a naming watch registration or cancellation.
    /// </summary>
    /// <param name="registration">The watch to send.</param>
    /// <param name="watch">Whether to register or cancel.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>A task that completes once the server acknowledged the request.</returns>
    /// <exception cref="NacosServerException">
    /// The server refused the pattern because a limit was reached.
    /// </exception>
    private async Task SendServiceWatchRequestAsync(
        ServiceWatchRegistration registration,
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

        var request = new NamingFuzzyWatchRequest
        {
            Namespace = registration.Namespace,
            GroupKeyPattern = registration.Pattern,

            // The set the client already holds is what makes the answer a diff. It is sent on
            // every request, including a cancellation, exactly as the official client does.
            ReceivedGroupKeys = knownKeys,
            WatchType = watch ? FuzzyWatchProtocol.Watch : FuzzyWatchProtocol.CancelWatch,
            Initializing = initializing,
        };

        try
        {
            await _connection.RequestAsync<NamingFuzzyWatchResponse>(request, ct).ConfigureAwait(false);
        }
        catch (NacosServerException ex)
            when (ex.ErrorCode is PatternOverLimitErrorCode or MatchCountOverLimitErrorCode)
        {
            NacosNamingLog.FuzzyWatchRefused(_logger, registration.Pattern, ex.ErrorCode);
            throw;
        }
    }

    /// <summary>
    /// Applies a service diff pushed by the server.
    /// </summary>
    /// <param name="request">The pushed diff.</param>
    /// <remarks>
    /// Runs on the read loop, so it must not block: every event is written to an unbounded channel
    /// and delivered by the consumer's own enumeration.
    /// </remarks>
    internal void OnFuzzyWatchSync(NamingFuzzyWatchSyncRequest request)
    {
        if (request.GroupKeyPattern is not { Length: > 0 } pattern
            || !_serviceWatches.TryGetValue(pattern, out ServiceWatchRegistration? registration))
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
                foreach (NamingFuzzyWatchContext context in contexts)
                {
                    if (!NacosFuzzyPattern.TryParseServiceKey(
                        context.ServiceKey,
                        out string namespaceId,
                        out string group,
                        out string serviceName))
                    {
                        NacosNamingLog.FuzzyWatchMalformedServiceKey(
                            _logger, pattern, context.ServiceKey);
                        continue;
                    }

                    var key = new NacosServiceKey(serviceName, group, namespaceId);
                    string serviceKey = context.ServiceKey!;

                    if (string.Equals(
                        context.ChangedType,
                        NamingFuzzyWatchChangeType.AddService,
                        StringComparison.Ordinal))
                    {
                        if (registration.KnownKeys.Add(serviceKey))
                        {
                            DeliverLocked(registration, NacosServiceWatchKind.Added, key, serviceKey);
                        }
                    }
                    else if (string.Equals(
                        context.ChangedType,
                        NamingFuzzyWatchChangeType.DeleteService,
                        StringComparison.Ordinal))
                    {
                        // The official Java client adds the key back here, which leaves its set
                        // wrong after a delete; this client removes it, matching the configuration
                        // watch.
                        if (registration.KnownKeys.Remove(serviceKey))
                        {
                            DeliverLocked(registration, NacosServiceWatchKind.Removed, key, serviceKey);
                        }
                    }
                    else
                    {
                        NacosNamingLog.FuzzyWatchUnknownChangeType(
                            _logger, pattern, context.ChangedType);
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
    /// Applies a single-service change pushed by the server.
    /// </summary>
    /// <param name="notification">The pushed change.</param>
    internal void OnFuzzyWatchChange(NamingFuzzyWatchChangeNotifyRequest notification)
    {
        if (!NacosFuzzyPattern.TryParseServiceKey(
            notification.ServiceKey,
            out string namespaceId,
            out string group,
            out string serviceName))
        {
            NacosNamingLog.FuzzyWatchMalformedServiceKey(_logger, "<push>", notification.ServiceKey);
            return;
        }

        var key = new NacosServiceKey(serviceName, group, namespaceId);
        string serviceKey = notification.ServiceKey!;

        foreach (ServiceWatchRegistration registration in _serviceWatches.Values)
        {
            // The server pushes to every pattern it matched, but routing by the local registrations
            // keeps a push for an unrelated watch from being delivered.
            if (!NacosFuzzyPattern.Matches(
                registration.Pattern, key.Namespace, key.GroupName, key.ServiceName))
            {
                continue;
            }

            lock (registration.Sync)
            {
                if (string.Equals(
                    notification.ChangedType,
                    NamingFuzzyWatchChangeType.AddService,
                    StringComparison.Ordinal))
                {
                    if (registration.KnownKeys.Add(serviceKey))
                    {
                        DeliverLocked(registration, NacosServiceWatchKind.Added, key, serviceKey);
                    }
                }
                else if (string.Equals(
                    notification.ChangedType,
                    NamingFuzzyWatchChangeType.DeleteService,
                    StringComparison.Ordinal))
                {
                    if (registration.KnownKeys.Remove(serviceKey))
                    {
                        DeliverLocked(registration, NacosServiceWatchKind.Removed, key, serviceKey);
                    }
                }
                else if (string.Equals(
                    notification.ChangedType,
                    NamingFuzzyWatchChangeType.InstanceChanged,
                    StringComparison.Ordinal))
                {
                    // The server only pushes a change to the matched membership, so this is
                    // defensive: an INSTANCE_CHANGED for a service the watch does not hold is
                    // treated as an add, exactly as the official client does for every instance
                    // change, and one for a known service is reported without changing the set.
                    NacosServiceWatchKind kind = registration.KnownKeys.Add(serviceKey)
                        ? NacosServiceWatchKind.Added
                        : NacosServiceWatchKind.InstancesChanged;

                    DeliverLocked(registration, kind, key, serviceKey);
                }
                else if (!string.Equals(
                    notification.ChangedType,
                    NamingFuzzyWatchChangeType.HeartBeat,
                    StringComparison.Ordinal))
                {
                    // A heartbeat does not change the matched set, so it is ignored silently rather
                    // than logged as an unknown change type.
                    NacosNamingLog.FuzzyWatchUnknownChangeType(
                        _logger, registration.Pattern, notification.ChangedType);
                }
            }
        }
    }

    /// <summary>
    /// Delivers one event to every subscription, suppressing what a subscription already knows.
    /// </summary>
    /// <param name="registration">The watch that produced the event.</param>
    /// <param name="kind">The kind of change.</param>
    /// <param name="service">The service.</param>
    /// <param name="serviceKey">The wire service key, which is the deduplication identity.</param>
    private void DeliverLocked(
        ServiceWatchRegistration registration,
        NacosServiceWatchKind kind,
        NacosServiceKey service,
        string serviceKey)
    {
        NacosNamingLog.FuzzyWatchEvent(_logger, registration.Pattern, kind, service.ToString());

        foreach (ServiceWatchSubscription subscription in registration.Subscriptions)
        {
            bool deliver = kind switch
            {
                NacosServiceWatchKind.Added => subscription.KnownKeys.Add(serviceKey),
                NacosServiceWatchKind.Removed => subscription.KnownKeys.Remove(serviceKey),
                _ => true,
            };

            if (deliver)
            {
                // Unbounded on purpose: dropping an add or a remove would silently corrupt the
                // consumer's view of the matched set, and no later event repairs it.
                subscription.Queue.Writer.TryWrite(
                    new NacosServiceWatchEvent(kind, registration.Pattern, service));
            }
        }
    }

    /// <summary>
    /// Ends the initial phase and reports it to every subscription that has not seen it.
    /// </summary>
    /// <param name="registration">The watch that finished its initial catch-up.</param>
    private void MarkInitializedLocked(ServiceWatchRegistration registration)
    {
        bool first = !registration.Initialized;
        registration.Initialized = true;

        foreach (ServiceWatchSubscription subscription in registration.Subscriptions)
        {
            if (subscription.Initialized)
            {
                continue;
            }

            subscription.Initialized = true;
            subscription.Queue.Writer.TryWrite(new NacosServiceWatchEvent(
                NacosServiceWatchKind.InitialSyncCompleted,
                registration.Pattern,
                service: null));
        }

        if (first)
        {
            NacosNamingLog.FuzzyWatchInitialized(
                _logger, registration.Pattern, registration.KnownKeys.Count);
        }
    }

    /// <summary>
    /// Seeds a new subscription with the services the watch already holds.
    /// </summary>
    /// <param name="registration">The watch being joined.</param>
    /// <param name="subscription">The subscription to seed.</param>
    private static void ReplayKnownKeysLocked(
        ServiceWatchRegistration registration,
        ServiceWatchSubscription subscription)
    {
        foreach (string serviceKey in registration.KnownKeys)
        {
            if (!NacosFuzzyPattern.TryParseServiceKey(
                serviceKey,
                out string namespaceId,
                out string group,
                out string serviceName))
            {
                continue;
            }

            subscription.KnownKeys.Add(serviceKey);
            subscription.Queue.Writer.TryWrite(new NacosServiceWatchEvent(
                NacosServiceWatchKind.Added,
                registration.Pattern,
                new NacosServiceKey(serviceName, group, namespaceId)));
        }

        if (!registration.Initialized)
        {
            return;
        }

        subscription.Initialized = true;
        subscription.Queue.Writer.TryWrite(new NacosServiceWatchEvent(
            NacosServiceWatchKind.InitialSyncCompleted,
            registration.Pattern,
            service: null));
    }

    /// <summary>
    /// Undoes a registration whose send failed, unless a later call for the same service has
    /// replaced it.
    /// </summary>
    /// <param name="service">The service the registration belongs to.</param>
    /// <param name="failed">The registration whose send failed.</param>
    /// <param name="previous">
    /// The registration the failed attempt replaced, or <see langword="null"/> when the service had
    /// none.
    /// </param>
    /// <remarks>
    /// The failed attempt never reached the server, so the set the server still holds is the one to
    /// replay: forgetting it would silently drop an instance the next reconnect should restore.
    /// </remarks>
    private void Revert(NacosServiceKey service, Registration failed, Registration? previous)
    {
        // Revert only if the map still holds the failed attempt: a concurrent registration for the
        // same service replaced the entry and owns it now.
        if (previous is null)
        {
            ((ICollection<KeyValuePair<string, Registration>>)_registrations)
                .Remove(new KeyValuePair<string, Registration>(service.ToString(), failed));
        }
        else
        {
            _registrations.TryUpdate(service.ToString(), previous, failed);
        }
    }

    private static string SubscriptionId(NacosServiceKey service, string? clusters) =>
        string.Concat(service.ToString(), "|", clusters ?? string.Empty);

    /// <summary>A registration this client owns and must replay after a reconnect.</summary>
    /// <remarks>
    /// Keyed by service, not by endpoint: the server keeps one published instance set per service
    /// per connection, so the last registration for a service is the whole truth to replay.
    /// </remarks>
    private sealed class Registration
    {
        public Registration(
            NacosServiceKey service,
            IReadOnlyList<NacosInstance> instances,
            bool isBatch)
        {
            Service = service;
            Instances = instances;
            IsBatch = isBatch;
        }

        public NacosServiceKey Service { get; }

        /// <summary>Gets the instances the registration publishes.</summary>
        public IReadOnlyList<NacosInstance> Instances { get; }

        /// <summary>Gets a value indicating whether the instances are published as one batch.</summary>
        public bool IsBatch { get; }

        /// <summary>
        /// Gets or sets a value indicating whether the server acknowledged the registration.
        /// </summary>
        public bool Confirmed { get; set; }
    }

    /// <summary>A subscription this client owns and must replay after a reconnect.</summary>
    /// <remarks>
    /// One subscription is shared by every consumer that observes the same service and cluster
    /// filter, including consumers that narrow the list differently: the server-side subscription
    /// and its replay are per service, while the view each consumer sees is per consumer.
    /// </remarks>
    private sealed class Subscription
    {
        private readonly List<SubscriptionConsumer> _consumers = [];
        private readonly Lock _sync = new();
        private readonly ILogger _logger;

        public Subscription(NacosServiceKey service, string? clusters, ILogger logger)
        {
            Service = service;
            Clusters = clusters;
            _logger = logger;
        }

        public NacosServiceKey Service { get; }

        public string? Clusters { get; }

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
                    return _consumers.Count == 0;
                }
            }
        }

        /// <summary>Reports whether this subscription observes the given service.</summary>
        /// <param name="key">The service to check.</param>
        /// <returns><see langword="true"/> when the subscription observes it.</returns>
        public bool Matches(NacosServiceKey key) =>
            string.Equals(Service.ToString(), key.ToString(), StringComparison.Ordinal);

        /// <summary>Adds a consumer.</summary>
        /// <param name="consumer">The consumer to add.</param>
        public void Attach(SubscriptionConsumer consumer)
        {
            lock (_sync)
            {
                _consumers.Add(consumer);
            }
        }

        /// <summary>Removes a consumer.</summary>
        /// <param name="consumer">The consumer to remove.</param>
        public void Detach(SubscriptionConsumer consumer)
        {
            lock (_sync)
            {
                _consumers.Remove(consumer);
            }
        }

        /// <summary>
        /// Applies a state to every consumer, delivering it only where the selected view changed.
        /// </summary>
        /// <param name="info">The full state the server reported.</param>
        /// <returns><see langword="true"/> when at least one consumer was notified.</returns>
        /// <remarks>
        /// The comparison is per consumer because two consumers of one service may narrow it
        /// differently: a change that matters to one is invisible to the other, and the second must
        /// not be woken by it. A selector that throws is contained to its own consumer, because
        /// this runs on the push path and an escaping exception would abandon the other consumers
        /// and surface as an unobserved task failure.
        /// </remarks>
        public bool Publish(NacosServiceInfo info)
        {
            SubscriptionConsumer[] consumers;
            lock (_sync)
            {
                consumers = [.. _consumers];
            }

            bool delivered = false;

            foreach (SubscriptionConsumer consumer in consumers)
            {
                NacosServiceInfo selected;
                try
                {
                    selected = Select(consumer.Selector, Service, Clusters, info);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    NacosNamingLog.SelectorFailed(_logger, Service.ToString(), ex.Message);
                    continue;
                }

                if (SameInstances(consumer.Delivered, selected))
                {
                    continue;
                }

                NacosServiceInfo? previous = consumer.Delivered;
                consumer.Delivered = selected;
                delivered = true;

                NacosNamingLog.ServiceChanged(
                    _logger,
                    Service.ToString(),
                    selected.Instances.Count,
                    previous?.Instances.Count ?? 0);

                // A bounded channel that drops the oldest entry can still refuse a write if it is
                // momentarily full, which is acceptable: the next push carries the full state.
                consumer.Writer.TryWrite(new NacosServiceChange(selected, previous?.Checksum));
            }

            return delivered;
        }

        /// <summary>Ends every consumer's enumeration.</summary>
        public void Complete()
        {
            SubscriptionConsumer[] consumers;
            lock (_sync)
            {
                consumers = [.. _consumers];
                _consumers.Clear();
            }

            foreach (SubscriptionConsumer consumer in consumers)
            {
                consumer.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// One consumer's view of a subscription: its queue, its filter and what it was last told.
    /// </summary>
    private sealed class SubscriptionConsumer
    {
        public SubscriptionConsumer(
            ChannelWriter<NacosServiceChange> writer,
            INacosInstanceSelector? selector)
        {
            Writer = writer;
            Selector = selector;
        }

        /// <summary>Gets the queue this consumer reads from.</summary>
        public ChannelWriter<NacosServiceChange> Writer { get; }

        /// <summary>
        /// Gets the filter applied to every state, or <see langword="null"/> when the consumer sees
        /// the whole list.
        /// </summary>
        public INacosInstanceSelector? Selector { get; }

        /// <summary>
        /// Gets or sets the view most recently delivered to this consumer, which is the selected
        /// subset rather than the server's full list, and is what the next push is compared with.
        /// </summary>
        public NacosServiceInfo? Delivered { get; set; }
    }

    /// <summary>
    /// The shared state of one naming fuzzy watch pattern.
    /// </summary>
    private sealed class ServiceWatchRegistration
    {
        public ServiceWatchRegistration(string pattern, string @namespace)
        {
            Pattern = pattern;
            Namespace = @namespace;
        }

        /// <summary>Gets the pattern the server watches.</summary>
        public string Pattern { get; }

        /// <summary>
        /// Gets the namespace sent with every request, which scopes the watch to the namespace the
        /// client is configured for.
        /// </summary>
        public string Namespace { get; }

        /// <summary>
        /// Gets an object that guards <see cref="KnownKeys"/>, <see cref="Initialized"/> and
        /// <see cref="Subscriptions"/>.
        /// </summary>
        public object Sync { get; } = new();

        /// <summary>
        /// Gets the service keys the server last reported as matching, which is the set a new
        /// subscriber is seeded from and what the next diff is computed against.
        /// </summary>
        public HashSet<string> KnownKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets a value indicating whether the initial diff has been applied, so a later
        /// request asks for a diff rather than a fresh initialisation.
        /// </summary>
        public bool Initialized { get; set; }

        /// <summary>Gets the subscriptions that receive this watch's events.</summary>
        public List<ServiceWatchSubscription> Subscriptions { get; } = [];
    }

    /// <summary>
    /// One consumer's view of a naming fuzzy watch.
    /// </summary>
    private sealed class ServiceWatchSubscription
    {
        /// <summary>
        /// Gets the queue of events. It is unbounded because dropping an add or a remove would leave
        /// the consumer's set wrong with no way to notice, unlike a value stream where only the
        /// newest value matters.
        /// </summary>
        public Channel<NacosServiceWatchEvent> Queue { get; } =
            Channel.CreateUnbounded<NacosServiceWatchEvent>(
                new UnboundedChannelOptions { SingleReader = true });

        /// <summary>Gets the service keys already delivered to this subscription.</summary>
        public HashSet<string> KnownKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets or sets a value indicating whether this subscription has received its one
        /// <see cref="NacosServiceWatchKind.InitialSyncCompleted"/> marker.
        /// </summary>
        public bool Initialized { get; set; }
    }
}
