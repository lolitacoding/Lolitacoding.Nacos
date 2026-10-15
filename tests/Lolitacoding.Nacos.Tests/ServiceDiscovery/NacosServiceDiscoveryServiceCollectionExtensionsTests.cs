using Lolitacoding.Nacos.Extensions.DependencyInjection;
using Lolitacoding.Nacos.Extensions.ServiceDiscovery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.ServiceDiscovery;

namespace Lolitacoding.Nacos.Tests.ServiceDiscovery;

/// <summary>
/// Verifies the dependency injection registration of the Nacos service discovery integration.
/// </summary>
public sealed class NacosServiceDiscoveryServiceCollectionExtensionsTests
{
    private const string Server = "http://127.0.0.1:8848";

    [Fact]
    public void AddNacosServiceDiscovery_RegistersTheNacosProviderFactory()
    {
        using ServiceProvider provider = Services().BuildServiceProvider();

        provider.GetServices<IServiceEndpointProviderFactory>()
            .ShouldContain(factory => factory is NacosServiceEndpointProviderFactory);
    }

    [Fact]
    public async Task AddNacosServiceDiscovery_KeepsTheResolverAvailable()
    {
        // The registration adds to the providers AddServiceDiscovery installs rather than
        // replacing them, so the resolver must still be constructible.
        await using ServiceProvider provider = Services().BuildServiceProvider();

        provider.GetRequiredService<ServiceEndpointResolver>().ShouldNotBeNull();
    }

    [Fact]
    public void AddNacosServiceDiscovery_IsIdempotent()
    {
        ServiceCollection services = Services();
        services.AddNacosServiceDiscovery(o => { });

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetServices<IServiceEndpointProviderFactory>()
            .Count(factory => factory is NacosServiceEndpointProviderFactory)
            .ShouldBe(1);
    }

    [Fact]
    public void AddNacosServiceDiscovery_BindsTheOptions()
    {
        using ServiceProvider provider = Services(o =>
        {
            o.GroupName = "TENANT_A";
            o.Cluster = "zone-a";
            o.HealthyOnly = false;
            o.ResolveAllServices = false;
        }).BuildServiceProvider();

        NacosServiceDiscoveryOptions options =
            provider.GetRequiredService<IOptions<NacosServiceDiscoveryOptions>>().Value;

        options.GroupName.ShouldBe("TENANT_A");
        options.Cluster.ShouldBe("zone-a");
        options.HealthyOnly.ShouldBeFalse();
        options.ResolveAllServices.ShouldBeFalse();
    }

    [Fact]
    public void AddNacosServiceDiscovery_RejectsAnEmptyGroupWhenResolved()
    {
        using ServiceProvider provider = Services(o => o.GroupName = " ").BuildServiceProvider();

        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<NacosServiceDiscoveryOptions>>().Value);
    }

    private static ServiceCollection Services(Action<NacosServiceDiscoveryOptions>? configure = null)
    {
        var services = new ServiceCollection();

        // A real host always provides configuration; the configuration endpoint provider the
        // ServiceDiscovery package registers depends on it.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddNacosServiceDiscovery(configure ?? (_ => { }));

        // AddNacosServiceDiscovery registers the client graph, but the server address still has to
        // come from somewhere so the shared options validate.
        services.AddNacos(o => o.ServerAddresses = [Server]);

        return services;
    }
}
