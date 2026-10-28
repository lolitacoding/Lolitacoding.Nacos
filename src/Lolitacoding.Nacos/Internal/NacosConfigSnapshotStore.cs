using System.Security.Cryptography;
using System.Text;
using Lolitacoding.Nacos.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    /// <summary>
    /// The marker that introduces the current metadata layout.
    /// </summary>
    /// <remarks>
    /// Snapshots written before the encrypted data key was stored begin directly with the content
    /// type and carry no marker. Keeping the marker explicit means those files stay readable: their
    /// first line is never mistaken for a version tag.
    /// </remarks>
    private const string MetadataVersionMarker = "v2\n";

    private readonly string _rootDirectory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigSnapshotStore"/> class.
    /// </summary>
    /// <param name="rootDirectory">The directory that holds the snapshots.</param>
    /// <param name="logger">
    /// The logger that reports a snapshot that could not be written or read, or
    /// <see langword="null"/> when the caller has none.
    /// </param>
    /// <remarks>
    /// The store reports failures itself rather than throwing. Every caller treats caching as an
    /// optimisation that must never fail the operation it decorates, so a failure that is only
    /// signalled through an exception would either be swallowed by each of them — which is how the
    /// failure used to disappear — or break a read that already succeeded.
    /// </remarks>
    public NacosConfigSnapshotStore(string rootDirectory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.Combine(rootDirectory, "config");
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Writes a snapshot, replacing any snapshot for the same configuration.
    /// </summary>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="content">The content to store.</param>
    /// <param name="contentType">The content type.</param>
    /// <param name="encryptedDataKey">
    /// The encrypted data key that belongs with the content, or <see langword="null"/>.
    /// </param>
    /// <param name="ct">A token that cancels the write.</param>
    /// <returns>A task that completes once the snapshot is durable.</returns>
    public async Task SaveAsync(
        NacosConfigKey key,
        string content,
        string? contentType,
        string? encryptedDataKey = null,
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

        // The metadata occupies the first section so the snapshot stays human-readable and the
        // separator can never be confused with payload that happens to start with a newline. The
        // content type and key are base64-encoded because both are arbitrary strings, while the
        // metadata layout is line-oriented.
        string payload = string.Concat(
            EncodeMetadata(contentType, encryptedDataKey),
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
        catch (IOException ex)
        {
            TryDelete(temporary);
            NacosConfigLog.SnapshotWriteFailed(_logger, key.ToString(), ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(temporary);
            NacosConfigLog.SnapshotWriteFailed(_logger, key.ToString(), ex.Message);
        }
    }

    /// <summary>
    /// Reads a snapshot.
    /// </summary>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>
    /// The content, its type and the encrypted data key that belongs with it, or
    /// <see langword="null"/> when no usable snapshot exists.
    /// </returns>
    /// <remarks>
    /// A file written before the encrypted data key was stored has no version marker; it is read
    /// as before, with a <see langword="null"/> key. A malformed metadata section is treated as an
    /// absent snapshot rather than as content that might not belong to the key beside it.
    /// </remarks>
    public async Task<(string Content, string? ContentType, string? EncryptedDataKey)?> LoadAsync(
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

            string metadata = payload[..separatorIndex];
            string content = payload[(separatorIndex + MetadataSeparator.Length)..];

            if (!TryDecodeMetadata(metadata, out string? contentType, out string? encryptedDataKey))
            {
                return null;
            }

            return (content, contentType, encryptedDataKey);
        }
        catch (IOException ex)
        {
            NacosConfigLog.SnapshotReadFailed(_logger, key.ToString(), ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            NacosConfigLog.SnapshotReadFailed(_logger, key.ToString(), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Encodes the metadata section of a snapshot.
    /// </summary>
    /// <param name="contentType">The content type, or <see langword="null"/>.</param>
    /// <param name="encryptedDataKey">The encrypted data key, or <see langword="null"/>.</param>
    /// <returns>The metadata section, without the separator.</returns>
    private static string EncodeMetadata(string? contentType, string? encryptedDataKey) =>
        string.Concat(
            MetadataVersionMarker,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(contentType ?? string.Empty)),
            "\n",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(encryptedDataKey ?? string.Empty)));

    /// <summary>
    /// Decodes the metadata section of a snapshot.
    /// </summary>
    /// <param name="metadata">The metadata section, without the separator.</param>
    /// <param name="contentType">The decoded content type, or <see langword="null"/>.</param>
    /// <param name="encryptedDataKey">The decoded key, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the section could be decoded.</returns>
    private static bool TryDecodeMetadata(
        string metadata,
        out string? contentType,
        out string? encryptedDataKey)
    {
        contentType = null;
        encryptedDataKey = null;

        if (!metadata.StartsWith(MetadataVersionMarker, StringComparison.Ordinal))
        {
            // The original layout stored the content type verbatim as the whole section.
            contentType = metadata.Length == 0 ? null : metadata;
            return true;
        }

        string[] lines = metadata[MetadataVersionMarker.Length..].Split('\n');

        if (lines.Length != 2)
        {
            return false;
        }

        try
        {
            contentType = DecodeOptional(lines[0]);
            encryptedDataKey = DecodeOptional(lines[1]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Decodes one base64 metadata line.</summary>
    /// <param name="value">The encoded value, which may be empty.</param>
    /// <returns>The decoded value, or <see langword="null"/> when the line was empty.</returns>
    private static string? DecodeOptional(string value) =>
        value.Length == 0 ? null : Encoding.UTF8.GetString(Convert.FromBase64String(value));

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
