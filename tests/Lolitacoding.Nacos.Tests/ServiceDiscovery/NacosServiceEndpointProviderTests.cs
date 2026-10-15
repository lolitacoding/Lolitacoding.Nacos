using System.Net;
using Lolitacoding.Nacos.Extensions.ServiceDiscovery;
using Lolitacoding.Nacos.Naming;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.ServiceDiscovery;

namespace Lolitacoding.Nacos.Tests.ServiceDiscovery;

/// <summary>
/// Verifies how the Nacos endpoint provider turns instances into endpoints.
/// </summary>
public sealed class NacosServiceEndpointProviderTests
{
    [Fact]
    public async Task PopulateAsync_MapsAnIPv4InstanceToAnIPEndPoint()
    {
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.5", 8080)));
        await using NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);

        builder.Endpoints.Count.ShouldBe(1);
        IPEndPoint endpoint = builder.Endpoints[0].EndPoint.ShouldBeOfType<IPEndPoint>();
        endpoint.Address.ShouldBe(IPAddress.Parse("10.0.0.5"));
        endpoint.Port.ShouldBe(8080);
    }

    [Fact]
    public async Task PopulateAsync_MapsEveryInstanceAndKeepsTheServerOrder()
    {
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080),
            ServiceDiscoveryTestData.Instance("10.0.0.2", 8081)));
        await using NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);

        builder.Endpoints.Count.ShouldBe(2);
        builder.Endpoints[0].EndPoint.ShouldBeOfType<IPEndPoint>().Port.ShouldBe(8080);
        builder.Endpoints[1].EndPoint.ShouldBeOfType<IPEndPoint>().Port.ShouldBe(8081);
    }

    [Fact]
    public async Task PopulateAsync_MapsAHostnameInstanceToADnsEndPoint()
    {
        // Nacos normally returns an address, but a deployment that registers a hostname must not
        // have its instances silently dropped.
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("order-host", 8080)));
        await using NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);

        DnsEndPoint endpoint = builder.Endpoints[0].EndPoint.ShouldBeOfType<DnsEndPoint>();
        endpoint.Host.ShouldBe("order-host");
        endpoint.Port.ShouldBe(8080);
    }

    [Fact]
    public async Task PopulateAsync_ExcludesInstancesThatCannotCarryTraffic()
    {
        // Routing to a disabled or unhealthy instance is almost never intended, so the default
        // must filter them without the caller having to ask.
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080),
            ServiceDiscoveryTestData.Instance("10.0.0.2", 8081, healthy: false),
            ServiceDiscoveryTestData.Instance("10.0.0.3", 8082, enabled: false)));
        await using NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);

        builder.Endpoints.Count.ShouldBe(1);
        builder.Endpoints[0].EndPoint.ShouldBeOfType<IPEndPoint>().Port.ShouldBe(8080);
    }

    [Fact]
    public async Task PopulateAsync_IncludesEveryInstanceWhenHealthFilteringIsOff()
    {
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080),
            ServiceDiscoveryTestData.Instance("10.0.0.2", 8081, healthy: false)));
        await using NacosServiceEndpointProvider provider = Provider(naming, healthyOnly: false);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);

        builder.Endpoints.Count.ShouldBe(2);
    }

    [Fact]
    public async Task PopulateAsync_PassesTheConfiguredCoordinatesToTheClient()
    {
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080)));
        await using NacosServiceEndpointProvider provider = Provider(
            naming, cluster: "zone-a", healthyOnly: true);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);

        naming.LastService.ShouldBe(ServiceDiscoveryTestData.Key);
        naming.LastCluster.ShouldBe("zone-a");
        naming.LastHealthyOnly.ShouldBe(true);
    }

    [Fact]
    public async Task PopulateAsync_ExposesTheServiceNameAsTheHostNameFeature()
    {
        // The HTTP handler sets the Host header from this feature, so it must be the name the
        // caller wrote rather than a resolved address.
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080)));
        await using NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);

        builder.Endpoints[0].Features.Get<IHostNameFeature>()!.HostName.ShouldBe("order-service");
    }

    [Fact]
    public async Task PopulateAsync_AddsAChangeToken()
    {
        // Without a token the resolved endpoints would never be refreshed.
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080)));
        await using NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);

        builder.ChangeTokens.ShouldHaveSingleItem();
        builder.ChangeTokens[0].HasChanged.ShouldBeFalse();
    }

    [Fact]
    public async Task PopulateAsync_ResolvesAnEmptyServiceWithoutFailing()
    {
        // A service that exists but has no healthy instance must not turn into a resolution error;
        // the pass-through provider is left to decide what an unresolvable name means.
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info());
        await using NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);

        builder.Endpoints.ShouldBeEmpty();
        builder.ChangeTokens.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task PopulateAsync_LeavesAnAlreadyResolvedServiceAlone()
    {
        // Merging two providers' views of the same service would produce a set neither intended.
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080)));
        await using NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();
        builder.Endpoints.Add(ServiceEndpoint.Create(new DnsEndPoint("elsewhere", 80)));

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);

        builder.Endpoints.Count.ShouldBe(1);
        naming.QueryCount.ShouldBe(0);
        builder.ChangeTokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task PopulateAsync_SignalsTheChangeTokenWhenTheServerPushesAChange()
    {
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080)));
        await using NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);
        IChangeToken token = builder.ChangeTokens.ShouldHaveSingleItem();

        // The push is only delivered once the subscription exists, so wait for it before sending.
        await naming.Subscribed;
        naming.Push(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080),
            ServiceDiscoveryTestData.Instance("10.0.0.2", 8081)));

        await ServiceDiscoveryTestData.WaitForAsync(() => token.HasChanged);
    }

    [Fact]
    public async Task PopulateAsync_PropagatesAFailedLookup()
    {
        // An explicit request for a Nacos service that cannot be resolved is an error the caller
        // must see, not something to hide behind an empty result.
        var naming = new FakeNamingClient(
            ServiceDiscoveryTestData.Info(),
            new NacosConnectionException("server is down"));
        await using NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await Should.ThrowAsync<NacosConnectionException>(
            async () => await provider.PopulateAsync(
                builder, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_EndsTheSubscription()
    {
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080)));
        NacosServiceEndpointProvider provider = Provider(naming);
        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await provider.PopulateAsync(builder, TestContext.Current.CancellationToken);
        await naming.Subscribed;

        await Should.NotThrowAsync(async () => await provider.DisposeAsync());
    }

    [Fact]
    public async Task PopulateAsync_ThrowsAfterDispose()
    {
        var naming = new FakeNamingClient(ServiceDiscoveryTestData.Info(
            ServiceDiscoveryTestData.Instance("10.0.0.1", 8080)));
        NacosServiceEndpointProvider provider = Provider(naming);
        await provider.DisposeAsync();

        var builder = new ServiceDiscoveryTestData.TestEndpointBuilder();

        await Should.ThrowAsync<ObjectDisposedException>(
            async () => await provider.PopulateAsync(
                builder, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ToString_ReportsTheProviderName() =>
        Provider(new FakeNamingClient(ServiceDiscoveryTestData.Info()))
            .ToString()
            .ShouldBe("Nacos");

    private static NacosServiceEndpointProvider Provider(
        FakeNamingClient naming,
        string? cluster = null,
        bool healthyOnly = true) =>
        new(
            naming,
            ServiceDiscoveryTestData.Key,
            "order-service",
            cluster,
            healthyOnly,
            NullLogger.Instance);
}
