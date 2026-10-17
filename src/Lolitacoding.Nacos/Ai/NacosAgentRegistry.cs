using System.Text.Json;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// The RAD agent registry: the agent contract Nacos 3.3 adds next to the A2A card.
/// </summary>
/// <remarks>
/// <para>
/// Where a card is a document an agent publishes about itself, this registry treats an agent as a
/// versioned resource with metadata, call interfaces and runtime endpoints — closer to how a prompt or
/// a skill is managed. The two coexist: a card remains how an A2A agent advertises itself, and this is
/// how an agent is published, searched and discovered with the registry's own model.
/// </para>
/// <para>
/// Publishing is a request rather than a document: the caller names the agent, the version, the
/// interfaces it speaks and whether the version should be submitted for review. A publish returns the
/// version detail the registry created, which is where the status and the labels come from.
/// </para>
/// </remarks>
public sealed class NacosAgentPublishRequest
{
    /// <summary>Gets or sets the agent name, which is the registry key.</summary>
    public string? AgentName { get; set; }

    /// <summary>Gets or sets the name shown to a human.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets what the agent does.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the URL of an icon that represents the agent.</summary>
    public string? IconUrl { get; set; }

    /// <summary>Gets or sets the organization that provides the agent.</summary>
    public NacosAgentRegistryProvider? Provider { get; set; }

    /// <summary>Gets or sets the tags the agent is discoverable by.</summary>
    public List<string>? Tags { get; set; }

    /// <summary>Gets or sets the agent's extensions.</summary>
    /// <remarks>Kept as JSON: the registry stores what it is given and the shape is the caller's.</remarks>
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Gets or sets the version this publish creates.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the interfaces the agent is reachable over.</summary>
    public List<NacosAgentCallInterface>? CallInterfaces { get; set; }

    /// <summary>Gets or sets who published the version.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets what changed in this version.</summary>
    public string? ChangeDescription { get; set; }

    /// <summary>Gets or sets the version this one was derived from.</summary>
    public string? BasedOnVersion { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the version is submitted for review as it is published.
    /// </summary>
    /// <remarks>
    /// A version that is not submitted stays editable and is not served, which is how a caller stages
    /// a version and publishes it later.
    /// </remarks>
    public bool AutoSubmit { get; set; }
}

/// <summary>
/// The organization that provides an agent, as the RAD registry models it.
/// </summary>
/// <remarks>
/// This is not the A2A card's <see cref="NacosAgentProvider"/>: the card's document names the
/// organization, while the registry's metadata names the provider. The two contracts use the same
/// word for slightly different members, so they are two types rather than one type with a field that
/// is meaningless in half its uses.
/// </remarks>
public sealed class NacosAgentRegistryProvider
{
    /// <summary>Gets or sets the provider's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the provider's URL.</summary>
    public string? Url { get; set; }
}

/// <summary>
/// One protocol an agent is callable over.
/// </summary>
/// <remarks>
/// <para>
/// A call interface is what a caller needs to reach the agent: the protocol, the version of it, and
/// the sets of endpoints that serve it. The endpoints are grouped by where they came from — declared
/// by the card, or registered at runtime — because a runtime endpoint can be withdrawn without the
/// declaration changing.
/// </para>
/// <para>
/// The native descriptor is kept as JSON: it is the agent framework's own document, and the registry
/// hands it back unchanged.
/// </para>
/// </remarks>
public sealed class NacosAgentCallInterface
{
    /// <summary>Gets or sets the protocol, such as <c>a2a</c> or <c>mcp</c>.</summary>
    public string? Protocol { get; set; }

    /// <summary>Gets or sets the version of that protocol.</summary>
    public string? ProtocolVersion { get; set; }

    /// <summary>Gets or sets the media type of <see cref="NativeDescriptor"/>.</summary>
    public string? DescriptorMediaType { get; set; }

    /// <summary>Gets or sets the agent's own description document.</summary>
    public JsonElement? NativeDescriptor { get; set; }

    /// <summary>Gets or sets the order the endpoint sources are consulted in.</summary>
    /// <remarks>
    /// A source that appears earlier wins when the same address is offered by several, so the order
    /// is what decides whether a declared endpoint or a runtime one is preferred.
    /// </remarks>
    public List<string>? EndpointSourceOrder { get; set; }

    /// <summary>Gets or sets the sets of endpoints that serve the interface.</summary>
    public List<NacosAgentEndpointSet>? EndpointSets { get; set; }
}

/// <summary>
/// One group of endpoints that share where they came from.
/// </summary>
/// <remarks>
/// <see cref="SourceRevision"/> changes when the group does, so a caller can tell whether what it is
/// looking at is the group it saw before without comparing every endpoint.
/// </remarks>
public sealed class NacosAgentEndpointSet
{
    /// <summary>Gets or sets where the group came from, <c>DECLARED</c> or <c>RUNTIME</c>.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the revision of the group.</summary>
    public string? SourceRevision { get; set; }

    /// <summary>Gets or sets when the group last changed, in milliseconds since the Unix epoch.</summary>
    public long? LastUpdatedTime { get; set; }

    /// <summary>Gets or sets the endpoints in the group.</summary>
    public List<NacosAgentRuntimeEndpoint>? Endpoints { get; set; }
}

/// <summary>
/// One endpoint of a RAD agent.
/// </summary>
/// <remarks>
/// The members that decide routing are <see cref="Uri"/>, <see cref="Transport"/> and
/// <see cref="Weight"/>; the rest is bookkeeping the registry keeps: a priority for ordering, a
/// health flag the server maintains, and the version bindings that say which agent versions the
/// endpoint serves.
/// </remarks>
public sealed class NacosAgentRuntimeEndpoint
{
    /// <summary>Gets or sets the URI the endpoint is reached at.</summary>
    public string? Uri { get; set; }

    /// <summary>Gets or sets the transport, such as <c>JSONRPC</c>.</summary>
    public string? Transport { get; set; }

    /// <summary>Gets or sets the ordering priority; lower is preferred.</summary>
    public int? Priority { get; set; }

    /// <summary>Gets or sets the load-balancing weight.</summary>
    public double? Weight { get; set; }

    /// <summary>Gets or sets the endpoint's metadata.</summary>
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>Gets or sets a value indicating whether the registry considers the endpoint healthy.</summary>
    public bool? Healthy { get; set; }

    /// <summary>Gets or sets the agent versions the endpoint serves.</summary>
    /// <remarks>
    /// Unset inherits the batch's <see cref="NacosAgentEndpointBatch.RuntimeVersion"/> and
    /// <see cref="NacosAgentEndpointBatch.VersionRange"/>, which is what a batch of endpoints that all
    /// serve the same range wants. A registration that names neither here nor there is refused.
    /// </remarks>
    public List<NacosAgentVersionBinding>? Bindings { get; set; }

    /// <summary>Gets or sets a value indicating whether the endpoint is enabled.</summary>
    public bool? Enabled { get; set; }
}

/// <summary>
/// A range of agent versions one endpoint serves.
/// </summary>
/// <remarks>
/// An endpoint that serves every version names a runtime version and the range it applies to, which is
/// how one deployment can serve several published versions without re-registering.
/// </remarks>
public sealed class NacosAgentVersionBinding
{
    /// <summary>Gets or sets the runtime version the endpoint was deployed as.</summary>
    public string? RuntimeVersion { get; set; }

    /// <summary>Gets or sets the range of agent versions it serves.</summary>
    public string? VersionRange { get; set; }
}

/// <summary>
/// One version of an agent, as a publish reports it.
/// </summary>
/// <remarks>
/// The detail is the version summary plus what identifies it: the agent and namespace it belongs to,
/// and the call interfaces the version was published with. The summary's own members are the
/// registry's bookkeeping — status, labels, protocols, who published it and when.
/// </remarks>
public sealed class NacosAgentVersionDetail : NacosAgentVersionSummary
{
    /// <summary>Gets or sets the namespace the agent belongs to.</summary>
    public string? NamespaceId { get; set; }

    /// <summary>Gets or sets the agent name.</summary>
    public string? AgentName { get; set; }

    /// <summary>Gets or sets the call interfaces the version was published with.</summary>
    public List<NacosAgentCallInterface>? CallInterfaces { get; set; }
}

/// <summary>
/// What the registry records about one version of an agent.
/// </summary>
public class NacosAgentVersionSummary
{
    /// <summary>Gets or sets the version.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the labels that point at this version.</summary>
    public List<string>? Labels { get; set; }

    /// <summary>Gets or sets the protocols the version declares.</summary>
    public List<string>? Protocols { get; set; }

    /// <summary>Gets or sets the version's lifecycle status.</summary>
    /// <remarks>
    /// One of <c>draft</c>, <c>reviewing</c>, <c>reviewed</c>, <c>online</c> or <c>offline</c>: a
    /// version is only served while it is online.
    /// </remarks>
    public string? Status { get; set; }

    /// <summary>Gets or sets the pipeline the version was published through.</summary>
    public string? PublishPipelineInfo { get; set; }

    /// <summary>Gets or sets who published the version.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets what changed in this version.</summary>
    public string? ChangeDescription { get; set; }

    /// <summary>Gets or sets the digest of the version's content.</summary>
    /// <remarks>
    /// This is the fingerprint a discovery answer carries, so a caller can tell whether what it has
    /// is what the registry would serve now.
    /// </remarks>
    public string? ContentDigest { get; set; }

    /// <summary>Gets or sets when the version was created, in milliseconds since the Unix epoch.</summary>
    public long? CreateTime { get; set; }

    /// <summary>Gets or sets when the version last changed, in milliseconds since the Unix epoch.</summary>
    public long? UpdateTime { get; set; }
}

/// <summary>
/// One agent as a search reports it.
/// </summary>
/// <remarks>
/// A summary is the agent's metadata plus its version manifest: which versions are online, which are
/// being edited, and which labels point where. It is what a caller browses before it reads one version.
/// </remarks>
public sealed class NacosAgentSummary
{
    /// <summary>Gets or sets the agent name.</summary>
    public string? AgentName { get; set; }

    /// <summary>Gets or sets the name shown to a human.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Gets or sets what the agent does.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the URL of an icon that represents the agent.</summary>
    public string? IconUrl { get; set; }

    /// <summary>Gets or sets the organization that provides the agent.</summary>
    public NacosAgentRegistryProvider? Provider { get; set; }

    /// <summary>Gets or sets the tags the agent is discoverable by.</summary>
    public List<string>? Tags { get; set; }

    /// <summary>Gets or sets the agent's extensions.</summary>
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Gets or sets the namespace the agent belongs to.</summary>
    public string? NamespaceId { get; set; }

    /// <summary>Gets or sets the agent's lifecycle status.</summary>
    /// <remarks><c>enable</c> while the agent accepts reads, <c>disable</c> while it is hidden.</remarks>
    public string? Status { get; set; }

    /// <summary>Gets or sets who owns the agent.</summary>
    public string? Owner { get; set; }

    /// <summary>Gets or sets the agent's visibility.</summary>
    public string? Scope { get; set; }

    /// <summary>Gets or sets the version manifest.</summary>
    public NacosAgentVersionInfo? VersionInfo { get; set; }

    /// <summary>Gets or sets the metadata revision.</summary>
    /// <remarks>
    /// It moves when anything about the agent changes, which is what a caller watches to know whether
    /// its copy of the summary is stale.
    /// </remarks>
    public long? MetaVersion { get; set; }

    /// <summary>Gets or sets when the agent was created, in milliseconds since the Unix epoch.</summary>
    public long? CreateTime { get; set; }

    /// <summary>Gets or sets when the agent last changed, in milliseconds since the Unix epoch.</summary>
    public long? UpdateTime { get; set; }
}

/// <summary>
/// The version manifest of an agent.
/// </summary>
public sealed class NacosAgentVersionInfo
{
    /// <summary>Gets or sets the version being edited, when there is one.</summary>
    public string? EditingVersion { get; set; }

    /// <summary>Gets or sets the version waiting for a review decision, when there is one.</summary>
    public string? ReviewingVersion { get; set; }

    /// <summary>Gets or sets the versions that are served.</summary>
    public List<NacosAgentVersionSummary>? OnlineVersions { get; set; }

    /// <summary>Gets or sets the labels and the versions they point at.</summary>
    public Dictionary<string, string>? Labels { get; set; }
}

/// <summary>
/// The query a search runs.
/// </summary>
/// <remarks>
/// Every filter is optional and they combine: a search that names a name fragment and a tag returns
/// the agents that match both.
/// </remarks>
public sealed class NacosAgentSearchRequest
{
    /// <summary>Gets or sets a fragment the agent's name has to contain.</summary>
    public string? AgentNameContains { get; set; }

    /// <summary>Gets or sets the tags the agent has to carry all of.</summary>
    public List<string>? TagsAll { get; set; }

    /// <summary>Gets or sets the protocols the agent has to speak at least one of.</summary>
    public List<string>? ProtocolsAny { get; set; }

    /// <summary>Gets or sets the page to read, counting from one.</summary>
    public int? PageNo { get; set; }

    /// <summary>Gets or sets how many agents a page holds.</summary>
    public int? PageSize { get; set; }
}

/// <summary>
/// One page of a search result.
/// </summary>
/// <typeparam name="T">What the page holds.</typeparam>
public sealed class NacosAgentPage<T>
{
    /// <summary>Gets or sets how many items matched in total.</summary>
    public int TotalCount { get; set; }

    /// <summary>Gets or sets which page this is, counting from one.</summary>
    public int PageNumber { get; set; }

    /// <summary>Gets or sets how many pages the match takes.</summary>
    public int PagesAvailable { get; set; }

    /// <summary>Gets or sets the items on this page.</summary>
    public List<T>? PageItems { get; set; }
}

/// <summary>
/// Which agent version a discovery is about.
/// </summary>
/// <remarks>
/// The version and the label are two ways of naming the same thing: the server resolves the version
/// when one is given and falls back to the label otherwise.
/// </remarks>
public sealed class NacosAgentReference
{
    /// <summary>Gets or sets the agent name.</summary>
    public string? AgentName { get; set; }

    /// <summary>Gets or sets the version to resolve.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the label to resolve.</summary>
    public string? Label { get; set; }
}

/// <summary>
/// Everything a discovery names: the namespace, the agent version, and what the caller can use.
/// </summary>
/// <remarks>
/// The three travel together because a watch subscribes with the whole thing: a subscription has to
/// describe its target completely, or the server cannot tell two watches apart that differ only in
/// what they filter for.
/// </remarks>
public sealed class NacosAgentDiscoveryRequest
{
    /// <summary>Gets or sets the namespace the agent belongs to.</summary>
    public string? NamespaceId { get; set; }

    /// <summary>Gets or sets which agent version the discovery is about.</summary>
    public NacosAgentReference? Reference { get; set; }

    /// <summary>Gets or sets what the caller can use.</summary>
    public NacosAgentDiscoveryFilter? Filter { get; set; }
}

/// <summary>
/// What a discovery is looking for beyond the agent itself.
/// </summary>
/// <remarks>
/// A filter narrows the answer to the endpoints a caller can actually use: a client that speaks one
/// protocol version and one transport asks for those, instead of receiving every endpoint and
/// discarding most of them.
/// </remarks>
public sealed class NacosAgentDiscoveryFilter
{
    /// <summary>Gets or sets the protocols the answer may use.</summary>
    public List<string>? Protocols { get; set; }

    /// <summary>Gets or sets the protocol version the answer must speak.</summary>
    public string? ProtocolVersion { get; set; }

    /// <summary>Gets or sets the transports the answer may use.</summary>
    public List<string>? Transports { get; set; }

    /// <summary>Gets or sets the endpoint sources the answer may come from.</summary>
    /// <remarks><c>DECLARED</c>, <c>RUNTIME</c>, or both.</remarks>
    public List<string>? EndpointSources { get; set; }

    /// <summary>Gets or sets metadata an endpoint has to carry.</summary>
    public Dictionary<string, string>? MetadataSelector { get; set; }
}

/// <summary>
/// The answer to a discovery.
/// </summary>
/// <remarks>
/// A discovery returns the agent's metadata and the interfaces that survived the filter, which is
/// everything a caller needs to make a call. The digest is the fingerprint of what was served, so a
/// caller can tell later whether its copy is still current.
/// </remarks>
public sealed class NacosAgentDiscoveryResult
{
    /// <summary>Gets or sets the namespace the agent belongs to.</summary>
    public string? NamespaceId { get; set; }

    /// <summary>Gets or sets the agent name.</summary>
    public string? AgentName { get; set; }

    /// <summary>Gets or sets what the agent does.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the tags the agent carries.</summary>
    public List<string>? Tags { get; set; }

    /// <summary>Gets or sets the version that was resolved.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the digest of what was served.</summary>
    public string? ContentDigest { get; set; }

    /// <summary>Gets or sets the call interfaces the agent offers.</summary>
    public List<NacosAgentCallInterface>? CallInterfaces { get; set; }
}

/// <summary>
/// One batch of runtime endpoints registered for an agent.
/// </summary>
/// <remarks>
/// <para>
/// The batch is registered as a whole, and a batch replaces what the same publisher registered for the
/// same protocol before. That is what makes a redeploy safe: the new process publishes its endpoints
/// and the old process' set is superseded rather than accumulating beside it.
/// </para>
/// <para>
/// The version fields say which agent versions the endpoints serve, so one deployment can serve a
/// range instead of one version. They are not optional in practice: an endpoint that carries no
/// binding of its own inherits the batch's, and the registry refuses a registration that names
/// neither — it has no version to bind the endpoints to. <see cref="RuntimeVersion"/> and
/// <see cref="VersionRange"/> are therefore what a caller normally sets.
/// </para>
/// </remarks>
public sealed class NacosAgentEndpointBatch
{
    /// <summary>Gets or sets the agent name.</summary>
    public string? AgentName { get; set; }

    /// <summary>Gets or sets the protocol the endpoints serve.</summary>
    public string? Protocol { get; set; }

    /// <summary>Gets or sets the endpoints.</summary>
    public List<NacosAgentRuntimeEndpoint>? Endpoints { get; set; }

    /// <summary>Gets or sets the runtime version the publisher was deployed as.</summary>
    /// <remarks>
    /// The version the endpoints are bound to when they carry no binding of their own. It has to fall
    /// inside <see cref="VersionRange"/>.
    /// </remarks>
    public string? RuntimeVersion { get; set; }

    /// <summary>Gets or sets the range of agent versions the endpoints serve.</summary>
    /// <remarks>
    /// Written the way the registry canonicalises it — <c>[1.0.0,2.0.0)</c> — because a range that is
    /// not canonical is refused rather than normalised.
    /// </remarks>
    public string? VersionRange { get; set; }
}
