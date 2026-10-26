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
    /// Registering the same instance twice is idempotent, so a caller may safely re-register on
    /// startup without checking first.
    /// </remarks>
    Task RegisterInstanceAsync(
        NacosServiceKey service,
        NacosInstance instance,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a service instance.
    /// </summary>
    /// <param name="service">The service the instance belongs to.</param>
    /// <param name="instance">The instance to remove.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes once the server accepted the removal.</returns>
    Task DeregisterInstanceAsync(
        NacosServiceKey service,
        NacosInstance instance,
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
