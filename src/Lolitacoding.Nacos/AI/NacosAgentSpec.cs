using System.Text.Json;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// One version of an agent specification as the registry serves it to a client.
/// </summary>
/// <remarks>
/// <para>
/// An agent specification is a package rather than a template: <c>manifest.json</c> describes the
/// worker, and the resources around it are the files it loads. Nacos serves the whole package as one
/// JSON object per version, and the version and the hash of the published content travel in the
/// response headers rather than in the body — which is why they are properties here even though the
/// server does not send them inside <see cref="Content"/>.
/// </para>
/// <para>
/// <see cref="Content"/> is the manifest exactly as it was stored: a JSON document in a string,
/// because the registry keeps it opaque and hands it back unchanged. A caller parses it when it needs
/// to, and the client never reinterprets it.
/// </para>
/// <para>
/// The resource map is keyed by an identifier the registry derives from the resource's type and name,
/// so the keys are not the paths: a resource at <c>config/SOUL.md</c> is keyed
/// <c>config_SOUL__md</c> and carries both its own <see cref="NacosAgentSpecResource.Name"/>
/// (<c>SOUL.md</c>) and its <see cref="NacosAgentSpecResource.ResourceIdentifier"/>
/// (<c>config::SOUL.md</c>). A caller that needs a path reads the identifier, not the key.
/// </para>
/// </remarks>
public sealed class NacosAgentSpec
{
    /// <summary>Gets or sets the namespace the specification belongs to.</summary>
    public string? NamespaceId { get; set; }

    /// <summary>Gets or sets the specification name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the description the registry recorded.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the business tags as the registry stores them.</summary>
    /// <remarks>
    /// The registry keeps the tags as a JSON array in a string, so this is that string rather than a
    /// list: it is reported as stored, and a caller that wants the tags parses it.
    /// </remarks>
    public string? BizTags { get; set; }

    /// <summary>Gets or sets the <c>manifest.json</c> content.</summary>
    public string? Content { get; set; }

    /// <summary>Gets or sets the resources the specification is built from.</summary>
    /// <remarks>
    /// Keyed by the registry's resource identifier rather than by path; see the remarks on this type.
    /// </remarks>
    public Dictionary<string, NacosAgentSpecResource>? Resource { get; set; }

    /// <summary>Gets or sets the version the server resolved.</summary>
    /// <remarks>
    /// Reported in the response header, not in the payload. The not-modified answer carries the hash
    /// alone, so this is <see langword="null"/> on a conditional read that changed nothing — unlike a
    /// skill, whose not-modified answer restates the version. A subscriber therefore keeps the version
    /// of the copy it already holds rather than reading one from a body that was not sent.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>Gets or sets the hash of the published content.</summary>
    /// <remarks>
    /// Reported in the response header, so a caller can record what it received. A subscription does
    /// not send it back: a conditional read of this resource cannot report a withdrawal, so the
    /// refresh reads the whole specification instead.
    /// </remarks>
    public string? Md5 { get; set; }
}

/// <summary>
/// One file of an agent specification.
/// </summary>
public sealed class NacosAgentSpecResource
{
    /// <summary>Gets or sets the file name, relative to the resource's type.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the resource type, such as <c>config</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the file content.</summary>
    /// <remarks>
    /// Text is stored as text; binary content is stored Base64-encoded, which is what the parsing
    /// metadata says. The registry reports what it stored rather than decoding it.
    /// </remarks>
    public string? Content { get; set; }

    /// <summary>Gets or sets the resource metadata.</summary>
    /// <remarks>
    /// Free-form, so it is kept as JSON rather than projected onto a model: a member the registry adds
    /// later survives a round trip through this client.
    /// </remarks>
    public JsonElement? Metadata { get; set; }

    /// <summary>Gets or sets the identifier that names the resource's path, such as <c>config::SOUL.md</c>.</summary>
    public string? ResourceIdentifier { get; set; }
}

/// <summary>
/// The v3 result envelope an agent specification is served in.
/// </summary>
/// <remarks>
/// The other AI resources are read over the gRPC connection and answered with a message; this one is
/// an ordinary OpenAPI endpoint, so the payload arrives inside the API's result envelope. The client
/// unwraps it and reports <see cref="Code"/> and <see cref="Message"/> as the server's error when it
/// is not a success.
/// </remarks>
internal sealed class NacosAgentSpecResult
{
    /// <summary>Gets or sets the result code, zero for success.</summary>
    public int Code { get; set; }

    /// <summary>Gets or sets the message, which names the code rather than the reason.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the specification.</summary>
    public NacosAgentSpec? Data { get; set; }
}
