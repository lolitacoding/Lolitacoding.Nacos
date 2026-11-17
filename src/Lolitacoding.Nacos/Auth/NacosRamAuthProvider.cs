using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Signs Nacos requests with an Alibaba Cloud RAM access key.
/// </summary>
/// <remarks>
/// <para>
/// This is the official client's <c>RamClientAuthServiceImpl</c>. The access key identifies the
/// caller and the secret key signs a resource string plus the current millisecond, so the
/// signature is recomputed for every request rather than fetched once like a token.
/// </para>
/// <para>
/// The field names are historical and differ by module, which is why they are reproduced exactly:
/// configuration and AI requests carry <c>Spas-AccessKey</c>, <c>Timestamp</c> and
/// <c>Spas-Signature</c>; naming requests carry <c>ak</c>, <c>data</c> and <c>signature</c>; lock
/// requests carry only <c>ak</c>. A configured region switches the signing key to the v4
/// derivation and adds <c>signatureVersion=v4</c>.
/// </para>
/// <para>
/// Only the gRPC transport is decorated here. The AI registry's HTTP transport and STS credential
/// discovery are not covered by this slice.
/// </para>
/// </remarks>
internal sealed class NacosRamAuthProvider : INacosAuthProvider, INacosRequestMetadataProvider
{
    private const string AccessKeyHeader = "Spas-AccessKey";
    private const string TimestampHeader = "Timestamp";
    private const string SignatureHeader = "Spas-Signature";
    private const string SignatureVersionHeader = "signatureVersion";
    private const string SignatureVersionV4 = "v4";

    private const string NamingAccessKeyHeader = "ak";
    private const string NamingSignatureHeader = "signature";
    private const string NamingDataHeader = "data";

    private const string V4KeyPrefix = "aliyun_v4";
    private const string V4ProductCode = "mse-nacos";
    private const string V4RequestConstant = "aliyun_v4_request";

    private const string DefaultGroup = "DEFAULT_GROUP";
    private const string GroupSeparator = "@@";

    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string? _regionId;
    private readonly string _defaultNamespace;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosRamAuthProvider"/> class.
    /// </summary>
    /// <param name="options">The RAM settings.</param>
    /// <param name="timeProvider">The clock used for the timestamp and the v4 date.</param>
    /// <param name="defaultNamespace">
    /// The namespace an AI request is signed for when it does not carry one of its own.
    /// </param>
    public NacosRamAuthProvider(
        NacosRamOptions options,
        TimeProvider timeProvider,
        string defaultNamespace)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _accessKey = options.AccessKey ?? string.Empty;
        _secretKey = options.SecretKey ?? string.Empty;
        _regionId = string.IsNullOrWhiteSpace(options.SignatureRegionId)
            ? null
            : options.SignatureRegionId;
        _defaultNamespace = string.IsNullOrWhiteSpace(defaultNamespace)
            ? "public"
            : defaultNamespace;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// RAM authenticates with headers, not a token, so there is nothing to return.
    /// </remarks>
    public ValueTask<string?> GetAccessTokenAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    /// <remarks>
    /// Every request is signed afresh, so a rejection needs no cached state discarded.
    /// </remarks>
    public void Invalidate()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <inheritdoc />
    public void ApplyRequestMetadata(NacosRequest request)
    {
        switch (request)
        {
            case ConfigRequest config:
                ApplyConfigMetadata(request, config.Tenant, config.Group);
                break;

            case NamingRequest naming:
                ApplyNamingMetadata(request, naming.ServiceName, naming.GroupName);
                break;

            case AIRequest ai:
                ApplyAiMetadata(request, ResolveAiNamespace(ai));
                break;

            case LockRequest:
                // The lock client attaches the access key only; a signed lock format has not been
                // defined, which matches the official client.
                request.SetHeader(NamingAccessKeyHeader, _accessKey);
                break;

            default:
                // Transport-internal requests (handshake, health check, metrics) carry no resource
                // and are not signed.
                break;
        }
    }

    /// <summary>
    /// Signs a configuration request over the namespace and group.
    /// </summary>
    private void ApplyConfigMetadata(NacosRequest request, string? tenant, string? group)
    {
        string resource = ConfigResource(tenant, group);
        string timestamp = Timestamp();
        string data = resource.Length == 0 ? timestamp : resource + "+" + timestamp;

        request.SetHeader(AccessKeyHeader, _accessKey);
        request.SetHeader(TimestampHeader, timestamp);
        request.SetHeader(SignatureHeader, Sign(data));
        ApplySignatureVersion(request);
    }

    /// <summary>
    /// Signs an AI request over the namespace and the default group.
    /// </summary>
    /// <remarks>
    /// The official client always signs AI requests with <c>DEFAULT_GROUP</c>, because the registry
    /// addresses its resources by name rather than by the configuration group.
    /// </remarks>
    private void ApplyAiMetadata(NacosRequest request, string? namespaceId)
    {
        string resource = (namespaceId ?? _defaultNamespace) + "+" + DefaultGroup;
        string timestamp = Timestamp();
        string data = resource + "+" + timestamp;

        request.SetHeader(AccessKeyHeader, _accessKey);
        request.SetHeader(TimestampHeader, timestamp);
        request.SetHeader(SignatureHeader, Sign(data));
        ApplySignatureVersion(request);
    }

    /// <summary>
    /// Signs a naming request over the grouped service name.
    /// </summary>
    private void ApplyNamingMetadata(NacosRequest request, string? serviceName, string? groupName)
    {
        string service = GroupedServiceName(serviceName, groupName);
        string timestamp = Timestamp();
        string data = service.Length == 0
            ? timestamp
            : timestamp + GroupSeparator + service;

        request.SetHeader(NamingAccessKeyHeader, _accessKey);
        request.SetHeader(NamingSignatureHeader, Sign(data));
        request.SetHeader(NamingDataHeader, data);
        ApplySignatureVersion(request);
    }

    private void ApplySignatureVersion(NacosRequest request)
    {
        if (_regionId is not null)
        {
            request.SetHeader(SignatureVersionHeader, SignatureVersionV4);
        }
    }

    private string Timestamp() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Computes the base64 HMAC-SHA1 signature over <paramref name="data"/>.
    /// </summary>
    /// <remarks>
    /// The v4 key is itself a base64 string used as the HMAC key, which is why the algorithm stays
    /// HMAC-SHA1 in both schemes; only the key derivation differs.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "HMAC-SHA1 is mandated by the Nacos RAM signing protocol; a stronger algorithm would produce signatures the server rejects.")]
    private string Sign(string data)
    {
        string key = _regionId is null
            ? _secretKey
            : DeriveV4Key(_secretKey, _regionId, _timeProvider.GetUtcNow());

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    /// <summary>
    /// Derives the v4 signing key the official client uses for a region.
    /// </summary>
    /// <param name="secret">The secret key.</param>
    /// <param name="region">The configured region.</param>
    /// <param name="now">The instant whose UTC date selects the first derivation step.</param>
    /// <returns>The base64-encoded key.</returns>
    private static string DeriveV4Key(string secret, string region, DateTimeOffset now)
    {
        string date = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        byte[] first = HmacSha256(
            Encoding.UTF8.GetBytes(V4KeyPrefix + secret),
            Encoding.UTF8.GetBytes(date));
        byte[] second = HmacSha256(first, Encoding.UTF8.GetBytes(region));
        byte[] third = HmacSha256(second, Encoding.UTF8.GetBytes(V4ProductCode));
        byte[] final = HmacSha256(third, Encoding.UTF8.GetBytes(V4RequestConstant));

        return Convert.ToBase64String(final);
    }

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    /// <summary>
    /// Builds the configuration resource string: <c>tenant+group</c>, or the group alone when the
    /// tenant is empty.
    /// </summary>
    private static string ConfigResource(string? tenant, string? group)
    {
        string value = group ?? string.Empty;

        if (string.IsNullOrWhiteSpace(tenant))
        {
            return value;
        }

        return tenant + "+" + value;
    }

    /// <summary>
    /// Qualifies a service name with its group unless it is already qualified.
    /// </summary>
    private static string GroupedServiceName(string? serviceName, string? groupName)
    {
        string service = serviceName ?? string.Empty;

        if (service.Contains(GroupSeparator, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(groupName))
        {
            return service;
        }

        return groupName + GroupSeparator + service;
    }

    /// <summary>
    /// Reads the namespace an AI request addresses.
    /// </summary>
    private static string? ResolveAiNamespace(AIRequest request) => request switch
    {
        McpRequest mcp => mcp.NamespaceId,
        PromptRequest prompt => prompt.NamespaceId,
        AgentRequest agent => agent.NamespaceId,
        AgentPublishRpcRequest publish => publish.NamespaceId,
        AgentSearchRpcRequest search => search.NamespaceId,
        AgentEndpointRegisterRpcRequest register => register.NamespaceId,
        AgentEndpointDeregisterRpcRequest deregister => deregister.NamespaceId,
        // The watch requests address a watch rather than a namespace, so they are signed for the
        // client's default namespace.
        _ => null,
    };
}
