namespace Lolitacoding.Nacos.AspNetCore;

/// <summary>
/// Configures automatic service registration for an ASP.NET Core application.
/// </summary>
/// <remarks>
/// <para>
/// The address advertised to Nacos is inferred from the addresses the server is actually bound
/// to, so the common case needs no address configuration at all. The explicit properties exist
/// for the cases the inference cannot cover: a container behind a port mapping, or a host that
/// binds a wildcard address and must advertise a routable one.
/// </para>
/// </remarks>
public sealed class NacosServiceRegistrationOptions
{
    /// <summary>
    /// Gets or sets the service name to register under.
    /// </summary>
    /// <remarks>
    /// When left unset the entry assembly's name is used, which is the name other services are
    /// most likely to look this one up by.
    /// </remarks>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Gets or sets the group the service is registered in.
    /// </summary>
    public string GroupName { get; set; } = NacosConstants.DefaultGroup;

    /// <summary>
    /// Gets or sets the cluster the instance is registered in.
    /// </summary>
    /// <remarks>
    /// Clusters partition a service by, for example, data centre, which lets a consumer prefer a
    /// nearby cluster. Left unset, the server records the instance in the default cluster.
    /// </remarks>
    public string? ClusterName { get; set; }

    /// <summary>
    /// Gets or sets the IP address to advertise.
    /// </summary>
    /// <remarks>
    /// When unset the address is inferred from the server's bound addresses, falling back to the
    /// first non-loopback interface address when the server binds a wildcard address.
    /// </remarks>
    public string? Ip { get; set; }

    /// <summary>
    /// Gets or sets the port to advertise.
    /// </summary>
    /// <remarks>When unset the port is inferred from the server's bound addresses.</remarks>
    public int? Port { get; set; }

    /// <summary>
    /// Gets or sets the load-balancing weight of the instance.
    /// </summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets a value indicating whether the instance is ephemeral.
    /// </summary>
    /// <remarks>
    /// An ephemeral instance is removed by the server as soon as its connection is lost, which is
    /// the correct behaviour for a container that may be rescheduled. A persistent instance
    /// survives a crash and must be deregistered explicitly, which suits a stable, long-lived host.
    /// </remarks>
    public bool Ephemeral { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the instance is deregistered when the host stops.
    /// </summary>
    /// <remarks>
    /// Deregistering during a graceful shutdown removes the instance immediately instead of
    /// waiting for the server to notice the missing heartbeat, which shortens the window in which
    /// callers can be routed to an instance that is already gone.
    /// </remarks>
    public bool DeregisterOnStop { get; set; } = true;

    /// <summary>
    /// Gets or sets the health-check path advertised as instance metadata.
    /// </summary>
    /// <remarks>
    /// Optional. Nacos consumes this only for server-side health checking; it is primarily useful
    /// for surfacing the path in tooling and for consumers that probe the instance themselves. It
    /// is published under the <c>health.check.path</c> metadata key.
    /// </remarks>
    public string? HealthCheckPath { get; set; }

    /// <summary>
    /// Gets or sets additional instance metadata.
    /// </summary>
    /// <remarks>
    /// Metadata is the extension point for application-defined routing hints such as a version or
    /// a zone. Library-defined keys are added on top of whatever is supplied here.
    /// </remarks>
    public IDictionary<string, string> Metadata { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the metadata key the health-check path is published under.
    /// </summary>
    internal const string HealthCheckPathKey = "health.check.path";

    /// <summary>
    /// Gets the metadata key recording which client performed the registration.
    /// </summary>
    internal const string RegisterSourceKey = "preserved.register.source";

    /// <summary>
    /// Gets the value published under <see cref="RegisterSourceKey"/>.
    /// </summary>
    internal const string RegisterSourceValue = "Lolitacoding.Nacos.AspNetCore";

    /// <summary>
    /// Resolves the service name, falling back to the entry assembly's name.
    /// </summary>
    /// <returns>The service name.</returns>
    /// <exception cref="NacosConfigurationException">No name could be determined.</exception>
    internal string ResolveServiceName()
    {
        if (!string.IsNullOrWhiteSpace(ServiceName))
        {
            return ServiceName;
        }

        string? entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;

        if (string.IsNullOrWhiteSpace(entryAssembly))
        {
            throw new NacosConfigurationException(
                "NacosServiceRegistrationOptions.ServiceName was not set and the entry assembly name could not be determined; set ServiceName explicitly.");
        }

        return entryAssembly;
    }
}
