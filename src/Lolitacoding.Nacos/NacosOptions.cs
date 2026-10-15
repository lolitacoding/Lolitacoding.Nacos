using System.ComponentModel.DataAnnotations;

namespace Lolitacoding.Nacos;

/// <summary>
/// Configures how a Nacos client connects to and communicates with a Nacos server.
/// </summary>
/// <remarks>
/// The same options object is shared by the configuration centre and the naming service.
/// Per-subsystem tuning lives on <see cref="Config"/> and <see cref="Naming"/>.
/// </remarks>
public sealed class NacosOptions
{
    /// <summary>
    /// Gets or sets the Nacos server base addresses, for example
    /// <c>http://127.0.0.1:8848</c>.
    /// </summary>
    /// <remarks>
    /// Multiple addresses enable failover: the client rotates through them when a server
    /// becomes unreachable. The gRPC endpoint is derived from each address by adding
    /// <see cref="NacosConstants.GrpcPortOffset"/> to the port.
    /// </remarks>
    public IList<string> ServerAddresses { get; set; } = [];

    /// <summary>
    /// Gets or sets the namespace (tenant) all operations default to.
    /// </summary>
    /// <remarks>
    /// Both an empty string and <c>public</c> select the default public namespace.
    /// </remarks>
    public string Namespace { get; set; } = NacosConstants.DefaultNamespace;

    /// <summary>
    /// Gets or sets the user name used to authenticate against a secured Nacos server.
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// Gets or sets the password used to authenticate against a secured Nacos server.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the timeout applied to a single request.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the timeout applied when establishing a connection or completing the
    /// gRPC handshake.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the directory used to store configuration snapshots and service list
    /// failover files.
    /// </summary>
    /// <remarks>
    /// When the server is unreachable the client falls back to these files, which keeps a
    /// process bootable during a control-plane outage. Set to <see langword="null"/> to
    /// disable local caching entirely.
    /// </remarks>
    public string? LocalCacheDirectory { get; set; }

    /// <summary>
    /// Gets or sets the HTTP context path of the Nacos server.
    /// </summary>
    public string ContextPath { get; set; } = NacosConstants.DefaultContextPath;

    /// <summary>
    /// Gets or sets a value indicating whether the client tolerates the Nacos server being
    /// unavailable at startup.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/> (the default) the client fails fast during the first
    /// connection attempt instead of silently degrading to locally cached values.
    /// </remarks>
    public bool Optional { get; set; }

    /// <summary>
    /// Gets or sets the reconnect policy applied when a gRPC connection is lost.
    /// </summary>
    public NacosReconnectOptions Reconnect { get; set; } = new();

    /// <summary>
    /// Gets or sets the configuration centre specific options.
    /// </summary>
    public NacosConfigOptions Config { get; set; } = new();

    /// <summary>
    /// Gets or sets the naming (service discovery) specific options.
    /// </summary>
    public NacosNamingOptions Naming { get; set; } = new();

    /// <summary>
    /// Gets a value indicating whether credentials were supplied.
    /// </summary>
    internal bool HasCredentials =>
        !string.IsNullOrEmpty(UserName) && !string.IsNullOrEmpty(Password);
}

/// <summary>
/// Controls how the client re-establishes a lost gRPC connection.
/// </summary>
public sealed class NacosReconnectOptions
{
    /// <summary>
    /// Gets or sets the delay before the first reconnect attempt.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the upper bound of the exponential reconnect delay.
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the factor applied to the delay after each failed attempt.
    /// </summary>
    [Range(1.0, 10.0)]
    public double Multiplier { get; set; } = 2.0;

    /// <summary>
    /// Gets or sets the maximum number of consecutive reconnect attempts before the client
    /// gives up. A value of <c>0</c> retries indefinitely.
    /// </summary>
    public int MaxAttempts { get; set; }

    /// <summary>
    /// Gets or sets the maximum random jitter added to (or subtracted from) each delay, as
    /// a fraction of the computed delay.
    /// </summary>
    /// <remarks>
    /// Jitter spreads reconnect storms across a fleet after a shared server restarts.
    /// </remarks>
    [Range(0.0, 1.0)]
    public double JitterFactor { get; set; } = 0.2;
}

/// <summary>
/// Tunes the configuration centre client.
/// </summary>
public sealed class NacosConfigOptions
{
    /// <summary>
    /// Gets or sets the timeout for a configuration long-polling listener request.
    /// </summary>
    public TimeSpan LongPollingTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets a value indicating whether the MD5 of a configuration is verified
    /// against the value the server reports.
    /// </summary>
    /// <remarks>
    /// Disabling this trades a safety check for one less hash computation per read. It is
    /// enabled by default.
    /// </remarks>
    public bool VerifyMd5 { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether successfully read configurations are written
    /// to the local snapshot directory.
    /// </summary>
    public bool EnableLocalCache { get; set; } = true;

    /// <summary>
    /// Gets or sets the content type applied when publishing a configuration and no explicit
    /// type is provided.
    /// </summary>
    public string DefaultContentType { get; set; } = NacosConstants.DefaultConfigType;
}

/// <summary>
/// Tunes the naming (service discovery) client.
/// </summary>
public sealed class NacosNamingOptions
{
    /// <summary>
    /// Gets or sets the interval at which heartbeat requests are sent for ephemeral
    /// instances when the client is not using the gRPC connection keep-alive.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the cluster name attached to registered instances by default.
    /// </summary>
    public string DefaultCluster { get; set; } = NacosConstants.DefaultCluster;

    /// <summary>
    /// Gets or sets a value indicating whether instances are registered as ephemeral.
    /// </summary>
    /// <remarks>
    /// Ephemeral instances are removed by the server when their heartbeat stops, which is
    /// the correct behaviour for most containerised workloads.
    /// </remarks>
    public bool Ephemeral { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the local service list is cached for failover.
    /// </summary>
    public bool EnableLocalCache { get; set; } = true;
}
