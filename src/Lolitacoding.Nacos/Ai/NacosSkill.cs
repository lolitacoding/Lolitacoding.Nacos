namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// One version of a skill as the registry serves it to a client.
/// </summary>
/// <remarks>
/// <para>
/// Unlike a prompt, a skill is not a document but a directory: the instructions in
/// <c>SKILL.md</c> plus the resource files the instructions refer to. Nacos serves it as a ZIP
/// archive rather than as a JSON object, so the model carries the archive itself and the two values
/// the response reports about it.
/// </para>
/// <para>
/// <see cref="Version"/> is the version the server resolved for the selector that was asked, not
/// the one that was named — a caller that asked for a label learns where the label currently points
/// by reading it, and can record the version it actually received.
/// </para>
/// <para>
/// <see cref="Md5"/> is the server's hash of the published content. A subscription sends it back on
/// the next read so an unchanged skill costs a round trip rather than the archive; a caller that
/// stores the archive can do the same by comparing hashes instead of bytes.
/// </para>
/// </remarks>
public sealed class NacosSkill
{
    /// <summary>Gets or sets the skill name.</summary>
    /// <remarks>
    /// This is the name the skill was requested by. The archive's entries are prefixed with it, so a
    /// caller that extracts it gets a directory of that name.
    /// </remarks>
    public string? Name { get; set; }

    /// <summary>Gets or sets the version the server resolved.</summary>
    /// <remarks>
    /// Reportable as <see langword="null"/> by a server that does not state one, which is why the
    /// archive is compared by content rather than by this value.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>Gets or sets the hash of the published content.</summary>
    public string? Md5 { get; set; }

    /// <summary>
    /// Gets or sets the skill as the ZIP archive the server serves.
    /// </summary>
    /// <remarks>
    /// The archive holds <c>{name}/SKILL.md</c> and the resource files under the same directory.
    /// Binary resources are decoded from the Base64 the registry stores them as, so the entries are
    /// the bytes a reader expects.
    /// </remarks>
    public ReadOnlyMemory<byte> Archive { get; set; }
}
