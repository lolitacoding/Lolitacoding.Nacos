using Lolitacoding.Nacos.Naming;

namespace Lolitacoding.Nacos.Tests.Naming;

/// <summary>
/// Verifies the client-side instance selectors against the semantics of the Java SDK's
/// <c>NamingSelectorFactory</c>.
/// </summary>
/// <remarks>
/// A selector decides what a subscription delivers, so each case here is about an instance that must
/// or must not reach a consumer. The Java implementation is the reference for the edge cases: an
/// empty collection keeps everything, an address pattern matches the whole address, and an instance
/// without metadata carries none of the wanted entries.
/// </remarks>
public sealed class NacosInstanceSelectorTests
{
    private static readonly NacosServiceKey Service = new("order-service", "DEFAULT_GROUP");

    [Fact]
    public void All_KeepsTheListItWasGiven()
    {
        IReadOnlyList<NacosInstance> instances = [Instance("10.0.0.1", 8080), Instance("10.0.0.2", 8081)];

        // The Java EMPTY_SELECTOR returns the list unchanged, so the common case must not copy it:
        // a subscription that filters nothing would otherwise allocate a second list on every push.
        NacosInstanceSelectors.All.Select(Context(instances)).ShouldBeSameAs(instances);
    }

    [Fact]
    public void Healthy_KeepsTheInstancesTheServerReportsAsHealthy()
    {
        // The Java HEALTHY_SELECTOR reads the flag and nothing else. An instance the server calls
        // healthy but an operator disabled is still kept, which is what distinguishes this selector
        // from NacosInstance.IsSelectable and from NacosServiceInfo.HealthyInstances.
        NacosInstance healthy = Instance("10.0.0.1", 8080);
        NacosInstance unhealthy = Instance("10.0.0.2", 8081, healthy: false);
        NacosInstance disabled = Instance("10.0.0.3", 8082) with { Enabled = false };

        IReadOnlyList<NacosInstance> selected =
            NacosInstanceSelectors.Healthy.Select(Context([healthy, unhealthy, disabled]));

        selected.ShouldBe([healthy, disabled]);
    }

    [Fact]
    public void Clusters_KeepsTheNamedClustersAndExcludesEverythingElse()
    {
        NacosInstance zoneA = Instance("10.0.0.1", 8080, cluster: "zone-a");
        NacosInstance zoneB = Instance("10.0.0.2", 8081, cluster: "zone-b");

        // An instance without a cluster has no name to compare, so it is excluded rather than
        // treated as the default cluster: the Java selector compares the string as it is.
        NacosInstance noCluster = Instance("10.0.0.3", 8082);

        IReadOnlyList<NacosInstance> selected =
            NacosInstanceSelectors.Clusters("zone-b").Select(Context([zoneA, zoneB, noCluster]));

        selected.ShouldBe([zoneB]);
    }

    [Fact]
    public void Clusters_CopiesTheNamesItWasGiven()
    {
        // A selector outlives the call that created it, so mutating the array afterwards must not
        // silently change what a running subscription delivers.
        string[] names = ["zone-a"];
        INacosInstanceSelector selector = NacosInstanceSelectors.Clusters(names);
        NacosInstance zoneB = Instance("10.0.0.2", 8081, cluster: "zone-b");

        names[0] = "zone-b";

        selector.Select(Context([zoneB])).ShouldBeEmpty();
    }

    [Fact]
    public void Clusters_WithNoNames_KeepsEveryInstance()
    {
        // The Java selector treats an empty set as no filter at all, so the factory returns the
        // pass-through selector rather than one that matches nothing.
        NacosInstance zoneA = Instance("10.0.0.1", 8080, cluster: "zone-a");

        NacosInstanceSelectors.Clusters().Select(Context([zoneA])).ShouldBe([zoneA]);
    }

    [Fact]
    public void IpAddress_MatchesTheWholeAddress()
    {
        // Java's Pattern.matches requires the entire address to match. A .NET Regex.Match succeeds on
        // a substring, so the selector compares the bounds of the match instead.
        INacosInstanceSelector selector = NacosInstanceSelectors.IpAddress(@"172\.18\.137");

        selector.Select(Context([Instance("172.18.137.120", 8080)])).ShouldBeEmpty();
        selector.Select(Context([Instance("x172.18.137", 8081)])).ShouldBeEmpty();
        selector.Select(Context([Instance("172.18.137", 8082)])).Count.ShouldBe(1);
    }

    [Fact]
    public void IpAddress_RejectsAnInvalidExpressionWhenTheSelectorIsCreated()
    {
        // The expression is compiled once. Failing here reports the bug at the call site, where a
        // stack trace names the caller; failing on a push would surface as a log line instead.
        Should.Throw<ArgumentException>(() => NacosInstanceSelectors.IpAddress("["));
    }

    [Fact]
    public void Metadata_RequiresEveryEntryByDefault()
    {
        IReadOnlyDictionary<string, string> wanted = new Dictionary<string, string>
        {
            ["zone"] = "a",
            ["tier"] = "gold",
        };

        NacosInstance both = Instance("10.0.0.1", 8080, metadata: Labels(("zone", "a"), ("tier", "gold")));
        NacosInstance one = Instance("10.0.0.2", 8081, metadata: Labels(("zone", "a")));
        NacosInstance none = Instance("10.0.0.3", 8082);

        NacosInstanceSelectors.Metadata(wanted)
            .Select(Context([both, one, none]))
            .ShouldBe([both]);
    }

    [Fact]
    public void Metadata_MatchAnyKeepsAnInstanceCarryingOneEntry()
    {
        IReadOnlyDictionary<string, string> wanted = new Dictionary<string, string>
        {
            ["zone"] = "a",
            ["tier"] = "gold",
        };

        NacosInstance both = Instance("10.0.0.1", 8080, metadata: Labels(("zone", "a"), ("tier", "gold")));
        NacosInstance one = Instance("10.0.0.2", 8081, metadata: Labels(("zone", "a")));
        NacosInstance none = Instance("10.0.0.3", 8082, metadata: Labels(("zone", "b")));

        NacosInstanceSelectors.Metadata(wanted, matchAny: true)
            .Select(Context([both, one, none]))
            .ShouldBe([both, one]);
    }

    [Fact]
    public void Metadata_WithNoEntries_KeepsEveryInstance()
    {
        // The Java selector checks the size of the wanted map and keeps everything when it is empty:
        // there is no entry that could fail to match.
        NacosInstance instance = Instance("10.0.0.1", 8080);

        NacosInstanceSelectors.Metadata(new Dictionary<string, string>())
            .Select(Context([instance]))
            .ShouldBe([instance]);
    }

    [Fact]
    public void Metadata_CopiesTheEntriesItWasGiven()
    {
        var wanted = new Dictionary<string, string> { ["zone"] = "a" };
        INacosInstanceSelector selector = NacosInstanceSelectors.Metadata(wanted);
        NacosInstance zoneB = Instance("10.0.0.2", 8081, metadata: Labels(("zone", "b")));

        wanted["zone"] = "b";

        selector.Select(Context([zoneB])).ShouldBeEmpty();
    }

    [Fact]
    public void Metadata_ComparesValuesOrdinally()
    {
        // Java compares strings with equals, so a label's case matters and a near miss excludes the
        // instance instead of matching it.
        NacosInstance instance = Instance("10.0.0.1", 8080, metadata: Labels(("tier", "Gold")));

        NacosInstanceSelectors.Metadata(Labels(("tier", "gold")))
            .Select(Context([instance]))
            .ShouldBeEmpty();
    }

    [Fact]
    public void From_GivesTheSelectorTheServiceAndTheClusterFilter()
    {
        NacosServiceKey? seenService = null;
        string? seenClusters = null;

        INacosInstanceSelector selector = NacosInstanceSelectors.From(context =>
        {
            seenService = context.Service;
            seenClusters = context.Clusters;
            return context.Instances;
        });

        IReadOnlyList<NacosInstance> instances = [Instance("10.0.0.1", 8080)];

        selector.Select(new NacosInstanceSelectorContext(Service, "zone-a", instances))
            .ShouldBeSameAs(instances);

        seenService.ShouldBe(Service);
        seenClusters.ShouldBe("zone-a");
    }

    [Fact]
    public void From_RejectsAFunctionThatReturnsNull()
    {
        // A null list would reach the client's comparison and then a consumer's enumeration, so the
        // selector fails where the bug is instead of corrupting the delivered state.
        INacosInstanceSelector selector = NacosInstanceSelectors.From(_ => null!);

        Should.Throw<InvalidOperationException>(() => selector.Select(Context([])));
    }

    [Fact]
    public void Select_RejectsANullContext()
    {
        Should.Throw<ArgumentNullException>(() => NacosInstanceSelectors.All.Select(null!));
    }

    private static NacosInstanceSelectorContext Context(
        IReadOnlyList<NacosInstance> instances, string? clusters = null) =>
        new(Service, clusters, instances);

    private static Dictionary<string, string> Labels(params (string Key, string Value)[] entries)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string key, string value) in entries)
        {
            labels[key] = value;
        }

        return labels;
    }

    private static NacosInstance Instance(
        string ipAddress,
        int port,
        string? cluster = null,
        bool healthy = true,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(ipAddress, port, clusterName: cluster, healthy: healthy, metadata: metadata);
}
