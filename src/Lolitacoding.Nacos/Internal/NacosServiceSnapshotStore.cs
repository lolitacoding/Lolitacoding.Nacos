using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lolitacoding.Nacos.Naming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// Persists a service's instance list on disk so a process that restarts during a control-plane
/// outage can still discover that service.
/// </summary>
/// <remarks>
/// <para>
/// The in-memory cache in the naming client survives a reconnect but not a restart, which is
/// exactly when failover matters most: a rolling restart during a Nacos outage must not turn into
/// an application outage. This store is what the official client calls a local failover file.
/// </para>
/// <para>
/// Writes are atomic so a process killed mid-write leaves either the previous snapshot or the new
/// one, never a truncated file that would poison the next boot. File names are a hash of the
/// service coordinates so arbitrary group and service names cannot collide or escape the
/// directory.
/// </para>
/// </remarks>
internal sealed class NacosServiceSnapshotStore
{
    /// <summary>The extension applied to service snapshot files.</summary>
    private const string FileExtension = ".service";

    private readonly string _rootDirectory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServiceSnapshotStore"/> class.
    /// </summary>
    /// <param name="rootDirectory">The directory that holds the snapshots.</param>
    /// <param name="logger">
    /// The logger that reports a snapshot that could not be written or read, or
    /// <see langword="null"/> when the caller has none.
    /// </param>
    /// <remarks>
    /// The store reports failures itself rather than throwing, for the same reason as the
    /// configuration store: every caller treats caching as an optimisation that must never fail the
    /// lookup it decorates.
    /// </remarks>
    public NacosServiceSnapshotStore(string rootDirectory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.Combine(rootDirectory, "naming");
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Writes a service's instance list, replacing any snapshot for the same service.
    /// </summary>
    /// <param name="key">The service coordinates.</param>
    /// <param name="info">The service state to store.</param>
    /// <param name="ct">A token that cancels the write.</param>
    /// <returns>A task that completes once the snapshot is durable.</returns>
    public async Task SaveAsync(
        NacosServiceKey key,
        NacosServiceInfo info,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(info);

        string path = GetPath(key);

        if (!IsPathSafe(path))
        {
            // Refusing to write is the safe response: caching is best-effort, so skipping it is
            // always preferable to writing outside the configured directory.
            return;
        }

        var snapshot = new NacosServiceSnapshot
        {
            Checksum = info.Checksum,
            Clusters = info.Clusters,
            Instances = [.. info.Instances.Select(ToSnapshotInstance)],
        };

        string payload = JsonSerializer.Serialize(
            snapshot, NacosServiceSnapshotJsonContext.Default.NacosServiceSnapshot);

        string temporary = string.Concat(path, ".tmp");

        try
        {
            // Directory creation is inside the guard on purpose. Caching is an optimisation, so a
            // read-only or permission-restricted cache location must degrade to "no snapshot"
            // rather than failing the lookup that already succeeded.
            Directory.CreateDirectory(_rootDirectory);

            await File.WriteAllTextAsync(temporary, payload, Encoding.UTF8, ct).ConfigureAwait(false);

            // The move is what makes the write atomic; a partially written temporary file is
            // never visible under the real name.
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException ex)
        {
            TryDelete(temporary);
            NacosNamingLog.SnapshotWriteFailed(_logger, key.ToString(), ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(temporary);
            NacosNamingLog.SnapshotWriteFailed(_logger, key.ToString(), ex.Message);
        }
    }

    /// <summary>
    /// Reads a service's last known instance list.
    /// </summary>
    /// <param name="key">The service coordinates.</param>
    /// <param name="ct">A token that cancels the read.</param>
    /// <returns>
    /// The service state, flagged as coming from a local cache, or <see langword="null"/> when no
    /// usable snapshot exists.
    /// </returns>
    /// <remarks>
    /// A snapshot that cannot be parsed is treated as absent rather than as an error: a corrupt
    /// file must fall through to the server, not fail the lookup.
    /// </remarks>
    public async Task<NacosServiceInfo?> LoadAsync(NacosServiceKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        string path = GetPath(key);

        string payload;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            payload = await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            NacosNamingLog.SnapshotReadFailed(_logger, key.ToString(), ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            NacosNamingLog.SnapshotReadFailed(_logger, key.ToString(), ex.Message);
            return null;
        }

        NacosServiceSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize(
                payload, NacosServiceSnapshotJsonContext.Default.NacosServiceSnapshot);
        }
        catch (JsonException)
        {
            // Written by an older version, or corrupted on disk. Falling through to the server is
            // always correct.
            return null;
        }

        if (snapshot is null)
        {
            return null;
        }

        try
        {
            return new NacosServiceInfo(
                key,
                [.. snapshot.Instances.Select(ToInstance)],
                snapshot.Clusters,
                snapshot.Checksum,
                fromLocalCache: true);
        }
        catch (ArgumentException)
        {
            // A stored instance that no longer satisfies the public invariants (for example a
            // tampered port) invalidates the whole snapshot.
            return null;
        }
    }

    /// <summary>
    /// Removes the snapshot for a service.
    /// </summary>
    /// <param name="key">The service coordinates.</param>
    /// <remarks>
    /// Called when a service is known to have no instances, so a later outage serves an empty list
    /// rather than a list of instances that no longer exist.
    /// </remarks>
    public void Remove(NacosServiceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        TryDelete(GetPath(key));
    }

    /// <summary>
    /// Converts a public instance into its snapshot form.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <returns>The snapshot instance.</returns>
    private static NacosServiceSnapshotInstance ToSnapshotInstance(NacosInstance instance) => new()
    {
        Ip = instance.IpAddress,
        Port = instance.Port,
        Weight = instance.Weight,
        ClusterName = instance.ClusterName,
        Healthy = instance.Healthy,
        Enabled = instance.Enabled,
        Ephemeral = instance.Ephemeral,
        InstanceId = instance.InstanceId,
        Metadata = instance.Metadata is null
            ? null
            : new Dictionary<string, string>(instance.Metadata, StringComparer.Ordinal),
    };

    /// <summary>
    /// Converts a snapshot instance back into the public model.
    /// </summary>
    /// <param name="instance">The snapshot instance.</param>
    /// <returns>The public instance.</returns>
    private static NacosInstance ToInstance(NacosServiceSnapshotInstance instance) =>
        new(
            instance.Ip,
            instance.Port,
            instance.Weight,
            instance.ClusterName,
            instance.Healthy,
            instance.Enabled,
            instance.Ephemeral,
            instance.Metadata)
        {
            InstanceId = instance.InstanceId,
        };

    /// <summary>
    /// Builds the snapshot path for a service.
    /// </summary>
    /// <param name="key">The service coordinates.</param>
    /// <returns>The absolute snapshot path.</returns>
    private string GetPath(NacosServiceKey key)
    {
        // The resolved key's string form carries namespace, group and service name; hashing it
        // keeps one service's storage from escaping into another's.
        string digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())));

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
