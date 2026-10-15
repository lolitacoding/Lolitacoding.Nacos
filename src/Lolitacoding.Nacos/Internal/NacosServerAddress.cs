using System.Globalization;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// A parsed, normalized Nacos server endpoint together with the derived gRPC endpoint.
/// </summary>
/// <remarks>
/// Nacos exposes its main HTTP API on the configured port and reserves
/// <c>port + 1000</c> for the gRPC transport that carries change notifications, so both are
/// derived once at construction rather than parsed per request.
/// </remarks>
internal sealed class NacosServerAddress
{
    private NacosServerAddress(
        string original,
        Uri httpBaseAddress,
        Uri grpcBaseAddress,
        string host,
        int port,
        bool useTls,
        string contextPath)
    {
        Original = original;
        HttpBaseAddress = httpBaseAddress;
        GrpcBaseAddress = grpcBaseAddress;
        Host = host;
        Port = port;
        UseTls = useTls;
        ContextPath = contextPath;
    }

    /// <summary>Gets the address exactly as the user supplied it, for diagnostics.</summary>
    public string Original { get; }

    /// <summary>Gets the base address of the HTTP OpenAPI, including the context path.</summary>
    public Uri HttpBaseAddress { get; }

    /// <summary>Gets the base address of the gRPC endpoint.</summary>
    public Uri GrpcBaseAddress { get; }

    /// <summary>Gets the host name or literal IP address.</summary>
    public string Host { get; }

    /// <summary>Gets the main (HTTP) port.</summary>
    public int Port { get; }

    /// <summary>Gets a value indicating whether the endpoint uses TLS.</summary>
    public bool UseTls { get; }

    /// <summary>Gets the normalized context path, always starting with '/'.</summary>
    public string ContextPath { get; }

    /// <summary>
    /// Parses a single server address.
    /// </summary>
    /// <param name="address">
    /// An address such as <c>127.0.0.1:8848</c>, <c>http://nacos.example.com:8848</c> or
    /// <c>https://nacos.example.com/nacos</c>. A missing scheme implies <c>http</c> and a
    /// missing port implies <see cref="NacosConstants.DefaultPort"/>.
    /// </param>
    /// <param name="defaultContextPath">The context path applied when the address omits one.</param>
    /// <returns>The parsed address.</returns>
    /// <exception cref="FormatException">The address is not a usable server endpoint.</exception>
    public static NacosServerAddress Parse(string address, string defaultContextPath = NacosConstants.DefaultContextPath)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new FormatException("A Nacos server address must not be null or whitespace.");
        }

        string candidate = address.Trim();

        // A bare "host:port" has no scheme; Uri would read "host" as the scheme.
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = string.Concat("http://", candidate);
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            throw new FormatException(
                string.Format(CultureInfo.InvariantCulture, "'{0}' is not a valid Nacos server address.", address));
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            throw new FormatException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "'{0}' uses the unsupported scheme '{1}'; Nacos server addresses must use http or https.",
                    address,
                    uri.Scheme));
        }

        string host = uri.Host;
        int port = uri.IsDefaultPort ? NacosConstants.DefaultPort : uri.Port;
        bool useTls = uri.Scheme == Uri.UriSchemeHttps;

        // Preserve an explicit context path such as /nacos from the supplied address,
        // otherwise fall back to the configured default.
        string contextPath = uri.AbsolutePath.TrimEnd('/');
        if (contextPath.Length == 0)
        {
            contextPath = NormalizeContextPath(defaultContextPath);
        }

        var httpBase = new UriBuilder(uri.Scheme, host, port, contextPath).Uri;

        // The SDK gRPC endpoint is always the main port plus the fixed offset, for plaintext and
        // TLS alike: Nacos negotiates the two protocols on the same SDK gRPC port, so HTTPS does
        // not move the endpoint to a different port.
        int grpcPort = port + NacosConstants.GrpcPortOffset;
        string grpcScheme = useTls ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var grpcBase = new UriBuilder(grpcScheme, host, grpcPort).Uri;

        return new NacosServerAddress(address, httpBase, grpcBase, host, port, useTls, contextPath);
    }

    /// <summary>
    /// Parses and de-duplicates a sequence of server addresses while preserving order.
    /// </summary>
    /// <param name="addresses">The configured addresses.</param>
    /// <param name="defaultContextPath">The context path applied to addresses that omit one.</param>
    /// <returns>The parsed addresses.</returns>
    /// <exception cref="FormatException">One of the addresses is invalid.</exception>
    public static IReadOnlyList<NacosServerAddress> ParseAll(
        IEnumerable<string> addresses,
        string defaultContextPath = NacosConstants.DefaultContextPath)
    {
        var result = new List<NacosServerAddress>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string address in addresses)
        {
            NacosServerAddress parsed = Parse(address, defaultContextPath);
            if (seen.Add(parsed.HttpBaseAddress.AbsoluteUri))
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    /// <summary>
    /// Normalizes a context path so it either starts with '/' or is empty.
    /// </summary>
    /// <param name="contextPath">The configured context path.</param>
    /// <returns>The normalized context path.</returns>
    public static string NormalizeContextPath(string? contextPath)
    {
        if (string.IsNullOrWhiteSpace(contextPath) || contextPath == "/")
        {
            return string.Empty;
        }

        string trimmed = contextPath.Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : string.Concat("/", trimmed);
    }

    /// <summary>
    /// Combines the HTTP base address with a relative request path.
    /// </summary>
    /// <param name="relativePath">A path such as <c>/v1/cs/configs</c>.</param>
    /// <returns>The absolute request URI.</returns>
    public Uri BuildHttpUri(string relativePath)
    {
        string path = relativePath.StartsWith('/') ? relativePath : string.Concat("/", relativePath);
        return new Uri(string.Concat(HttpBaseAddress.AbsoluteUri, path));
    }

    /// <inheritdoc />
    public override string ToString() => Original;

    /// <summary>
    /// Returns the gRPC endpoint address in host:port form, as expected by
    /// <c>GrpcChannel.ForAddress</c>.
    /// </summary>
    /// <returns>The gRPC authority string.</returns>
    public string ToGrpcAddress() => string.Create(
        CultureInfo.InvariantCulture,
        $"{GrpcBaseAddress.Scheme}://{Host}:{GrpcBaseAddress.Port}");
}
