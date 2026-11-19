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
    public void AddNacosClient_derives_the_grpc_port_offset_from_the_published_ports()
    {
        // Aspire publishes the HTTP and gRPC container ports on independent host ports, so the
        // client has to use the difference instead of assuming the default 1000.
        IHostApplicationBuilder builder = CreateBuilder(
            ("ConnectionStrings:nacos", "http://localhost:51234"),
            ("NACOS_PORT", "51234"),
            ("NACOS_GRPCPORT", "52345"));

        builder.AddNacosClient();

        NacosOptions options = ResolveOptions(builder);
        options.ServerAddresses.ShouldBe(new[] { "http://localhost:51234" });
        options.GrpcPortOffset.ShouldBe(1111);
    }

    [Fact]
    public void AddNacosClient_encodes_the_connection_name_for_the_property_prefix()
    {
        // Aspire encodes a resource name the same way it encodes environment variable names, so a
        // name containing a hyphen becomes an underscore in the injected prefix.
        IHostApplicationBuilder builder = CreateBuilder(
            ("ConnectionStrings:my-nacos", "http://localhost:51234"),
            ("MY_NACOS_PORT", "51234"),
            ("MY_NACOS_GRPCPORT", "52000"));

        builder.AddNacosClient("my-nacos");

        ResolveOptions(builder).GrpcPortOffset.ShouldBe(766);
    }

    [Fact]
    public void AddNacosClient_keeps_the_default_offset_without_the_published_ports()
    {
        // A plain connection string, or an older hosting resource that does not publish the gRPC
        // port, must keep the Nacos default rather than guessing an offset.
        IHostApplicationBuilder builder = CreateBuilder(
            ("ConnectionStrings:nacos", "http://localhost:8848"),
            ("NACOS_PORT", "8848"));

        builder.AddNacosClient();

        ResolveOptions(builder).GrpcPortOffset.ShouldBe(NacosConstants.GrpcPortOffset);
    }

    [Fact]
    public void AddNacosClient_lets_the_caller_override_the_derived_offset()
    {
        IHostApplicationBuilder builder = CreateBuilder(
            ("ConnectionStrings:nacos", "http://localhost:51234"),
            ("NACOS_PORT", "51234"),
            ("NACOS_GRPCPORT", "52345"));

        builder.AddNacosClient(configure: options => options.GrpcPortOffset = 1000);

        ResolveOptions(builder).GrpcPortOffset.ShouldBe(1000);
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
