using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// The capability an MCP server provides.
/// </summary>
/// <remarks>
/// This is a Nacos-side classification rather than part of the MCP protocol itself. Nacos fills it
/// in while it inspects a released server, so a caller normally reads it rather than sets it. The
/// values are the Java enum member names, which is what the wire carries
/// (<c>McpCapability</c> in the official API).
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<NacosMcpCapability>))]
public enum NacosMcpCapability
{
    /// <summary>The server provides tools.</summary>
    [JsonStringEnumMemberName("TOOL")]
    Tool,

    /// <summary>The server provides prompts.</summary>
    [JsonStringEnumMemberName("PROMPT")]
    Prompt,

    /// <summary>The server provides resources.</summary>
    [JsonStringEnumMemberName("RESOURCE")]
    Resource,
}

/// <summary>
/// Describes one MCP server version as the official MCP registry publishes it.
/// </summary>
/// <remarks>
/// The JSON names are snake case, unlike every other Nacos AI model, because the fields come from
/// the official MCP registry schema. Nacos stores the object verbatim.
/// </remarks>
public sealed class NacosMcpVersionDetail
{
    /// <summary>Gets or sets the version identifier.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    /// <summary>Gets or sets the registry release date.</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the latest version.</summary>
    [JsonPropertyName("is_latest")]
    public bool? IsLatest { get; set; }
}

/// <summary>
/// The source repository an MCP server or its packages come from.
/// </summary>
/// <remarks>Part of the official MCP registry schema, stored by Nacos verbatim.</remarks>
public sealed class NacosMcpRepository
{
    /// <summary>Gets or sets the repository URL.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the registry the repository belongs to.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the repository identifier.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the subfolder inside the repository.</summary>
    public string? Subfolder { get; set; }
}

/// <summary>
/// The media type of an MCP server icon.
/// </summary>
public enum NacosMcpIconMimeType
{
    /// <summary>A PNG image.</summary>
    [JsonStringEnumMemberName("image/png")]
    ImagePng,

    /// <summary>A JPEG image.</summary>
    [JsonStringEnumMemberName("image/jpeg")]
    ImageJpeg,

    /// <summary>A JPEG image registered under the short extension.</summary>
    [JsonStringEnumMemberName("image/jpg")]
    ImageJpg,

    /// <summary>An SVG image.</summary>
    [JsonStringEnumMemberName("image/svg+xml")]
    ImageSvgXml,

    /// <summary>A WebP image.</summary>
    [JsonStringEnumMemberName("image/webp")]
    ImageWebp,
}

/// <summary>
/// The colour theme an MCP server icon is meant for.
/// </summary>
public enum NacosMcpIconTheme
{
    /// <summary>The icon is meant for a light background.</summary>
    [JsonStringEnumMemberName("light")]
    Light,

    /// <summary>The icon is meant for a dark background.</summary>
    [JsonStringEnumMemberName("dark")]
    Dark,
}

/// <summary>
/// An icon published with an MCP server.
/// </summary>
/// <remarks>Part of the official MCP registry schema, stored by Nacos verbatim.</remarks>
public sealed class NacosMcpIcon
{
    /// <summary>Gets or sets the icon URL or data URI.</summary>
    public string? Src { get; set; }

    /// <summary>Gets or sets the icon media type.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NacosMcpIconMimeType>))]
    public NacosMcpIconMimeType? MimeType { get; set; }

    /// <summary>Gets or sets the icon sizes, for example <c>48x48</c>.</summary>
    public IList<string>? Sizes { get; set; }

    /// <summary>Gets or sets the colour theme the icon is meant for.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NacosMcpIconTheme>))]
    public NacosMcpIconTheme? Theme { get; set; }
}

/// <summary>
/// The complete description of one MCP server version.
/// </summary>
/// <remarks>
/// <para>
/// This is the object a caller builds to release a server and the object Nacos returns when one is
/// queried. The official API splits the field set across <c>McpServerBasicInfo</c> (everything a
/// caller supplies) and <c>McpServerDetailInfo</c> (everything a read returns, including the
/// resolved endpoints), and the two Nacos types here mirror that split.
/// </para>
/// <para>
/// The properties are mutable rather than init-only because a caller assembles a specification
/// field by field and Nacos stores whatever it is given, and because this type is deserialized
/// directly from the server's reply rather than projected into a client-side model.
/// </para>
/// </remarks>
public class NacosMcpServer
{
    /// <summary>Gets or sets the namespace the server belongs to.</summary>
    public string? NamespaceId { get; set; }

    /// <summary>Gets or sets the internal identifier Nacos assigns to the server.</summary>
    /// <remarks>
    /// A caller normally leaves this unset: Nacos derives it from the namespace and the name. It is
    /// filled in on a read and on a release reply.
    /// </remarks>
    public string? Id { get; set; }

    /// <summary>Gets or sets the server name.</summary>
    /// <remarks>The name is unique per namespace and is the key every other operation uses.</remarks>
    public string? Name { get; set; }

    /// <summary>Gets or sets the protocol the server speaks.</summary>
    /// <remarks>
    /// One of <c>stdio</c>, <c>mcp-sse</c>, <c>mcp-streamable</c>, <c>http</c> or <c>dubbo</c>; see
    /// <see cref="NacosMcpProtocol"/> for the constants. A <c>stdio</c> server runs beside its
    /// client and is described by <see cref="LocalServerConfig"/>; every other protocol is reached
    /// over the network and is described by <see cref="RemoteServerConfig"/>.
    /// </remarks>
    public string? Protocol { get; set; }

    /// <summary>Gets or sets the protocol Nacos serves the server over.</summary>
    /// <remarks>
    /// Nacos can bridge a backend protocol onto a front protocol, so a <c>stdio</c> server may be
    /// served to clients as <c>mcp-sse</c>. When unset the backend protocol is used.
    /// </remarks>
    public string? FrontProtocol { get; set; }

    /// <summary>Gets or sets the human-readable description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the source repository, when the server is published from one.</summary>
    public NacosMcpRepository? Repository { get; set; }

    /// <summary>
    /// Gets or sets the official-registry package list.
    /// </summary>
    /// <remarks>
    /// The list is passed through as JSON rather than modelled, because the official MCP registry
    /// defines a polymorphic package schema (per-transport argument and environment descriptions)
    /// that no Nacos operation inspects. Each element is an object with the registry's
    /// <c>registryType</c>, <c>identifier</c>, <c>version</c> and <c>transport</c> members.
    /// </remarks>
    public JsonArray? Packages { get; set; }

    /// <summary>Gets or sets the icons published with the server.</summary>
    public IList<NacosMcpIcon>? Icons { get; set; }

    /// <summary>Gets or sets the project website.</summary>
    public string? WebsiteUrl { get; set; }

    /// <summary>Gets or sets the version in the official-registry shape.</summary>
    /// <remarks>
    /// Nacos replaced the plain <see cref="Version"/> string with this object so a version can carry
    /// its release date and latest flag. The plain string is still accepted for compatibility.
    /// </remarks>
    public NacosMcpVersionDetail? VersionDetail { get; set; }

    /// <summary>Gets or sets the plain version string.</summary>
    /// <remarks>Superseded by <see cref="VersionDetail"/>; still the field older servers read.</remarks>
    public string? Version { get; set; }

    /// <summary>Gets or sets the description of the backend service.</summary>
    /// <remarks>Set for every protocol except <c>stdio</c>.</remarks>
    public NacosMcpRemoteServiceConfig? RemoteServerConfig { get; set; }

    /// <summary>Gets or sets the local process description.</summary>
    /// <remarks>
    /// Set for <c>stdio</c> servers. The shape is the MCP client configuration for the command to
    /// run — typically <c>command</c>, <c>args</c> and <c>env</c> — so it is passed through as JSON
    /// rather than modelled.
    /// </remarks>
    public JsonObject? LocalServerConfig { get; set; }

    /// <summary>Gets or sets a value indicating whether the server is enabled.</summary>
    /// <remarks>
    /// This is an administrative flag separate from <see cref="Status"/>: a deprecated server can
    /// still be enabled, and a disabled one is never served.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the lifecycle status.</summary>
    /// <remarks>One of <c>active</c>, <c>deprecated</c> or <c>deleted</c>.</remarks>
    public string? Status { get; set; } = NacosMcpStatus.Active;

    /// <summary>Gets or sets the capabilities Nacos discovered in the server.</summary>
    /// <remarks>
    /// This is server-derived. Nacos inspects the released server's tool, prompt and resource
    /// listings to fill it in, so a caller does not set it on release.
    /// </remarks>
    public IList<NacosMcpCapability>? Capabilities { get; set; }
}

/// <summary>
/// One MCP server version together with everything Nacos resolved for it.
/// </summary>
/// <remarks>
/// This is the read projection: a query by name and optional version returns the server
/// specification, the tools and resources it publishes, the endpoints Nacos routed to it, and the
/// versions that exist. It is the wire shape of the official API's <c>McpServerDetailInfo</c>.
/// </remarks>
public sealed class NacosMcpServerDetail : NacosMcpServer
{
    /// <summary>Gets or sets the endpoints Nacos routes tool calls to.</summary>
    /// <remarks>
    /// A backend endpoint is the server Nacos itself calls. A <c>REF</c> endpoint names a service
    /// registered in Nacos, and Nacos resolves it to the healthy instances of that service at call
    /// time, so the list here is the service reference rather than a fixed address.
    /// </remarks>
    public IList<NacosMcpEndpointInfo>? BackendEndpoints { get; set; }

    /// <summary>Gets or sets the endpoints Nacos serves the server on.</summary>
    /// <remarks>
    /// A frontend endpoint is where a client finds the server. For a bridged server this is Nacos
    /// itself, which is why it can differ from the backend endpoint.
    /// </remarks>
    public IList<NacosMcpEndpointInfo>? FrontendEndpoints { get; set; }

    /// <summary>Gets or sets the tools this version publishes.</summary>
    /// <remarks>
    /// Named <c>toolSpec</c> on the wire and in the official API, hence the short property name.
    /// </remarks>
    public NacosMcpToolSpecification? ToolSpec { get; set; }

    /// <summary>Gets or sets the resources this version publishes.</summary>
    /// <remarks>Named <c>resourceSpec</c> on the wire and in the official API.</remarks>
    public NacosMcpResourceSpecification? ResourceSpec { get; set; }

    /// <summary>Gets or sets every version of the server that exists.</summary>
    public IList<NacosMcpVersionDetail>? AllVersions { get; set; }
}
