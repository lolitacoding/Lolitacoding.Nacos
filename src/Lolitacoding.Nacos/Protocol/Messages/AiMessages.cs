using Lolitacoding.Nacos.Ai;

namespace Lolitacoding.Nacos.Protocol.Messages;

/// <summary>
/// Base type for requests addressed to the Nacos AI registry module.
/// </summary>
/// <remarks>
/// The AI registry is identified by the module discriminator rather than by a service name, so it
/// needs a base type of its own. The value matches <c>Constants.AI.AI_MODULE</c> on the server.
/// </remarks>
internal abstract class AiRequest : NacosRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AiRequest"/> class.
    /// </summary>
    protected AiRequest() => Module = NacosModule.Ai;
}

/// <summary>
/// Base type for the requests that address one MCP server by name.
/// </summary>
/// <remarks>
/// The server resolves the namespace from the request rather than from the name, so the identifier
/// and the namespace travel beside the name even though every client operation is written in terms
/// of the name alone.
/// </remarks>
internal abstract class McpRequest : AiRequest
{
    /// <summary>Gets or sets the namespace the MCP server belongs to.</summary>
    public string? NamespaceId { get; set; }

    /// <summary>Gets or sets the internal MCP identifier.</summary>
    /// <remarks>
    /// Unset for every client operation: the server derives it from the namespace and the name.
    /// </remarks>
    public string? McpId { get; set; }

    /// <summary>Gets or sets the MCP server name.</summary>
    public string? McpName { get; set; }
}

/// <summary>
/// A request for one MCP server version.
/// </summary>
internal sealed class QueryMcpServerRequest : McpRequest
{
    /// <summary>Gets or sets the version to read.</summary>
    /// <remarks>
    /// Unset asks for the latest published version, which is the version label <c>latest</c> is
    /// moved to on every release.
    /// </remarks>
    public string? Version { get; set; }
}

/// <summary>
/// A request to create an MCP server or a new version of one.
/// </summary>
/// <remarks>
/// Releasing is idempotent in the useful direction: a release only creates what does not exist. A
/// server that does not exist yet is created with the specification; an existing server with a new
/// version creates that version; an existing version is refused as a conflict rather than
/// overwritten, so a release can never silently replace what clients are already calling.
/// </remarks>
internal sealed class ReleaseMcpServerRequest : McpRequest
{
    /// <summary>Gets or sets the server specification.</summary>
    public NacosMcpServer? ServerSpecification { get; set; }

    /// <summary>Gets or sets the tool specification.</summary>
    public NacosMcpToolSpecification? ToolSpecification { get; set; }

    /// <summary>Gets or sets the resource specification.</summary>
    public NacosMcpResourceSpecification? ResourceSpecification { get; set; }

    /// <summary>Gets or sets how the backend is reached.</summary>
    /// <remarks>
    /// Unset asks Nacos to register a service reference for the server, which is the behaviour an
    /// MCP server that is itself registered in Nacos wants.
    /// </remarks>
    public NacosMcpEndpointSpec? EndpointSpecification { get; set; }

    /// <summary>Gets or sets a value indicating whether to create a lifecycle draft.</summary>
    /// <remarks>
    /// A draft is stored but not served, so it can be reviewed before clients see it. Nacos only
    /// accepts a draft when it advertises the MCP draft ability.
    /// </remarks>
    public bool CreateDraft { get; set; }
}

/// <summary>
/// A request to register or remove one runtime endpoint of an MCP server.
/// </summary>
/// <remarks>
/// The endpoint is not stored with the server specification: Nacos registers it as an instance of
/// the service the server's <c>REF</c> endpoint names, so ordinary service discovery, health
/// checking and load balancing apply to it. That also means the endpoint lives and dies with the
/// connection that registered it, which is why a client replays the registrations it still owns
/// after a reconnect.
/// </remarks>
internal sealed class McpServerEndpointRequest : McpRequest
{
    /// <summary>Gets or sets the endpoint address.</summary>
    public string? Address { get; set; }

    /// <summary>Gets or sets the endpoint port.</summary>
    public int Port { get; set; }

    /// <summary>
    /// Gets or sets the server version the endpoint serves.
    /// </summary>
    /// <remarks>
    /// Unset publishes the endpoint for every version of the server; Nacos records the version a
    /// named one carries as instance metadata when it registers the endpoint.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the operation.
    /// </summary>
    /// <remarks>
    /// One of <see cref="NacosAiRemote.RegisterEndpoint"/>,
    /// <see cref="NacosAiRemote.BatchRegisterEndpoint"/> or
    /// <see cref="NacosAiRemote.DeregisterEndpoint"/>. The server rejects anything else, so it is a
    /// required field rather than an optional one.
    /// </remarks>
    public string? Type { get; set; }
}

/// <summary>
/// The reply to an endpoint request.
/// </summary>
internal sealed class McpServerEndpointResponse : NacosResponse
{
    /// <summary>Gets or sets the operation the server performed.</summary>
    /// <remarks>Echoed from the request; the client reads it only to confirm what it asked for.</remarks>
    public string? Type { get; set; }
}

/// <summary>
/// The reply to a request for one MCP server version.
/// </summary>
internal sealed class QueryMcpServerResponse : NacosResponse
{
    /// <summary>Gets or sets the resolved server detail.</summary>
    /// <remarks>
    /// Unset when the request failed, which the caller sees as the failure result code rather than
    /// as a missing field.
    /// </remarks>
    public NacosMcpServerDetail? McpServerDetailInfo { get; set; }
}

/// <summary>
/// The reply to a release request.
/// </summary>
internal sealed class ReleaseMcpServerResponse : NacosResponse
{
    /// <summary>Gets or sets the internal identifier of the released server.</summary>
    public string? McpId { get; set; }
}
