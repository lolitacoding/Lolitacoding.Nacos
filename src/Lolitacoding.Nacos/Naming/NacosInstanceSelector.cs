using System.Text.RegularExpressions;

namespace Lolitacoding.Nacos.Naming;

/// <summary>
/// Narrows the instance list a subscription delivers.
/// </summary>
/// <remarks>
/// <para>
/// This is the client-side selector of the official Java SDK
/// (<c>com.alibaba.nacos.api.naming.selector.NamingSelector</c>): a filter applied to the instances
/// the server reports, so a consumer can follow one zone, one label or one address range without
/// asking the server for a narrower list. It is <em>not</em> a load balancer — it keeps a subset,
/// where <see cref="INacosLoadBalancer"/> picks one instance from whatever it is given.
/// </para>
/// <para>
/// A selector is called on every push, on the client's push path, for every subscription that uses
/// it. Implementations must therefore be fast, must not block, and must be safe for concurrent
/// calls: two pushes can be applied at the same time.
/// </para>
/// <para>
/// <see cref="Select"/> must return instances taken from
/// <see cref="NacosInstanceSelectorContext.Instances"/>; it is a filter, not a factory. Returning
/// instances the client did not supply would deliver state the server never reported, and the
/// next push would then appear to undo it.
/// </para>
/// </remarks>
public interface INacosInstanceSelector
{
    /// <summary>
    /// Chooses the instances to deliver.
    /// </summary>
    /// <param name="context">The service, its clusters and the instances to filter.</param>
    /// <returns>The instances this subscription should see.</returns>
    /// <remarks>
    /// The result is compared with the previous result, and a push whose result is unchanged is not
    /// delivered. That comparison is what keeps a subscriber from being woken by a change to an
    /// instance it filtered out.
    /// </remarks>
    IReadOnlyList<NacosInstance> Select(NacosInstanceSelectorContext context);
}

/// <summary>
/// What a selector is given: the service being observed and the instances the server reported.
/// </summary>
/// <remarks>
/// The context mirrors the Java SDK's <c>NamingContext</c>. The built-in selectors only read
/// <see cref="Instances"/>, but a selector that needs to know which service or cluster filter it is
/// running for can read the other two.
/// </remarks>
public sealed class NacosInstanceSelectorContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosInstanceSelectorContext"/> class.
    /// </summary>
    /// <param name="service">The service the instances belong to.</param>
    /// <param name="clusters">The cluster filter the subscription was opened with, if any.</param>
    /// <param name="instances">The instances the server reported.</param>
    internal NacosInstanceSelectorContext(
        NacosServiceKey service,
        string? clusters,
        IReadOnlyList<NacosInstance> instances)
    {
        Service = service;
        Clusters = clusters;
        Instances = instances;
    }

    /// <summary>Gets the service the instances belong to.</summary>
    public NacosServiceKey Service { get; }

    /// <summary>
    /// Gets the cluster filter the subscription was opened with, or <see langword="null"/> when it
    /// observes every cluster.
    /// </summary>
    /// <remarks>
    /// A subscription opened with a selector observes every cluster, because the narrowing is the
    /// selector's job. This is therefore usually <see langword="null"/>.
    /// </remarks>
    public string? Clusters { get; }

    /// <summary>Gets the instances the server reported.</summary>
    public IReadOnlyList<NacosInstance> Instances { get; }
}

/// <summary>
/// Builds the client-side selectors a subscription can use.
/// </summary>
/// <remarks>
/// The set mirrors <c>com.alibaba.nacos.client.naming.selector.NamingSelectorFactory</c>. The Java
/// SDK also had CMDB-label and expression selectors in earlier versions; they were removed upstream
/// in 3.x, so they are not reproduced here.
/// </remarks>
public static class NacosInstanceSelectors
{
    /// <summary>
    /// Gets a selector that keeps every instance.
    /// </summary>
    /// <remarks>
    /// This is the Java SDK's <c>EMPTY_SELECTOR</c>: it exists so a caller can hold a selector
    /// unconditionally, not because it changes what is delivered.
    /// </remarks>
    public static INacosInstanceSelector All { get; } = new AllInstancesSelector();

    /// <summary>
    /// Gets a selector that keeps the instances the server reports as healthy.
    /// </summary>
    /// <remarks>
    /// Only the <c>healthy</c> flag is consulted, which is what the Java SDK's
    /// <c>HEALTHY_SELECTOR</c> does. This differs from
    /// <see cref="NacosServiceInfo.HealthyInstances"/>, which also excludes administratively
    /// disabled instances and non-positive weights — combine this selector with a load balancer if
    /// those matter.
    /// </remarks>
    public static INacosInstanceSelector Healthy { get; } =
        new PredicateSelector(static instance => instance.Healthy);

    /// <summary>
    /// Creates a selector that keeps the instances in the given clusters.
    /// </summary>
    /// <param name="clusters">The cluster names to keep.</param>
    /// <returns>The selector, or <see cref="All"/> when no cluster is named.</returns>
    /// <remarks>
    /// The names are compared ordinally and the collection is copied, so mutating the argument
    /// afterwards does not change the selector. Naming a cluster an instance does not have simply
    /// excludes it, which is why an unknown name yields an empty list rather than an error.
    /// </remarks>
    public static INacosInstanceSelector Clusters(params string[] clusters)
    {
        ArgumentNullException.ThrowIfNull(clusters);

        if (clusters.Length == 0)
        {
            return All;
        }

        var wanted = new HashSet<string>(clusters, StringComparer.Ordinal);

        return new PredicateSelector(
            instance => instance.ClusterName is { } cluster && wanted.Contains(cluster));
    }

    /// <summary>
    /// Creates a selector that keeps the instances whose address matches a regular expression.
    /// </summary>
    /// <param name="regex">The regular expression the whole address must match.</param>
    /// <returns>The selector.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="regex"/> is null or is not a valid regular expression.
    /// </exception>
    /// <remarks>
    /// The match is a whole-string match, which is what the Java SDK gets from
    /// <c>Pattern.matches(regex, ip)</c>: <c>172\.18\.137</c> matches neither
    /// <c>172.18.137.120</c> nor <c>x172.18.137y</c>, and a caller who wants a prefix must write
    /// <c>^172\.18\.137.*</c>. The expression is compiled once, when the selector is created, so an
    /// invalid expression fails here rather than on every push.
    /// </remarks>
    public static INacosInstanceSelector IpAddress(string regex)
    {
        ArgumentNullException.ThrowIfNull(regex);

        var pattern = new Regex(regex, RegexOptions.CultureInvariant);

        return new PredicateSelector(instance =>
        {
            Match match = pattern.Match(instance.IpAddress);

            // Pattern.matches requires the entire input to match, while Regex.Match succeeds on a
            // substring; comparing the bounds reproduces the Java behaviour exactly.
            return match.Success
                && match.Index == 0
                && match.Length == instance.IpAddress.Length;
        });
    }

    /// <summary>
    /// Creates a selector that keeps the instances carrying the given metadata.
    /// </summary>
    /// <param name="metadata">The metadata entries to look for.</param>
    /// <param name="matchAny">
    /// <see langword="true"/> to keep an instance carrying any one of the entries;
    /// <see langword="false"/> to require all of them.
    /// </param>
    /// <returns>The selector.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="metadata"/> is null or contains a null key.
    /// </exception>
    /// <remarks>
    /// Values are compared ordinally, and an instance may carry metadata the selector does not name.
    /// An empty map keeps every instance, which is the Java SDK's behaviour: there is nothing to
    /// fail to match.
    /// </remarks>
    public static INacosInstanceSelector Metadata(
        IReadOnlyDictionary<string, string> metadata,
        bool matchAny = false)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // Copied so a caller mutating its dictionary afterwards cannot change a running selector.
        var wanted = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in metadata)
        {
            ArgumentNullException.ThrowIfNull(entry.Key, nameof(metadata));
            wanted[entry.Key] = entry.Value;
        }

        if (wanted.Count == 0)
        {
            return All;
        }

        return new PredicateSelector(instance =>
        {
            IReadOnlyDictionary<string, string>? actual = instance.Metadata;

            if (actual is null)
            {
                return false;
            }

            if (matchAny)
            {
                foreach (KeyValuePair<string, string> entry in wanted)
                {
                    if (actual.TryGetValue(entry.Key, out string? value)
                        && string.Equals(value, entry.Value, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }

            foreach (KeyValuePair<string, string> entry in wanted)
            {
                if (!actual.TryGetValue(entry.Key, out string? value)
                    || !string.Equals(value, entry.Value, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        });
    }

    /// <summary>
    /// Creates a selector from a function, for a filter the built-in ones do not cover.
    /// </summary>
    /// <param name="select">The function that chooses the instances to keep.</param>
    /// <returns>The selector.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="select"/> is null.</exception>
    /// <remarks>
    /// This is the .NET counterpart of implementing the Java interface: the delegate receives the
    /// whole context, so a selector can read the service and the cluster filter as well as the
    /// instances.
    /// </remarks>
    public static INacosInstanceSelector From(
        Func<NacosInstanceSelectorContext, IReadOnlyList<NacosInstance>> select)
    {
        ArgumentNullException.ThrowIfNull(select);

        return new DelegateSelector(select);
    }

    /// <summary>Keeps every instance, returning the list it was given.</summary>
    private sealed class AllInstancesSelector : INacosInstanceSelector
    {
        /// <inheritdoc />
        public IReadOnlyList<NacosInstance> Select(NacosInstanceSelectorContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return context.Instances;
        }
    }

    /// <summary>Keeps the instances a predicate accepts.</summary>
    private sealed class PredicateSelector : INacosInstanceSelector
    {
        private readonly Func<NacosInstance, bool> _filter;

        public PredicateSelector(Func<NacosInstance, bool> filter) => _filter = filter;

        /// <inheritdoc />
        public IReadOnlyList<NacosInstance> Select(NacosInstanceSelectorContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            List<NacosInstance> selected = [];

            foreach (NacosInstance instance in context.Instances)
            {
                if (_filter(instance))
                {
                    selected.Add(instance);
                }
            }

            return selected;
        }
    }

    /// <summary>Applies a caller-supplied function.</summary>
    private sealed class DelegateSelector : INacosInstanceSelector
    {
        private readonly Func<NacosInstanceSelectorContext, IReadOnlyList<NacosInstance>> _select;

        public DelegateSelector(Func<NacosInstanceSelectorContext, IReadOnlyList<NacosInstance>> select) =>
            _select = select;

        /// <inheritdoc />
        public IReadOnlyList<NacosInstance> Select(NacosInstanceSelectorContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            return _select(context)
                ?? throw new InvalidOperationException(
                    "A naming instance selector returned null instead of a list of instances.");
        }
    }
}
