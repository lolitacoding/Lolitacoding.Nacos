namespace Lolitacoding.Nacos.AI;

/// <summary>
/// The ability keys this SDK advertises to a Nacos server for the AI module.
/// </summary>
/// <remarks>
/// Nacos keeps two ability tables with the same key vocabulary: the one a client sends with its
/// connection setup (<c>AbilityMode.SDK_CLIENT</c>) and the one a server answers with
/// (<c>AbilityMode.SERVER</c>). The names come from
/// <c>com.alibaba.nacos.api.ability.constant.AbilityKey</c>, which rejects duplicate names per mode,
/// so they are exactly the strings a server understands.
/// </remarks>
internal static class NacosAIAbility
{
    /// <summary>The client supports the complete AI MCP registry contract.</summary>
    public const string Mcp = "mcp";

    /// <summary>The client supports the AI agent registry contract.</summary>
    public const string Agent = "agent";

    /// <summary>The client supports the RAD watch hint push.</summary>
    public const string RadWatchV1 = "radWatchV1";
}

/// <summary>
/// The keys a Nacos server uses to advertise AI module abilities in its handshake acknowledgement.
/// </summary>
/// <remarks>
/// The names come from <c>com.alibaba.nacos.api.ability.constant.AbilityKey</c>. A key that is absent
/// from the acknowledgement means the server did not state the ability — which is not the same as
/// stating it is unsupported, so a client may only refuse a request when the key is present and
/// <see langword="false"/>.
/// </remarks>
internal static class NacosAIServerAbility
{
    /// <summary>The server accepts a persistent instance registration over gRPC.</summary>
    public const string PersistentInstanceByGrpc = "supportPersistentInstanceByGrpc";

    /// <summary>The server implements the distributed lock service.</summary>
    public const string Lock = "lock";

    /// <summary>The server can release an MCP server and register its endpoints.</summary>
    public const string Mcp = "mcp";

    /// <summary>The server can create an MCP lifecycle draft through a client release request.</summary>
    public const string McpDraftRelease = "mcpDraftRelease";

    /// <summary>The server can release an agent and register its endpoints.</summary>
    public const string Agent = "agent";

    /// <summary>The server implements the A2A AgentCard 1.0 contract.</summary>
    public const string AgentCardV1 = "agentCardV1";

    /// <summary>The server implements the complete RAD v1 contract introduced by Nacos 3.3.</summary>
    public const string RadV1 = "radV1";

    /// <summary>The server support the RAD watch hint binding.</summary>
    public const string RadWatchV1 = "radWatchV1";
}

/// <summary>
/// The kinds of hint a server pushes for a RAD Watch.
/// </summary>
/// <remarks>
/// Values come from <c>com.alibaba.nacos.api.ai.model.agent.AgentWatchEventType</c> and travel as the
/// plain enum name. Only the server decides which kind applies: the client reacts to the hint rather
/// than inferring a change from the state itself.
/// </remarks>
internal static class NacosAIWatchEvent
{
    /// <summary>A new current result may exist and the client should read it.</summary>
    public const string Invalidate = "INVALIDATE";

    /// <summary>The client must read again even when the fingerprint it holds matches.</summary>
    public const string Revalidate = "REVALIDATE";

    /// <summary>The watch is over and the server will send no more hints for it.</summary>
    public const string Terminated = "TERMINATED";
}

/// <summary>
/// The capability contract an AI client binding exposes over HTTP.
/// </summary>
/// <remarks>
/// Values come from <c>com.alibaba.nacos.api.ai.constant.AiConstants.Capability</c>. The contract is a
/// separate negotiation channel from the gRPC ability table: it is served by the AI module over HTTP
/// and names the AI features a binding implements.
/// </remarks>
internal static class NacosAICapability
{
    /// <summary>The path of the capability document.</summary>
    public const string Path = "/v3/client/ai/capabilities";

    /// <summary>The schema version of the capability document.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The complete RAD v1 contract.</summary>
    public const string RadV1 = "radV1";

    /// <summary>The MCP registry contract.</summary>
    public const string Mcp = "mcp";

    /// <summary>The skill registry contract.</summary>
    public const string Skill = "skill";

    /// <summary>The prompt registry contract.</summary>
    public const string Prompt = "prompt";

    /// <summary>The agent spec registry contract.</summary>
    public const string AgentSpec = "agentSpec";
}

/// <summary>
/// The lifecycle statuses of an AI resource metadata row and of one of its versions.
/// </summary>
/// <remarks>
/// Values come from <c>com.alibaba.nacos.api.ai.constant.AiConstants.Agent</c>. The metadata row is
/// either <c>enable</c> or <c>disable</c>; a version moves from <c>draft</c> to <c>reviewing</c> to
/// <c>online</c>, and can be taken <c>offline</c> again.
/// </remarks>
internal static class NacosAIStatus
{
    /// <summary>The resource accepts reads.</summary>
    public const string ResourceEnable = "enable";

    /// <summary>The resource is hidden from readers.</summary>
    public const string ResourceDisable = "disable";

    /// <summary>The version is being edited and is not published.</summary>
    public const string VersionDraft = "draft";

    /// <summary>The version is waiting for a review decision.</summary>
    public const string VersionReviewing = "reviewing";

    /// <summary>The version was reviewed and accepted.</summary>
    public const string VersionReviewed = "reviewed";

    /// <summary>The version is published and served to clients.</summary>
    public const string VersionOnline = "online";

    /// <summary>The version was published and then withdrawn.</summary>
    public const string VersionOffline = "offline";
}

/// <summary>
/// Names and metadata keys the A2A agent binding uses.
/// </summary>
/// <remarks>
/// Values come from <c>com.alibaba.nacos.api.ai.constant.AiConstants.A2a</c>. The two metadata keys
/// are reserved: a server strips them from an endpoint's user metadata and reads them as the
/// endpoint's protocol version and tenant.
/// </remarks>
internal static class NacosA2a
{
    /// <summary>The reserved metadata key that carries an endpoint's A2A protocol version.</summary>
    public const string EndpointProtocolVersionKey = "__nacos.agent.endpoint.protocolVersion__";

    /// <summary>The reserved metadata key that carries an endpoint's tenant.</summary>
    public const string EndpointTenantKey = "__nacos.agent.endpoint.tenant__";

    /// <summary>The namespace an A2A agent defaults to when none is given.</summary>
    public const string DefaultNamespace = "public";

    /// <summary>The endpoint is addressed by the agent card's own URL.</summary>
    public const string EndpointTypeUrl = "URL";

    /// <summary>The endpoint is the agent's backend service, resolved through Nacos.</summary>
    public const string EndpointTypeService = "SERVICE";

    /// <summary>The default transport of an A2A endpoint.</summary>
    public const string DefaultTransport = "JSONRPC";

    /// <summary>The default protocol of an A2A endpoint.</summary>
    public const string DefaultProtocol = "HTTP";
}

/// <summary>
/// Constants the AI module uses on the gRPC connection and in request bodies.
/// </summary>
/// <remarks>
/// Values come from <c>com.alibaba.nacos.api.ai.remote.AiRemoteConstants</c>. The client identifier is
/// a per-process publisher identity: the server records it with an MCP or agent endpoint the
/// connection registers, so an endpoint can be attributed to the process that owns it.
/// </remarks>
internal static class NacosAIRemote
{
    /// <summary>The connection label that carries the AI publisher identity.</summary>
    public const string ClientUuidLabel = "aiClientUuid";

    /// <summary>The request type that registers one MCP server endpoint.</summary>
    public const string RegisterEndpoint = "registerEndpoint";

    /// <summary>The request type that registers several MCP server endpoints at once.</summary>
    public const string BatchRegisterEndpoint = "batchRegisterEndpoint";

    /// <summary>The request type that removes an MCP server endpoint.</summary>
    public const string DeregisterEndpoint = "deregisterEndpoint";
}
