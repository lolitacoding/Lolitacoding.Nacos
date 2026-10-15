using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Lolitacoding.Aspire.Hosting.Nacos;

namespace Lolitacoding.Nacos.Tests.Aspire;

/// <summary>
/// Verifies the shape of the application model that <see cref="NacosBuilderExtensions.AddNacos"/>
/// produces, without starting a container.
/// </summary>
/// <remarks>
/// The published host ports matter for Nacos specifically: a client derives the gRPC port from the
/// main port, so these tests pin the fixed offsets between the endpoints.
/// </remarks>
public sealed class NacosBuilderExtensionsTests
{
    [Fact]
    public void AddNacos_registers_a_container_resource_with_the_default_image()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder().AddNacos("nacos");

        nacos.Resource.Name.ShouldBe("nacos");

        ContainerImageAnnotation image = nacos.Resource.Annotations.OfType<ContainerImageAnnotation>().ShouldHaveSingleItem();
        image.Registry.ShouldBe("docker.io");
        image.Image.ShouldBe("nacos/nacos-server");
        image.Tag.ShouldBe("latest");
    }

    [Fact]
    public void AddNacos_publishes_the_http_and_grpc_endpoints_at_fixed_offsets()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder().AddNacos("nacos");

        Dictionary<string, EndpointAnnotation> endpoints = nacos.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .ToDictionary(endpoint => endpoint.Name!);

        endpoints.Keys.ShouldBe(new[] { "http", "grpc", "grpc-server" }, ignoreOrder: true);

        endpoints["http"].TargetPort.ShouldBe(8848);
        endpoints["http"].Port.ShouldBe(8848);
        endpoints["http"].UriScheme.ShouldBe("http");

        endpoints["grpc"].TargetPort.ShouldBe(9848);
        endpoints["grpc"].Port.ShouldBe(9848);

        endpoints["grpc-server"].TargetPort.ShouldBe(9849);
        endpoints["grpc-server"].Port.ShouldBe(9849);
    }

    [Fact]
    public void AddNacos_shifts_every_port_with_a_custom_http_port()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder().AddNacos("nacos", httpPort: 18848);

        Dictionary<string, EndpointAnnotation> endpoints = nacos.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .ToDictionary(endpoint => endpoint.Name!);

        endpoints["http"].Port.ShouldBe(18848);
        endpoints["grpc"].Port.ShouldBe(19848);
        endpoints["grpc-server"].Port.ShouldBe(19849);
    }

    [Fact]
    public async Task AddNacos_runs_the_server_in_standalone_mode_without_authentication()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder().AddNacos("nacos");

        Dictionary<string, object> variables = await GetEnvironmentVariablesAsync(nacos.Resource);

        variables["MODE"].ShouldBe("standalone");
        variables.ShouldNotContainKey("NACOS_AUTH_ENABLE");
    }

    [Fact]
    public async Task AddNacos_registers_a_readiness_health_check()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder().AddNacos("nacos");

        nacos.Resource.Annotations.OfType<HealthCheckAnnotation>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task WithAuthentication_enables_the_authentication_plugin()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder()
            .AddNacos("nacos")
            .WithAuthentication();

        Dictionary<string, object> variables = await GetEnvironmentVariablesAsync(nacos.Resource);

        variables["NACOS_AUTH_ENABLE"].ShouldBe("true");
    }

    [Fact]
    public void WithDataVolume_mounts_the_nacos_data_directory_as_a_volume()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder()
            .AddNacos("nacos")
            .WithDataVolume("nacos-data");

        ContainerMountAnnotation mount = nacos.Resource.Annotations.OfType<ContainerMountAnnotation>().ShouldHaveSingleItem();
        mount.Type.ShouldBe(ContainerMountType.Volume);
        mount.Source.ShouldBe("nacos-data");
        mount.Target.ShouldBe("/home/nacos/data");
    }

    [Fact]
    public void WithDataVolume_generates_a_volume_name_when_none_is_supplied()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder()
            .AddNacos("nacos")
            .WithDataVolume();

        ContainerMountAnnotation mount = nacos.Resource.Annotations.OfType<ContainerMountAnnotation>().ShouldHaveSingleItem();
        mount.Source.ShouldNotBeNullOrWhiteSpace();
        mount.Target.ShouldBe("/home/nacos/data");
    }

    [Fact]
    public void WithDataBindMount_mounts_the_nacos_data_directory_from_the_host()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder()
            .AddNacos("nacos")
            .WithDataBindMount("/host/nacos-data");

        ContainerMountAnnotation mount = nacos.Resource.Annotations.OfType<ContainerMountAnnotation>().ShouldHaveSingleItem();
        mount.Type.ShouldBe(ContainerMountType.BindMount);
        mount.Source.ShouldBe("/host/nacos-data");
        mount.Target.ShouldBe("/home/nacos/data");
    }

    [Fact]
    public void Connection_string_is_the_http_endpoint_url()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder().AddNacos("nacos");

        EndpointReferenceExpression expression = nacos.Resource.ConnectionStringExpression.ValueProviders
            .OfType<EndpointReferenceExpression>()
            .ShouldHaveSingleItem();

        expression.Endpoint.EndpointName.ShouldBe("http");
        expression.Property.ShouldBe(EndpointProperty.Url);
    }

    [Fact]
    public void AddNacos_rejects_a_null_builder()
    {
        IDistributedApplicationBuilder builder = null!;

        Should.Throw<ArgumentNullException>(() => builder.AddNacos("nacos"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AddNacos_rejects_an_empty_name(string? name)
    {
        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();

        Should.Throw<ArgumentException>(() => builder.AddNacos(name!));
    }

    private static async Task<Dictionary<string, object>> GetEnvironmentVariablesAsync(NacosResource resource)
    {
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        var variables = new Dictionary<string, object>(StringComparer.Ordinal);
        var context = new EnvironmentCallbackContext(executionContext, resource, variables, CancellationToken.None);

        foreach (EnvironmentCallbackAnnotation annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        return variables;
    }
}
