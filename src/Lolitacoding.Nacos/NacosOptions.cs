using System.ComponentModel.DataAnnotations;
using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Internal;

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
    /// <remarks>
    /// A request that arrives while the client is reconnecting has no connection to send on, and
    /// this is the budget it waits for one within before it is failed and the caller falls back to
    /// its local copy. See <c>docs/faq.md</c>.
    /// </remarks>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the timeout applied when establishing a connection or completing the
    /// gRPC handshake.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets how long a connection may sit idle before the client verifies it with a
    /// health check.
    /// </summary>
    /// <remarks>
    /// Nacos closes connections that stop responding, so an idle connection is probed rather
    /// than assumed healthy. This mirrors the official client's keep-alive interval.
    /// </remarks>
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the timeout applied to a single health check request.
    /// </summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets the directory used to store configuration snapshots, service list failover files
    /// and AI resource snapshots.
    /// </summary>
    /// <remarks>
    /// When the server is unreachable the client falls back to these files, which keeps a process
    /// bootable during a control-plane outage. Left unset, the per-user application data directory is
    /// used; each module's own <c>EnableLocalCache</c> switch decides whether it caches at all.
    /// </remarks>
    public string? LocalCacheDirectory { get; set; }

    /// <summary>
    /// Gets or sets the HTTP context path of the Nacos server.
    /// </summary>
    public string ContextPath { get; set; } = NacosConstants.DefaultContextPath;

    /// <summary>
    /// Gets or sets the application name reported to the Nacos server.
    /// </summary>
    /// <remarks>
    /// The server records this as the connection's application label and surfaces it in the
    /// console, so it is worth setting in any environment where more than one service connects.
    /// </remarks>
    public string? AppName { get; set; }

    /// <summary>
    /// Gets or sets the local IP address reported to the Nacos server.
    /// </summary>
    /// <remarks>
    /// Nacos treats this purely as a hint: the server derives the authoritative client address
    /// from the connection, so a wrong value here cannot be used to impersonate another host.
    /// When left unset the client reports no address at all rather than guessing one.
    /// </remarks>
    public string? LocalClientIp { get; set; }

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
    /// Gets or sets the AI registry specific options.
    /// </summary>
    public NacosAiOptions Ai { get; set; } = new();

    /// <summary>
    /// Gets or sets the TLS settings applied to servers configured with the <c>https</c> scheme.
    /// </summary>
    /// <remarks>
    /// TLS is enabled by using <c>https://</c> in <see cref="ServerAddresses"/>; the settings here
    /// refine how the handshake is performed. See <see cref="NacosTlsOptions"/>.
    /// </remarks>
    public NacosTlsOptions Tls { get; set; } = new();

    /// <summary>
    /// Gets a value indicating whether credentials were supplied.
    /// </summary>
    internal bool HasCredentials =>
        !string.IsNullOrEmpty(UserName) && !string.IsNullOrEmpty(Password);

    /// <summary>
    /// Validates the options and normalizes the values that have aliases.
    /// </summary>
    /// <exception cref="NacosConfigurationException">
    /// The options are not usable, for example because no server address was supplied.
    /// </exception>
    /// <remarks>
    /// Validation runs once when the clients are built rather than on every request. A
    /// misconfigured client is a programming error that would otherwise surface as a confusing
    /// connection failure long after startup, so it fails immediately and names the offending
    /// setting.
    /// </remarks>
    internal void Validate()
    {
        if (ServerAddresses.Count == 0)
        {
            throw new NacosConfigurationException(
                "At least one Nacos server address must be configured on NacosOptions.ServerAddresses.");
        }

        foreach (string address in ServerAddresses)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new NacosConfigurationException(
                    "NacosOptions.ServerAddresses contains an empty entry; every entry must be a host or URL such as 'http://127.0.0.1:8848'.");
            }

            // Parsing here turns a malformed address into a startup failure instead of a
            // per-request one.
            NacosServerAddress.Parse(address);
        }

        if (!string.IsNullOrEmpty(UserName) && string.IsNullOrEmpty(Password))
        {
            throw new NacosConfigurationException(
                "NacosOptions.UserName is set but NacosOptions.Password is empty; both are required to authenticate.");
        }

        if (!string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(UserName))
        {
            throw new NacosConfigurationException(
                "NacosOptions.Password is set but NacosOptions.UserName is empty; both are required to authenticate.");
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new NacosConfigurationException(
                "NacosOptions.RequestTimeout must be greater than zero.");
        }

        if (ConnectionTimeout <= TimeSpan.Zero)
        {
            throw new NacosConfigurationException(
                "NacosOptions.ConnectionTimeout must be greater than zero.");
        }

        ValidateNamespace();
        Config.Validate();
        Naming.Validate();
        Ai.Validate();
        Tls.Validate();
        Reconnect.Validate();
    }

    /// <summary>
    /// Rejects a namespace id that cannot be used as one.
    /// </summary>
    /// <remarks>
    /// A namespace is carried in gRPC metadata, so a value containing control characters or
    /// non-ASCII text would be rejected by the transport at call time rather than at startup.
    /// </remarks>
    private void ValidateNamespace()
    {
        if (string.IsNullOrEmpty(Namespace))
        {
            // Nacos treats an empty namespace as the public default, so it is normalized rather
            // than rejected.
            Namespace = NacosConstants.DefaultNamespace;
            return;
        }

        if (!string.Equals(Namespace, Namespace.Trim(), StringComparison.Ordinal))
        {
            Namespace = Namespace.Trim();
        }

        foreach (char character in Namespace)
        {
            if (char.IsControl(character) || character > 127)
            {
                throw new NacosConfigurationException(
                    string.Concat(
                        "NacosOptions.Namespace '", Namespace,
                        "' contains characters that cannot be sent as request metadata; use an ASCII namespace id."));
            }
        }
    }
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

    /// <summary>
    /// Validates the reconnect policy.
    /// </summary>
    /// <exception cref="NacosConfigurationException">A value is out of range.</exception>
    internal void Validate()
    {
        if (InitialDelay < TimeSpan.Zero)
        {
            throw new NacosConfigurationException(
                "NacosReconnectOptions.InitialDelay cannot be negative.");
        }

        if (MaxDelay < InitialDelay)
        {
            throw new NacosConfigurationException(
                "NacosReconnectOptions.MaxDelay must be greater than or equal to InitialDelay.");
        }

        if (Multiplier < 1.0)
        {
            // A multiplier below one would shrink the delay on every attempt, turning a reconnect
            // into a hot retry loop against a server that is already struggling.
            throw new NacosConfigurationException(
                "NacosReconnectOptions.Multiplier must be at least 1.0 so that retry delays do not shrink.");
        }

        if (JitterFactor is < 0.0 or > 1.0)
        {
            throw new NacosConfigurationException(
                "NacosReconnectOptions.JitterFactor must be between 0.0 and 1.0.");
        }

        if (MaxAttempts < 0)
        {
            throw new NacosConfigurationException(
                "NacosReconnectOptions.MaxAttempts cannot be negative; use 0 to retry indefinitely.");
        }
    }
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

    /// <summary>
    /// Gets or sets how often a fuzzy watch re-sends its known set to the server.
    /// </summary>
    /// <remarks>
    /// The server pushes every change to the matched set, but a push that was lost while the
    /// connection looked healthy would otherwise leave the client's view wrong until the next
    /// reconnect. The periodic full synchronisation is what bounds that window. The official Java
    /// client uses three minutes (<c>FUZZY_LISTEN_ALL_SYNC_INTERNAL</c>).
    /// </remarks>
    public TimeSpan FuzzyWatchResyncInterval { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Gets the encryption providers used for data ids that start with <c>cipher-</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When this list is not empty the client adds a <see cref="NacosConfigEncryptionFilter"/> to
    /// its filter chain automatically, so an application only has to supply the algorithm
    /// implementation. The filter selects a provider by the algorithm named in the data id, for
    /// example <c>cipher-AES-application.json</c> selects the provider whose
    /// <see cref="INacosConfigEncryptionProvider.AlgorithmName"/> is <c>AES</c>.
    /// </para>
    /// <para>
    /// The official Java client discovers the same implementations through a service loader and
    /// enables encryption without any configuration. There is no equivalent for arbitrary .NET
    /// assemblies, so the list is explicit; the first provider with a matching name wins.
    /// </para>
    /// </remarks>
    public IList<INacosConfigEncryptionProvider> EncryptionProviders { get; } = [];

    /// <summary>
    /// Validates the configuration options.
    /// </summary>
    /// <exception cref="NacosConfigurationException">A value is invalid.</exception>
    internal void Validate()
    {
        if (LongPollingTimeout <= TimeSpan.Zero)
        {
            throw new NacosConfigurationException(
                "NacosConfigOptions.LongPollingTimeout must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(DefaultContentType))
        {
            throw new NacosConfigurationException(
                "NacosConfigOptions.DefaultContentType cannot be empty.");
        }

        if (FuzzyWatchResyncInterval <= TimeSpan.Zero)
        {
            throw new NacosConfigurationException(
                "NacosConfigOptions.FuzzyWatchResyncInterval must be greater than zero.");
        }

        foreach (INacosConfigEncryptionProvider? provider in EncryptionProviders)
        {
            if (provider is null)
            {
                throw new NacosConfigurationException(
                    "NacosConfigOptions.EncryptionProviders contains a null entry.");
            }

            if (string.IsNullOrWhiteSpace(provider.AlgorithmName))
            {
                throw new NacosConfigurationException(
                    "Every entry of NacosConfigOptions.EncryptionProviders must have a non-empty AlgorithmName.");
            }
        }
    }
}

/// <summary>
/// Tunes the AI registry client.
/// </summary>
/// <remarks>
/// A resource a process subscribes to is cached locally and kept current, so that a change is reported
/// without the caller having to poll and so that a subscription outlives a control-plane outage. A
/// resource the process only reads is served straight from the server.
/// </remarks>
public sealed class NacosAiOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether AI resources are cached locally for failover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The in-memory copy a subscription keeps is always on; this adds a copy on disk, under
    /// <see cref="NacosOptions.LocalCacheDirectory"/>, so a subscription can start from what it last
    /// read even after the process was restarted. Without it a restart during a control-plane outage
    /// comes up with an empty cache and nothing to serve.
    /// </para>
    /// <para>
    /// The disk copy is consulted only when a read cannot reach the server, and a resource the
    /// server reports as no longer served has its copy removed, so a withdrawal is never undone by
    /// an outage that follows it.
    /// </para>
    /// </remarks>
    public bool EnableLocalCache { get; set; } = true;

    /// <summary>
    /// Gets or sets the interval at which a subscribed AI resource is refreshed.
    /// </summary>
    /// <remarks>
    /// Nacos has no AI change push, so this interval is the only thing that makes a subscription fire:
    /// the client re-reads the server and reports the detail again when it differs from the copy that
    /// was cached. The official client's <c>nacosAiMcpServerCacheUpdateInterval</c> and its siblings
    /// default to ten seconds (<c>AiConstants.DEFAULT_AI_CACHE_UPDATE_INTERVAL</c>).
    /// </remarks>
    public TimeSpan CacheRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Validates the AI options.
    /// </summary>
    /// <exception cref="NacosConfigurationException">A value is invalid.</exception>
    internal void Validate()
    {
        if (CacheRefreshInterval <= TimeSpan.Zero)
        {
            throw new NacosConfigurationException(
                "NacosAiOptions.CacheRefreshInterval must be greater than zero.");
        }
    }
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

    /// <summary>
    /// Gets or sets how often a naming fuzzy watch re-sends its known set to the server.
    /// </summary>
    /// <remarks>
    /// The server pushes every change to the matched set, but a push that was lost while the
    /// connection looked healthy would otherwise leave the client's view wrong until the next
    /// reconnect. The periodic full synchronisation is what bounds that window. The official Java
    /// client uses three minutes (<c>FUZZY_LISTEN_ALL_SYNC_INTERNAL</c>), the same value as the
    /// configuration watch.
    /// </remarks>
    public TimeSpan FuzzyWatchResyncInterval { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Validates the naming options.
    /// </summary>
    /// <exception cref="NacosConfigurationException">A value is invalid.</exception>
    internal void Validate()
    {
        if (HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new NacosConfigurationException(
                "NacosNamingOptions.HeartbeatInterval must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(DefaultCluster))
        {
            throw new NacosConfigurationException(
                "NacosNamingOptions.DefaultCluster cannot be empty.");
        }

        if (FuzzyWatchResyncInterval <= TimeSpan.Zero)
        {
            throw new NacosConfigurationException(
                "NacosNamingOptions.FuzzyWatchResyncInterval must be greater than zero.");
        }
    }
}
