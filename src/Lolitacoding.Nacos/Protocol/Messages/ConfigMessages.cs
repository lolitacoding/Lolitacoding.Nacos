namespace Lolitacoding.Nacos.Protocol.Messages;

/// <summary>
/// Base type for requests addressed to a configuration identified by data id, group and tenant.
/// </summary>
internal abstract class ConfigRequest : NacosRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigRequest"/> class.
    /// </summary>
    protected ConfigRequest() => Module = NacosModule.Config;

    /// <summary>Gets or sets the configuration data id.</summary>
    public string? DataId { get; set; }

    /// <summary>Gets or sets the configuration group.</summary>
    public string? Group { get; set; }

    /// <summary>Gets or sets the namespace id.</summary>
    public string? Tenant { get; set; }
}

/// <summary>
/// Requests the content of a single configuration. <c>metadata.type = "ConfigQueryRequest"</c>.
/// </summary>
internal sealed class ConfigQueryRequest : ConfigRequest
{
    /// <summary>Gets or sets the configuration tag.</summary>
    public string? Tag { get; set; }

    /// <summary>
    /// Gets or sets the MD5 of the content the client already holds.
    /// </summary>
    /// <remarks>
    /// Nacos 3.3 introduced this field together with the <c>304</c> response code: when the
    /// server sees a matching hash it answers "not modified" and omits the content, expecting
    /// the client to serve it from a local snapshot.
    /// </remarks>
    public string? LocalMd5 { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this query originates from a change notification.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the wire the authoritative signal is the <c>notify</c> gRPC metadata header, which is
    /// what the server reads and what <see cref="MarkAsNotify"/> sets. Nacos' request bean also
    /// exposes a derived <c>isNotify()</c> getter that is serialised into the body, so this
    /// property mirrors it for wire fidelity.
    /// </para>
    /// <para>
    /// Note that the official client empties the bean's header map before serialising the body,
    /// which means its body copy always reads <c>false</c> regardless of the header. The server
    /// ignores the body copy entirely, so both forms are accepted; this client reports the
    /// caller's actual intent instead of reproducing that serialisation artefact.
    /// </para>
    /// </remarks>
    public bool Notify { get; set; }

    /// <summary>
    /// Marks this query as notification-driven, setting the mirrored header and property.
    /// </summary>
    public void MarkAsNotify()
    {
        Notify = true;
        SetHeader(ProtocolHeaders.Notify, "true");
    }
}

/// <summary>
/// Carries a configuration's content and metadata.
/// <c>metadata.type = "ConfigQueryResponse"</c>.
/// </summary>
internal sealed class ConfigQueryResponse : NacosResponse
{
    /// <summary>No configuration exists for the requested coordinates.</summary>
    public const int ConfigNotFound = 300;

    /// <summary>The client's cached content is still current; no content is returned.</summary>
    public const int ConfigNotModified = 304;

    /// <summary>The configuration was modified concurrently.</summary>
    public const int ConfigQueryConflict = 400;

    /// <summary>The caller is not authorised to read the configuration.</summary>
    public const int NoRight = 403;

    /// <summary>Gets or sets the configuration content.</summary>
    public string? Content { get; set; }

    /// <summary>Gets or sets the key protecting encrypted content.</summary>
    public string? EncryptedDataKey { get; set; }

    /// <summary>Gets or sets the content type, for example <c>text</c> or <c>json</c>.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets or sets the MD5 of the content as computed by the server.</summary>
    public string? Md5 { get; set; }

    /// <summary>Gets or sets the last modification timestamp.</summary>
    public long LastModified { get; set; }

    /// <summary>Gets or sets the configuration tag.</summary>
    public string? Tag { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this content came from a beta release.
    /// </summary>
    /// <remarks>
    /// The Java property is <c>isBeta</c>; Nacos' serialiser drops the <c>is</c> prefix, so the
    /// wire name is <c>beta</c>.
    /// </remarks>
    public bool Beta { get; set; }
}

/// <summary>
/// Publishes (creates or overwrites) a configuration.
/// <c>metadata.type = "ConfigPublishRequest"</c>.
/// </summary>
internal sealed class ConfigPublishRequest : ConfigRequest
{
    /// <summary>Gets or sets the configuration content.</summary>
    public string? Content { get; set; }

    /// <summary>
    /// Gets or sets the compare-and-swap MD5 guard.
    /// </summary>
    /// <remarks>
    /// When set, the server rejects the publish unless the stored content still hashes to this
    /// value, which turns the write into an optimistic-concurrency update.
    /// </remarks>
    public string? CasMd5 { get; set; }

    /// <summary>
    /// Gets or sets additional parameters such as <c>type</c> or <c>src_user</c>.
    /// </summary>
    public Dictionary<string, string>? AdditionMap { get; set; }
}

/// <summary>Confirms a publish. <c>metadata.type = "ConfigPublishResponse"</c>.</summary>
internal sealed class ConfigPublishResponse : NacosResponse
{
}

/// <summary>
/// Removes a configuration. <c>metadata.type = "ConfigRemoveRequest"</c>.
/// </summary>
internal sealed class ConfigRemoveRequest : ConfigRequest
{
    /// <summary>Gets or sets the configuration tag.</summary>
    public string? Tag { get; set; }
}

/// <summary>Confirms a removal. <c>metadata.type = "ConfigRemoveResponse"</c>.</summary>
internal sealed class ConfigRemoveResponse : NacosResponse
{
}

/// <summary>
/// One entry of a batch listener registration.
/// </summary>
internal sealed class ConfigListenContext
{
    /// <summary>Gets or sets the configuration group.</summary>
    public string? Group { get; set; }

    /// <summary>Gets or sets the MD5 the client currently holds.</summary>
    public string? Md5 { get; set; }

    /// <summary>Gets or sets the configuration data id.</summary>
    public string? DataId { get; set; }

    /// <summary>Gets or sets the namespace id.</summary>
    public string? Tenant { get; set; }
}

/// <summary>
/// Registers or removes configuration listeners in a single call.
/// <c>metadata.type = "ConfigBatchListenRequest"</c>.
/// </summary>
/// <remarks>
/// Despite the name it suggests, this travels over the unary <c>Request/request</c> RPC rather
/// than the notification stream: the server has a registered request handler for it. The
/// <em>response</em> is the type with "Change" in its name,
/// <see cref="ConfigChangeBatchListenResponse"/>.
/// </remarks>
internal sealed class ConfigBatchListenRequest : ConfigRequest
{
    /// <summary>Gets or sets a value indicating whether this registers or removes listeners.</summary>
    public bool Listen { get; set; } = true;

    /// <summary>Gets or sets the configurations to listen for.</summary>
    public List<ConfigListenContext>? ConfigListenContexts { get; set; }
}

/// <summary>
/// Identifies one configuration that changed.
/// </summary>
internal sealed class ConfigContext
{
    /// <summary>Gets or sets the configuration group.</summary>
    public string? Group { get; set; }

    /// <summary>Gets or sets the configuration data id.</summary>
    public string? DataId { get; set; }

    /// <summary>Gets or sets the namespace id.</summary>
    public string? Tenant { get; set; }
}

/// <summary>
/// Reports which of the listened configurations changed.
/// <c>metadata.type = "ConfigChangeBatchListenResponse"</c>.
/// </summary>
internal sealed class ConfigChangeBatchListenResponse : NacosResponse
{
    /// <summary>Gets or sets the configurations whose content changed.</summary>
    public List<ConfigContext>? ChangedConfigs { get; set; }
}

/// <summary>
/// Server push announcing that a configuration changed.
/// <c>metadata.type = "ConfigChangeNotifyRequest"</c>.
/// </summary>
/// <remarks>
/// This arrives on the notification stream and carries only coordinates: the client must query
/// the configuration to obtain the new content.
/// </remarks>
internal sealed class ConfigChangeNotifyRequest : NacosRequest
{
    /// <summary>Gets or sets the configuration group.</summary>
    public string? Group { get; set; }

    /// <summary>Gets or sets the configuration data id.</summary>
    public string? DataId { get; set; }

    /// <summary>Gets or sets the namespace id.</summary>
    public string? Tenant { get; set; }
}

/// <summary>Acknowledges a change notification. Carries no fields.</summary>
internal sealed class ConfigChangeNotifyResponse : NacosResponse
{
}
