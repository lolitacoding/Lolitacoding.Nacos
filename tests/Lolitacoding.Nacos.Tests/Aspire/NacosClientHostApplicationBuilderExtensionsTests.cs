using Lolitacoding.Aspire.Nacos;
using Lolitacoding.Nacos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Lolitacoding.Nacos.Tests.Aspire;

/// <summary>
/// Verifies that the Aspire client helper turns an injected connection string into configured
/// <see cref="NacosOptions"/>.
/// </summary>
public sealed class NacosClientHostApplicationBuilderExtensionsTests
{
    [Fact]
    public void AddNacosClient_reads_the_connection_string_named_after_the_default()
    {
        IHostApplicationBuilder builder = CreateBuilder(("ConnectionStrings:nacos", "http://localhost:8848"));

        builder.AddNacosClient();

        NacosOptions options = ResolveOptions(builder);
        options.ServerAddresses.ShouldBe(new[] { "http://localhost:8848" });
    }

    [Fact]
    public void AddNacosClient_reads_a_named_connection_string()
    {
        IHostApplicationBuilder builder = CreateBuilder(("ConnectionStrings:registry", "http://nacos:8848"));

        builder.AddNacosClient("registry");

        NacosOptions options = ResolveOptions(builder);
        options.ServerAddresses.ShouldBe(new[] { "http://nacos:8848" });
    }

    [Fact]
    public void AddNacosClient_applies_additional_configuration_after_the_connection_string()
    {
        IHostApplicationBuilder builder = CreateBuilder(("ConnectionStrings:nacos", "http://localhost:8848"));

        builder.AddNacosClient(configure: options =>
        {
            options.Namespace = "orders";
            options.ServerAddresses = ["http://override:8848"];
        });

        NacosOptions options = ResolveOptions(builder);
        options.Namespace.ShouldBe("orders");
        options.ServerAddresses.ShouldBe(new[] { "http://override:8848" });
    }

    [Fact]
    public void AddNacosClient_throws_when_the_connection_string_is_missing()
    {
        IHostApplicationBuilder builder = CreateBuilder();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => builder.AddNacosClient());

        exception.Message.ShouldContain("nacos");
    }

    [Fact]
    public void AddNacosClient_rejects_a_null_builder()
    {
        IHostApplicationBuilder builder = null!;

        Should.Throw<ArgumentNullException>(() => builder.AddNacosClient());
    }

    private static IHostApplicationBuilder CreateBuilder(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(
            settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)));

        IHostApplicationBuilder builder = Substitute.For<IHostApplicationBuilder>();
        builder.Configuration.Returns(configuration);
        builder.Services.Returns(new ServiceCollection());

        return builder;
    }

    private static NacosOptions ResolveOptions(IHostApplicationBuilder builder)
    {
        using ServiceProvider provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<NacosOptions>>().Value;
    }
}
