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

    /// <summary>The default TLS port used by Nacos 3.x.</summary>
    public const int DefaultTlsPort = 9848;

    /// <summary>
    /// The offset added to the main port to obtain the gRPC port. Nacos reserves
    /// <c>main + 1000</c> for the client gRPC endpoint.
    /// </summary>
    public const int GrpcPortOffset = 1000;

    /// <summary>
    /// The offset added to the main port to obtain the gRPC TLS port (<c>main + 1001</c>).
    /// </summary>
    public const int GrpcTlsPortOffset = 1001;

    /// <summary>The default HTTP context path of a Nacos server.</summary>
    public const string DefaultContextPath = "/nacos";

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
