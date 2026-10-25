using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Naming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies service registration and discovery end to end against real Nacos servers.
/// </summary>
/// <remarks>
/// PLAN.md's acceptance criterion for M3 is that a subscriber sees instances come online and go
/// offline, and that an instance is removed when its process exits. Both depend on the server's
/// own connection lifecycle, which a scripted server cannot reproduce.
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class NamingIntegrationTests
{
    [Fact]
    public async Task RegisterReadDeregister_RoundTripsThroughARealServer()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using NacosClientFactory client = CreateClient(container);
        await client.ConnectAsync(TestContext.Current.CancellationToken);

        var service = new NacosServiceKey(NacosHttp.UniqueId("naming-it"), "DEFAULT_GROUP");
        var instance = new NacosInstance("10.20.30.40", 8080);

        await client.Naming.RegisterInstanceAsync(service, instance, TestContext.Current.CancellationToken);

        // A registration answers before the server lists the instance, so the read waits for it.
        NacosServiceInfo info = await NacosEventually.BecomesAsync(
            () => client.Naming.GetInstancesAsync(service, ct: TestContext.Current.CancellationToken),
            result => result.Instances.Any(i => i.Port == 8080 && i.IpAddress == "10.20.30.40"),
            TestContext.Current.CancellationToken);

        info.Instances.ShouldContain(i => i.Port == 8080 && i.IpAddress == "10.20.30.40");

        await client.Naming.DeregisterInstanceAsync(service, instance, TestContext.Current.CancellationToken);

        NacosServiceInfo after = await NacosEventually.BecomesAsync(
            () => client.Naming.GetInstancesAsync(service, ct: TestContext.Current.CancellationToken),
            result => !result.Instances.Any(i => i.Port == 8080),
            TestContext.Current.CancellationToken);

        after.Instances.ShouldNotContain(i => i.Port == 8080);
    }

    [Fact]
    public async Task Subscribe_ReceivesInstancesComingOnlineAndGoingOffline()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using NacosClientFactory client = CreateClient(container);
        await client.ConnectAsync(TestContext.Current.CancellationToken);

        var service = new NacosServiceKey(NacosHttp.UniqueId("naming-subscribe-it"), "DEFAULT_GROUP");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        await using IAsyncEnumerator<NacosServiceChange> enumerator =
            client.Naming.SubscribeAsync(service, ct: cts.Token).GetAsyncEnumerator(cts.Token);

        // The subscription's first element is the (empty) current state.
        (await enumerator.MoveNextAsync()).ShouldBeTrue();

        var instance = new NacosInstance("10.20.30.41", 8081);

        // The subscription is registered with the server just after its current state is yielded, so
        // an instance registered in that window is not pushed; the state is toggled until the online
        // push arrives, because re-registering an instance the server already holds is not a change.
        await NacosEventually.PushedAsync(
            async ct =>
            {
                await client.Naming.DeregisterInstanceAsync(service, instance, ct).ConfigureAwait(false);
                await client.Naming.RegisterInstanceAsync(service, instance, ct).ConfigureAwait(false);
            },
            enumerator,
            change => change.Instances.Any(i => i.Port == 8081),
            cts.Token);

        enumerator.Current.Instances.ShouldContain(i => i.Port == 8081);

        // The subscription is effective by now, so the offline push follows the deregistration.
        await NacosEventually.PushedAsync(
            ct => client.Naming.DeregisterInstanceAsync(service, instance, ct),
            enumerator,
            change => change.Instances.All(i => i.Port != 8081),
            cts.Token);

        enumerator.Current.Instances.ShouldNotContain(i => i.Port == 8081);
    }

    [Fact]
    public async Task Watch_ReportsServicesEnteringAndLeavingTheMatchedSet()
    {
        // A naming fuzzy watch tracks which services match a pattern, not their instances: the
        // server pushes an add when the first instance creates the service and a delete when its
        // last instance goes away. Instance lists stay the job of SubscribeAsync.
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using NacosClientFactory client = CreateClient(container);
        await client.ConnectAsync(TestContext.Current.CancellationToken);

        string prefix = NacosHttp.UniqueId("naming-fuzzy-it");
        var service = new NacosServiceKey(prefix, "DEFAULT_GROUP");
        var instance = new NacosInstance("10.20.30.43", 8083);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        await using IAsyncEnumerator<NacosServiceWatchEvent> events =
            client.Naming.WatchAsync($"{prefix}*", "DEFAULT_GROUP", cts.Token)
                .GetAsyncEnumerator(cts.Token);

        // Nothing matches the pattern yet, so the initial sync is the finish marker alone.
        await NacosEventually.ReportsAsync(
            events,
            item => item.Kind == NacosServiceWatchKind.InitialSyncCompleted,
            cts.Token);

        // The watch is effective by now, so the first registration is pushed. The cycle is repeated
        // because a second registration of an instance the server already holds is not a change.
        await NacosEventually.PushedAsync(
            async ct =>
            {
                await client.Naming.DeregisterInstanceAsync(service, instance, ct).ConfigureAwait(false);
                await client.Naming.RegisterInstanceAsync(service, instance, ct).ConfigureAwait(false);
            },
            events,
            item => item.Kind == NacosServiceWatchKind.Added
                && item.Service?.ServiceName == service.ServiceName,
            cts.Token);

        events.Current.Service!.GroupName.ShouldBe("DEFAULT_GROUP");
        events.Current.Service.Namespace.ShouldBe("public");

        await NacosEventually.PushedAsync(
            ct => client.Naming.DeregisterInstanceAsync(service, instance, ct),
            events,
            item => item.Kind == NacosServiceWatchKind.Removed
                && item.Service?.ServiceName == service.ServiceName,
            cts.Token);

        // The service is gone from the set, so registering it again is an addition rather than an
        // update to something the subscription never learned about.
        await NacosEventually.PushedAsync(
            ct => client.Naming.RegisterInstanceAsync(service, instance, ct),
            events,
            item => item.Kind == NacosServiceWatchKind.Added
                && item.Service?.ServiceName == service.ServiceName,
            cts.Token);

        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
    }

    [Fact]
    public async Task Dispose_RemovesTheEphemeralInstanceFromTheServer()
    {
        // The server removes an ephemeral instance when the connection that owns it closes, which
        // is what makes "the process exited" equivalent to "the instance is gone".
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);

        var service = new NacosServiceKey(NacosHttp.UniqueId("naming-dispose-it"), "DEFAULT_GROUP");
        var instance = new NacosInstance("10.20.30.42", 8082);

        await using (NacosClientFactory owner = CreateClient(container))
        {
            await owner.ConnectAsync(TestContext.Current.CancellationToken);
            await owner.Naming.RegisterInstanceAsync(service, instance, TestContext.Current.CancellationToken);

            // The instance is confirmed to exist while its owner is connected, so that the wait after
            // the owner goes away proves a removal rather than a registration that never happened.
            await NacosEventually.BecomesAsync(
                () => owner.Naming.GetInstancesAsync(service, ct: TestContext.Current.CancellationToken),
                result => result.Instances.Any(i => i.Port == 8082),
                TestContext.Current.CancellationToken);
        }

        await using NacosClientFactory observer = CreateClient(container);
        await observer.ConnectAsync(TestContext.Current.CancellationToken);

        // The server drops an ephemeral instance when its connection closes, which it does
        // asynchronously, so the read waits for the instance to disappear.
        NacosServiceInfo info = await NacosEventually.BecomesAsync(
            () => observer.Naming.GetInstancesAsync(service, ct: TestContext.Current.CancellationToken),
            result => !result.Instances.Any(i => i.Port == 8082),
            TestContext.Current.CancellationToken);

        info.Instances.ShouldNotContain(i => i.Port == 8082);
    }

    private static NacosClientFactory CreateClient(NacosContainer container) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                Reconnect = new NacosReconnectOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(500),
                    MaxDelay = TimeSpan.FromSeconds(5),
                    JitterFactor = 0.1,
                },
            },
            httpClient: null,
            channelFactory: null,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance);
}
