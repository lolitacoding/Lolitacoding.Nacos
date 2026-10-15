namespace Lolitacoding.Nacos.Naming;

/// <summary>
/// Selects one instance from a service's instance list.
/// </summary>
/// <remarks>
/// <para>
/// A strategy is a plain object over a list rather than a method on the client. Load balancing is
/// an application concern — which replica to call next is a decision about the caller's traffic,
/// not about how to talk to the registry — and keeping it separate means it can be swapped,
/// tested and composed without touching the client.
/// </para>
/// <para>
/// Implementations must not cache the list they are given: the caller passes the current list on
/// every call so a changed instance set takes effect immediately.
/// </para>
/// </remarks>
public interface INacosLoadBalancer
{
    /// <summary>
    /// Chooses one instance from a list.
    /// </summary>
    /// <param name="instances">The candidate instances.</param>
    /// <returns>
    /// The chosen instance, or <see langword="null"/> when there is nothing to choose from.
    /// </returns>
    /// <remarks>
    /// Returning <see langword="null"/> rather than throwing makes the empty case part of the
    /// return type, since a service with no available instances is a normal transient state during
    /// a deployment.
    /// </remarks>
    NacosInstance? Select(IReadOnlyList<NacosInstance> instances);
}

/// <summary>
/// Selects instances in rotation.
/// </summary>
/// <remarks>
/// The counter is advanced with an atomic increment, so concurrent callers each get a distinct
/// turn rather than colliding on the same instance. Rotation is over the list it is handed, not
/// over remembered identities: when the instance set changes, the next call simply starts at a
/// different index in the new list.
/// </remarks>
public sealed class NacosRoundRobinLoadBalancer : INacosLoadBalancer
{
    private long _counter;

    /// <inheritdoc />
    public NacosInstance? Select(IReadOnlyList<NacosInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);

        if (instances.Count == 0)
        {
            return null;
        }

        long turn = unchecked(Interlocked.Increment(ref _counter) - 1);

        // Keep the modulo in 64 bits: narrowing first would produce a negative index once the
        // turn passes int.MaxValue. The mask also keeps indices valid when the long wraps.
        int index = (int)((turn & long.MaxValue) % instances.Count);

        return instances[index];
    }
}

/// <summary>
/// Selects instances randomly, favouring the ones with a higher weight.
/// </summary>
/// <remarks>
/// <para>
/// Weighted selection is what makes a Nacos weight meaningful: an instance registered with weight
/// two should receive roughly twice the traffic of one registered with weight one.
/// </para>
/// <para>
/// The random source is injectable so a test can make the choice deterministic instead of
/// asserting on a distribution.
/// </para>
/// </remarks>
public sealed class NacosWeightedRandomLoadBalancer : INacosLoadBalancer
{
    private readonly Random _random;
    private readonly object _randomLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosWeightedRandomLoadBalancer"/> class.
    /// </summary>
    /// <param name="random">The random source, or <see langword="null"/> for a shared one.</param>
    public NacosWeightedRandomLoadBalancer(Random? random = null) => _random = random ?? Random.Shared;

    /// <inheritdoc />
    public NacosInstance? Select(IReadOnlyList<NacosInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);

        if (instances.Count == 0)
        {
            return null;
        }

        if (instances.Count == 1)
        {
            return instances[0];
        }

        double maximum = 0;
        foreach (NacosInstance instance in instances)
        {
            maximum = Math.Max(maximum, instance.Weight);
        }

        // Normalize before summing: even several finite weights can otherwise overflow to
        // infinity. Each term is at most one, so the total is bounded by the list count.
        // This also avoids loss of resolution when every weight is subnormal.
        double total = 0;
        foreach (NacosInstance instance in instances)
        {
            total += instance.Weight / maximum;
        }

        double sample;
        lock (_randomLock)
        {
            // Random.Shared is thread-safe, but an injected Random need not be. Only the draw
            // needs serialization; the current instance list is never retained or locked.
            sample = _random.NextDouble();
        }

        double target = sample * total;
        double cumulative = 0;

        foreach (NacosInstance instance in instances)
        {
            cumulative += instance.Weight / maximum;

            if (target < cumulative)
            {
                return instance;
            }
        }

        // Reached only through floating-point rounding at the very end of the range; the last
        // instance is the correct answer there.
        return instances[^1];
    }
}

/// <summary>
/// Selects instances by round robin while preferring the cluster the caller is in.
/// </summary>
/// <remarks>
/// Cluster affinity lets a deployment keep traffic inside a zone when possible without giving up
/// the fallback: when the local cluster has no available instance, any other cluster is used
/// rather than failing the call.
/// </remarks>
public sealed class NacosClusterAwareLoadBalancer : INacosLoadBalancer
{
    private readonly string _preferredCluster;
    private readonly NacosRoundRobinLoadBalancer _preferred = new();
    private readonly NacosRoundRobinLoadBalancer _fallback = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosClusterAwareLoadBalancer"/> class.
    /// </summary>
    /// <param name="preferredCluster">The cluster to prefer.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="preferredCluster"/> is null or whitespace.
    /// </exception>
    public NacosClusterAwareLoadBalancer(string preferredCluster)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preferredCluster);
        _preferredCluster = preferredCluster;
    }

    /// <inheritdoc />
    public NacosInstance? Select(IReadOnlyList<NacosInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(instances);

        if (instances.Count == 0)
        {
            return null;
        }

        List<NacosInstance> local = [.. instances.Where(
            instance => string.Equals(instance.ClusterName, _preferredCluster, StringComparison.Ordinal))];

        // Separate rotation counters for the two groups keep the preference stable: sharing one
        // counter would let a fallback call shift the local rotation.
        return local.Count > 0 ? _preferred.Select(local) : _fallback.Select(instances);
    }
}
