namespace Lolitacoding.Nacos.Naming;

/// <summary>
/// Registers service instances and discovers them.
/// </summary>
/// <remarks>
/// Implementations are expected to be safe for concurrent use, since a hosted application will
/// serve many requests through one client.
/// </remarks>
public interface INacosNamingClient : IAsyncDisposable
{
    /// <summary>
    /// Registers or updates a service instance.
    /// </summary>
    /// <param name="service">The service to register under.</param>
    /// <param name="instance">The instance to register.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes once the server accepted the registration.</returns>
    /// <remarks>
    /// <para>
    /// Registering the same instance twice is idempotent, so a caller may safely re-register on
    /// startup without checking first.
    /// </para>
    /// <para>
    /// The server keeps one published instance set per service per connection: this call replaces
    /// whatever this client last published for the service, including the instances of a previous
    /// <see cref="RegisterInstancesAsync"/>. Use the batch method to publish several instances of
    /// one service from one connection.
    /// </para>
    /// </remarks>
    Task RegisterInstanceAsync(
        NacosServiceKey service,
        NacosInstance instance,
        CancellationToken ct = default);

    /// <summary>
    /// Registers several instances of one service in a single round trip.
    /// </summary>
    /// <param name="service">The service to register the instances under.</param>
    /// <param name="instances">The instances to publish together.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes once the server accepted the batch.</returns>
    /// <remarks>
    /// <para>
    /// This is the batch form of <see cref="RegisterInstanceAsync"/>: one request publishes the whole
    /// list instead of one request per instance. The list replaces whatever this client last
    /// published for the service, so a second batch or a single registration supersedes the first
    /// rather than adding to it. That is the server's model — one published instance set per
    /// service per connection — not a limitation of this client, and it is why a deployment that
    /// must publish several instances of one service either uses this method or opens one
    /// connection per instance.
    /// </para>
    /// <para>
    /// Every instance must be ephemeral. Nacos has no batch path for persistent instances: the
    /// server's batch handler is the ephemeral one, and the persistent store's batch method is still
    /// unimplemented upstream, so a persistent instance would be dropped. It is refused here before
    /// anything is sent.
    /// </para>
    /// <para>
    /// The list is published as given. Duplicate endpoints are not removed; the server stores the
    /// list it receives.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="instances"/> is empty or contains a <see langword="null"/> element, or one of
    /// the instances is persistent.
    /// </exception>
    Task RegisterInstancesAsync(
        NacosServiceKey service,
        IReadOnlyList<NacosInstance> instances,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a service instance.
    /// </summary>
    /// <param name="service">The service the instance belongs to.</param>
    /// <param name="instance">The instance to remove.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes once the server accepted the removal.</returns>
    /// <remarks>
    /// The server removes the published instance set of the service on this connection, so any other
    /// instance this client published for the same service — including the rest of a batch — goes
    /// with it. The argument identifies the service and is echoed in the request; it does not select
    /// one entry out of a batch.
    /// </remarks>
    Task DeregisterInstanceAsync(
        NacosServiceKey service,
        NacosInstance instance,
        CancellationToken ct = default);

    /// <summary>
    /// Removes some instances of a service that was published with
    /// <see cref="RegisterInstancesAsync"/>.
    /// </summary>
    /// <param name="service">The service the instances belong to.</param>
    /// <param name="instances">The instances to remove.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes once the server accepted the new set.</returns>
    /// <remarks>
    /// <para>
    /// Nacos has no batch deregistration message. The official client removes the named instances
    /// from the batch it last registered and publishes the remainder as a new batch, which is what
    /// this method does; when nothing remains it sends an ordinary deregistration instead of an
    /// empty batch. Instances are matched by address and port, the same comparison the official
    /// client uses.
    /// </para>
    /// <para>
    /// The method only applies to a batch published by this client. Removing instances of a single
    /// registration is what <see cref="DeregisterInstanceAsync"/> is for, and there is nothing to
    /// subtract from a registration this client does not own.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="instances"/> is empty or contains a <see langword="null"/> element.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// This client did not publish <paramref name="service"/> with
    /// <see cref="RegisterInstancesAsync"/>.
    /// </exception>
    Task DeregisterInstancesAsync(
        NacosServiceKey service,
        IReadOnlyList<NacosInstance> instances,
        CancellationToken ct = default);

    /// <summary>
    /// Lists the instances of a service.
    /// </summary>
    /// <param name="service">The service to look up.</param>
    /// <param name="cluster">
    /// A cluster to restrict the result to, or <see langword="null"/> for every cluster.
    /// </param>
    /// <param name="healthyOnly">
    /// Whether to ask the server to exclude unhealthy instances.
    /// </param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>The matching instances.</returns>
    /// <exception cref="NacosConnectionException">
    /// The server could not be reached and no local cache was available.
    /// </exception>
    Task<NacosServiceInfo> GetInstancesAsync(
        NacosServiceKey service,
        string? cluster = null,
        bool healthyOnly = true,
        CancellationToken ct = default);

    /// <summary>
    /// Lists one page of the services in a group.
    /// </summary>
    /// <param name="pageNo">The one-based page number.</param>
    /// <param name="pageSize">The number of names to return per page.</param>
    /// <param name="groupName">
    /// The group to list, or <see langword="null"/> for <see cref="NacosConstants.DefaultGroup"/>.
    /// </param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>The names on the requested page and the total number in the group.</returns>
    /// <remarks>
    /// <para>
    /// This is the paged alternative to a fuzzy watch: it names every service in a group, including
    /// services the client has never subscribed to, at the cost of one round trip per page and no
    /// change notifications. Use
    /// <see cref="WatchAsync(string, CancellationToken)"/> when the set has to stay current.
    /// </para>
    /// <para>
    /// The server pages a snapshot of the group, so a service created or removed between two page
    /// reads can shift the names across the page boundaries. A caller that needs a consistent view
    /// should read all pages and reconcile the union, or follow the group with a watch.
    /// </para>
    /// <para>
    /// The result's <see cref="NacosServiceListPage.Count"/> is the total for the group, not the size
    /// of the page. A page beyond the last one is not an error: it returns no names and still
    /// reports the count.
    /// </para>
    /// <para>
    /// The Java SDK's selector argument is not reproduced. Nacos 3.x accepts a selector field on the
    /// request but the server handler ignores it (<c>TODO select service by selector</c> upstream),
    /// so exposing one here would advertise a filter that does nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pageNo"/> or <paramref name="pageSize"/> is less than one.
    /// </exception>
    /// <exception cref="NacosConnectionException">
    /// The server could not be reached. Unlike an instance query this is not served from a local
    /// cache: the list describes the namespace, and a stale copy would silently hide new services.
    /// </exception>
    Task<NacosServiceListPage> GetServicesAsync(
        int pageNo = 1,
        int pageSize = 10,
        string? groupName = null,
        CancellationToken ct = default);

    /// <summary>
    /// Observes a service's instance list until the caller stops enumerating or cancels.
    /// </summary>
    /// <param name="service">The service to observe.</param>
    /// <param name="clusters">
    /// A comma-separated cluster filter, or <see langword="null"/> for every cluster.
    /// </param>
    /// <param name="ct">A token that ends the enumeration.</param>
    /// <returns>
    /// A stream of changes. The first element reports the state currently known, so a consumer can
    /// initialise from the same path that handles later changes.
    /// </returns>
    /// <remarks>
    /// The stream never completes on its own: a subscription that quietly ended would leave an
    /// application routing to a stale instance list with no indication that anything was wrong.
    /// </remarks>
    IAsyncEnumerable<NacosServiceChange> SubscribeAsync(
        NacosServiceKey service,
        string? clusters = null,
        CancellationToken ct = default);

    /// <summary>
    /// Observes the part of a service's instance list a selector keeps.
    /// </summary>
    /// <param name="service">The service to observe.</param>
    /// <param name="selector">The client-side filter to apply to every reported instance list.</param>
    /// <returns>
    /// A stream of changes in which every instance list is the selected subset of the state the
    /// server reported.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the selector overload of the official Java SDK's
    /// <c>NamingService.subscribe(serviceName, groupName, selector, listener)</c>. The server is
    /// asked for every cluster and the narrowing happens on the client, which is what the Java
    /// implementation does: its selector overload subscribes with an empty cluster filter. Use
    /// <see cref="NacosInstanceSelectors.Clusters"/> when the cluster is all you want to narrow by,
    /// and the clusters overload of the plain <c>SubscribeAsync</c> when the server should do the
    /// narrowing.
    /// </para>
    /// <para>
    /// A push whose selected subset is unchanged is not delivered, so a subscriber is not woken by
    /// an instance it filtered out. The first element is the selected subset of the state known when
    /// the stream opened, and later elements follow the same shape.
    /// </para>
    /// <para>
    /// The selector runs on the client's push path. A selector that throws is reported and the
    /// subscription keeps the view it last delivered, rather than the failure escaping into the
    /// shared read loop.
    /// </para>
    /// <para>
    /// This overload takes no token of its own: the token is supplied when the stream is enumerated,
    /// with the <c>WithCancellation</c> extension, as in
    /// <c>await foreach (NacosServiceChange change in
    /// client.SubscribeAsync(service, selector).WithCancellation(ct))</c>. Leaving the enumeration,
    /// which a <c>break</c> does, unsubscribes as well. Two subscription overloads that both ended in
    /// an optional token would be indistinguishable at a call site that names only the service, so
    /// the overloads differ by their second argument instead.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<NacosServiceChange> SubscribeAsync(
        NacosServiceKey service,
        INacosInstanceSelector selector);

    /// <summary>
    /// Observes which services match a pattern until the caller stops enumerating or cancels.
    /// </summary>
    /// <param name="serviceNamePattern">
    /// The service name pattern: a literal name, <c>*</c>, <c>prefix*</c>, <c>*suffix</c> or
    /// <c>*text*</c>.
    /// </param>
    /// <param name="groupPattern">The group pattern, with the same forms.</param>
    /// <param name="ct">A token that ends the enumeration.</param>
    /// <returns>
    /// A stream of service events. The initial catch-up reports every service that matched when the
    /// watch began, then ends with <see cref="NacosServiceWatchKind.InitialSyncCompleted"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This is the naming counterpart of the configuration fuzzy watch: it follows the <em>set</em>
    /// of services that match, not the instances of any one service. An event names a service, and a
    /// consumer that needs its instances opens a subscription for it with
    /// <see cref="SubscribeAsync(NacosServiceKey, string?, CancellationToken)"/> or reads them with
    /// <see cref="GetInstancesAsync"/>.
    /// </para>
    /// <para>
    /// The watch is scoped to the namespace the client is configured for, because one pattern cannot
    /// span namespaces. The stream never completes on its own; it ends when the token is cancelled
    /// or the client is disposed.
    /// </para>
    /// </remarks>
    /// <exception cref="NacosServerException">
    /// The server states that it does not implement fuzzy watch, or refuses the pattern because its
    /// pattern or match limit was reached.
    /// </exception>
    IAsyncEnumerable<NacosServiceWatchEvent> WatchAsync(
        string serviceNamePattern,
        string groupPattern,
        CancellationToken ct = default);

    /// <summary>
    /// Observes every service in a group until the caller stops enumerating or cancels.
    /// </summary>
    /// <param name="groupPattern">The group pattern.</param>
    /// <param name="ct">A token that ends the enumeration.</param>
    /// <returns>
    /// A stream of service events, as <see cref="WatchAsync(string, string, CancellationToken)"/>.
    /// </returns>
    /// <remarks>
    /// The token is required rather than optional: a watch that follows every service in a group
    /// would otherwise have no way to be ended by accident, and an accidental unbounded watch is
    /// harder to notice than a missing argument.
    /// </remarks>
    IAsyncEnumerable<NacosServiceWatchEvent> WatchAsync(
        string groupPattern,
        CancellationToken ct);
}
