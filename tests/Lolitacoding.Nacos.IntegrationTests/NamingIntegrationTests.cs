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
    /// <summary>Each server generation the client claims to support.</summary>
    public static TheoryData<string> Images => new()
    {
        NacosIntegrationImages.V2Image,
        NacosIntegrationImages.V3Image,
    };

    [Theory]
    [MemberData(nameof(Images))]
    public async Task RegisterReadDeregister_RoundTripsThroughARealServer(string image)
    {
        await using NacosContainer container = await NacosContainer.StartAsync(image);
        await using NacosClientFactory client = CreateClient(container);

        var service = new NacosServiceKey(NacosHttp.UniqueId("naming-it"), "DEFAULT_GROUP");
        var instance = new NacosInstance("10.20.30.40", 8080);

        await client.Naming.RegisterInstanceAsync(service, instance, TestContext.Current.CancellationToken);

        NacosServiceInfo info = await client.Naming.GetInstancesAsync(
            service, ct: TestContext.Current.CancellationToken);

        info.Instances.ShouldContain(i => i.Port == 8080 && i.IpAddress == "10.20.30.40");

        await client.Naming.DeregisterInstanceAsync(service, instance, TestContext.Current.CancellationToken);

        NacosServiceInfo after = await client.Naming.GetInstancesAsync(
            service, ct: TestContext.Current.CancellationToken);

        after.Instances.ShouldNotContain(i => i.Port == 8080);
    }

    [Theory]
    [MemberData(nameof(Images))]
    public async Task Subscribe_ReceivesInstancesComingOnlineAndGoingOffline(string image)
    {
        await using NacosContainer container = await NacosContainer.StartAsync(image);
        await using NacosClientFactory client = CreateClient(container);

        var service = new NacosServiceKey(NacosHttp.UniqueId("naming-subscribe-it"), "DEFAULT_GROUP");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        await using IAsyncEnumerator<NacosServiceChange> enumerator =
            client.Naming.SubscribeAsync(service, ct: cts.Token).GetAsyncEnumerator(cts.Token);

        // The subscription's first element is the (empty) current state.
        (await enumerator.MoveNextAsync()).ShouldBeTrue();

        var instance = new NacosInstance("10.20.30.41", 8081);
        await client.Naming.RegisterInstanceAsync(service, instance, cts.Token);

        // Wait for the online push.
        while (await enumerator.MoveNextAsync())
        {
            if (enumerator.Current.Instances.Any(i => i.Port == 8081))
            {
                break;
            }
        }

        enumerator.Current.Instances.ShouldContain(i => i.Port == 8081);

        await client.Naming.DeregisterInstanceAsync(service, instance, cts.Token);

        // Wait for the offline push.
        while (await enumerator.MoveNextAsync())
        {
            if (enumerator.Current.Instances.All(i => i.Port != 8081))
            {
                break;
            }
        }

        enumerator.Current.Instances.ShouldNotContain(i => i.Port == 8081);
    }

    [Theory]
    [MemberData(nameof(Images))]
    public async Task Dispose_RemovesTheEphemeralInstanceFromTheServer(string image)
    {
        // The server removes an ephemeral instance when the connection that owns it closes, which
        // is what makes "the process exited" equivalent to "the instance is gone".
        await using NacosContainer container = await NacosContainer.StartAsync(image);

        var service = new NacosServiceKey(NacosHttp.UniqueId("naming-dispose-it"), "DEFAULT_GROUP");
        var instance = new NacosInstance("10.20.30.42", 8082);

        await using (NacosClientFactory owner = CreateClient(container))
        {
            await owner.Naming.RegisterInstanceAsync(service, instance, TestContext.Current.CancellationToken);
        }

        await using NacosClientFactory observer = CreateClient(container);

        // Removal is asynchronous on the server, so the assertion polls rather than assuming.
        for (int attempt = 0; ; attempt++)
        {
            NacosServiceInfo info = await observer.Naming.GetInstancesAsync(
                service, ct: TestContext.Current.CancellationToken);

            if (!info.Instances.Any(i => i.Port == 8082))
            {
                break;
            }

            if (attempt >= 30)
            {
                throw new TimeoutException(
                    "The ephemeral instance was not removed after its owning connection closed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }
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
