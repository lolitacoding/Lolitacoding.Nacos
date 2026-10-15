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
    public const string ReadinessPath = "/nacos/v1/console/health/readiness";

    /// <summary>
    /// Adds a Nacos server container to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">
    /// The name of the resource. This name is also the connection string name a referencing
    /// resource uses.
    /// </param>
    /// <param name="httpPort">
    /// The host port for the HTTP endpoint. Defaults to <c>8848</c>. It is also the base from which
    /// the gRPC ports are derived, because a Nacos client computes the gRPC port by adding
    /// <c>1000</c> to the address it was given.
    /// </param>
    /// <param name="grpcPort">
    /// The host port for the client gRPC endpoint. Defaults to <paramref name="httpPort"/> plus
    /// <c>1000</c>; changing it independently of <paramref name="httpPort"/> will break clients
    /// that discover the gRPC port from the HTTP address.
    /// </param>
    /// <param name="grpcServerPort">
    /// The host port for the server gRPC endpoint. Defaults to <paramref name="httpPort"/> plus
    /// <c>1001</c>.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <remarks>
    /// <para>
    /// The server runs in standalone mode with authentication disabled, which is the convenient
    /// default for local development. Call <see cref="WithAuthentication"/> to enable the
    /// authentication plugin.
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
        int? grpcServerPort = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        int mainPort = httpPort ?? NacosPorts.Http;
        int clientGrpcPort = grpcPort ?? mainPort + NacosPorts.ClientGrpcOffset;
        int serverGrpcPort = grpcServerPort ?? mainPort + NacosPorts.ServerGrpcOffset;

        var resource = new NacosResource(name);

        return builder.AddResource(resource)
            .WithImage(NacosContainerImageTags.Image, NacosContainerImageTags.Tag)
            .WithImageRegistry(NacosContainerImageTags.Registry)
            .WithHttpEndpoint(
                port: mainPort,
                targetPort: NacosPorts.Http,
                name: NacosResource.HttpEndpointName)
            .WithEndpoint(
                port: clientGrpcPort,
                targetPort: NacosPorts.Http + NacosPorts.ClientGrpcOffset,
                name: NacosResource.GrpcEndpointName,
                scheme: "tcp")
            .WithEndpoint(
                port: serverGrpcPort,
                targetPort: NacosPorts.Http + NacosPorts.ServerGrpcOffset,
                name: NacosResource.GrpcServerEndpointName,
                scheme: "tcp")
            .WithEnvironment(NacosEnvironmentVariables.Mode, "standalone")
            .WithHttpHealthCheck(ReadinessPath, endpointName: NacosResource.HttpEndpointName);
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
    /// The server's built-in <c>nacos</c> user keeps its default password (<c>nacos</c>) unless it
    /// is changed, so a development client can authenticate with the default credentials.
    /// </remarks>
    public static IResourceBuilder<NacosResource> WithAuthentication(
        this IResourceBuilder<NacosResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEnvironment(NacosEnvironmentVariables.AuthEnable, "true");
    }
}
