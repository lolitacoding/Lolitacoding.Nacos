using Aspire.Hosting.ApplicationModel;

namespace Lolitacoding.Aspire.Hosting.Nacos;

/// <summary>
/// A Nacos server that runs in a container and can be referenced by other resources.
/// </summary>
/// <remarks>
/// <para>
/// The connection string handed to a referencing resource is the server's HTTP base address,
/// for example <c>http://localhost:51234</c>. The Nacos client appends its default context path
/// (<c>/nacos</c>) and normally derives the gRPC port by adding <c>1000</c> to the main port.
/// </para>
/// <para>
/// Aspire publishes each container port on its own host port, so the two are not a fixed distance
/// apart. The resource therefore exposes the published gRPC port as a connection property
/// (<see cref="GrpcPort"/>, injected as <c>NACOS_GRPCPORT</c>), and
/// <c>Lolitacoding.Aspire.Nacos.AddNacosClient</c> uses the difference between the published
/// ports as <c>NacosOptions.GrpcPortOffset</c>. A client that reads only the connection
/// string can still connect when the AppHost publishes the ports at the default offset, or when
/// <c>GrpcPortOffset</c> is configured explicitly.
/// </para>
/// <para>
/// The web console is a separate application in Nacos 3.x and is published on its own endpoint,
/// <see cref="ConsoleEndpoint"/>, so the dashboard link opens the UI instead of the API root.
/// </para>
/// </remarks>
public sealed class NacosResource : ContainerResource, IResourceWithConnectionString
{
    internal const string HttpEndpointName = "http";
    internal const string GrpcEndpointName = "grpc";
    internal const string GrpcServerEndpointName = "grpc-server";
    internal const string ConsoleEndpointName = "console";

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    public NacosResource(string name)
        : base(name, entrypoint: null)
    {
    }

    /// <summary>
    /// Gets the HTTP endpoint of the server API.
    /// </summary>
    /// <remarks>
    /// This is the address a Nacos client connects to. It does not serve the web console in Nacos
    /// 3.x; use <see cref="ConsoleEndpoint"/> for that.
    /// </remarks>
    public EndpointReference HttpEndpoint => field ??= new EndpointReference(this, HttpEndpointName);

    /// <summary>
    /// Gets the gRPC endpoint used by Nacos clients.
    /// </summary>
    /// <remarks>
    /// Nacos serves the client gRPC transport on its own port. Read the published port through
    /// <see cref="GrpcPort"/> rather than assuming it is the main port plus a fixed offset.
    /// </remarks>
    public EndpointReference GrpcEndpoint => field ??= new EndpointReference(this, GrpcEndpointName);

    /// <summary>
    /// Gets the gRPC endpoint Nacos servers use to talk to each other.
    /// </summary>
    public EndpointReference GrpcServerEndpoint =>
        field ??= new EndpointReference(this, GrpcServerEndpointName);

    /// <summary>
    /// Gets the HTTP endpoint of the web console.
    /// </summary>
    public EndpointReference ConsoleEndpoint =>
        field ??= new EndpointReference(this, ConsoleEndpointName);

    /// <summary>Gets the host of the HTTP endpoint.</summary>
    public EndpointReferenceExpression Host => HttpEndpoint.Property(EndpointProperty.Host);

    /// <summary>Gets the port of the HTTP endpoint.</summary>
    public EndpointReferenceExpression Port => HttpEndpoint.Property(EndpointProperty.Port);

    /// <summary>Gets the host of the client gRPC endpoint.</summary>
    public EndpointReferenceExpression GrpcHost => GrpcEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// Gets the published port of the client gRPC endpoint.
    /// </summary>
    /// <remarks>
    /// This is the port a client must dial, which is not necessarily the main port plus the
    /// server's default offset: Aspire assigns each published port independently.
    /// </remarks>
    public EndpointReferenceExpression GrpcPort => GrpcEndpoint.Property(EndpointProperty.Port);

    /// <summary>Gets the URL of the web console.</summary>
    public EndpointReferenceExpression ConsoleUri =>
        ConsoleEndpoint.Property(EndpointProperty.Url);

    /// <summary>
    /// Gets the connection string for the server, in the form <c>http://host:port</c>.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"{HttpEndpoint.Property(EndpointProperty.Url)}");

    /// <summary>
    /// Gets the connection string for the server.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>The connection string in the form <c>http://host:port</c>.</returns>
    public ValueTask<string?> GetConnectionStringAsync(CancellationToken cancellationToken = default) =>
        ConnectionStringExpression.GetValueAsync(cancellationToken);

    /// <summary>
    /// Gets the connection metadata that Aspire injects alongside the connection string.
    /// </summary>
    /// <returns>
    /// The published host, ports and URLs. With the default <c>WithReference</c> flags they are
    /// injected as <c>NACOS_HOST</c>, <c>NACOS_PORT</c>, <c>NACOS_URI</c>, <c>NACOS_GRPCHOST</c>,
    /// <c>NACOS_GRPCPORT</c> and <c>NACOS_CONSOLEURI</c>.
    /// </returns>
    /// <remarks>
    /// The gRPC port is exposed separately because a Nacos client normally derives it from the
    /// main port. When the two are published independently, the difference between
    /// <c>NACOS_GRPCPORT</c> and <c>NACOS_PORT</c> is the offset the client must use.
    /// </remarks>
    public IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties()
    {
        yield return new KeyValuePair<string, ReferenceExpression>(
            "Host", ReferenceExpression.Create($"{Host}"));
        yield return new KeyValuePair<string, ReferenceExpression>(
            "Port", ReferenceExpression.Create($"{Port}"));
        yield return new KeyValuePair<string, ReferenceExpression>(
            "Uri", ConnectionStringExpression);
        yield return new KeyValuePair<string, ReferenceExpression>(
            "GrpcHost", ReferenceExpression.Create($"{GrpcHost}"));
        yield return new KeyValuePair<string, ReferenceExpression>(
            "GrpcPort", ReferenceExpression.Create($"{GrpcPort}"));
        yield return new KeyValuePair<string, ReferenceExpression>(
            "ConsoleUri", ReferenceExpression.Create($"{ConsoleUri}"));
    }
}
