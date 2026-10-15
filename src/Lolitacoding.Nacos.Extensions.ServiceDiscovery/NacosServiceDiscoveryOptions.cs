namespace Lolitacoding.Nacos.Extensions.ServiceDiscovery;

/// <summary>
/// Configures how services are resolved from the Nacos registry.
/// </summary>
/// <remarks>
/// <para>
/// The options decide which service names this provider claims. Claiming too much is the more
/// dangerous mistake: a name it claims but cannot resolve would otherwise have been handled by
/// another provider, so the defaults are chosen to leave ordinary host names alone.
/// </para>
/// <para>
/// A URI that carries one of the configured <see cref="Schemes"/> is always claimed, which is how
/// a caller opts in explicitly, for example <c>nacos://order-service</c> or, with the built-in
/// scheme-composition support, <c>http+nacos://order-service</c>.
/// </para>
/// </remarks>
public sealed class NacosServiceDiscoveryOptions
{
    /// <summary>
    /// Gets or sets the URI schemes that select the Nacos provider.
    /// </summary>
    /// <remarks>
    /// A service discovery URI may combine a transport scheme with a discovery scheme using
    /// <c>+</c>, as in <c>http+nacos://order-service</c>. Including <c>nacos</c> here is what makes
    /// the Nacos provider responsible for such a URI, while the leading scheme still decides the
    /// protocol of the resulting request.
    /// </remarks>
    public IList<string> Schemes { get; set; } = [NacosServiceDiscoveryDefaults.Scheme];

    /// <summary>
    /// Gets or sets a value indicating whether a service name that carries no scheme at all is
    /// resolved from Nacos.
    /// </summary>
    /// <remarks>
    /// A bare name such as <c>order-service</c> has no scheme to match on. Treating it as a Nacos
    /// service is the useful default for an application whose services all live in Nacos, because
    /// it makes names such as <c>order-service</c> work without a scheme prefix. Set this to
    /// <see langword="false"/> to require an explicit scheme so that host names meant for the
    /// pass-through provider are never looked up in Nacos.
    /// </remarks>
    public bool ResolveAllServices { get; set; } = true;

    /// <summary>
    /// Gets or sets the Nacos group the resolved services belong to.
    /// </summary>
    /// <remarks>
    /// A service discovery name is a bare service name with no room for a group, so the group is
    /// configured here instead of being encoded in every URI.
    /// </remarks>
    public string GroupName { get; set; } = NacosConstants.DefaultGroup;

    /// <summary>
    /// Gets or sets the cluster to restrict resolution to, or <see langword="null"/> for every
    /// cluster.
    /// </summary>
    /// <remarks>
    /// Clusters partition a service by, for example, data centre. Restricting resolution here lets
    /// a client prefer a nearby cluster without changing the URI.
    /// </remarks>
    public string? Cluster { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only healthy, enabled instances become endpoints.
    /// </summary>
    /// <remarks>
    /// Enabled by default: routing to an unhealthy or administratively disabled instance is almost
    /// never intended. Disable it only when the caller applies its own health check.
    /// </remarks>
    public bool HealthyOnly { get; set; } = true;
}
