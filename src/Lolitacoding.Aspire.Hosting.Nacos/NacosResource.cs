using Aspire.Hosting.ApplicationModel;

namespace Lolitacoding.Aspire.Hosting.Nacos;

/// <summary>
/// A Nacos server that runs in a container and can be referenced by other resources.
/// </summary>
/// <remarks>
/// <para>
/// The connection string handed to a referencing resource is the server's HTTP base address,
/// for example <c>http://localhost:8848</c>. The Nacos client appends its default context path
/// (<c>/nacos</c>) and derives the gRPC port by adding <c>1000</c> to the main port, which is why
/// <see cref="NacosBuilderExtensions.AddNacos"/> keeps the three published ports a fixed distance
/// apart.
/// </para>
/// </remarks>
public sealed class NacosResource : ContainerResource, IResourceWithConnectionString
{
    internal const string HttpEndpointName = "http";
    internal const string GrpcEndpointName = "grpc";
    internal const string GrpcServerEndpointName = "grpc-server";

    private EndpointReference? _httpEndpoint;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    public NacosResource(string name)
        : base(name, entrypoint: null)
    {
    }

    /// <summary>
    /// Gets the HTTP endpoint. It also serves the Nacos console under <c>/nacos</c>.
    /// </summary>
    public EndpointReference HttpEndpoint => _httpEndpoint ??= new EndpointReference(this, HttpEndpointName);

    /// <summary>
    /// Gets the gRPC endpoint used by Nacos clients, published at the main port plus <c>1000</c>.
    /// </summary>
    public EndpointReference GrpcEndpoint => new(this, GrpcEndpointName);

    /// <summary>Gets the host of the HTTP endpoint.</summary>
    public EndpointReferenceExpression Host => HttpEndpoint.Property(EndpointProperty.Host);

    /// <summary>Gets the port of the HTTP endpoint.</summary>
    public EndpointReferenceExpression Port => HttpEndpoint.Property(EndpointProperty.Port);

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
}
