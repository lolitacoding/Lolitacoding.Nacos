using System.Security.Cryptography;
using System.Text;
using Lolitacoding.Nacos.Config;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// Persists configuration values on disk so a process can start and keep running while the Nacos
/// control plane is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// Failover is the reason this exists: during a control-plane outage an application must still be
/// able to read the configuration it was last given, otherwise a routine restart becomes an
/// outage of its own. This is the behaviour the official client calls a local snapshot.
/// </para>
/// <para>
/// Writes are atomic — the value is written to a temporary file and then moved into place — so a
/// process killed mid-write leaves either the previous snapshot or the new one, never a truncated
/// file. Without that, a crash during startup would poison the snapshot and the next boot would
/// fail against the cached copy instead of falling back to the server.
/// </para>
/// <para>
/// File names are derived from a hash of the coordinates rather than from the coordinates
/// themselves. Data ids and group names are arbitrary caller-supplied strings that may contain
/// characters invalid in a file name, and hashing them keeps one configuration's storage from
/// escaping into another's, which percent-encoding alone would not guarantee.
/// </para>
/// </remarks>
internal sealed class NacosConfigSnapshotStore
{
    /// <summary>The extension applied to snapshot files.</summary>
    private const string FileExtension = ".config";

    /// <summary>
    /// The marker separating content from metadata inside a snapshot file.
    /// </summary>
    /// <remarks>
    /// A line-oriented format is used rather than JSON because the payload is arbitrary text that
    /// may itself contain any byte sequence, including something JSON-shaped. Splitting on a
    /// dedicated delimiter keeps the content stored verbatim.
    /// </remarks>
    private const string MetadataSeparator = "\n---nacos-meta---\n";

    private readonly string _rootDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigSnapshotStore"/> class.
    /// </summary>
    /// <param name="rootDirectory">The directory that holds the snapshots.</param>
    public NacosConfigSnapshotStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.Combine(rootDirectory, "config");
    }

    /// <summary>
    /// Writes a snapshot, replacing any snapshot for the same configuration.
    /// </summary>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="content">The content to store.</param>
    /// <param name="contentType">The content type.</param>
    /// <param name="ct">A token that cancels the write.</param>
    /// <returns>A task that completes once the snapshot is durable.</returns>
    public async Task SaveAsync(
        NacosConfigKey key,
        string content,
        string? contentType,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);

        string path = GetPath(key);

        if (!IsPathSafe(path))
        {
            // Refusing to write is the safe response: caching is best-effort, so skipping it is
            // always preferable to writing outside the configured directory.
            return;
        }

        // The content type occupies the first line so the snapshot stays human-readable and the
        // separator can never be confused with payload that happens to start with a newline.
        string payload = string.Concat(
            contentType ?? string.Empty,
            MetadataSeparator,
            content);

        string temporary = string.Concat(path, ".tmp");

        try
        {
            // Directory creation is inside the guard on purpose. Caching is an optimisation, so a
            // read-only or permission-restricted cache location must degrade to "no snapshot"
            // rather than failing the read that already succeeded.
            Directory.CreateDirectory(_rootDirectory);

            await File.WriteAllTextAsync(temporary, payload, Encoding.UTF8, ct).ConfigureAwait(false);

            // The move is what makes the write atomic; a partially written temporary file is
            // never visible under the real name.
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            TryDelete(temporary);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temporary);
        }
    }

    /// <summary>
    /// Reads a snapshot.
    /// </summary>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>
    /// The content and its type, or <see langword="null"/> when no usable snapshot exists.
    /// </returns>
    public async Task<(string Content, string? ContentType)?> LoadAsync(
        NacosConfigKey key,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        string path = GetPath(key);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string payload = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);

            int separatorIndex = payload.IndexOf(MetadataSeparator, StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                // A file that does not carry the separator is not one of ours, or it predates this
                // format. Either way its content cannot be trusted, so it is treated as absent and
                // the caller falls through to the server.
                return null;
            }

            string contentType = payload[..separatorIndex];
            string content = payload[(separatorIndex + MetadataSeparator.Length)..];

            return (content, contentType.Length == 0 ? null : contentType);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes the snapshot for a configuration.
    /// </summary>
    /// <param name="key">The configuration coordinates.</param>
    /// <remarks>
    /// Called when the server reports the configuration no longer exists. Keeping a deleted value
    /// on disk would resurrect it during the next outage, which is exactly the wrong direction for
    /// a failover source to fail in.
    /// </remarks>
    public void Remove(NacosConfigKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        TryDelete(GetPath(key));
    }

    /// <summary>
    /// Builds the snapshot path for a configuration.
    /// </summary>
    /// <param name="key">The configuration coordinates.</param>
    /// <returns>The absolute snapshot path.</returns>
    private string GetPath(NacosConfigKey key)
    {
        string identity = string.Concat(
            key.Namespace ?? string.Empty, "\u0000", key.Group, "\u0000", key.DataId);

        // SHA-256 is used rather than the protocol's MD5 because this digest is purely a local
        // file name: nothing outside this process ever sees it, so there is no compatibility
        // reason to prefer the weaker algorithm.
        string digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

        return Path.Combine(_rootDirectory, string.Concat(digest, FileExtension));
    }

    /// <summary>
    /// Determines whether a path lies inside the snapshot root.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <returns><see langword="true"/> when the path is contained.</returns>
    /// <remarks>
    /// The path is derived from a hash, so it cannot escape today. The check exists so that a
    /// future change to the naming scheme cannot silently turn caller-supplied coordinates into a
    /// path traversal.
    /// </remarks>
    private bool IsPathSafe(string path)
    {
        string root = Path.GetFullPath(_rootDirectory);
        string candidate = Path.GetFullPath(path);

        return candidate.StartsWith(
            string.Concat(root, Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}
