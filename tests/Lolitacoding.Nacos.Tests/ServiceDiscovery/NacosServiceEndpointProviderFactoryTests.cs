using Lolitacoding.Nacos.Extensions.ServiceDiscovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.ServiceDiscovery;

namespace Lolitacoding.Nacos.Tests.ServiceDiscovery;

/// <summary>
/// Verifies which service names the Nacos provider claims.
/// </summary>
/// <remarks>
/// Claiming is a promise made without a network call, so these tests pin the rule exactly: an
/// explicit scheme opts a name in, and a bare name is claimed only when the host allows it.
/// </remarks>
public sealed class NacosServiceEndpointProviderFactoryTests
{
    [Theory]
    [InlineData("nacos://order-service")]
    [InlineData("http+nacos://order-service")]
    [InlineData("order-service")]
    public void TryCreateProvider_ClaimsNacosAndBareNames(string serviceUri)
    {
        NacosServiceEndpointProviderFactory factory = Factory();

        factory.TryCreateProvider(Query(serviceUri), out IServiceEndpointProvider? provider)
            .ShouldBeTrue();

        provider.ShouldNotBeNull();
        provider.ShouldBeOfType<NacosServiceEndpointProvider>();
    }

    [Theory]
    [InlineData("http://order-service")]
    [InlineData("https://order-service")]
    public void TryCreateProvider_LeavesOtherSchemesAlone(string serviceUri)
    {
        // Claiming an ordinary HTTP host would shadow the pass-through provider and send a
        // non-Nacos service through the registry.
        NacosServiceEndpointProviderFactory factory = Factory();

        factory.TryCreateProvider(Query(serviceUri), out IServiceEndpointProvider? provider)
            .ShouldBeFalse();

        provider.ShouldBeNull();
    }

    [Fact]
    public void TryCreateProvider_LeavesBareNamesAloneWhenResolutionIsRestricted()
    {
        // A host that shares the pass-through provider with Nacos can require an explicit scheme
        // so ordinary host names are never looked up in the registry.
        NacosServiceEndpointProviderFactory factory = Factory(
            new NacosServiceDiscoveryOptions { ResolveAllServices = false });

        factory.TryCreateProvider(Query("order-service"), out IServiceEndpointProvider? provider)
            .ShouldBeFalse();

        provider.ShouldBeNull();
    }

    [Fact]
    public void TryCreateProvider_StillClaimsAnExplicitSchemeWhenResolutionIsRestricted()
    {
        NacosServiceEndpointProviderFactory factory = Factory(
            new NacosServiceDiscoveryOptions { ResolveAllServices = false });

        factory.TryCreateProvider(Query("nacos://order-service"), out IServiceEndpointProvider? _)
            .ShouldBeTrue();
    }

    [Fact]
    public void TryCreateProvider_ClaimsAConfiguredScheme()
    {
        // A host that writes http://order-service rather than http+nacos://order-service can list
        // the transport scheme instead of the discovery scheme.
        NacosServiceEndpointProviderFactory factory = Factory(
            new NacosServiceDiscoveryOptions { Schemes = ["http"] });

        factory.TryCreateProvider(Query("http://order-service"), out IServiceEndpointProvider? _)
            .ShouldBeTrue();
    }

    [Fact]
    public void TryCreateProvider_MatchesASchemeCaseInsensitively()
    {
        NacosServiceEndpointProviderFactory factory = Factory();

        factory.TryCreateProvider(Query("NACOS://order-service"), out IServiceEndpointProvider? _)
            .ShouldBeTrue();
    }

    [Fact]
    public void TryCreateProvider_BuildsAProviderThatReportsItsName()
    {
        NacosServiceEndpointProviderFactory factory = Factory();

        factory.TryCreateProvider(Query("nacos://order-service"), out IServiceEndpointProvider? provider);

        provider!.ToString().ShouldBe("Nacos");
    }

    private static NacosServiceEndpointProviderFactory Factory(
        NacosServiceDiscoveryOptions? options = null) =>
        new(
            new FakeNamingClient(ServiceDiscoveryTestData.Info()),
            Options.Create(options ?? new NacosServiceDiscoveryOptions()),
            NullLoggerFactory.Instance);

    private static ServiceEndpointQuery Query(string uri)
    {
        ServiceEndpointQuery.TryParse(uri, out ServiceEndpointQuery? query).ShouldBeTrue();
        return query!;
    }
}
