namespace Lolitacoding.Nacos.Protocol;

/// <summary>
/// Names of the gRPC metadata headers the Nacos client and server exchange.
/// </summary>
/// <remarks>
/// Header names are case-insensitive on the wire because Nacos stores them in a
/// case-insensitive map; comparisons here use <see cref="StringComparison.OrdinalIgnoreCase"/>.
/// </remarks>
internal static class ProtocolHeaders
{
    /// <summary>The character set of a configuration payload.</summary>
    public const string Charset = "charset";

    /// <summary>The client application name.</summary>
    public const string ClientAppName = "Client-AppName";

    /// <summary>The client request timestamp.</summary>
    public const string ClientRequestTs = "Client-RequestTS";

    /// <summary>The client request token.</summary>
    public const string ClientRequestToken = "Client-RequestToken";

    /// <summary>Marks a configuration query as driven by a change notification.</summary>
    public const string Notify = "notify";

    /// <summary>The access token presented on authenticated requests.</summary>
    public const string AccessToken = "accessToken";

    /// <summary>The application name label carried during connection setup.</summary>
    public const string AppName = "AppName";

    /// <summary>The client version label.</summary>
    public const string ClientVersion = "ClientVersion";

    /// <summary>The namespace identifier.</summary>
    public const string NamespaceId = "namespaceId";
}
