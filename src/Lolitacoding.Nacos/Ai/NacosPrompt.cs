namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// A placeholder a prompt template can be filled with.
/// </summary>
/// <remarks>
/// The variable set is metadata <em>about</em> the template rather than part of it: the template
/// carries the placeholders as text (<c>{{name}}</c>), and this describes each one. A variable with
/// no default is one a caller has to supply, which is why the absence of a default is meaningful
/// rather than an oversight.
/// </remarks>
public sealed class NacosPromptVariable
{
    /// <summary>Gets or sets the name, matching the placeholder in the template.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the default value, or <see langword="null"/> when the variable is required.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Gets or sets the human-readable description of what the variable is for.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// One version of a prompt as the registry serves it to a client.
/// </summary>
/// <remarks>
/// <para>
/// This is the client-facing shape rather than the stored one, and Nacos projects one onto the other
/// before answering: the registry holds a prompt as a version row with an author, a commit message
/// and a lifecycle status, and serves only the content a caller needs to render the prompt. The
/// fields here are the ones the official client's <c>Prompt</c> has, so nothing is lost by reading
/// through this type.
/// </para>
/// <para>
/// A prompt is identified by its key and resolved to a version by the server: a read may name a
/// version, name a label that Nacos moves between versions, or name neither and take the version
/// labelled <c>latest</c>. The version in the answer is the one that was resolved, not the one that
/// was asked for, which is what lets a caller read a label without knowing where it points.
/// </para>
/// <para>
/// The properties are mutable rather than init-only because this type is deserialized directly from
/// the server's reply rather than projected into a client-side model.
/// </para>
/// </remarks>
public sealed class NacosPrompt
{
    /// <summary>Gets or sets the prompt key.</summary>
    /// <remarks>The key is unique within a namespace and is what every read is addressed by.</remarks>
    public string? PromptKey { get; set; }

    /// <summary>Gets or sets the resolved version.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the template text.</summary>
    /// <remarks>
    /// The placeholders are left as they are written: Nacos stores the template verbatim and renders
    /// nothing, so substituting the variables is the caller's job.
    /// </remarks>
    public string? Template { get; set; }

    /// <summary>Gets or sets the content hash Nacos computed for this version.</summary>
    /// <remarks>
    /// The hash is what makes a conditional read possible: passing the hash of the copy a caller
    /// already holds is answered with "not modified" rather than with the content again.
    /// </remarks>
    public string? Md5 { get; set; }

    /// <summary>
    /// Gets or sets the placeholders the template uses.
    /// </summary>
    /// <remarks>
    /// Unset for a prompt stored without variable metadata, which is the shape older prompts were
    /// written in, so a caller cannot assume the set is complete even when the template has
    /// placeholders.
    /// </remarks>
    public IList<NacosPromptVariable>? Variables { get; set; }
}
