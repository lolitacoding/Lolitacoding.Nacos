using System.Text.Json.Serialization;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// The on-disk representation of a service's last known instance list.
/// </summary>
/// <remarks>
/// This is deliberately a dedicated shape rather than the wire <c>ServiceInfo</c> message: the
/// snapshot is local state that only this client reads, so it does not have to track the server's
/// protocol and cannot be broken by a server-side field change.
/// </remarks>
internal sealed class NacosServiceSnapshot
{
    /// <summary>Gets or sets the checksum the server reported for the list.</summary>
    public string? Checksum { get; set; }

    /// <summary>Gets or sets the cluster list the server reported.</summary>
    public string? Clusters { get; set; }

    /// <summary>Gets or sets the instances.</summary>
    public List<NacosServiceSnapshotInstance> Instances { get; set; } = [];
}

/// <summary>
/// One instance inside a <see cref="NacosServiceSnapshot"/>.
/// </summary>
internal sealed class NacosServiceSnapshotInstance
{
    /// <summary>Gets or sets the instance address.</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>Gets or sets the instance port.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the load-balancing weight.</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>Gets or sets the cluster the instance belongs to.</summary>
    public string? ClusterName { get; set; }

    /// <summary>Gets or sets a value indicating whether the instance was healthy.</summary>
    public bool Healthy { get; set; }

    /// <summary>Gets or sets a value indicating whether the instance was enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the instance was ephemeral.</summary>
    public bool Ephemeral { get; set; }

    /// <summary>Gets or sets the server-assigned instance identifier.</summary>
    public string? InstanceId { get; set; }

    /// <summary>Gets or sets the instance metadata.</summary>
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Source-generated JSON contracts for the naming failover snapshot.
/// </summary>
/// <remarks>
/// A separate context from <c>NacosJsonContext</c> keeps the local file format independent of the
/// server protocol; source generation means the snapshot reader stays trim- and Native AOT-safe.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(NacosServiceSnapshot))]
internal sealed partial class NacosServiceSnapshotJsonContext : JsonSerializerContext
{
}
