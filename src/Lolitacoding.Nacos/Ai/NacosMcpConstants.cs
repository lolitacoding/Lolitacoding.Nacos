namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// The MCP transports Nacos recognises.
/// </summary>
/// <remarks>
/// <para>
/// The values are what Nacos stores and compares, so they are strings rather than an enum: the set
/// is open, and a Nacos plugin can add a transport this client has never heard of.
/// </para>
/// <para>
/// The names are taken from <c>AiConstants.Mcp</c> in the official API.
/// </para>
/// </remarks>
public static class NacosMcpProtocol
{
    /// <summary>A server that runs as a local process and speaks over its standard streams.</summary>
    public const string Stdio = "stdio";

    /// <summary>A server reached over server-sent events.</summary>
    public const string Sse = "mcp-sse";

    /// <summary>A server reached over streamable HTTP.</summary>
    public const string Streamable = "mcp-streamable";

    /// <summary>A server reached over plain HTTP.</summary>
    public const string Http = "http";

    /// <summary>A server reached over Dubbo.</summary>
    public const string Dubbo = "dubbo";
}

/// <summary>
/// The lifecycle status of an MCP server.
/// </summary>
/// <remarks>
/// The names are taken from <c>McpServerStatusEnum</c> in the official API.
/// </remarks>
public static class NacosMcpStatus
{
    /// <summary>The server is in service.</summary>
    public const string Active = "active";

    /// <summary>The server is still served but should not be chosen for new work.</summary>
    public const string Deprecated = "deprecated";

    /// <summary>The server was removed.</summary>
    public const string Deleted = "deleted";
}

/// <summary>
/// How Nacos finds an MCP server's backend.
/// </summary>
/// <remarks>
/// The names are taken from <c>AiConstants.Mcp</c> in the official API. Each type implies the keys
/// its <see cref="NacosMcpEndpointSpec.Data"/> must carry, which are on
/// <see cref="NacosMcpEndpointData"/>.
/// </remarks>
public static class NacosMcpEndpointType
{
    /// <summary>
    /// Nacos registers the backend as a Nacos service and resolves it at call time.
    /// </summary>
    /// <remarks>
    /// This is what a release with no endpoint specification creates: Nacos registers a service
    /// named after the MCP server, so health checking and load balancing apply to the backend.
    /// </remarks>
    public const string Ref = "REF";

    /// <summary>Nacos calls a fixed address and port.</summary>
    public const string Direct = "DIRECT";
}

/// <summary>
/// The keys an <see cref="NacosMcpEndpointSpec"/> carries for each endpoint type.
/// </summary>
public static class NacosMcpEndpointData
{
    /// <summary>The backend address of a <see cref="NacosMcpEndpointType.Direct"/> endpoint.</summary>
    public const string Address = "address";

    /// <summary>The backend port of a <see cref="NacosMcpEndpointType.Direct"/> endpoint.</summary>
    public const string Port = "port";

    /// <summary>The namespace of the service a <see cref="NacosMcpEndpointType.Ref"/> endpoint names.</summary>
    public const string NamespaceId = "namespaceId";

    /// <summary>The group of the service a <see cref="NacosMcpEndpointType.Ref"/> endpoint names.</summary>
    public const string GroupName = "groupName";

    /// <summary>The name of the service a <see cref="NacosMcpEndpointType.Ref"/> endpoint names.</summary>
    public const string ServiceName = "serviceName";
}
