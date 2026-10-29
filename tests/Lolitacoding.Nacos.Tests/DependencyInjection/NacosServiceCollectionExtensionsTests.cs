using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Extensions.DependencyInjection;
using Lolitacoding.Nacos.Locking;
using Lolitacoding.Nacos.Naming;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.Tests.DependencyInjection;

/// <summary>
/// Verifies the dependency injection registration.
/// </summary>
public sealed class NacosServiceCollectionExtensionsTests
{
    private const string Server = "http://127.0.0.1:8848";

    [Fact]
    public void AddNacos_RegistersTheSharedClientAndEveryFeatureClient()
    {
        // Disposal is synchronous on purpose: every feature client supports it, so the container's
        // default `using IHost` shutdown does not throw once the feature clients have been resolved.
        using ServiceProvider provider = Build(o => o.ServerAddresses = [Server]);

        NacosClient client = provider.GetRequiredService<NacosClient>();

        // The feature clients must be the ones the shared client exposes, not separate instances
        // with their own connections.
        provider.GetRequiredService<INacosConfigClient>().ShouldBeSameAs(client.Config);
        provider.GetRequiredService<INacosNamingClient>().ShouldBeSameAs(client.Naming);
        provider.GetRequiredService<INacosLockClient>().ShouldBeSameAs(client.Lock);
        provider.GetRequiredService<INacosAIClient>().ShouldBeSameAs(client.AI);
        provider.GetRequiredService<TimeProvider>().ShouldBe(TimeProvider.System);
    }

    [Fact]
    public void AddNacos_RegistersTheCoreOptionsValidator()
    {
        using ServiceProvider provider = Build(o => o.ServerAddresses = [Server]);

        provider.GetRequiredService<IValidateOptions<NacosOptions>>()
            .ShouldBeOfType<NacosOptionsValidator>();
    }

    [Fact]
    public void AddNacos_RegistersTheLifecycleHostedService()
    {
        var services = new ServiceCollection();
        services.AddNacos(o => o.ServerAddresses = [Server]);

        services
            .ShouldContain(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(NacosHostedService));
    }

    [Fact]
    public void AddNacos_RejectsOptionsWithoutAServerWhenResolved()
    {
        // A misconfigured client is a startup failure, not a surprise on the first request.
        using ServiceProvider provider = Build(o => o.Namespace = "public");

        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<NacosOptions>>().Value);
    }

    [Fact]
    public void AddNacos_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddNacos(o => o.ServerAddresses = [Server]);
        services.AddNacos(o => o.ServerAddresses = ["http://127.0.0.1:8849"]);

        using ServiceProvider provider = services.BuildServiceProvider();

        // Both calls must resolve to the same singleton, so the second cannot open a second
        // connection to a different server.
        provider.GetRequiredService<NacosClient>()
            .ShouldBeSameAs(provider.GetRequiredService<NacosClient>());
    }

    [Fact]
    public void AddNacos_BindsOptionsFromASection()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nacos:ServerAddresses:0"] = Server,
                ["Nacos:Namespace"] = "tenant-a",
            })
            .Build();

        using ServiceProvider provider = Build(section: configuration.GetSection("Nacos"));

        NacosOptions options = provider.GetRequiredService<IOptions<NacosOptions>>().Value;

        options.ServerAddresses.ShouldContain(Server);
        options.Namespace.ShouldBe("tenant-a");
    }

    [Fact]
    public void HttpClientName_HasTheDocumentedValue()
    {
        NacosServiceCollectionExtensions.HttpClientName.ShouldBe("Lolitacoding.Nacos");
    }

    private static ServiceProvider Build(
        Action<NacosOptions>? configure = null,
        IConfigurationSection? section = null)
    {
        var services = new ServiceCollection();

        if (section is not null)
        {
            services.AddNacos(section);
        }
        else
        {
            services.AddNacos(configure ?? (_ => { }));
        }

        return services.BuildServiceProvider();
    }
}
