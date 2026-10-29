using System.Text.Json.Nodes;

namespace Lolitacoding.Nacos.AI;

/// <summary>
/// The tools an MCP server version publishes, together with how they are stored.
/// </summary>
/// <remarks>
/// A caller releases this alongside the server specification. Nacos stores the tool list verbatim
/// and serves it to MCP clients, so the shape of each tool is the MCP protocol's, not Nacos's.
/// </remarks>
public sealed class NacosMcpToolSpecification
{
    /// <summary>Gets or sets the storage form of the specification.</summary>
    /// <remarks>
    /// Empty or <c>normal</c> means the tool list is stored in the clear and Nacos parses it. Any
    /// other value, such as <c>encrypted</c> or <c>encrypt-kms</c>, tells Nacos to store
    /// <see cref="EncryptData"/> as-is and to skip parsing, which is how a caller keeps a tool list
    /// that only its own MCP clients can decrypt out of reach of the registry.
    /// </remarks>
    public string? SpecificationType { get; set; }

    /// <summary>Gets or sets the encrypted payload.</summary>
    /// <remarks>Only meaningful when <see cref="SpecificationType"/> requests encryption.</remarks>
    public NacosMcpEncryptObject? EncryptData { get; set; }

    /// <summary>Gets or sets the tools.</summary>
    public IList<NacosMcpTool>? Tools { get; set; }

    /// <summary>Gets or sets per-tool Nacos metadata, keyed by tool name.</summary>
    /// <remarks>
    /// This is Nacos's own layer over the tool list: it can disable one tool of a server without
    /// changing the server, and it holds the disabled-time invocation context the console shows.
    /// </remarks>
    public IDictionary<string, NacosMcpToolMeta>? ToolsMeta { get; set; }

    /// <summary>Gets or sets the security schemes the tools refer to.</summary>
    public IList<NacosMcpSecurityScheme>? SecuritySchemes { get; set; }

    /// <summary>Gets or sets provider extensions.</summary>
    /// <remarks>Passed through as JSON; Nacos stores it without interpreting it.</remarks>
    public JsonObject? Extensions { get; set; }
}

/// <summary>
/// One tool an MCP server publishes.
/// </summary>
public sealed class NacosMcpTool
{
    /// <summary>Gets or sets the tool name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the tool description.</summary>
    /// <remarks>The description is what a model reads when deciding whether to call the tool.</remarks>
    public string? Description { get; set; }

    /// <summary>Gets or sets the JSON Schema of the tool arguments.</summary>
    /// <remarks>
    /// Left as JSON because the schema vocabulary is the MCP protocol's and is passed to the model
    /// unchanged; modelling it would mean re-implementing JSON Schema.
    /// </remarks>
    public JsonObject? InputSchema { get; set; }

    /// <summary>Gets or sets the JSON Schema of the tool result.</summary>
    public JsonObject? OutputSchema { get; set; }

    /// <summary>Gets or sets the MCP <c>_meta</c> member.</summary>
    /// <remarks>
    /// The underscore is part of the MCP protocol's extension convention, which is why the property
    /// is attributed with the wire name rather than following the camel-case policy.
    /// </remarks>
    [System.Text.Json.Serialization.JsonPropertyName("_meta")]
    public JsonObject? Meta { get; set; }

    /// <summary>Gets or sets the behaviour hints a model reads.</summary>
    public NacosMcpToolAnnotations? Annotations { get; set; }
}

/// <summary>
/// The behaviour hints an MCP tool publishes.
/// </summary>
/// <remarks>
/// Every hint is a nullable boolean because the three states are distinct: unset means the server
/// made no claim, and a client that neither sees <see langword="true"/> nor <see langword="false"/>
/// must fall back to its own assumption rather than treating the tool as safe.
/// </remarks>
public sealed class NacosMcpToolAnnotations
{
    /// <summary>Gets or sets a display title for the tool.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets whether the tool only reads state.</summary>
    public bool? ReadOnlyHint { get; set; }

    /// <summary>Gets or sets whether the tool can destroy data.</summary>
    public bool? DestructiveHint { get; set; }

    /// <summary>Gets or sets whether repeating the call is harmless.</summary>
    public bool? IdempotentHint { get; set; }

    /// <summary>Gets or sets whether the tool can reach state outside its own environment.</summary>
    public bool? OpenWorldHint { get; set; }
}

/// <summary>
/// Nacos's own metadata for one tool of an MCP server.
/// </summary>
public sealed class NacosMcpToolMeta
{
    /// <summary>Gets or sets the context Nacos recorded when the tool was invoked.</summary>
    public IDictionary<string, string>? InvokeContext { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the tool is offered to clients.
    /// </summary>
    /// <remarks>
    /// This is the registry-side switch: disabling a tool leaves the tool list untouched, so the
    /// decision can be reversed without the server republishing.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the tool templates Nacos recorded.</summary>
    public JsonObject? Templates { get; set; }
}

/// <summary>
/// A security scheme an MCP tool's arguments refer to.
/// </summary>
/// <remarks>
/// The shape follows the OpenAPI security scheme object, which the MCP protocol reuses for tools
/// whose arguments carry credentials.
/// </remarks>
public sealed class NacosMcpSecurityScheme
{
    /// <summary>Gets or sets the scheme identifier the tools refer to.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the scheme type, for example <c>apiKey</c> or <c>http</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the HTTP authentication scheme, for example <c>bearer</c>.</summary>
    public string? Scheme { get; set; }

    /// <summary>Gets or sets where an API key is carried, for example <c>header</c>.</summary>
    public string? In { get; set; }

    /// <summary>Gets or sets the name of the header or query parameter carrying the key.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the credential Nacos uses when it proxies the call.</summary>
    public string? DefaultCredential { get; set; }
}
