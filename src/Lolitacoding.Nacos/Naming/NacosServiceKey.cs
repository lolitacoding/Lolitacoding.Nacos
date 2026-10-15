namespace Lolitacoding.Nacos.Naming;

/// <summary>
/// Identifies a Nacos service.
/// </summary>
/// <remarks>
/// As with a configuration key, the namespace is optional so the common single-namespace
/// deployment does not repeat the configured default on every call.
/// </remarks>
public sealed record NacosServiceKey
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServiceKey"/> record.
    /// </summary>
    /// <param name="serviceName">The service name.</param>
    /// <param name="groupName">The service group.</param>
    /// <param name="namespace">
    /// The namespace id, or <see langword="null"/> to use the configured default.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="serviceName"/> or <paramref name="groupName"/> is null or whitespace.
    /// </exception>
    public NacosServiceKey(string serviceName, string groupName, string? @namespace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        ServiceName = serviceName;
        GroupName = groupName;
        Namespace = @namespace;
    }

    /// <summary>Gets the service name.</summary>
    public string ServiceName { get; }

    /// <summary>Gets the service group.</summary>
    public string GroupName { get; }

    /// <summary>
    /// Gets the namespace id, or <see langword="null"/> to use the configured default.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// Applies the configured default namespace when this key does not name one.
    /// </summary>
    /// <param name="defaultNamespace">The namespace to fall back to.</param>
    /// <returns>A key with a concrete namespace.</returns>
    internal NacosServiceKey WithDefaultNamespace(string defaultNamespace) =>
        string.IsNullOrEmpty(Namespace) ? this with { Namespace = defaultNamespace } : this;

    /// <inheritdoc />
    public override string ToString() => string.Concat(
        string.IsNullOrEmpty(Namespace) ? "<default>" : Namespace,
        "/", GroupName, "@@", ServiceName);
}

/// <summary>
/// Describes a service instance to register or one returned by the server.
/// </summary>
/// <remarks>
/// <para>
/// This is an immutable description rather than a mutable object because the same instance
/// description is replayed on every reconnect: mutating it while a re-registration is in flight
/// would make the server's state depend on a race.
/// </para>
/// <para>
/// The heartbeat properties are derived from reserved metadata keys by the official client. They
/// are modelled as ordinary properties here because the resulting wire form is identical.
/// </para>
/// </remarks>
public sealed record NacosInstance
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosInstance"/> record.
    /// </summary>
    /// <param name="ipAddress">The instance address.</param>
    /// <param name="port">The instance port.</param>
    /// <param name="weight">The load-balancing weight.</param>
    /// <param name="clusterName">The cluster, or <see langword="null"/> to use the default.</param>
    /// <param name="healthy">Whether the instance is healthy.</param>
    /// <param name="enabled">Whether the instance accepts traffic.</param>
    /// <param name="ephemeral">Whether the instance is removed when its heartbeat stops.</param>
    /// <param name="metadata">The instance metadata.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="ipAddress"/> is null or whitespace.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="port"/> or <paramref name="weight"/> is outside its valid range.
    /// </exception>
    public NacosInstance(
        string ipAddress,
        int port,
        double weight = 1.0,
        string? clusterName = null,
        bool healthy = true,
        bool enabled = true,
        bool ephemeral = true,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ipAddress);

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port), port, "An instance port must be between 1 and 65535.");
        }

        // Non-finite weights cannot be serialized as ordinary JSON numbers or balanced safely.
        if (!double.IsFinite(weight) || weight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(weight), weight, "An instance weight must be finite and greater than zero.");
        }

        IpAddress = ipAddress;
        Port = port;
        Weight = weight;
        ClusterName = clusterName;
        Healthy = healthy;
        Enabled = enabled;
        Ephemeral = ephemeral;
        Metadata = metadata;
    }

    /// <summary>Gets the instance address.</summary>
    public string IpAddress { get; }

    /// <summary>Gets the instance port.</summary>
    public int Port { get; }

    /// <summary>Gets the load-balancing weight.</summary>
    public double Weight { get; }

    /// <summary>Gets the cluster, or <see langword="null"/> to use the configured default.</summary>
    public string? ClusterName { get; init; }

    /// <summary>
    /// Gets a value indicating whether the server considers the instance healthy.
    /// </summary>
    /// <remarks>
    /// On registration this is advisory: the server decides health from heartbeats for ephemeral
    /// instances, so a caller-set value only takes effect when the instance is persistent.
    /// </remarks>
    public bool Healthy { get; init; }

    /// <summary>Gets a value indicating whether the instance accepts traffic.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets a value indicating whether the instance is removed when its heartbeat stops.
    /// </summary>
    public bool Ephemeral { get; init; }

    /// <summary>Gets the instance metadata.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>Gets the server-assigned instance identifier, when known.</summary>
    public string? InstanceId { get; init; }

    /// <summary>
    /// Gets a value indicating whether this instance can receive traffic.
    /// </summary>
    /// <remarks>
    /// Both flags matter: the server may report an instance as healthy while it is administratively
    /// disabled, and such an instance must not be selected.
    /// </remarks>
    public bool IsSelectable => Healthy && Enabled;

    /// <summary>Gets the <c>host:port</c> form of the instance address.</summary>
    public string Endpoint => string.Concat(IpAddress, ":", Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public override string ToString() =>
        ClusterName is null ? Endpoint : string.Concat(Endpoint, "@", ClusterName);
}

/// <summary>
/// The result of looking up the instances of a service.
/// </summary>
public sealed record NacosServiceInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServiceInfo"/> record.
    /// </summary>
    /// <param name="key">The service that was queried.</param>
    /// <param name="instances">The instances.</param>
    /// <param name="clusters">The cluster list reported by the server.</param>
    /// <param name="checksum">The checksum used to detect changes.</param>
    /// <param name="fromLocalCache">Whether the value came from a local cache.</param>
    internal NacosServiceInfo(
        NacosServiceKey key,
        IReadOnlyList<NacosInstance> instances,
        string? clusters,
        string? checksum,
        bool fromLocalCache)
    {
        Key = key;
        Instances = instances;
        Clusters = clusters;
        Checksum = checksum;
        FromLocalCache = fromLocalCache;
    }

    /// <summary>Gets the service that was queried.</summary>
    public NacosServiceKey Key { get; init; }

    /// <summary>Gets every instance the server reported.</summary>
    public IReadOnlyList<NacosInstance> Instances { get; init; }

    /// <summary>Gets the cluster list reported by the server.</summary>
    public string? Clusters { get; init; }

    /// <summary>Gets the checksum used to detect changes.</summary>
    public string? Checksum { get; init; }

    /// <summary>
    /// Gets a value indicating whether the value was served from a local cache because the server
    /// was unreachable.
    /// </summary>
    public bool FromLocalCache { get; init; }

    /// <summary>Gets the instances that can receive traffic.</summary>
    /// <remarks>
    /// Filtering here rather than at each call site means a caller cannot accidentally route to a
    /// disabled or unhealthy instance by forgetting the check.
    /// </remarks>
    public IReadOnlyList<NacosInstance> HealthyInstances =>
        [.. Instances.Where(instance => instance.IsSelectable)];
}

/// <summary>
/// Describes a change to a subscribed service.
/// </summary>
public sealed record NacosServiceChange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServiceChange"/> record.
    /// </summary>
    /// <param name="service">The new service state.</param>
    /// <param name="previousChecksum">The checksum of the state that was replaced.</param>
    internal NacosServiceChange(NacosServiceInfo service, string? previousChecksum)
    {
        Service = service;
        PreviousChecksum = previousChecksum;
    }

    /// <summary>Gets the new service state.</summary>
    public NacosServiceInfo Service { get; }

    /// <summary>Gets the checksum of the state that was replaced.</summary>
    public string? PreviousChecksum { get; }

    /// <summary>Gets the key of the service that changed.</summary>
    public NacosServiceKey Key => Service.Key;

    /// <summary>Gets the new instances.</summary>
    public IReadOnlyList<NacosInstance> Instances => Service.Instances;
}
