namespace Lolitacoding.Nacos;

/// <summary>
/// Well-known Nacos protocol constants shared by the Config and Naming clients.
/// </summary>
/// <remarks>
/// Values are taken from the official server and Java client sources; see
/// <c>docs/protocol-notes.md</c> for the exact upstream references.
/// </remarks>
public static class NacosConstants
{
    /// <summary>The default Nacos server main (HTTP / gRPC entry) port.</summary>
    public const int DefaultPort = 8848;

    /// <summary>
    /// The offset added to the main port to obtain the SDK gRPC port. Nacos reserves
    /// <c>main + 1000</c> for the client gRPC endpoint.
    /// </summary>
    /// <remarks>
    /// The same port carries both plaintext and TLS. A Nacos server negotiates TLS and plaintext
    /// on one SDK gRPC port (port unification) instead of reserving a second one, so an
    /// <c>https://</c> server address still dials <c>main + 1000</c>. The endpoint one port above
    /// this (<c>main + 1001</c>) is the cluster-to-cluster gRPC port, which an SDK client never
    /// dials.
    /// </remarks>
    public const int GrpcPortOffset = 1000;

    /// <summary>The default HTTP context path of a Nacos server.</summary>
    public const string DefaultContextPath = "/nacos";

    /// <summary>
    /// The default resource name an address server answers with its list of Nacos servers.
    /// </summary>
    /// <remarks>
    /// This is the value the official Java client requests when no custom cluster name is set.
    /// </remarks>
    public const string DefaultServerListName = "serverlist";

    /// <summary>The default configuration group name.</summary>
    public const string DefaultGroup = "DEFAULT_GROUP";

    /// <summary>
    /// The default namespace identifier. Nacos treats an empty string and the literal
    /// <c>public</c> as the same public namespace.
    /// </summary>
    public const string DefaultNamespace = "public";

    /// <summary>The default cluster name used when registering an instance.</summary>
    public const string DefaultCluster = "DEFAULT";

    /// <summary>The content type assumed for configuration values when none is supplied.</summary>
    public const string DefaultConfigType = "text";

    /// <summary>The charset used for configuration payloads.</summary>
    public const string Charset = "UTF-8";
}
