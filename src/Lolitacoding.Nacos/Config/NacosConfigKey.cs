namespace Lolitacoding.Nacos.Config;

/// <summary>
/// Identifies a single configuration.
/// </summary>
/// <remarks>
/// <para>
/// Nacos addresses a configuration by the triple of namespace, group and data id. The namespace
/// is the outermost container, the group partitions a namespace, and the data id names an
/// individual document within a group.
/// </para>
/// <para>
/// The namespace is optional on the type: leaving it unset means "use the namespace configured on
/// <see cref="NacosOptions.Namespace"/>" rather than "the empty namespace", which is rarely what a
/// caller means and would otherwise duplicate the default on every call.
/// </para>
/// </remarks>
public sealed record NacosConfigKey
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigKey"/> record.
    /// </summary>
    /// <param name="dataId">The configuration data id.</param>
    /// <param name="group">The configuration group.</param>
    /// <param name="namespace">
    /// The namespace id, or <see langword="null"/> to use the configured default.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="dataId"/> or <paramref name="group"/> is null or whitespace.
    /// </exception>
    public NacosConfigKey(string dataId, string group, string? @namespace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        DataId = dataId;
        Group = group;
        Namespace = @namespace;
    }

    /// <summary>Gets the configuration data id.</summary>
    public string DataId { get; }

    /// <summary>Gets the configuration group.</summary>
    public string Group { get; }

    /// <summary>
    /// Gets the namespace id, or <see langword="null"/> to use the configured default.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// Applies the configured default namespace when this key does not name one.
    /// </summary>
    /// <param name="defaultNamespace">The namespace to fall back to.</param>
    /// <returns>A key with a concrete namespace.</returns>
    internal NacosConfigKey WithDefaultNamespace(string defaultNamespace) =>
        string.IsNullOrEmpty(Namespace) ? this with { Namespace = defaultNamespace } : this;

    /// <inheritdoc />
    public override string ToString() => string.Concat(
        string.IsNullOrEmpty(Namespace) ? "<default>" : Namespace, "/", Group, "/", DataId);
}

/// <summary>
/// The outcome of reading a configuration.
/// </summary>
/// <remarks>
/// The content is optional because "no such configuration" is a normal, expected answer rather
/// than an error, and callers overwhelmingly want to branch on it. Exposing that as a nullable
/// content property keeps the common case — a default that may not exist yet — free of exception
/// handling, while genuine failures still throw.
/// </remarks>
public sealed record NacosConfigResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigResult"/> record.
    /// </summary>
    /// <param name="key">The configuration that was read.</param>
    /// <param name="content">The content, or <see langword="null"/> when it does not exist.</param>
    /// <param name="md5">The content hash reported by the server.</param>
    /// <param name="contentType">The content type, for example <c>text</c> or <c>json</c>.</param>
    /// <param name="lastModified">The last modification time.</param>
    /// <param name="fromLocalCache">Whether the value came from a local snapshot.</param>
    /// <param name="encryptedDataKey">
    /// The encrypted data key that travels with the content, or <see langword="null"/>.
    /// </param>
    internal NacosConfigResult(
        NacosConfigKey key,
        string? content,
        string? md5,
        string? contentType,
        DateTimeOffset lastModified,
        bool fromLocalCache,
        string? encryptedDataKey = null)
    {
        Key = key;
        Content = content;
        Md5 = md5;
        ContentType = contentType;
        LastModified = lastModified;
        FromLocalCache = fromLocalCache;
        EncryptedDataKey = encryptedDataKey;
    }

    /// <summary>Gets the configuration that was read.</summary>
    public NacosConfigKey Key { get; }

    /// <summary>
    /// Gets the configuration content, or <see langword="null"/> when it does not exist.
    /// </summary>
    public string? Content { get; }

    /// <summary>Gets the content hash reported by the server.</summary>
    public string? Md5 { get; }

    /// <summary>Gets the content type, for example <c>text</c> or <c>json</c>.</summary>
    public string? ContentType { get; }

    /// <summary>
    /// Gets the encrypted data key that travels with the content, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The key is the one the server stored, in its encrypted form. It is carried here so a caller
    /// that stores the value elsewhere can keep the content and the key together; the client itself
    /// keeps both in its snapshot. For a configuration that is not encrypted this is
    /// <see langword="null"/> (or the empty string the server reports).
    /// </remarks>
    public string? EncryptedDataKey { get; }

    /// <summary>Gets the time the configuration was last modified.</summary>
    public DateTimeOffset LastModified { get; }

    /// <summary>
    /// Gets a value indicating whether the value was served from a local snapshot because the
    /// server was unreachable.
    /// </summary>
    /// <remarks>
    /// Callers that must not act on stale data — a database migration key, for instance — can
    /// check this and decide for themselves, rather than having the failover hidden from them.
    /// </remarks>
    public bool FromLocalCache { get; }

    /// <summary>Gets a value indicating whether the configuration exists.</summary>
    public bool Exists => Content is not null;
}

/// <summary>
/// Describes a configuration change delivered to a listener.
/// </summary>
/// <remarks>
/// The content is carried on the change rather than being left for the caller to fetch, because
/// the server push carries only coordinates: fetching is mandatory to obtain the new value, so
/// doing it once here is strictly better than making every listener repeat it.
/// </remarks>
public sealed record NacosConfigChange
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigChange"/> record.
    /// </summary>
    /// <param name="key">The configuration that changed.</param>
    /// <param name="result">The new value.</param>
    internal NacosConfigChange(NacosConfigKey key, NacosConfigResult result)
    {
        Key = key;
        Result = result;
    }

    /// <summary>Gets the configuration that changed.</summary>
    public NacosConfigKey Key { get; }

    /// <summary>Gets the new value.</summary>
    public NacosConfigResult Result { get; }

    /// <summary>Gets the new content.</summary>
    public string? Content => Result.Content;

    /// <summary>
    /// Gets the hash of the content before the change, when the previous value was known.
    /// </summary>
    public string? PreviousMd5 { get; init; }
}
