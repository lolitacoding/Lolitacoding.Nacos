using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Lolitacoding.Aspire.Hosting.Nacos;

/// <summary>
/// Extension methods for adding a Nacos server to an Aspire application.
/// </summary>
public static class NacosBuilderExtensions
{
    /// <summary>
    /// The relative path of the Nacos readiness endpoint, used for the built-in health check.
    /// </summary>
    /// <remarks>
    /// The v3 admin core API. A Nacos 3.2 server has dropped the earlier v1 console health path, so
    /// this is the one that works across the supported servers, and the client's own container
    /// harness waits on the same path.
    /// </remarks>
    public const string ReadinessPath = "/nacos/v3/admin/core/state/readiness";

    /// <summary>
    /// The signing key the container is given for local development.
    /// </summary>
    /// <remarks>
    /// The image requires a signing key and a server identity from 3.2 on, whether or not the
    /// authentication plugin is enabled, and exits without them. These are always set — to the same
    /// development values the repository's compose file and CI use — so that the container starts
    /// out of the box, and a deployment overrides them with <c>WithEnvironment</c>.
    /// </remarks>
    private const string DevelopmentAuthToken =
        "SecretKey012345678901234567890123456789012345678901234567890123456789";

    /// <summary>The identity key that goes with <see cref="DevelopmentAuthToken"/>.</summary>
    private const string DevelopmentAuthIdentityKey = "serverIdentity";

    /// <summary>The identity value that goes with <see cref="DevelopmentAuthToken"/>.</summary>
    private const string DevelopmentAuthIdentityValue = "security";

    /// <summary>
    /// Adds a Nacos server container to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">
    /// The name of the resource. This name is also the connection string name a referencing
    /// resource uses.
    /// </param>
    /// <param name="httpPort">
    /// The host port for the HTTP endpoint. When <see langword="null"/> (the default) Aspire
    /// assigns a free port. Naming a port here pins only this endpoint; the gRPC ports keep
    /// whatever Aspire assigns unless they are given explicitly.
    /// </param>
    /// <param name="grpcPort">
    /// The host port for the client gRPC endpoint. When <see langword="null"/> (the default) Aspire
    /// assigns a free port. The resource publishes the actual port, so it does not have to be a
    /// fixed distance from <paramref name="httpPort"/>.
    /// </param>
    /// <param name="grpcServerPort">
    /// The host port for the server gRPC endpoint. When <see langword="null"/> (the default) Aspire
    /// assigns a free port.
    /// </param>
    /// <param name="consolePort">
    /// The host port for the web console. When <see langword="null"/> (the default) Aspire assigns
    /// a free port. The console always listens on <c>8080</c> inside the container.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <remarks>
    /// <para>
    /// The server runs in standalone mode with authentication disabled, which is the convenient
    /// default for local development. Call <see cref="WithAuthentication"/> to enable the
    /// authentication plugin. The container image requires a signing key and a server identity even
    /// with the plugin off, so development values are always set; override them with
    /// <c>WithEnvironment</c> for anything but local development.
    /// </para>
    /// <para>
    /// Nacos 3.x serves the web console from a separate application on port 8080, not under the main
    /// port's context path as 2.x did. The console is published as its own endpoint so the
    /// dashboard's link opens the UI rather than the API root, which answers 404.
    /// </para>
    /// <para>
    /// No host port is fixed: Aspire publishes each endpoint on a free port and the dashboard shows
    /// the address that was assigned. A Nacos client normally derives the gRPC port from the main
    /// port, so the resource publishes the gRPC port as a connection property and
    /// <c>Lolitacoding.Aspire.Nacos.AddNacosClient</c> uses the difference between the two. A client
    /// that only has the connection string must either be told the offset or connect through a
    /// fixed mapping set with <see cref="WithHttpPort"/> and <see cref="WithGrpcPort"/>.
    /// </para>
    /// <para>
    /// The container includes a health check against the readiness endpoint, so a resource added
    /// with <c>WaitFor</c> starts only once Nacos can serve requests.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<NacosResource> AddNacos(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? httpPort = null,
        int? grpcPort = null,
        int? grpcServerPort = null,
        int? consolePort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Every port argument pins exactly the endpoint it names. Nothing is derived: Aspire
        // assigns a free host port to each endpoint that was left alone, and the resource publishes
        // the ones it got so a client never has to guess an offset.
        var resource = new NacosResource(name);

        return builder.AddResource(resource)
            .WithImage(NacosContainerImageTags.Image, NacosContainerImageTags.Tag)
            .WithImageRegistry(NacosContainerImageTags.Registry)
            .WithHttpEndpoint(
                port: httpPort,
                targetPort: NacosPorts.Http,
                name: NacosResource.HttpEndpointName)
            .WithEndpoint(
                port: grpcPort,
                targetPort: NacosPorts.Http + NacosPorts.ClientGrpcOffset,
                name: NacosResource.GrpcEndpointName,
                scheme: "tcp")
            .WithEndpoint(
                port: grpcServerPort,
                targetPort: NacosPorts.Http + NacosPorts.ServerGrpcOffset,
                name: NacosResource.GrpcServerEndpointName,
                scheme: "tcp")
            .WithHttpEndpoint(
                port: consolePort,
                targetPort: NacosPorts.Console,
                name: NacosResource.ConsoleEndpointName)
            .WithEnvironment(NacosEnvironmentVariables.Mode, "standalone")
            .WithEnvironment(NacosEnvironmentVariables.AuthToken, DevelopmentAuthToken)
            .WithEnvironment(NacosEnvironmentVariables.AuthIdentityKey, DevelopmentAuthIdentityKey)
            .WithEnvironment(NacosEnvironmentVariables.AuthIdentityValue, DevelopmentAuthIdentityValue)
            .WithHttpHealthCheck(ReadinessPath, endpointName: NacosResource.HttpEndpointName);
    }

    /// <summary>
    /// Publishes the Nacos server API on a specific host port.
    /// </summary>
    /// <param name="builder">The Nacos resource builder.</param>
    /// <param name="port">The host port to publish the HTTP endpoint on.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <remarks>
    /// This changes only the HTTP port. The gRPC port keeps whatever Aspire assigned, so a client
    /// that derives the gRPC port from the main port must be given the difference through
    /// <c>NacosOptions.GrpcPortOffset</c>, or the two ports must be published explicitly with
    /// <see cref="WithGrpcPort"/>.
    /// </remarks>
    public static IResourceBuilder<NacosResource> WithHttpPort(
        this IResourceBuilder<NacosResource> builder,
        int port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEndpoint(
            NacosResource.HttpEndpointName,
            endpoint => endpoint.Port = port);
    }

    /// <summary>
    /// Publishes the Nacos client gRPC endpoint on a specific host port.
    /// </summary>
    /// <param name="builder">The Nacos resource builder.</param>
    /// <param name="port">The host port to publish the client gRPC endpoint on.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <remarks>
    /// A client that only knows the HTTP address derives the gRPC port from it, so a port chosen
    /// here has to match that derivation unless the client is configured with
    /// <c>NacosOptions.GrpcPortOffset</c>.
    /// </remarks>
    public static IResourceBuilder<NacosResource> WithGrpcPort(
        this IResourceBuilder<NacosResource> builder,
        int port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEndpoint(
            NacosResource.GrpcEndpointName,
            endpoint => endpoint.Port = port);
    }

    /// <summary>
    /// Publishes the Nacos server-to-server gRPC endpoint on a specific host port.
    /// </summary>
    /// <param name="builder">The Nacos resource builder.</param>
    /// <param name="port">The host port to publish the server gRPC endpoint on.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static IResourceBuilder<NacosResource> WithGrpcServerPort(
        this IResourceBuilder<NacosResource> builder,
        int port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEndpoint(
            NacosResource.GrpcServerEndpointName,
            endpoint => endpoint.Port = port);
    }

    /// <summary>
    /// Publishes the Nacos web console on a specific host port.
    /// </summary>
    /// <param name="builder">The Nacos resource builder.</param>
    /// <param name="port">The host port to publish the console on.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static IResourceBuilder<NacosResource> WithConsolePort(
        this IResourceBuilder<NacosResource> builder,
        int port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEndpoint(
            NacosResource.ConsoleEndpointName,
            endpoint => endpoint.Port = port);
    }

    /// <summary>
    /// Adds a named volume for the Nacos data directory.
    /// </summary>
    /// <param name="builder">The Nacos resource builder.</param>
    /// <param name="name">
    /// The volume name. Defaults to an auto-generated name based on the application and resource
    /// names.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    public static IResourceBuilder<NacosResource> WithDataVolume(
        this IResourceBuilder<NacosResource> builder,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/home/nacos/data");
    }

    /// <summary>
    /// Adds a bind mount for the Nacos data directory.
    /// </summary>
    /// <param name="builder">The Nacos resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is null or empty.</exception>
    public static IResourceBuilder<NacosResource> WithDataBindMount(
        this IResourceBuilder<NacosResource> builder,
        string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);
        return builder.WithBindMount(source, "/home/nacos/data");
    }

    /// <summary>
    /// Enables the Nacos authentication plugin.
    /// </summary>
    /// <param name="builder">The Nacos resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <remarks>
    /// A Nacos 3.x server ships no accounts, so a client cannot authenticate until the first
    /// administrator exists: it is created once, either through the console on its first run or by
    /// posting to <c>/nacos/v3/auth/user/admin</c> on the server's main port. Enabling authentication
    /// without creating that account leaves every login rejected.
    /// </remarks>
    public static IResourceBuilder<NacosResource> WithAuthentication(
        this IResourceBuilder<NacosResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEnvironment(NacosEnvironmentVariables.AuthEnable, "true");
    }
}
