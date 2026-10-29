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
    private string _namespace = NacosConstants.DefaultNamespace;

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
    public string Namespace
    {
        get => _namespace;
        set
        {
            _namespace = value;

            // Remembered because the address server is only asked for a namespace the caller
            // actually chose; normalizing an empty value to "public" must not count as a choice.
            NamespaceWasConfigured = !string.IsNullOrEmpty(value);
        }
    }

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
    /// Gets or sets the Nacos address server that publishes the list of servers to connect to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An address server lets a deployment add or retire Nacos nodes without redeploying its
    /// clients: the client polls the endpoint, and the servers it dials follow the published list.
    /// Supply a host name or IP address here and the port through
    /// <see cref="EndpointPort"/>; the URL is built as
    /// <c>http://{Endpoint}:{EndpointPort}{contextPath}/{EndpointServerListName}</c>.
    /// </para>
    /// <para>
    /// When this is set it takes precedence over <see cref="ServerAddresses"/>, and the client
    /// starts with an empty list until the endpoint answers for the first time. This mirrors the
    /// official Java client's <c>endpoint</c> setting.
    /// </para>
    /// </remarks>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the port of the address server.
    /// </summary>
    /// <remarks>
    /// The address server is contacted over plain HTTP; this is the port it listens on, which is
    /// unrelated to the ports of the Nacos servers it publishes.
    /// </remarks>
    public int EndpointPort { get; set; } = 8080;

    /// <summary>
    /// Gets or sets the context path used to reach the address server.
    /// </summary>
    /// <remarks>
    /// Left unset, <see cref="ContextPath"/> is used, which is right when the address server and the
    /// Nacos servers are served under the same prefix.
    /// </remarks>
    public string? EndpointContextPath { get; set; }

    /// <summary>
    /// Gets or sets the name of the resource that lists the servers.
    /// </summary>
    /// <remarks>
    /// The default, <c>serverlist</c>, is what the official Java client requests.
    /// </remarks>
    public string EndpointServerListName { get; set; } = NacosConstants.DefaultServerListName;

    /// <summary>
    /// Gets or sets extra query parameters appended to the address server URL.
    /// </summary>
    /// <remarks>
    /// The value is appended verbatim after the parameters the client adds itself, so it must
    /// already be URL-encoded and must not start with <c>?</c> or <c>&amp;</c>.
    /// </remarks>
    public string? EndpointQueryParams { get; set; }

    /// <summary>
    /// Gets or sets how often the published server list is polled.
    /// </summary>
    /// <remarks>
    /// A change is applied at most once every 30 seconds even when this is shorter, so a flapping
    /// address server cannot make the client reconnect in a tight loop.
    /// </remarks>
    public TimeSpan EndpointRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

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
    public NacosAIOptions AI { get; set; } = new();

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

    /// <summary>Gets a value indicating whether an address server is configured.</summary>
    internal bool HasEndpoint => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>
    /// Gets a value indicating whether the namespace was supplied rather than defaulted.
    /// </summary>
    /// <remarks>
    /// The address server is only asked for a namespace when the caller chose one, matching the
    /// official client, which leaves the parameter off for the public default.
    /// </remarks>
    internal bool NamespaceWasConfigured { get; private set; }

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
        if (HasEndpoint)
        {
            ValidateEndpoint();
        }
        else if (ServerAddresses.Count == 0)
        {
            throw new NacosConfigurationException(
                "Configure NacosOptions.ServerAddresses, or an address server through NacosOptions.Endpoint, so the client knows which Nacos server to talk to.");
        }
        else
        {
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
        AI.Validate();
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
            // than rejected. The field is written directly so the value does not look like a
            // namespace the caller chose.
            _namespace = NacosConstants.DefaultNamespace;
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

    /// <summary>
    /// Rejects an address server configuration the client could not turn into a URL.
    /// </summary>
    /// <remarks>
    /// The endpoint is a host rather than a URL: the scheme, port and path are supplied separately,
    /// which keeps a typo in one setting from silently pointing the client at another resource.
    /// </remarks>
    private void ValidateEndpoint()
    {
        string endpoint = Endpoint!;

        if (!string.Equals(endpoint, endpoint.Trim(), StringComparison.Ordinal) ||
            endpoint.Any(char.IsWhiteSpace))
        {
            throw new NacosConfigurationException(
                "NacosOptions.Endpoint must not contain whitespace.");
        }

        if (endpoint.Contains("://", StringComparison.Ordinal) ||
            endpoint.Contains('/') ||
            endpoint.Contains('?') ||
            endpoint.Contains('#'))
        {
            throw new NacosConfigurationException(
                "NacosOptions.Endpoint must be a host name or IP address such as 'address-server.example.com'; set the port on NacosOptions.EndpointPort and the path on NacosOptions.EndpointContextPath.");
        }

        if (EndpointPort is < 1 or > 65535)
        {
            throw new NacosConfigurationException(
                "NacosOptions.EndpointPort must be between 1 and 65535.");
        }

        EndpointServerListName = EndpointServerListName.Trim().TrimStart('/');

        if (EndpointServerListName.Length == 0 ||
            EndpointServerListName.Contains('?') ||
            EndpointServerListName.Contains('#'))
        {
            throw new NacosConfigurationException(
                "NacosOptions.EndpointServerListName must be a resource name such as 'serverlist'.");
        }

        if (EndpointRefreshInterval <= TimeSpan.Zero)
        {
            throw new NacosConfigurationException(
                "NacosOptions.EndpointRefreshInterval must be greater than zero.");
        }

        if (EndpointQueryParams is not null)
        {
            // Accept the value with or without the separator, since both read naturally in
            // configuration, but store it in the one form the URL builder appends.
            EndpointQueryParams = EndpointQueryParams.Trim().TrimStart('?').TrimStart('&');

            if (EndpointQueryParams.Length == 0)
            {
                EndpointQueryParams = null;
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
public sealed class NacosAIOptions
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
    /// Gets or sets how an agent watch is kept current.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A watch has two server-side push contracts. The gRPC one installs a watch on the AI registry
    /// connection and is what Nacos 3.3 prefers, while the HTTP one sends the client's whole watch set
    /// as a batch long poll. The official client routes between them, and falls back to periodic
    /// discovery when neither is available; <see cref="NacosAgentWatchTransport.Auto"/> is that
    /// routing, and the other values pin one transport for deployments that need to.
    /// </para>
    /// <para>
    /// Only the watch is affected. Reading, publishing and searching an agent stay on the AI registry
    /// connection whatever this value says, because the HTTP batch is a watch binding rather than a
    /// second implementation of discovery.
    /// </para>
    /// </remarks>
    public NacosAgentWatchTransport AgentWatchTransport { get; set; } = NacosAgentWatchTransport.Auto;

    /// <summary>
    /// Validates the AI options.
    /// </summary>
    /// <exception cref="NacosConfigurationException">A value is invalid.</exception>
    internal void Validate()
    {
        if (CacheRefreshInterval <= TimeSpan.Zero)
        {
            throw new NacosConfigurationException(
                "NacosAIOptions.CacheRefreshInterval must be greater than zero.");
        }

        if (!Enum.IsDefined(AgentWatchTransport))
        {
            throw new NacosConfigurationException(
                "NacosAIOptions.AgentWatchTransport is not a defined value.");
        }
    }
}

/// <summary>
/// Selects the server push contract an agent watch is installed on.
/// </summary>
public enum NacosAgentWatchTransport
{
    /// <summary>
    /// Uses the gRPC watch when the server advertises it, otherwise the HTTP batch long poll, and
    /// periodic discovery when neither works.
    /// </summary>
    Auto,

    /// <summary>
    /// Requires the gRPC watch and fails a watch when the server does not implement it.
    /// </summary>
    Grpc,

    /// <summary>
    /// Uses the HTTP batch long poll without trying the gRPC watch.
    /// </summary>
    Http,

    /// <summary>
    /// Re-reads the agent on <see cref="NacosAIOptions.CacheRefreshInterval"/> without asking the
    /// server for a push.
    /// </summary>
    /// <remarks>
    /// This is the compatibility fallback the official client keeps internal. It is exposed because a
    /// deployment behind a proxy that buffers long polls has no other way to keep a watch alive.
    /// </remarks>
    Polling,
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
