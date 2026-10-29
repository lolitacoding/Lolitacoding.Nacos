using System.Text.Json.Nodes;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// How Nacos should find an MCP server's backend.
/// </summary>
/// <remarks>
/// The endpoint specification is a type tag plus a string map rather than a typed object, because
/// the map's keys depend on the type: a <c>DIRECT</c> endpoint carries <c>address</c> and
/// <c>port</c>, and a <c>REF</c> endpoint carries <c>namespaceId</c>, <c>groupName</c> and
/// <c>serviceName</c>. The constants for both shapes live on <see cref="NacosMcpEndpointType"/>.
/// </remarks>
public sealed class NacosMcpEndpointSpec
{
    /// <summary>Gets or sets the endpoint type.</summary>
    /// <remarks>One of the constants on <see cref="NacosMcpEndpointType"/>.</remarks>
    public string? Type { get; set; }

    /// <summary>Gets or sets the endpoint description.</summary>
    public IDictionary<string, string>? Data { get; set; }
}

/// <summary>
/// An endpoint Nacos resolved for an MCP server.
/// </summary>
/// <remarks>
/// This is the read projection. A <c>REF</c> endpoint reports the service reference Nacos
/// registered on the server's behalf, and a <c>DIRECT</c> endpoint reports the address and port an
/// instance registered from.
/// </remarks>
public sealed class NacosMcpEndpointInfo
{
    /// <summary>Gets or sets the protocol of the endpoint.</summary>
    public string? Protocol { get; set; }

    /// <summary>Gets or sets the address.</summary>
    public string? Address { get; set; }

    /// <summary>Gets or sets the port.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the URL path.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the headers to send with every call.</summary>
    public IList<NacosMcpKeyValueInput>? Headers { get; set; }
}

/// <summary>
/// A Nacos service an MCP server's backend is published as.
/// </summary>
/// <remarks>
/// Releasing a server with a <c>REF</c> endpoint or with no endpoint at all makes Nacos register
/// the backend as a service, so ordinary service discovery, load balancing and health checking
/// apply to it. The release reply then reports the reference so the caller knows what was created.
/// </remarks>
public sealed class NacosMcpServiceRef
{
    /// <summary>Gets or sets the namespace of the service.</summary>
    public string? NamespaceId { get; set; }

    /// <summary>Gets or sets the group of the service.</summary>
    public string? GroupName { get; set; }

    /// <summary>Gets or sets the service name.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Gets or sets the transport protocol of the service.</summary>
    public string? TransportProtocol { get; set; }
}

/// <summary>
/// The backend service of an MCP server that is reached over the network.
/// </summary>
/// <remarks>Set on a server specification whose protocol is not <c>stdio</c>.</remarks>
public sealed class NacosMcpRemoteServiceConfig
{
    /// <summary>Gets or sets the service the backend is published as.</summary>
    public NacosMcpServiceRef? ServiceRef { get; set; }

    /// <summary>Gets or sets the path Nacos exports the server under.</summary>
    public string? ExportPath { get; set; }

    /// <summary>Gets or sets the front endpoints Nacos serves the server on.</summary>
    /// <remarks>
    /// A front endpoint rewrites the address a client sees, which is how a server behind a gateway
    /// or a protocol bridge is described.
    /// </remarks>
    public IList<NacosMcpFrontEndpointConfig>? FrontEndpointConfigList { get; set; }
}

/// <summary>
/// One address Nacos serves an MCP server on, as a client should see it.
/// </summary>
public sealed class NacosMcpFrontEndpointConfig
{
    /// <summary>Gets or sets the endpoint role, for example <c>BACKEND</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the protocol of the front endpoint.</summary>
    public string? Protocol { get; set; }

    /// <summary>Gets or sets what <see cref="EndpointData"/> describes.</summary>
    public string? EndpointType { get; set; }

    /// <summary>Gets or sets the endpoint description.</summary>
    /// <remarks>
    /// Passed through as JSON: the shape depends on <see cref="EndpointType"/>, and can be either a
    /// direct address or a service reference.
    /// </remarks>
    public JsonNode? EndpointData { get; set; }

    /// <summary>Gets or sets the URL path.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the headers to send with every call.</summary>
    public IList<NacosMcpKeyValueInput>? Headers { get; set; }
}

/// <summary>
/// A named value with the optional schema the official MCP registry describes it by.
/// </summary>
/// <remarks>
/// The registry uses this shape for endpoint headers and for the arguments and environment
/// variables of a package, so the descriptive fields are inherited from the registry's input
/// object and are flattened here the way Jackson flattens them on the wire.
/// </remarks>
public sealed class NacosMcpKeyValueInput
{
    /// <summary>Gets or sets the name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a value indicating whether the value must be supplied.</summary>
    public bool? IsRequired { get; set; }

    /// <summary>Gets or sets the value format the registry asks for.</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets the value.</summary>
    public string? Value { get; set; }

    /// <summary>Gets or sets a value indicating whether the value is a secret.</summary>
    /// <remarks>
    /// The flag is advisory: it tells a client to hide the value, and Nacos stores the value
    /// whatever the flag says.
    /// </remarks>
    public bool? IsSecret { get; set; }

    /// <summary>Gets or sets the value to use when none is supplied.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Gets or sets the values to choose from.</summary>
    public IList<string>? Choices { get; set; }

    /// <summary>Gets or sets the placeholder a client shows for the value.</summary>
    public string? Placeholder { get; set; }

    /// <summary>Gets or sets the nested variable descriptors.</summary>
    /// <remarks>
    /// Carried as JSON: the registry describes a variable as another input object, which would
    /// otherwise need the whole input hierarchy modelled for a field no Nacos operation reads.
    /// </remarks>
    public JsonObject? Variables { get; set; }
}

/// <summary>
/// An encrypted MCP specification payload and the metadata needed to decrypt it.
/// </summary>
/// <remarks>
/// Nacos never inspects the payload; it stores it and hands it back to clients that ask for the
/// specification. The encrypt info tells those clients which key and algorithm were used.
/// </remarks>
public sealed class NacosMcpEncryptObject
{
    /// <summary>Gets or sets the encrypted payload.</summary>
    public string? Data { get; set; }

    /// <summary>Gets or sets the decryption metadata.</summary>
    public IDictionary<string, string>? EncryptInfo { get; set; }
}
