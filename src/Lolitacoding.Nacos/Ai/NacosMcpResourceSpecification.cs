using System.Text.Json.Nodes;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// The resources an MCP server version publishes, together with how they are stored.
/// </summary>
/// <remarks>
/// Resources and resource templates are MCP protocol objects — a URI, a name, a media type and
/// optional annotations — and Nacos neither validates nor rewrites them, so they are carried as
/// JSON. Modelling them would mean tracking a protocol that evolves independently of Nacos.
/// </remarks>
public sealed class NacosMcpResourceSpecification
{
    /// <summary>Gets or sets the storage form of the specification.</summary>
    /// <remarks>
    /// Empty or <c>normal</c> means the resource lists are stored in the clear and Nacos parses
    /// them. Any other value, such as <c>encrypted</c> or <c>encrypt-kms</c>, tells Nacos to store
    /// <see cref="EncryptData"/> as-is and to skip parsing.
    /// </remarks>
    public string? SpecificationType { get; set; }

    /// <summary>Gets or sets the encrypted payload.</summary>
    /// <remarks>Only meaningful when <see cref="SpecificationType"/> requests encryption.</remarks>
    public NacosMcpEncryptObject? EncryptData { get; set; }

    /// <summary>Gets or sets the fixed resources.</summary>
    public JsonArray? Resources { get; set; }

    /// <summary>Gets or sets the parameterised resource templates.</summary>
    public JsonArray? ResourceTemplates { get; set; }

    /// <summary>Gets or sets provider extensions.</summary>
    public JsonObject? Extensions { get; set; }
}
