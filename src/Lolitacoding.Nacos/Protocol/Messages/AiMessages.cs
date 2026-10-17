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

/// <summary>
/// Base type for the requests that address one prompt by key.
/// </summary>
/// <remarks>
/// The namespace travels beside the key for the same reason it does for an MCP server: the server
/// resolves the prompt from the pair, while every client operation is written in terms of the key.
/// </remarks>
internal abstract class PromptRequest : AiRequest
{
    /// <summary>Gets or sets the namespace the prompt belongs to.</summary>
    public string? NamespaceId { get; set; }

    /// <summary>Gets or sets the prompt key.</summary>
    public string? PromptKey { get; set; }
}

/// <summary>
/// A request for one version of a prompt.
/// </summary>
/// <remarks>
/// The version and the label are two ways of naming the same thing and only one of them is resolved:
/// the server prefers the version when both are set, and falls back to the latest published version
/// when neither is. The hash is the one conditional field on the request, and it is what a refresh
/// loop sends so an unchanged prompt is answered with "not modified" rather than with its content.
/// </remarks>
internal sealed class QueryPromptRequest : PromptRequest
{
    /// <summary>Gets or sets the version to read.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the label to resolve to a version.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the hash of the copy the caller already holds.</summary>
    /// <remarks>
    /// Unset asks for the content unconditionally. Set, it asks the server to answer "not modified"
    /// when the version it resolved still hashes to that value.
    /// </remarks>
    public string? Md5 { get; set; }
}

/// <summary>
/// The reply to a request for one prompt version.
/// </summary>
internal sealed class QueryPromptResponse : NacosResponse
{
    /// <summary>Gets or sets the resolved prompt.</summary>
    /// <remarks>
    /// Unset when the request failed, which the caller sees as the failure result code rather than
    /// as a missing field. A hash that still matches is reported the same way, as the "not modified"
    /// result code, so an unset prompt always means something went wrong.
    /// </remarks>
    public NacosPrompt? PromptInfo { get; set; }
}

/// <summary>
/// Base type for the requests that address one A2A agent by name.
/// </summary>
/// <remarks>
/// The A2A binding is the older of the two agent contracts and is addressed by the agent's name
/// alone — the card's own version travels as a separate field rather than as part of the key. The
/// namespace is sent beside the name for the same reason it is for an MCP server: the server resolves
/// the pair, while every client operation is written in terms of the name.
/// </remarks>
internal abstract class AgentRequest : AiRequest
{
    /// <summary>Gets or sets the namespace the agent belongs to.</summary>
    public string? NamespaceId { get; set; }

    /// <summary>Gets or sets the agent name.</summary>
    public string? AgentName { get; set; }
}

/// <summary>
/// A request for one A2A agent card.
/// </summary>
internal sealed class QueryAgentCardRequest : AgentRequest
{
    /// <summary>Gets or sets the card version to read.</summary>
    /// <remarks>Unset asks for the card the registry currently serves as the agent's latest.</remarks>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets how the card was registered, <c>URL</c> or <c>SERVICE</c>.
    /// </summary>
    /// <remarks>
    /// Unset lets the server answer with the card as stored. It matters because the two kinds of
    /// card differ in where the address comes from: a <c>URL</c> card carries it in
    /// <see cref="NacosAgentCard.Url"/>, while a <c>SERVICE</c> card is reached through the
    /// endpoints registered against it.
    /// </remarks>
    public string? RegistrationType { get; set; }
}

/// <summary>
/// A request to publish one version of an A2A agent card.
/// </summary>
/// <remarks>
/// Releasing is a publish rather than an update: the card carries its own version, and publishing
/// the same name with a new version adds one rather than replacing what callers are already using.
/// </remarks>
internal sealed class ReleaseAgentCardRequest : AgentRequest
{
    /// <summary>Gets or sets the card to publish.</summary>
    public NacosAgentCard? AgentCard { get; set; }

    /// <summary>
    /// Gets or sets how the card is registered, <c>SERVICE</c> unless the caller says otherwise.
    /// </summary>
    /// <remarks>
    /// <c>SERVICE</c> is the default because an agent published from inside a Nacos-registered
    /// process is normally reachable at an address only known at runtime.
    /// </remarks>
    public string? RegistrationType { get; set; } = NacosA2a.EndpointTypeService;

    /// <summary>Gets or sets a value indicating whether this card becomes the latest one.</summary>
    /// <remarks>
    /// Unset publishes the card without moving the label a read without a version resolves to, which
    /// is how a card is staged without redirecting callers to it.
    /// </remarks>
    public bool SetAsLatest { get; set; }
}

/// <summary>
/// The reply to a card release.
/// </summary>
/// <remarks>
/// A release reports nothing on success: the card's identity is its name and version, both of which
/// the caller already has.
/// </remarks>
internal sealed class ReleaseAgentCardResponse : NacosResponse
{
}

/// <summary>
/// A request to register or remove one runtime endpoint of an A2A agent.
/// </summary>
/// <remarks>
/// Like an MCP endpoint, this is a Naming instance registration rather than an AI record: the server
/// ties it to the connection that made it, so a client replays the registrations it still owns after
/// a reconnect.
/// </remarks>
internal sealed class AgentEndpointRequest : AgentRequest
{
    /// <summary>Gets or sets the endpoint.</summary>
    public NacosAgentEndpoint? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the operation, <see cref="NacosAiRemote.RegisterEndpoint"/> or
    /// <see cref="NacosAiRemote.DeregisterEndpoint"/>.
    /// </summary>
    public string? Type { get; set; }
}

/// <summary>
/// The reply to an A2A endpoint request.
/// </summary>
internal sealed class AgentEndpointResponse : NacosResponse
{
    /// <summary>Gets or sets the operation the server performed.</summary>
    public string? Type { get; set; }
}

/// <summary>
/// The reply to a request for an A2A agent card.
/// </summary>
internal sealed class QueryAgentCardResponse : NacosResponse
{
    /// <summary>Gets or sets the resolved card.</summary>
    /// <remarks>
    /// Unset when the request failed, which the caller sees as the failure result code rather than
    /// as a missing field.
    /// </remarks>
    public NacosAgentCardDetail? AgentCardDetailInfo { get; set; }
}
