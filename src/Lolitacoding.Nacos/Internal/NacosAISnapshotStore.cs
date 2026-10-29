using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lolitacoding.Nacos.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// Persists AI registry resources on disk so a subscription survives a restart while the control
/// plane is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// An AI subscription already keeps its resource in memory and keeps it across a control-plane
/// outage, so what is left for a disk copy to add is surviving the process itself. An agent host
/// that restarts into an outage would otherwise come up with nothing to serve, which is the same
/// problem configuration and naming solve the same way; the official client calls this a local
/// snapshot.
/// </para>
/// <para>
/// A snapshot is only ever written after the server answered, and it is read only when a read could
/// not reach the server at all. It is therefore never preferred over the server, only over an
/// outage, and a resource the server reports as gone has its snapshot removed rather than kept.
/// </para>
/// <para>
/// Writes are atomic — the payload is written beside the target and then moved into place — so a
/// process killed mid-write leaves either the previous snapshot or the new one, never a truncated
/// file that would then fail to deserialize on the next boot.
/// </para>
/// <para>
/// The payload is the resource's JSON, serialized through the same source-generated contracts the
/// protocol uses. A file that does not deserialize is treated as absent, which is why the format
/// needs no version marker: an unusable snapshot and a missing one call for the same behaviour.
/// </para>
/// </remarks>
internal sealed class NacosAISnapshotStore
{
    /// <summary>The kind of a prompt snapshot.</summary>
    internal const string PromptKind = "prompt";

    /// <summary>The kind of a skill snapshot.</summary>
    internal const string SkillKind = "skill";

    /// <summary>The kind of an agent specification snapshot.</summary>
    internal const string AgentSpecKind = "agentspec";

    /// <summary>The kind of an agent card snapshot.</summary>
    internal const string AgentCardKind = "agentcard";

    /// <summary>The kind of an MCP server snapshot.</summary>
    internal const string McpKind = "mcp";

    /// <summary>The extension applied to snapshot files.</summary>
    private const string FileExtension = ".ai";

    /// <summary>The directory under the cache root that holds the AI snapshots.</summary>
    private const string DirectoryName = "ai";

    private readonly string _rootDirectory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAISnapshotStore"/> class.
    /// </summary>
    /// <param name="rootDirectory">The directory that holds the snapshots.</param>
    /// <param name="logger">
    /// The logger that reports a snapshot that could not be written or read, or
    /// <see langword="null"/> when the caller has none.
    /// </param>
    /// <remarks>
    /// The store reports failures itself rather than throwing, for the same reason as the
    /// configuration and naming stores: caching must never fail the read it decorates.
    /// </remarks>
    public NacosAISnapshotStore(string rootDirectory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.Combine(rootDirectory, DirectoryName);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Writes a snapshot, replacing any snapshot for the same resource and selector.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="kind">The resource kind, which keeps two resources of the same name apart.</param>
    /// <param name="key">The selector the resource was read for.</param>
    /// <param name="value">The resource to store.</param>
    /// <param name="typeInfo">The contract to serialize it with.</param>
    /// <param name="ct">A token that cancels the write.</param>
    /// <returns>A task that completes once the snapshot is durable.</returns>
    public async Task SaveAsync<T>(
        string kind,
        string key,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(typeInfo);

        string path = GetPath(kind, key);

        if (!IsPathSafe(path))
        {
            // Refusing to write is the safe response: caching is best-effort, so skipping it is
            // always preferable to writing outside the configured directory.
            return;
        }

        string payload = JsonSerializer.Serialize(value, typeInfo);
        string temporary = string.Concat(path, ".tmp");

        try
        {
            // Directory creation is inside the guard on purpose. Caching is an optimisation, so a
            // read-only or permission-restricted cache location must degrade to "no snapshot"
            // rather than failing a read that already succeeded.
            Directory.CreateDirectory(_rootDirectory);

            await File.WriteAllTextAsync(temporary, payload, Encoding.UTF8, ct).ConfigureAwait(false);

            // The move is what makes the write atomic; a partially written temporary file is never
            // visible under the real name.
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException ex)
        {
            TryDelete(temporary);
            NacosAILog.SnapshotWriteFailed(_logger, kind, key, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(temporary);
            NacosAILog.SnapshotWriteFailed(_logger, kind, key, ex.Message);
        }
    }

    /// <summary>
    /// Reads a snapshot.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="kind">The resource kind.</param>
    /// <param name="key">The selector the resource was read for.</param>
    /// <param name="typeInfo">The contract to deserialize it with.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>The resource, or <see langword="null"/> when no usable snapshot exists.</returns>
    public async Task<T?> LoadAsync<T>(
        string kind,
        string key,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(typeInfo);

        string path = GetPath(kind, key);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string payload = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);

            return JsonSerializer.Deserialize(payload, typeInfo);
        }
        catch (IOException ex)
        {
            NacosAILog.SnapshotReadFailed(_logger, kind, key, ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            NacosAILog.SnapshotReadFailed(_logger, kind, key, ex.Message);
            return null;
        }
        catch (JsonException)
        {
            // A snapshot that cannot be read back is no better than one that was never written, and
            // the caller falls through to the server either way.
            return null;
        }
    }

    /// <summary>
    /// Removes the snapshot for a resource.
    /// </summary>
    /// <param name="kind">The resource kind.</param>
    /// <param name="key">The selector the resource was read for.</param>
    /// <remarks>
    /// Called when the server reports the resource as no longer served. Keeping it on disk would
    /// resurrect it during the next outage, which is exactly the wrong direction for a failover
    /// source to fail in.
    /// </remarks>
    public void Remove(string kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        TryDelete(GetPath(kind, key));
    }

    /// <summary>
    /// Builds the snapshot path for one resource and selector.
    /// </summary>
    /// <param name="kind">The resource kind.</param>
    /// <param name="key">The selector the resource was read for.</param>
    /// <returns>The absolute snapshot path.</returns>
    private string GetPath(string kind, string key)
    {
        string identity = string.Concat(kind, "\u0000", key);

        // SHA-256 is used rather than the protocol's MD5 because this digest is purely a local file
        // name: nothing outside this process ever sees it, so there is no compatibility reason to
        // prefer the weaker algorithm.
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
    /// The path is derived from a hash, so it cannot escape today. The check exists so that a future
    /// change to the naming scheme cannot silently turn caller-supplied names into a path traversal.
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
