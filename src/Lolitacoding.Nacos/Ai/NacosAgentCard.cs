using System.Text.Json;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// An A2A agent card: what an agent is and how to call it, as the registry serves it.
/// </summary>
/// <remarks>
/// <para>
/// A card is the A2A protocol's own document rather than a Nacos model, so its members keep the
/// protocol's names and shapes. Nacos stores the card and hands it back unchanged, which is why the
/// members whose shape the protocol leaves open — a security scheme, a signature, an extension's
/// parameters — are kept as JSON instead of being projected onto a model that would drop a member
/// this client has not been taught about.
/// </para>
/// <para>
/// A card is identified by the agent's name and its own <see cref="Version"/>, which is the card's
/// version rather than a Nacos release number: publishing the same agent twice with different
/// content is two versions of the card, and the registry keeps the latest one labelled.
/// </para>
/// </remarks>
public class NacosAgentCard
{
    /// <summary>Gets or sets the A2A protocol version the agent speaks.</summary>
    public string? ProtocolVersion { get; set; }

    /// <summary>Gets or sets the agent's name, which is the registry key.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the description a caller uses to decide whether to call the agent.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the card's version.</summary>
    /// <remarks>
    /// This is the agent's own version, not a Nacos release number. It is what a read pins and what
    /// a registration is recorded against.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>Gets or sets the URL of an icon that represents the agent.</summary>
    public string? IconUrl { get; set; }

    /// <summary>Gets or sets the optional features the agent supports.</summary>
    public NacosAgentCapabilities? Capabilities { get; set; }

    /// <summary>Gets or sets the skills the agent offers.</summary>
    public List<NacosAgentSkill>? Skills { get; set; }

    /// <summary>Gets or sets the URL the agent is called at.</summary>
    /// <remarks>
    /// This is the address a card registered as a <c>URL</c> endpoint is reached at. A card
    /// registered as a <c>SERVICE</c> is reached through the endpoints registered against it
    /// instead, because the address of a service-backed agent is only known at runtime.
    /// </remarks>
    public string? Url { get; set; }

    /// <summary>Gets or sets the transport the agent prefers.</summary>
    public string? PreferredTransport { get; set; }

    /// <summary>Gets or sets the transports the agent also accepts.</summary>
    public List<NacosAgentInterface>? AdditionalInterfaces { get; set; }

    /// <summary>Gets or sets the interfaces the registry reports the agent as supporting.</summary>
    public List<NacosAgentInterface>? SupportedInterfaces { get; set; }

    /// <summary>Gets or sets the organization that provides the agent.</summary>
    public NacosAgentProvider? Provider { get; set; }

    /// <summary>Gets or sets the URL of the agent's documentation.</summary>
    public string? DocumentationUrl { get; set; }

    /// <summary>Gets or sets the security schemes the agent's calls may use.</summary>
    /// <remarks>
    /// Kept as JSON: an A2A security scheme is an open object whose members differ per scheme type,
    /// so projecting it would either lose members or invent a shape the protocol does not fix.
    /// </remarks>
    public Dictionary<string, JsonElement>? SecuritySchemes { get; set; }

    /// <summary>Gets or sets the security requirements a caller has to satisfy.</summary>
    /// <remarks>
    /// A list of alternatives, each naming the schemes it needs and the scopes within them.
    /// </remarks>
    public List<Dictionary<string, List<string>>>? Security { get; set; }

    /// <summary>Gets or sets the extended security requirements, when the agent publishes them.</summary>
    public List<Dictionary<string, List<string>>>? SecurityRequirements { get; set; }

    /// <summary>Gets or sets the input media types the agent accepts by default.</summary>
    public List<string>? DefaultInputModes { get; set; }

    /// <summary>Gets or sets the output media types the agent produces by default.</summary>
    public List<string>? DefaultOutputModes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the agent serves an extended card to authenticated
    /// callers.
    /// </summary>
    public bool? SupportsAuthenticatedExtendedCard { get; set; }

    /// <summary>Gets or sets the signatures that attest to the card's content.</summary>
    public List<Dictionary<string, JsonElement>>? Signatures { get; set; }
}

/// <summary>
/// An agent card together with what the registry knows about the copy it served.
/// </summary>
/// <remarks>
/// The two extra members are the registry's rather than the protocol's: they say how the card was
/// registered and whether it is the newest one, neither of which belongs in the card itself.
/// </remarks>
public sealed class NacosAgentCardDetail : NacosAgentCard
{
    /// <summary>Gets or sets how the card was registered.</summary>
    /// <remarks>
    /// <c>URL</c> means the card's own <see cref="NacosAgentCard.Url"/> is the endpoint, and
    /// <c>SERVICE</c> means the agent is reached through the endpoints registered against it.
    /// </remarks>
    public string? RegistrationType { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the agent's newest card.</summary>
    public bool? LatestVersion { get; set; }
}

/// <summary>
/// One interface an agent is reachable over.
/// </summary>
/// <remarks>
/// An agent that speaks more than one transport or protocol version publishes one of these per
/// combination, so a caller can pick the one it supports rather than guessing from the card's
/// preferred transport.
/// </remarks>
public sealed class NacosAgentInterface
{
    /// <summary>Gets or sets the URL the interface is reached at.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the transport, such as <c>JSONRPC</c>.</summary>
    public string? Transport { get; set; }

    /// <summary>Gets or sets the protocol binding the interface uses.</summary>
    public string? ProtocolBinding { get; set; }

    /// <summary>Gets or sets the A2A protocol version the interface speaks.</summary>
    public string? ProtocolVersion { get; set; }

    /// <summary>Gets or sets the tenant the interface serves.</summary>
    public string? Tenant { get; set; }
}

/// <summary>
/// One skill an agent offers.
/// </summary>
/// <remarks>
/// A skill is how a card advertises a capability in terms a caller can choose between: an identifier,
/// a description, and examples of what to ask for.
/// </remarks>
public sealed class NacosAgentSkill
{
    /// <summary>Gets or sets the skill's identifier.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the skill's name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets what the skill does.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the tags the skill is discoverable by.</summary>
    public List<string>? Tags { get; set; }

    /// <summary>Gets or sets example requests the skill handles.</summary>
    public List<string>? Examples { get; set; }

    /// <summary>Gets or sets the input media types the skill accepts.</summary>
    public List<string>? InputModes { get; set; }

    /// <summary>Gets or sets the output media types the skill produces.</summary>
    public List<string>? OutputModes { get; set; }
}

/// <summary>
/// The optional A2A features an agent supports.
/// </summary>
/// <remarks>
/// Each flag is optional in the protocol, so an unset one means the agent did not state it rather
/// than that it does not support it.
/// </remarks>
public sealed class NacosAgentCapabilities
{
    /// <summary>Gets or sets a value indicating whether the agent streams responses.</summary>
    public bool? Streaming { get; set; }

    /// <summary>Gets or sets a value indicating whether the agent pushes notifications.</summary>
    public bool? PushNotifications { get; set; }

    /// <summary>Gets or sets a value indicating whether the agent reports state transitions.</summary>
    public bool? StateTransitionHistory { get; set; }

    /// <summary>Gets or sets a value indicating whether an extended card is available.</summary>
    public bool? ExtendedAgentCard { get; set; }

    /// <summary>Gets or sets the protocol extensions the agent supports.</summary>
    public List<NacosAgentExtension>? Extensions { get; set; }
}

/// <summary>
/// One A2A protocol extension an agent supports.
/// </summary>
public sealed class NacosAgentExtension
{
    /// <summary>Gets or sets the URI that identifies the extension.</summary>
    public string? Uri { get; set; }

    /// <summary>Gets or sets what the extension does.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets a value indicating whether a caller must understand the extension.</summary>
    public bool? Required { get; set; }

    /// <summary>Gets or sets the extension's parameters.</summary>
    /// <remarks>Kept as JSON: the protocol leaves the parameters to the extension that defines them.</remarks>
    public Dictionary<string, JsonElement>? Params { get; set; }
}

/// <summary>
/// The organization that provides an agent.
/// </summary>
public sealed class NacosAgentProvider
{
    /// <summary>Gets or sets the organization's name.</summary>
    public string? Organization { get; set; }

    /// <summary>Gets or sets the organization's URL.</summary>
    public string? Url { get; set; }
}

/// <summary>
/// The two ways an A2A agent card can be registered.
/// </summary>
/// <remarks>
/// The distinction decides where a caller's address comes from: a <c>URL</c> card names it itself,
/// while a <c>SERVICE</c> card is reached through the endpoints registered against it. The values are
/// the strings the registry stores and compares, so they are what a caller passes as
/// <c>registrationType</c>.
/// </remarks>
public static class NacosAgentRegistrationType
{
    /// <summary>The card's own URL is the address the agent is called at.</summary>
    public const string Url = "URL";

    /// <summary>The agent is reached through the endpoints registered against the card.</summary>
    public const string Service = "SERVICE";
}
