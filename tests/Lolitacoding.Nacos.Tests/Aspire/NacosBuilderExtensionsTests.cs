using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Lolitacoding.Aspire.Hosting.Nacos;

namespace Lolitacoding.Nacos.Tests.Aspire;

/// <summary>
/// Verifies the shape of the application model that <see cref="NacosBuilderExtensions.AddNacos"/>
/// produces, without starting a container.
/// </summary>
/// <remarks>
/// Host ports are left to Aspire by default, so these tests pin the container-side ports and the
/// connection metadata a client uses to find the published gRPC port. A port argument or a
/// <c>With*Port</c> helper pins exactly one endpoint and leaves the rest to Aspire.
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
    public void AddNacos_leaves_the_host_ports_to_aspire()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder().AddNacos("nacos");

        Dictionary<string, EndpointAnnotation> endpoints = nacos.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .ToDictionary(endpoint => endpoint.Name!);

        endpoints.Keys.ShouldBe(
            new[] { "http", "grpc", "grpc-server", "console" }, ignoreOrder: true);

        // The container ports are fixed; the host ports are assigned when the app starts.
        endpoints["http"].TargetPort.ShouldBe(8848);
        endpoints["http"].Port.ShouldBeNull();
        endpoints["http"].UriScheme.ShouldBe("http");

        endpoints["grpc"].TargetPort.ShouldBe(9848);
        endpoints["grpc"].Port.ShouldBeNull();

        endpoints["grpc-server"].TargetPort.ShouldBe(9849);
        endpoints["grpc-server"].Port.ShouldBeNull();
    }

    [Fact]
    public void AddNacos_publishes_the_console_on_its_own_port()
    {
        // In Nacos 3.x the console is a separate application on 8080; the main port answers 404 at
        // its root, so a dashboard link to it does not open the UI.
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder().AddNacos("nacos");

        EndpointAnnotation console = nacos.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Single(endpoint => endpoint.Name == "console");

        console.TargetPort.ShouldBe(8080);
        console.Port.ShouldBeNull();
        console.UriScheme.ShouldBe("http");

        nacos.Resource.ConsoleEndpoint.EndpointName.ShouldBe("console");
    }

    [Fact]
    public void AddNacos_pins_only_the_port_it_was_given()
    {
        // A port argument names one endpoint. Deriving the gRPC ports from the main one would take
        // those host ports away from Aspire, which is what leaving them unset exists to avoid.
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder()
            .AddNacos("nacos", httpPort: 18848);

        Dictionary<string, EndpointAnnotation> endpoints = nacos.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .ToDictionary(endpoint => endpoint.Name!);

        endpoints["http"].Port.ShouldBe(18848);
        endpoints["grpc"].Port.ShouldBeNull();
        endpoints["grpc-server"].Port.ShouldBeNull();
        endpoints["console"].Port.ShouldBeNull();

        // The container keeps the ports the image listens on.
        endpoints["http"].TargetPort.ShouldBe(8848);
        endpoints["grpc"].TargetPort.ShouldBe(9848);
        endpoints["grpc-server"].TargetPort.ShouldBe(9849);
    }

    [Fact]
    public void Port_helpers_publish_each_endpoint_on_the_given_host_port()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder()
            .AddNacos("nacos")
            .WithHttpPort(18848)
            .WithGrpcPort(19848)
            .WithGrpcServerPort(19849)
            .WithConsolePort(18080);

        Dictionary<string, EndpointAnnotation> endpoints = nacos.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .ToDictionary(endpoint => endpoint.Name!);

        endpoints["http"].Port.ShouldBe(18848);
        endpoints["grpc"].Port.ShouldBe(19848);
        endpoints["grpc-server"].Port.ShouldBe(19849);
        endpoints["console"].Port.ShouldBe(18080);

        // Only the host side moves; the container keeps its own ports.
        endpoints["http"].TargetPort.ShouldBe(8848);
        endpoints["grpc"].TargetPort.ShouldBe(9848);
        endpoints["grpc-server"].TargetPort.ShouldBe(9849);
        endpoints["console"].TargetPort.ShouldBe(8080);
    }

    [Fact]
    public void AddNacos_moves_the_console_when_a_console_port_is_given()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder()
            .AddNacos("nacos", consolePort: 18080);

        EndpointAnnotation console = nacos.Resource.Annotations
            .OfType<EndpointAnnotation>()
            .Single(endpoint => endpoint.Name == "console");

        // Only the host side moves; the container keeps listening on 8080.
        console.Port.ShouldBe(18080);
        console.TargetPort.ShouldBe(8080);
    }

    [Fact]
    public async Task AddNacos_runs_the_server_in_standalone_mode_without_authentication()
    {
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder().AddNacos("nacos");

        Dictionary<string, object> variables = await GetEnvironmentVariablesAsync(nacos.Resource);

        variables["MODE"].ShouldBe("standalone");
        variables.ShouldNotContainKey("NACOS_AUTH_ENABLE");

        // The image requires a signing key and a server identity from 3.2 on, whether or not the
        // authentication plugin is enabled, and exits without them. The key the server signs with is
        // decoded from base64 and has to be at least 32 bytes long; the identity is what it presents
        // to its peers.
        variables["NACOS_AUTH_TOKEN"].ShouldBeOfType<string>()
            .Length.ShouldBeGreaterThanOrEqualTo(32);

        variables["NACOS_AUTH_IDENTITY_KEY"].ShouldBe("serverIdentity");
        variables["NACOS_AUTH_IDENTITY_VALUE"].ShouldBe("security");
    }

    [Fact]
    public void ReadinessPath_is_the_v3_admin_core_path()
    {
        // The earlier v1 console health path is one a Nacos 3.2 server no longer serves, so the
        // built-in health check has to ask the v3 admin core API, and the client's container harness
        // waits on the same path.
        NacosBuilderExtensions.ReadinessPath.ShouldBe("/nacos/v3/admin/core/state/readiness");
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
    public void Connection_properties_expose_the_published_ports_and_urls()
    {
        // A client cannot assume the gRPC port is the main port plus 1000 when Aspire publishes
        // each container port on its own host port, so the published port is part of the
        // connection metadata.
        IResourceBuilder<NacosResource> nacos = DistributedApplication.CreateBuilder().AddNacos("nacos");

        Dictionary<string, ReferenceExpression> properties = nacos.Resource
            .GetConnectionProperties()
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        properties.Keys.ShouldBe(
            new[] { "Host", "Port", "Uri", "GrpcHost", "GrpcPort", "ConsoleUri" },
            ignoreOrder: true);

        EndpointReferenceExpression grpcPort = properties["GrpcPort"].ValueProviders
            .OfType<EndpointReferenceExpression>()
            .ShouldHaveSingleItem();

        grpcPort.Endpoint.EndpointName.ShouldBe("grpc");
        grpcPort.Property.ShouldBe(EndpointProperty.Port);

        EndpointReferenceExpression grpcHost = properties["GrpcHost"].ValueProviders
            .OfType<EndpointReferenceExpression>()
            .ShouldHaveSingleItem();

        grpcHost.Endpoint.EndpointName.ShouldBe("grpc");
        grpcHost.Property.ShouldBe(EndpointProperty.Host);

        EndpointReferenceExpression uri = properties["Uri"].ValueProviders
            .OfType<EndpointReferenceExpression>()
            .ShouldHaveSingleItem();

        uri.Endpoint.EndpointName.ShouldBe("http");
        uri.Property.ShouldBe(EndpointProperty.Url);
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
