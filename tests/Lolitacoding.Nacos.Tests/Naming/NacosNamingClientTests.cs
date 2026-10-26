using System.Threading.Channels;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Naming;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Lolitacoding.Nacos.Tests.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Naming;

/// <summary>
/// Verifies the naming client against a scripted in-memory server.
/// </summary>
/// <remarks>
/// The client's contract is almost entirely about what it puts on the wire and how it interprets
/// what comes back, so every test here asserts on the decoded request or on the model the client
/// produced rather than on incidental log output.
/// </remarks>
public sealed class NacosNamingClientTests
{
    private static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:8848");
    private static readonly NacosServiceKey Service = new("order-service", "DEFAULT_GROUP");

    [Fact]
    public async Task RegisterInstance_SendsTheRegistrationWithItsCoordinates()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        InstanceRequest request = harness.LastRequest<InstanceRequest>();
        request.Type.ShouldBe(InstanceRequest.RegisterType);
        request.Namespace.ShouldBe("public");
        request.ServiceName.ShouldBe("order-service");
        request.GroupName.ShouldBe("DEFAULT_GROUP");
        request.Instance.ShouldNotBeNull();
        request.Instance.Ip.ShouldBe("10.0.0.5");
        request.Instance.Port.ShouldBe(8080);
        request.Instance.ServiceName.ShouldBe("order-service");
    }

    [Fact]
    public async Task RegisterInstance_AppliesTheConfiguredNamespaceAndCluster()
    {
        // A registration that omitted either would land in the public namespace's default cluster
        // rather than where the caller configured it to go.
        using NamingHarness harness = new(@namespace: "tenant-a", cluster: "zone-b");
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        InstanceRequest request = harness.LastRequest<InstanceRequest>();
        request.Namespace.ShouldBe("tenant-a");
        request.Instance!.ClusterName.ShouldBe("zone-b");
    }

    [Fact]
    public async Task RegisterInstance_UsesAPersistentRequestForAPersistentInstance()
    {
        // The server routes a persistent instance to a different handler, which is what makes it
        // survive the connection; sending the wrong request type silently stores it as ephemeral.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstanceAsync(
            Service,
            new NacosInstance("10.0.0.5", 8080, ephemeral: false),
            TestContext.Current.CancellationToken);

        harness.LastRequestTypeName().ShouldBe(nameof(PersistentInstanceRequest));
    }

    [Fact]
    public async Task RegisterInstance_HonoursReservedMetadataOnTheWireInstance()
    {
        // The official client expresses the heartbeat settings as reserved metadata keys; a caller
        // that sets them that way must still have them reach the computed wire fields.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Instance.InstanceHeartBeatIntervalKey] = "7000",
        };

        await harness.Client.RegisterInstanceAsync(
            Service,
            new NacosInstance("10.0.0.5", 8080, metadata: metadata),
            TestContext.Current.CancellationToken);

        harness.LastRequest<InstanceRequest>().Instance!.InstanceHeartBeatInterval.ShouldBe(7000);
    }

    [Fact]
    public async Task RegisterInstance_WhenTheServerRejects_ForgetsTheRegistration()
    {
        // A registration that never reached the server must not be replayed later as though it had
        // been accepted, or a reconnect would resurrect an instance the caller was told failed.
        using NamingHarness harness = new(handler: request =>
            PayloadCodec.ReadTypeName(request) is "InstanceRequest" or "PersistentInstanceRequest"
                ? PayloadCodec.Encode(new InstanceResponse
                {
                    ResultCode = NacosResponse.FailureCode,
                    ErrorCode = 20005,
                    Message = "rejected",
                })
                : NamingHarness.Fallback(request));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NacosServerException>(
            () => harness.Client.RegisterInstanceAsync(
                Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken));

        await harness.DropAndReconnectAsync();

        harness.CountRequests("InstanceRequest").ShouldBe(1);
        harness.CountRequests("PersistentInstanceRequest").ShouldBe(0);
    }

    [Fact]
    public async Task RegisterInstance_WhenAReplacementIsRejected_KeepsTheEarlierRegistration()
    {
        // The failed attempt never reached the server, so the server still holds the earlier set.
        // Forgetting it would lose that instance at the next reconnect, when only the replay can
        // restore what the connection took with it.
        bool reject = false;

        using NamingHarness harness = new(handler: request =>
            reject && PayloadCodec.ReadTypeName(request) == "InstanceRequest"
                ? PayloadCodec.Encode(new InstanceResponse
                {
                    ResultCode = NacosResponse.FailureCode,
                    ErrorCode = 20005,
                    Message = "rejected",
                })
                : NamingHarness.Fallback(request));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        reject = true;

        await Should.ThrowAsync<NacosServerException>(
            () => harness.Client.RegisterInstanceAsync(
                Service, new NacosInstance("10.0.0.6", 8081), TestContext.Current.CancellationToken));

        reject = false;

        int before = harness.CountRequests("InstanceRequest");

        await harness.DropAndReconnectAsync();
        await NamingHarness.WaitForAsync(
            () => harness.CountRequests("InstanceRequest") > before);

        // The replay restores the set the server still has, not the attempt it refused.
        harness.LastRequest<InstanceRequest>().Instance!.Ip.ShouldBe("10.0.0.5");
    }

    [Fact]
    public async Task DeregisterInstance_SendsTheDeregistration()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.DeregisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        InstanceRequest request = harness.LastRequest<InstanceRequest>();
        request.Type.ShouldBe(InstanceRequest.DeregisterType);
        request.Instance!.Ip.ShouldBe("10.0.0.5");
    }

    [Fact]
    public async Task RegisterInstances_SendsOneBatchRequest()
    {
        // The point of the batch is one round trip for the whole list, so the test asserts both the
        // request shape and the absence of per-instance requests.
        using NamingHarness harness = new(@namespace: "tenant-a");
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstancesAsync(
            Service,
            [
                new NacosInstance("10.0.0.5", 8080, clusterName: "zone-a"),
                new NacosInstance("10.0.0.6", 8081),
            ],
            TestContext.Current.CancellationToken);

        harness.CountRequests("BatchInstanceRequest").ShouldBe(1);
        harness.CountRequests("InstanceRequest").ShouldBe(0);

        BatchInstanceRequest request = harness.LastRequest<BatchInstanceRequest>();
        request.Namespace.ShouldBe("tenant-a");
        request.ServiceName.ShouldBe("order-service");
        request.GroupName.ShouldBe("DEFAULT_GROUP");
        request.Type.ShouldBe(BatchInstanceRequest.RegisterType);
        request.Instances!.Count.ShouldBe(2);
        request.Instances[0].Ip.ShouldBe("10.0.0.5");
        request.Instances[0].ClusterName.ShouldBe("zone-a");

        // A batch instance without a cluster still lands in the configured default, like a single
        // registration.
        request.Instances[1].ClusterName.ShouldBe("DEFAULT");
    }

    [Fact]
    public async Task RegisterInstances_RejectsAnEmptyBatch()
    {
        // An empty batch is not a no-op on the wire: it would replace the service's published set
        // with nothing, so it is a deregistration spelled as a registration.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentException>(
            () => harness.Client.RegisterInstancesAsync(
                Service, [], TestContext.Current.CancellationToken));

        harness.CountRequests("BatchInstanceRequest").ShouldBe(0);
    }

    [Fact]
    public async Task RegisterInstances_RejectsAPersistentInstance()
    {
        // Nacos has no batch path for persistent instances; the ephemeral handler would silently
        // store the registration as ephemeral.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentException>(
            () => harness.Client.RegisterInstancesAsync(
                Service,
                [new NacosInstance("10.0.0.5", 8080, ephemeral: false)],
                TestContext.Current.CancellationToken));

        harness.CountRequests("BatchInstanceRequest").ShouldBe(0);
    }

    [Fact]
    public async Task RegisterInstances_RejectsANullInstance()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentException>(
            () => harness.Client.RegisterInstancesAsync(
                Service, [null!], TestContext.Current.CancellationToken));

        harness.CountRequests("BatchInstanceRequest").ShouldBe(0);
    }

    [Fact]
    public async Task RegisterInstances_WhenTheServerRejects_ForgetsTheRegistration()
    {
        using NamingHarness harness = new(handler: request =>
            PayloadCodec.ReadTypeName(request) == "BatchInstanceRequest"
                ? PayloadCodec.Encode(new BatchInstanceResponse
                {
                    ResultCode = NacosResponse.FailureCode,
                    ErrorCode = 20005,
                    Message = "rejected",
                })
                : NamingHarness.Fallback(request));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NacosServerException>(
            () => harness.Client.RegisterInstancesAsync(
                Service,
                [new NacosInstance("10.0.0.5", 8080), new NacosInstance("10.0.0.6", 8081)],
                TestContext.Current.CancellationToken));

        await harness.DropAndReconnectAsync();

        harness.CountRequests("BatchInstanceRequest").ShouldBe(1);
    }

    [Fact]
    public async Task Reconnect_ReplaysABatchRegistration()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstancesAsync(
            Service,
            [new NacosInstance("10.0.0.5", 8080), new NacosInstance("10.0.0.6", 8081)],
            TestContext.Current.CancellationToken);

        int before = harness.CountRequests("BatchInstanceRequest");

        await harness.DropAndReconnectAsync();
        await NamingHarness.WaitForAsync(
            () => harness.CountRequests("BatchInstanceRequest") > before);

        // The whole set is re-asserted in one request, not one request per instance.
        harness.CountRequests("BatchInstanceRequest").ShouldBe(before + 1);
        harness.LastRequest<BatchInstanceRequest>().Instances!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RegisterInstance_AfterABatch_ReplaysOnlyTheSingleInstance()
    {
        // The server keeps one published instance set per service per connection, so a single
        // registration supersedes the batch. Replaying both would resurrect the batch's instances.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstancesAsync(
            Service,
            [new NacosInstance("10.0.0.5", 8080), new NacosInstance("10.0.0.6", 8081)],
            TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstanceAsync(
            Service, new NacosInstance("10.0.0.7", 8082), TestContext.Current.CancellationToken);

        int batchesBefore = harness.CountRequests("BatchInstanceRequest");
        int singlesBefore = harness.CountRequests("InstanceRequest");

        await harness.DropAndReconnectAsync();
        await NamingHarness.WaitForAsync(
            () => harness.CountRequests("InstanceRequest") > singlesBefore);

        harness.CountRequests("InstanceRequest").ShouldBe(singlesBefore + 1);
        harness.CountRequests("BatchInstanceRequest").ShouldBe(batchesBefore);
        harness.LastRequest<InstanceRequest>().Instance!.Ip.ShouldBe("10.0.0.7");
    }

    [Fact]
    public async Task RegisterInstances_AfterASingle_ReplacesTheSingleOnReplay()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstancesAsync(
            Service,
            [new NacosInstance("10.0.0.6", 8081), new NacosInstance("10.0.0.7", 8082)],
            TestContext.Current.CancellationToken);

        int batchesBefore = harness.CountRequests("BatchInstanceRequest");
        int singlesBefore = harness.CountRequests("InstanceRequest");

        await harness.DropAndReconnectAsync();
        await NamingHarness.WaitForAsync(
            () => harness.CountRequests("BatchInstanceRequest") > batchesBefore);

        harness.CountRequests("BatchInstanceRequest").ShouldBe(batchesBefore + 1);
        harness.CountRequests("InstanceRequest").ShouldBe(singlesBefore);
        harness.LastRequest<BatchInstanceRequest>().Instances!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DeregisterInstances_PublishesTheRemainder()
    {
        // Nacos has no batch deregistration message: the remainder is published as a new batch.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        var first = new NacosInstance("10.0.0.5", 8080);
        var second = new NacosInstance("10.0.0.6", 8081);

        await harness.Client.RegisterInstancesAsync(
            Service, [first, second], TestContext.Current.CancellationToken);

        await harness.Client.DeregisterInstancesAsync(
            Service, [first], TestContext.Current.CancellationToken);

        harness.CountRequests("BatchInstanceRequest").ShouldBe(2);

        BatchInstanceRequest request = harness.LastRequest<BatchInstanceRequest>();
        request.Instances!.ShouldHaveSingleItem().Ip.ShouldBe("10.0.0.6");

        // The replay must follow the new set, not the batch the caller removed from.
        int before = harness.CountRequests("BatchInstanceRequest");

        await harness.DropAndReconnectAsync();
        await NamingHarness.WaitForAsync(
            () => harness.CountRequests("BatchInstanceRequest") > before);

        harness.LastRequest<BatchInstanceRequest>().Instances!.ShouldHaveSingleItem()
            .Ip.ShouldBe("10.0.0.6");
    }

    [Fact]
    public async Task DeregisterInstances_WhenEverythingIsRemoved_SendsADeregistration()
    {
        // The official client publishes an empty batch here; an ordinary deregistration leaves the
        // same server state without a lingering empty published set.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        var first = new NacosInstance("10.0.0.5", 8080);
        var second = new NacosInstance("10.0.0.6", 8081);

        await harness.Client.RegisterInstancesAsync(
            Service, [first, second], TestContext.Current.CancellationToken);

        int batches = harness.CountRequests("BatchInstanceRequest");

        await harness.Client.DeregisterInstancesAsync(
            Service, [first, second], TestContext.Current.CancellationToken);

        harness.CountRequests("BatchInstanceRequest").ShouldBe(batches);

        InstanceRequest request = harness.LastRequest<InstanceRequest>();
        request.Type.ShouldBe(InstanceRequest.DeregisterType);
        request.Instance!.Ip.ShouldBe("10.0.0.5");

        // Nothing is left to replay.
        int deregistrations = harness.CountRequests("InstanceRequest");

        await harness.DropAndReconnectAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        harness.CountRequests("BatchInstanceRequest").ShouldBe(batches);
        harness.CountRequests("InstanceRequest").ShouldBe(deregistrations);
    }

    [Fact]
    public async Task DeregisterInstances_RejectsAServiceThatWasNotBatchRegistered()
    {
        // There is no batch to subtract from, and guessing that the caller meant a plain
        // deregistration would remove more than they asked for.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => harness.Client.DeregisterInstancesAsync(
                Service, [new NacosInstance("10.0.0.5", 8080)], TestContext.Current.CancellationToken));

        harness.CountRequests("InstanceRequest").ShouldBe(1);
    }

    [Fact]
    public async Task DeregisterInstances_RejectsAnEmptyList()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstancesAsync(
            Service, [new NacosInstance("10.0.0.5", 8080)], TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentException>(
            () => harness.Client.DeregisterInstancesAsync(
                Service, [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegisterInstances_ThrowsAfterDispose()
    {
        using NamingHarness harness = new();
        await harness.Client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => harness.Client.RegisterInstancesAsync(
                Service, [new NacosInstance("10.0.0.5", 8080)],
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInstances_SendsTheQueryCoordinatesAndFilter()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.GetInstancesAsync(
            Service, cluster: "zone-a", healthyOnly: true, TestContext.Current.CancellationToken);

        ServiceQueryRequest request = harness.LastRequest<ServiceQueryRequest>();
        request.Namespace.ShouldBe("public");
        request.ServiceName.ShouldBe("order-service");
        request.GroupName.ShouldBe("DEFAULT_GROUP");
        request.Cluster.ShouldBe("zone-a");
        request.HealthyOnly.ShouldBeTrue();
    }

    [Fact]
    public async Task GetInstances_MapsTheWireInstancesToThePublicModel()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        NacosServiceInfo info = await harness.Client.GetInstancesAsync(
            Service, ct: TestContext.Current.CancellationToken);

        info.Instances.Count.ShouldBe(2);
        info.Checksum.ShouldBe("sum-1");
        info.FromLocalCache.ShouldBeFalse();

        NacosInstance first = info.Instances[0];
        first.IpAddress.ShouldBe("10.0.0.1");
        first.Port.ShouldBe(8080);
        first.InstanceId.ShouldBe("inst-8080");
    }

    [Fact]
    public async Task GetInstances_TreatsANonPositiveWeightAsUnweighted()
    {
        // A malformed entry must not fail the whole lookup, and a zero weight cannot be balanced.
        using NamingHarness harness = new(handler: request =>
            PayloadCodec.ReadTypeName(request) == "ServiceQueryRequest"
                ? QueryResponse([Host(port: 8080, weight: 0)])
                : NamingHarness.Fallback(request));
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        NacosServiceInfo info = await harness.Client.GetInstancesAsync(
            Service, ct: TestContext.Current.CancellationToken);

        info.Instances[0].Weight.ShouldBe(1.0);
    }

    [Fact]
    public async Task GetInstances_ServesTheLastKnownListWhenTheServerIsUnreachable()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        // Populate the cache through a successful query first.
        await harness.Client.GetInstancesAsync(Service, ct: TestContext.Current.CancellationToken);

        harness.Handler = _ => throw new NacosConnectionException("server is down");

        NacosServiceInfo cached = await harness.Client.GetInstancesAsync(
            Service, ct: TestContext.Current.CancellationToken);

        cached.FromLocalCache.ShouldBeTrue();
        cached.Instances.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetInstances_ThrowsWhenThereIsNoCachedList()
    {
        using NamingHarness harness = new(
            handler: _ => throw new NacosConnectionException("server is down"));
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NacosConnectionException>(
            () => harness.Client.GetInstancesAsync(Service, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInstances_FailsOverToTheOnDiskSnapshotAfterARestart()
    {
        // The in-memory cache dies with the process, which is exactly when failover matters most:
        // a rolling restart during a control-plane outage must not become an application outage.
        string root = TestPaths.CreateScratchDirectory("naming-failover");
        try
        {
            var snapshots = new NacosServiceSnapshotStore(root);

            // A first process populates the snapshot through a successful query, then exits.
            using (NamingHarness first = new(snapshots: snapshots))
            {
                await first.ConnectAsync(TestContext.Current.CancellationToken);
                await first.Client.GetInstancesAsync(
                    Service, ct: TestContext.Current.CancellationToken);
            }

            // A second process starts while the server is unreachable and has no in-process cache.
            using NamingHarness restarted = new(
                handler: _ => throw new NacosConnectionException("server is down"),
                snapshots: snapshots);
            await restarted.ConnectAsync(TestContext.Current.CancellationToken);

            NacosServiceInfo info = await restarted.Client.GetInstancesAsync(
                Service, ct: TestContext.Current.CancellationToken);

            info.FromLocalCache.ShouldBeTrue();
            info.Instances.Count.ShouldBe(2);
            info.Instances[0].IpAddress.ShouldBe("10.0.0.1");
        }
        finally
        {
            TestPaths.TryDelete(root);
        }
    }

    [Fact]
    public async Task Subscribe_DeliversTheInitialStateThenLaterPushes()
    {
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(Service);

        NacosServiceChange initial = await subscription.NextAsync();
        initial.PreviousChecksum.ShouldBeNull();
        initial.Service.Checksum.ShouldBe("sum-1");

        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-2", ports: [8080, 8082]),
            TestContext.Current.CancellationToken);

        NacosServiceChange change = await subscription.NextAsync();
        change.PreviousChecksum.ShouldBe("sum-1");
        change.Service.Checksum.ShouldBe("sum-2");
        change.Instances.Count.ShouldBe(2);
        change.Instances.ShouldContain(instance => instance.Port == 8082);
    }

    [Fact]
    public async Task Subscribe_IgnoresAPushThatDoesNotChangeTheInstances()
    {
        // The server notifies on changes it may already have delivered. It leaves the checksum out
        // of what it sends, so the state itself is what decides: a push that repeats the instance
        // list must not reach user code as a change, even though it carries another checksum.
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(Service);
        await subscription.NextAsync();

        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-2", ports: [8080, 8081]),
            TestContext.Current.CancellationToken);

        (await subscription.TryNextAsync(TimeSpan.FromMilliseconds(200))).ShouldBeNull();
    }

    [Fact]
    public async Task Subscribe_AppliesTheStateTheServerReportsWhenTheSubscriptionBecomesLive()
    {
        // The read that seeds a subscription happens before the subscription exists on the server,
        // so a change landing in between is pushed to nobody and would be lost. The answer to the
        // subscribe request carries the state at the instant the subscription became live, and
        // applying it is what closes that window.
        using NamingHarness harness = new(handler: request => PayloadCodec.ReadTypeName(request) switch
        {
            "ServiceQueryRequest" => QueryResponse([Host(8080)]),
            "SubscribeServiceRequest" => SubscribeResponse(checksum: "sum-2", ports: [8080, 8082]),
            _ => NamingHarness.Fallback(request),
        });

        await using Subscription subscription = await harness.SubscribeAsync(Service);

        NacosServiceChange initial = await subscription.RequireNextAsync();
        initial.PreviousChecksum.ShouldBeNull();
        initial.Service.Checksum.ShouldBe("sum-1");
        initial.Instances.Count.ShouldBe(1);

        // The state the server reported at registration is newer than the one the read produced, so
        // the consumer is told about it exactly as if it had arrived as a push.
        NacosServiceChange live = await subscription.RequireNextAsync();
        live.PreviousChecksum.ShouldBe("sum-1");
        live.Service.Checksum.ShouldBe("sum-2");
        live.Instances.ShouldContain(instance => instance.Port == 8082);
    }

    [Fact]
    public async Task Subscribe_DoesNotRepeatTheStateTheServerEchoesBack()
    {
        // The answer to a subscription normally repeats the state the seeding read already
        // produced. Delivering it again would look to a consumer like a change that never happened.
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(Service);

        (await subscription.RequireNextAsync()).Service.Checksum.ShouldBe("sum-1");

        (await subscription.TryNextAsync(TimeSpan.FromMilliseconds(200))).ShouldBeNull();
    }

    [Fact]
    public async Task Subscribe_WithSelector_DeliversTheSelectedSubsetOfTheInitialState()
    {
        // The first element has to have the same shape as every later one: a consumer initialises
        // from it, so handing it the unfiltered list would leave it routing to instances the
        // selector excluded until the first push arrived.
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(
            Service, KeepPort(8081));

        NacosServiceChange initial = await subscription.RequireNextAsync();
        initial.PreviousChecksum.ShouldBeNull();
        initial.Service.Checksum.ShouldBe("sum-1");
        initial.Instances.ShouldHaveSingleItem().Port.ShouldBe(8081);
    }

    [Fact]
    public async Task Subscribe_WithSelector_AsksTheServerForEveryCluster()
    {
        // Java's selector overload subscribes with an empty cluster filter and narrows on the
        // client, so the server must not be asked for a subset: a selector can only keep what it is
        // given, and a cluster filter would hide instances it was meant to choose between.
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(
            Service, NacosInstanceSelectors.Clusters("zone-a"));

        await subscription.RequireNextAsync();

        harness.LastRequest<SubscribeServiceRequest>().Clusters.ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task Subscribe_WithSelector_IgnoresAPushThatOnlyTouchesAFilteredOutInstance()
    {
        // A narrowed subscription must not be woken by a change to an instance it never sees. The
        // comparison is against the selected view, not the server's list, which is what the Java
        // selector wrapper does.
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(
            Service, KeepPort(8081));

        await subscription.RequireNextAsync();

        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-2", ports: [8082, 8081]),
            TestContext.Current.CancellationToken);

        (await subscription.TryNextAsync(TimeSpan.FromMilliseconds(200))).ShouldBeNull();
    }

    [Fact]
    public async Task Subscribe_WithSelector_DeliversWhenTheSelectedViewChanges()
    {
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(
            Service, KeepPort(8081));

        await subscription.RequireNextAsync();

        // The kept instance is gone, so the selected view becomes empty even though the server's
        // list is still non-empty: the consumer has to be told, or it keeps routing to an instance
        // the server no longer reports.
        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-3", ports: [8082, 8083]),
            TestContext.Current.CancellationToken);

        NacosServiceChange change = await subscription.RequireNextAsync();
        change.Instances.ShouldBeEmpty();
        change.PreviousChecksum.ShouldBe("sum-1");
    }

    [Fact]
    public async Task Subscribe_WithSelector_DeliversAnInstanceThatEntersTheSelection()
    {
        using NamingHarness harness = new(handler: request => PayloadCodec.ReadTypeName(request) switch
        {
            "ServiceQueryRequest" => QueryResponse([Host(8080)]),
            "SubscribeServiceRequest" => SubscribeResponse(checksum: "sum-1", ports: [8080]),
            _ => NamingHarness.Fallback(request),
        });

        await using Subscription subscription = await harness.SubscribeAsync(
            Service, KeepPort(8081));

        (await subscription.RequireNextAsync()).Instances.ShouldBeEmpty();

        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-2", ports: [8080, 8081]),
            TestContext.Current.CancellationToken);

        (await subscription.RequireNextAsync()).Instances.ShouldHaveSingleItem().Port.ShouldBe(8081);
    }

    [Fact]
    public async Task Subscribe_WithSelector_FiltersOnlyItsOwnConsumer()
    {
        // The server subscription and its replay are per service, while the view is per consumer.
        // Sharing the subscription must not make a plain subscriber inherit someone else's filter.
        using NamingHarness harness = new();

        await using Subscription plain = await harness.SubscribeAsync(Service);
        await using Subscription narrowed = await harness.SubscribeAsync(Service, KeepPort(8081));

        await plain.RequireNextAsync();
        await narrowed.RequireNextAsync();

        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-2", ports: [8082, 8081]),
            TestContext.Current.CancellationToken);

        (await plain.RequireNextAsync()).Instances.Count.ShouldBe(2);
        (await narrowed.TryNextAsync(TimeSpan.FromMilliseconds(200))).ShouldBeNull();

        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-3", ports: [8082, 8083]),
            TestContext.Current.CancellationToken);

        (await narrowed.RequireNextAsync()).Instances.ShouldBeEmpty();
        (await plain.RequireNextAsync()).Instances.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Subscribe_WithSelector_ThatThrows_KeepsTheViewItLastDelivered()
    {
        // The selector runs on the shared push path, so its failure is caught and logged rather than
        // escaping: throwing there would abandon the other consumers of the service and surface as
        // an unobserved task failure. The subscription keeps the view it last delivered, which the
        // suppressed push below proves: had the failure reset it, the instance would look new.
        using NamingHarness harness = new();

        // Port 8082 is the marker that makes the selector fail. Basing the failure on the state
        // rather than on a call count keeps the test independent of how many times the client asks
        // the selector — it also runs for the state the server echoes back when a subscription
        // becomes live.
        INacosInstanceSelector selector = NacosInstanceSelectors.From(context =>
        {
            if (context.Instances.Any(instance => instance.Port == 8082))
            {
                throw new InvalidOperationException("selector failed");
            }

            return [.. context.Instances.Where(instance => instance.Port == 8081)];
        });

        await using Subscription subscription = await harness.SubscribeAsync(Service, selector);

        (await subscription.RequireNextAsync()).Instances.ShouldHaveSingleItem().Port.ShouldBe(8081);

        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-2", ports: [8082, 8083]),
            TestContext.Current.CancellationToken);

        (await subscription.TryNextAsync(TimeSpan.FromMilliseconds(200))).ShouldBeNull();

        // The kept instance is back, which leaves the selected view exactly as it was before the
        // failure, so there is nothing to deliver.
        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-3", ports: [8083, 8081]),
            TestContext.Current.CancellationToken);

        (await subscription.TryNextAsync(TimeSpan.FromMilliseconds(200))).ShouldBeNull();

        // A later change that really alters the view is still delivered, so the failure did not
        // wedge the subscription.
        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-4", ports: [8083, 8084]),
            TestContext.Current.CancellationToken);

        (await subscription.RequireNextAsync()).Instances.ShouldBeEmpty();
    }

    [Fact]
    public async Task Subscribe_WithSelector_EndsWhenTheEnumerationIsCancelled()
    {
        // The selector overload has no token parameter, so the token has to arrive through
        // WithCancellation; without this test the overload would only ever end on disposal.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();

        await using var changes = harness.Client.SubscribeAsync(Service, NacosInstanceSelectors.All)
            .WithCancellation(cts.Token)
            .GetAsyncEnumerator();

        (await changes.MoveNextAsync()).ShouldBeTrue();

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () =>
            {
                await changes.MoveNextAsync();
            });
    }

    [Fact]
    public async Task Reconnect_DeliversWhatChangedWhileTheSubscriptionWasGone()
    {
        // The server discarded the subscription with the connection, so nothing could be pushed
        // while the client was away. The answer to the replay carries the state at the instant the
        // subscription was restored, which is the only report of what was missed.
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(Service);
        (await subscription.NextAsync()).Service.Checksum.ShouldBe("sum-1");

        // The service changes while the client is disconnected.
        harness.Handler = request => PayloadCodec.ReadTypeName(request) switch
        {
            "SubscribeServiceRequest" => SubscribeResponse(checksum: "sum-3", ports: [8080, 8083]),
            _ => NamingHarness.Fallback(request),
        };

        int subscriptionsBefore = harness.CountRequests("SubscribeServiceRequest");

        await harness.DropAndReconnectAsync();

        await NamingHarness.WaitForAsync(
            () => harness.CountRequests("SubscribeServiceRequest") > subscriptionsBefore);

        NacosServiceChange missed = await subscription.RequireNextAsync();
        missed.PreviousChecksum.ShouldBe("sum-1");
        missed.Service.Checksum.ShouldBe("sum-3");
        missed.Instances.ShouldContain(instance => instance.Port == 8083);
    }

    [Fact]
    public async Task Reconnect_ReplaysRegistrationsAndSubscriptions()
    {
        // PLAN.md requires the client to restore everything the server discarded with the
        // connection, or the instance silently stops being discoverable after an outage.
        using NamingHarness harness = new();

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.RegisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        await using Subscription subscription = await harness.SubscribeAsync(Service);
        await subscription.NextAsync();

        int registrationsBefore = harness.CountRequests("InstanceRequest");
        int subscriptionsBefore = harness.CountRequests("SubscribeServiceRequest");

        await harness.DropAndReconnectAsync();

        await NamingHarness.WaitForAsync(
            () => harness.CountRequests("InstanceRequest") > registrationsBefore);

        harness.CountRequests("InstanceRequest").ShouldBe(registrationsBefore + 1);
        harness.CountRequests("SubscribeServiceRequest").ShouldBeGreaterThanOrEqualTo(subscriptionsBefore + 1);
    }

    [Fact]
    public async Task Dispose_EndsEverySubscription()
    {
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(Service);
        await subscription.NextAsync();

        await harness.Client.DisposeAsync();

        // The stream completed rather than hanging, so the enumeration ends cleanly.
        (await subscription.TryNextAsync(TimeSpan.FromSeconds(2))).ShouldBeNull();
    }

    [Fact]
    public async Task RegisterInstance_ThrowsAfterDispose()
    {
        using NamingHarness harness = new();
        await harness.Client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => harness.Client.RegisterInstanceAsync(
                Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetServices_SendsThePageAndGroup()
    {
        using NamingHarness harness = new(@namespace: "tenant-a", handler: request =>
            PayloadCodec.ReadTypeName(request) == "ServiceListRequest"
                ? ServicesResponse(count: 5, "alpha", "beta")
                : NamingHarness.Fallback(request));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        NacosServiceListPage page = await harness.Client.GetServicesAsync(
            pageNo: 2, pageSize: 2, groupName: "tenant-a-group",
            ct: TestContext.Current.CancellationToken);

        ServiceListRequest request = harness.LastRequest<ServiceListRequest>();
        request.Namespace.ShouldBe("tenant-a");
        request.GroupName.ShouldBe("tenant-a-group");
        request.PageNo.ShouldBe(2);
        request.PageSize.ShouldBe(2);

        // Count is the group total the server reported before paging, not the page size.
        page.Count.ShouldBe(5);
        page.Services.ShouldBe(["alpha", "beta"]);
    }

    [Fact]
    public async Task GetServices_DefaultsToTheFirstPageOfTheDefaultGroup()
    {
        // The Java SDK's two-argument overload lists DEFAULT_GROUP; a caller that omits both the
        // group and the paging has to land on the same request.
        using NamingHarness harness = new(handler: request =>
            PayloadCodec.ReadTypeName(request) == "ServiceListRequest"
                ? ServicesResponse(count: 0)
                : NamingHarness.Fallback(request));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.GetServicesAsync(ct: TestContext.Current.CancellationToken);

        ServiceListRequest request = harness.LastRequest<ServiceListRequest>();
        request.GroupName.ShouldBe(NacosConstants.DefaultGroup);
        request.PageNo.ShouldBe(1);
        request.PageSize.ShouldBe(10);
    }

    [Fact]
    public async Task GetServices_ReturnsTheGroupCountForAPagePastTheEnd()
    {
        // The server answers a page beyond the last one with no names and the full count, so the
        // caller can still tell how many pages there were.
        using NamingHarness harness = new(handler: request =>
            PayloadCodec.ReadTypeName(request) == "ServiceListRequest"
                ? ServicesResponse(count: 3)
                : NamingHarness.Fallback(request));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        NacosServiceListPage page = await harness.Client.GetServicesAsync(
            pageNo: 4, ct: TestContext.Current.CancellationToken);

        page.Count.ShouldBe(3);
        page.Services.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetServices_TreatsAMissingNameListAsEmpty()
    {
        // The server omits the list when a namespace holds no services; that is an empty page, not
        // a failure to read the answer.
        using NamingHarness harness = new(handler: request =>
            PayloadCodec.ReadTypeName(request) == "ServiceListRequest"
                ? PayloadCodec.Encode(new ServiceListResponse { Count = 0, ServiceNames = null })
                : NamingHarness.Fallback(request));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        NacosServiceListPage page = await harness.Client.GetServicesAsync(
            ct: TestContext.Current.CancellationToken);

        page.Count.ShouldBe(0);
        page.Services.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetServices_RejectsAPageNumberBelowOne(int pageNo)
    {
        // The server would clamp the start offset, which turns a caller's mistake into a silently
        // wrong page; rejecting it here makes the mistake visible at the call site.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => harness.Client.GetServicesAsync(
                pageNo: pageNo, ct: TestContext.Current.CancellationToken));

        harness.CountRequests("ServiceListRequest").ShouldBe(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetServices_RejectsAPageSizeBelowOne(int pageSize)
    {
        // A non-positive page size would reach the server's subList and fail there with an
        // unrelated error.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => harness.Client.GetServicesAsync(
                pageSize: pageSize, ct: TestContext.Current.CancellationToken));

        harness.CountRequests("ServiceListRequest").ShouldBe(0);
    }

    [Fact]
    public async Task GetServices_DoesNotServeFromCacheWhenTheServerIsUnreachable()
    {
        // Unlike an instance list, the service list describes the namespace: a stale copy would
        // silently hide services that were added during the outage, so there is no failover here.
        using NamingHarness harness = new(handler: _ =>
            throw new NacosConnectionException("server is down"));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NacosConnectionException>(
            () => harness.Client.GetServicesAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetServices_ThrowsAfterDispose()
    {
        using NamingHarness harness = new();
        await harness.Client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => harness.Client.GetServicesAsync(ct: TestContext.Current.CancellationToken));
    }

    /// <summary>Builds a successful service list response.</summary>
    /// <param name="count">The total number of services in the group.</param>
    /// <param name="names">The names on the page.</param>
    /// <returns>The encoded response.</returns>
    private static Payload ServicesResponse(int count, params string[] names) =>
        PayloadCodec.Encode(new ServiceListResponse
        {
            Count = count,
            ServiceNames = [.. names],
        });

    /// <summary>Builds a successful query response carrying the given hosts.</summary>
    private static Payload QueryResponse(IReadOnlyList<Instance> hosts) =>
        PayloadCodec.Encode(new QueryServiceResponse
        {
            ServiceInfo = new ServiceInfo
            {
                Name = "order-service",
                GroupName = "DEFAULT_GROUP",
                Checksum = "sum-1",
                Hosts = [.. hosts],
            },
        });

    /// <summary>Builds a successful subscription response for the default service.</summary>
    /// <param name="checksum">The checksum of the state the server reports.</param>
    /// <param name="ports">The ports the server reports.</param>
    /// <returns>The encoded response.</returns>
    private static Payload SubscribeResponse(string checksum = "sum-1", IReadOnlyList<int>? ports = null)
    {
        IReadOnlyList<int> effectivePorts = ports ?? new[] { 8080, 8081 };

        return PayloadCodec.Encode(new SubscribeServiceResponse
        {
            ServiceInfo = new ServiceInfo
            {
                Name = "order-service",
                GroupName = "DEFAULT_GROUP",
                Checksum = checksum,
                Hosts = [.. effectivePorts.Select(port => Host(port))],
            },
        });
    }

    /// <summary>Builds a full subscriber push for the default service.</summary>
    /// <remarks>
    /// The server only populates the service info, so the push deliberately leaves the request's own
    /// namespace, service and group fields unset; a scripted push that filled them in would hide a
    /// client that reads the identity from the wrong place.
    /// </remarks>
    private static NotifySubscriberRequest Push(string checksum, IReadOnlyList<int> ports) => new()
    {
        ServiceInfo = new ServiceInfo
        {
            Name = "order-service",
            GroupName = "DEFAULT_GROUP",
            Checksum = checksum,
            Hosts = [.. ports.Select(port => Host(port))],
        },
    };

    /// <summary>Builds a selector that keeps the instances of one port.</summary>
    /// <param name="port">The port to keep.</param>
    /// <returns>The selector.</returns>
    private static INacosInstanceSelector KeepPort(int port) =>
        NacosInstanceSelectors.From(context =>
            [.. context.Instances.Where(instance => instance.Port == port)]);

    /// <summary>Builds a single healthy wire host.</summary>
    private static Instance Host(int port, double weight = 1.0) => new()
    {
        Ip = "10.0.0.1",
        Port = port,
        Weight = weight,
        ClusterName = "DEFAULT",
        Healthy = true,
        Enabled = true,
        InstanceId = "inst-" + port.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Wires a naming client to a fake transport and exposes the requests it sent.
    /// </summary>
    private sealed class NamingHarness : IDisposable
    {
        private readonly Queue<FakeNacosDuplexStream> _streams;
        private readonly List<FakeNacosChannel> _channels = [];
        private readonly NacosGrpcConnection _connection;

        private FakeNacosDuplexStream _currentStream = null!;

        public NamingHarness(
            string @namespace = "public",
            string cluster = "DEFAULT",
            Func<Payload, Payload>? handler = null,
            NacosServiceSnapshotStore? snapshots = null)
        {
            Handler = handler ?? Fallback;
            Snapshots = snapshots;

            // A generous supply of streams so tests can drop one and let a fresh one replace it.
            _streams = new Queue<FakeNacosDuplexStream>(
                Enumerable.Range(0, 4).Select(_ => new FakeNacosDuplexStream()));

            var factory = new FakeNacosChannelFactory(address =>
            {
                var channel = new FakeNacosChannel(
                    address,
                    request => PayloadCodec.ReadTypeName(request) == "ServerCheckRequest"
                        ? FakeServerCheck.Responding()(request)
                        : Handler(request),
                    NextStream());

                _channels.Add(channel);
                return channel;
            });

            var options = new NacosOptions
            {
                ServerAddresses = ["127.0.0.1:8848"],
                Namespace = @namespace,
                Naming = new NacosNamingOptions { DefaultCluster = cluster },
                Reconnect = new NacosReconnectOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    MaxDelay = TimeSpan.FromMilliseconds(2),
                    JitterFactor = 0,
                },
            };

            _connection = new NacosGrpcConnection(
                options,
                NacosConnectionModule.Naming,
                [Server],
                factory,
                TimeProvider.System,
                NullLogger.Instance,
                new Random(1234));

            Client = new NacosNamingClient(
                options, _connection, TimeProvider.System, NullLogger<NacosNamingClient>.Instance, Snapshots);
        }

        /// <summary>Gets or sets the request handler, so a test can change behaviour midway.</summary>
        public Func<Payload, Payload> Handler { get; set; }

        public NacosNamingClient Client { get; }

        /// <summary>Gets the snapshot store the client writes to, when the test supplied one.</summary>
        public NacosServiceSnapshotStore? Snapshots { get; }

        /// <summary>
        /// Answers request types the test did not script with a successful response of the
        /// matching kind.
        /// </summary>
        /// <param name="request">The request to answer.</param>
        /// <returns>The encoded response.</returns>
        public static Payload Fallback(Payload request) => PayloadCodec.ReadTypeName(request) switch
        {
            "ServiceQueryRequest" => QueryResponse([Host(8080), Host(8081)]),
            "ServiceListRequest" => PayloadCodec.Encode(
                new ServiceListResponse { Count = 0, ServiceNames = [] }),
            "SubscribeServiceRequest" => SubscribeResponse(),
            "InstanceRequest" or "PersistentInstanceRequest" => PayloadCodec.Encode(new InstanceResponse()),
            "BatchInstanceRequest" => PayloadCodec.Encode(new BatchInstanceResponse()),
            _ => throw new InvalidOperationException(
                $"The harness has no response for '{PayloadCodec.ReadTypeName(request)}'."),
        };

        /// <summary>Establishes the transport.</summary>
        /// <param name="ct">A token that cancels the connection.</param>
        /// <returns>A task that completes once connected.</returns>
        public Task ConnectAsync(CancellationToken ct) => _connection.ConnectAsync(ct);

        /// <summary>Counts the unary requests of a payload type name across every channel.</summary>
        /// <param name="typeName">The payload type name.</param>
        /// <returns>The number of matching requests.</returns>
        public int CountRequests(string typeName) =>
            _channels.Sum(channel => channel.UnaryRequests.Count(
                payload => PayloadCodec.ReadTypeName(payload) == typeName));

        /// <summary>Decodes the most recent request of the given type.</summary>
        /// <typeparam name="T">The expected request type.</typeparam>
        /// <returns>The decoded request.</returns>
        public T LastRequest<T>()
            where T : NacosRequest
        {
            for (int i = _channels.Count - 1; i >= 0; i--)
            {
                for (int j = _channels[i].UnaryRequests.Count - 1; j >= 0; j--)
                {
                    Payload payload = _channels[i].UnaryRequests[j];

                    if (PayloadCodec.ReadTypeName(payload) == typeof(T).Name)
                    {
                        return PayloadCodec.Decode<T>(payload)
                            ?? throw new InvalidOperationException(
                                $"The last '{typeof(T).Name}' request could not be decoded.");
                    }
                }
            }

            throw new InvalidOperationException($"No '{typeof(T).Name}' request was sent.");
        }

        /// <summary>Gets the payload type name of the most recent unary request.</summary>
        /// <returns>The type name.</returns>
        public string LastRequestTypeName() =>
            PayloadCodec.ReadTypeName(_channels[^1].UnaryRequests[^1]);

        /// <summary>Starts a subscription and returns a handle that reads its changes.</summary>
        /// <param name="service">The service to observe.</param>
        /// <param name="selector">
        /// The client-side filter to apply, or <see langword="null"/> for the plain subscription.
        /// </param>
        /// <returns>The subscription handle.</returns>
        public async Task<Subscription> SubscribeAsync(
            NacosServiceKey service, INacosInstanceSelector? selector = null)
        {
            await _connection.ConnectAsync(TestContext.Current.CancellationToken);

            var changes = Channel.CreateUnbounded<NacosServiceChange>();
            var cts = new CancellationTokenSource();

            Task pump = Task.Run(async () =>
            {
                try
                {
                    if (selector is null)
                    {
                        await foreach (NacosServiceChange change in
                            Client.SubscribeAsync(service, ct: cts.Token))
                        {
                            changes.Writer.TryWrite(change);
                        }
                    }
                    else
                    {
                        // The selector overload takes no token of its own, so the handle supplies
                        // one the way a consumer would: through WithCancellation. That also makes
                        // disposal a check that the token really does reach the iterator.
                        await foreach (NacosServiceChange change in
                            Client.SubscribeAsync(service, selector).WithCancellation(cts.Token))
                        {
                            changes.Writer.TryWrite(change);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when the handle is disposed.
                }
                finally
                {
                    changes.Writer.TryComplete();
                }
            });

            // The server-side subscription exists only after the request was sent, so wait for it
            // before allowing a test to push a change.
            await WaitForAsync(() => CountRequests("SubscribeServiceRequest") >= 1);

            return new Subscription(changes.Reader, cts, pump);
        }

        /// <summary>Drops the current stream and waits for the automatic reconnect to connect.</summary>
        /// <returns>A task that completes once a fresh channel is in use.</returns>
        public async Task DropAndReconnectAsync()
        {
            int channelsBefore = _channels.Count;
            _currentStream.EndStream();

            await WaitForAsync(() => _channels.Count > channelsBefore);

            // Wait for the fresh channel to have completed a handshake before returning.
            await WaitForAsync(() => _channels[^1].UnaryRequests.Count >= 1);
        }

        /// <summary>Waits until a condition holds, or throws when it never does.</summary>
        /// <param name="condition">The condition to wait for.</param>
        /// <returns>A task that completes once the condition holds.</returns>
        public static async Task WaitForAsync(Func<bool> condition)
        {
            for (int i = 0; i < 1000; i++)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("The expected condition was not met within the timeout.");
        }

        public void Dispose()
        {
            Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private FakeNacosDuplexStream NextStream()
        {
            _currentStream = _streams.Count > 0 ? _streams.Dequeue() : new FakeNacosDuplexStream();
            return _currentStream;
        }
    }

    /// <summary>Reads changes from a running subscription.</summary>
    private sealed class Subscription : IAsyncDisposable
    {
        private readonly ChannelReader<NacosServiceChange> _reader;
        private readonly CancellationTokenSource _cts;
        private readonly Task _pump;

        public Subscription(
            ChannelReader<NacosServiceChange> reader, CancellationTokenSource cts, Task pump)
        {
            _reader = reader;
            _cts = cts;
            _pump = pump;
        }

        /// <summary>Waits for the next change.</summary>
        /// <returns>The change.</returns>
        public Task<NacosServiceChange> NextAsync() =>
            _reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();

        /// <summary>
        /// Waits for the next change and fails the test when it never arrives.
        /// </summary>
        /// <returns>The change.</returns>
        /// <remarks>
        /// Reading without a bound would hang the whole suite when the delivery under test never
        /// happens, which hides the failure instead of reporting it.
        /// </remarks>
        public async Task<NacosServiceChange> RequireNextAsync()
        {
            NacosServiceChange? change = await TryNextAsync(TimeSpan.FromSeconds(10));

            change.ShouldNotBeNull("The subscription produced no change but one was expected.");
            return change;
        }

        /// <summary>Waits a bounded time for a change, returning null when none arrives.</summary>
        /// <param name="timeout">How long to wait.</param>
        /// <returns>The change, or <see langword="null"/>.</returns>
        public async Task<NacosServiceChange?> TryNextAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await _reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ChannelClosedException)
            {
                // The subscription ended, which is what a disposal test is asserting.
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _pump;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            _cts.Dispose();
        }
    }
}
