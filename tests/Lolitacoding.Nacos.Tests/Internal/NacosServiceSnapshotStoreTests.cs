using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Naming;

namespace Lolitacoding.Nacos.Tests.Internal;

/// <summary>
/// Verifies the on-disk service snapshot store used for naming failover.
/// </summary>
/// <remarks>
/// The store is what lets a process that restarts during a control-plane outage keep discovering a
/// service, so the tests here focus on the two properties that matter: every field a caller needs
/// to route survives a round trip, and anything unusable on disk degrades to "no snapshot" rather
/// than to an error or a corrupt result.
/// </remarks>
public sealed class NacosServiceSnapshotStoreTests : IDisposable
{
    private readonly string _root = TestPaths.CreateScratchDirectory("service-snapshot");

    public void Dispose() => TestPaths.TryDelete(_root);

    [Fact]
    public async Task SaveThenLoad_RoundTripsInstancesAndServiceState()
    {
        var store = new NacosServiceSnapshotStore(_root);
        var key = new NacosServiceKey("order-service", "DEFAULT_GROUP", "public");
        NacosServiceInfo info = Info(
            key,
            new NacosInstance(
                "10.0.0.5",
                8080,
                weight: 2.5,
                clusterName: "zone-a",
                healthy: false,
                enabled: false,
                ephemeral: false,
                metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["version"] = "2" })
            {
                InstanceId = "inst-8080",
            });

        await store.SaveAsync(key, info, TestContext.Current.CancellationToken);

        NacosServiceInfo? loaded = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Checksum.ShouldBe("sum-1");
        loaded.Clusters.ShouldBe("zone-a,zone-b");

        // A snapshot is by definition stale, so it must be flagged; a caller that cannot tolerate
        // stale data relies on this to tell.
        loaded.FromLocalCache.ShouldBeTrue();

        NacosInstance instance = loaded.Instances.ShouldHaveSingleItem();
        instance.IpAddress.ShouldBe("10.0.0.5");
        instance.Port.ShouldBe(8080);
        instance.Weight.ShouldBe(2.5);
        instance.ClusterName.ShouldBe("zone-a");
        instance.Healthy.ShouldBeFalse();
        instance.Enabled.ShouldBeFalse();
        instance.Ephemeral.ShouldBeFalse();
        instance.InstanceId.ShouldBe("inst-8080");
        instance.Metadata.ShouldNotBeNull();
        instance.Metadata["version"].ShouldBe("2");
    }

    [Fact]
    public async Task SaveThenLoad_PreservesTheInstanceOrder()
    {
        // Callers may treat the first instance as a preferred one, so persisting must not reorder.
        var store = new NacosServiceSnapshotStore(_root);
        var key = new NacosServiceKey("order-service", "DEFAULT_GROUP", "public");
        NacosServiceInfo info = Info(
            key,
            new NacosInstance("10.0.0.1", 8080),
            new NacosInstance("10.0.0.2", 8081),
            new NacosInstance("10.0.0.3", 8082));

        await store.SaveAsync(key, info, TestContext.Current.CancellationToken);

        NacosServiceInfo? loaded = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Instances.Select(instance => instance.IpAddress)
            .ShouldBe(["10.0.0.1", "10.0.0.2", "10.0.0.3"]);
    }

    [Fact]
    public async Task SaveThenLoad_HandlesAnEmptyInstanceList()
    {
        // A service that legitimately has no instances must still round-trip, so a later outage
        // serves "no instances" rather than falling through and failing the lookup.
        var store = new NacosServiceSnapshotStore(_root);
        var key = new NacosServiceKey("order-service", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, Info(key), TestContext.Current.CancellationToken);

        NacosServiceInfo? loaded = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        loaded.Instances.ShouldBeEmpty();
    }

    [Fact]
    public async Task Load_ReturnsNullWhenNoSnapshotExists()
    {
        var store = new NacosServiceSnapshotStore(_root);

        NacosServiceInfo? loaded = await store.LoadAsync(
            new NacosServiceKey("missing", "DEFAULT_GROUP", "public"),
            TestContext.Current.CancellationToken);

        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task Save_ReplacesAnExistingSnapshot()
    {
        var store = new NacosServiceSnapshotStore(_root);
        var key = new NacosServiceKey("order-service", "DEFAULT_GROUP", "public");
        var first = new NacosInstance("10.0.0.1", 8080);
        var second = new NacosInstance("10.0.0.2", 9090);

        await store.SaveAsync(key, Info(key, first), TestContext.Current.CancellationToken);
        await store.SaveAsync(key, Info(key, second), TestContext.Current.CancellationToken);

        NacosServiceInfo? loaded = await store.LoadAsync(key, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();
        NacosInstance instance = loaded.Instances.ShouldHaveSingleItem();
        instance.IpAddress.ShouldBe("10.0.0.2");
        instance.Port.ShouldBe(9090);
    }

    [Fact]
    public async Task Save_KeepsServicesIndependent()
    {
        var store = new NacosServiceSnapshotStore(_root);

        var first = new NacosServiceKey("order-service", "GROUP_A", "public");
        var second = new NacosServiceKey("order-service", "GROUP_B", "public");
        var third = new NacosServiceKey("order-service", "GROUP_A", "other-namespace");

        await store.SaveAsync(
            first, Info(first, new NacosInstance("10.0.0.1", 8080)), TestContext.Current.CancellationToken);
        await store.SaveAsync(
            second, Info(second, new NacosInstance("10.0.0.2", 8080)), TestContext.Current.CancellationToken);
        await store.SaveAsync(
            third, Info(third, new NacosInstance("10.0.0.3", 8080)), TestContext.Current.CancellationToken);

        (await store.LoadAsync(first, TestContext.Current.CancellationToken))!
            .Instances[0].IpAddress.ShouldBe("10.0.0.1");
        (await store.LoadAsync(second, TestContext.Current.CancellationToken))!
            .Instances[0].IpAddress.ShouldBe("10.0.0.2");
        (await store.LoadAsync(third, TestContext.Current.CancellationToken))!
            .Instances[0].IpAddress.ShouldBe("10.0.0.3");
    }

    [Fact]
    public async Task Save_WritesUnderTheNamingDirectoryWithoutLeavingATemporaryFile()
    {
        // A leftover temporary file would accumulate one entry per write for the life of the
        // application.
        var store = new NacosServiceSnapshotStore(_root);
        var key = new NacosServiceKey("order-service", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, Info(key, new NacosInstance("10.0.0.1", 8080)),
            TestContext.Current.CancellationToken);

        string namingDirectory = Path.Combine(_root, "naming");
        Directory.GetFiles(namingDirectory).ShouldHaveSingleItem();
        Directory.GetFiles(namingDirectory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_DeletesTheSnapshot()
    {
        var store = new NacosServiceSnapshotStore(_root);
        var key = new NacosServiceKey("order-service", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, Info(key, new NacosInstance("10.0.0.1", 8080)),
            TestContext.Current.CancellationToken);
        store.Remove(key);

        (await store.LoadAsync(key, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public void Remove_IsSafeWhenNothingWasStored()
    {
        var store = new NacosServiceSnapshotStore(_root);

        Should.NotThrow(() =>
            store.Remove(new NacosServiceKey("absent", "DEFAULT_GROUP", "public")));
    }

    [Fact]
    public async Task Load_ReturnsNullWhenTheFileIsNotValidJson()
    {
        // A truncated file from a process killed mid-write must be treated as absent so the caller
        // still tries the server instead of failing.
        var store = new NacosServiceSnapshotStore(_root);
        var key = new NacosServiceKey("order-service", "DEFAULT_GROUP", "public");
        await store.SaveAsync(key, Info(key, new NacosInstance("10.0.0.1", 8080)),
            TestContext.Current.CancellationToken);

        string file = Directory.GetFiles(Path.Combine(_root, "naming")).ShouldHaveSingleItem();
        await File.WriteAllTextAsync(file, "{ not json", TestContext.Current.CancellationToken);

        (await store.LoadAsync(key, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Load_ReturnsNullWhenAStoredInstanceViolatesTheModelInvariants()
    {
        // A tampered or foreign file must not be able to hand the caller an instance the public
        // model would have rejected, such as one with an unusable port.
        var store = new NacosServiceSnapshotStore(_root);
        var key = new NacosServiceKey("order-service", "DEFAULT_GROUP", "public");
        await store.SaveAsync(key, Info(key, new NacosInstance("10.0.0.1", 8080)),
            TestContext.Current.CancellationToken);

        string file = Directory.GetFiles(Path.Combine(_root, "naming")).ShouldHaveSingleItem();
        await File.WriteAllTextAsync(
            file,
            """{"instances":[{"ip":"10.0.0.1","port":0,"weight":1}]}""",
            TestContext.Current.CancellationToken);

        (await store.LoadAsync(key, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Save_UsesAPathDerivedFromACoordinatesHash()
    {
        // Service names and groups are arbitrary caller input, so the file name must not be derived
        // from them directly: a name containing a path separator would otherwise escape the
        // directory.
        var store = new NacosServiceSnapshotStore(_root);
        var key = new NacosServiceKey("../../escape", "DEFAULT_GROUP", "public");

        await store.SaveAsync(key, Info(key, new NacosInstance("10.0.0.1", 8080)),
            TestContext.Current.CancellationToken);

        string namingDirectory = Path.Combine(_root, "naming");
        Directory.GetFiles(namingDirectory).ShouldHaveSingleItem();

        // Nothing may have been created outside the snapshot directory.
        File.Exists(Path.Combine(_root, "escape")).ShouldBeFalse();
        File.Exists(Path.GetFullPath(Path.Combine(namingDirectory, "..", "..", "escape"))).ShouldBeFalse();
    }

    /// <summary>Builds a service state carrying the given instances.</summary>
    private static NacosServiceInfo Info(NacosServiceKey key, params NacosInstance[] instances) =>
        new(key, [.. instances], "zone-a,zone-b", "sum-1", fromLocalCache: false);
}
