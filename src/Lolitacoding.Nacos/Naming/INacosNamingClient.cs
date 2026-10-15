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
}
