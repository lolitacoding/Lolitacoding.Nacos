using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Config;

/// <summary>
/// Identifies the direction in which a configuration filter runs.
/// </summary>
public enum NacosConfigFilterStage
{
    /// <summary>The value is on its way to the server.</summary>
    Publish = 0,

    /// <summary>The value arrived from the server or from a local snapshot.</summary>
    Read = 1,
}

/// <summary>
/// The value a <see cref="INacosConfigFilter"/> sees and may rewrite.
/// </summary>
/// <remarks>
/// <para>
/// A filter mutates the context rather than returning a new value, because a chain has to be able
/// to accumulate changes from several filters: one may encrypt the content while another adds a
/// prefix, and each has to see what the previous one produced. This mirrors the request/response
/// pair the official Java client hands to <c>IConfigFilter</c>.
/// </para>
/// <para>
/// <see cref="Md5"/> and <see cref="FromLocalCache"/> are read-only because they describe where the
/// value came from, not what it is. The hash in particular is the hash of the bytes the server
/// stored, so changing it from a filter would break the client's change detection.
/// </para>
/// </remarks>
public sealed class NacosConfigFilterContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigFilterContext"/> class.
    /// </summary>
    /// <param name="key">The configuration the value belongs to.</param>
    /// <param name="stage">Whether the value is being published or read.</param>
    /// <param name="content">The content, or <see langword="null"/> when there is none.</param>
    /// <param name="contentType">The content type, for example <c>text</c> or <c>json</c>.</param>
    /// <param name="encryptedDataKey">
    /// The encrypted data key that travels with the content, or <see langword="null"/>.
    /// </param>
    /// <param name="md5">The hash the server reported, or <see langword="null"/>.</param>
    /// <param name="fromLocalCache">Whether the value came from a local snapshot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public NacosConfigFilterContext(
        NacosConfigKey key,
        NacosConfigFilterStage stage,
        string? content = null,
        string? contentType = null,
        string? encryptedDataKey = null,
        string? md5 = null,
        bool fromLocalCache = false)
    {
        ArgumentNullException.ThrowIfNull(key);

        Key = key;
        Stage = stage;
        Content = content;
        ContentType = contentType;
        EncryptedDataKey = encryptedDataKey;
        Md5 = md5;
        FromLocalCache = fromLocalCache;
    }

    /// <summary>Gets the configuration the value belongs to.</summary>
    public NacosConfigKey Key { get; }

    /// <summary>Gets whether the value is being published or read.</summary>
    public NacosConfigFilterStage Stage { get; }

    /// <summary>Gets or sets the content.</summary>
    /// <remarks>
    /// On the publish path a filter that leaves this <see langword="null"/> aborts the publish: a
    /// configuration whose value a filter deleted by accident must not reach the server as an empty
    /// document.
    /// </remarks>
    public string? Content { get; set; }

    /// <summary>Gets or sets the content type.</summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Gets or sets the encrypted data key that travels with the content.
    /// </summary>
    /// <remarks>
    /// The key is carried next to the content rather than inside it so the server can store both
    /// and hand them back together. An encryption filter sets it while publishing and reads it
    /// while decrypting; a filter that does not deal with encryption leaves it untouched.
    /// </remarks>
    public string? EncryptedDataKey { get; set; }

    /// <summary>Gets the content hash reported by the server.</summary>
    /// <remarks>
    /// This is the hash of the stored value, computed before filters ran, which is what the client
    /// compares to decide whether a pushed notification really changed anything.
    /// </remarks>
    public string? Md5 { get; }

    /// <summary>
    /// Gets a value indicating whether the value came from a local snapshot rather than the server.
    /// </summary>
    public bool FromLocalCache { get; }
}

/// <summary>
/// Transforms configuration content on its way to or from the server.
/// </summary>
/// <remarks>
/// <para>
/// Filters run in <see cref="Order"/> order, lowest first, on every publish and on every read that
/// yields content, including a read served from a local snapshot. This is the client-side extension
/// point the official Java client exposes as <c>ConfigService.addConfigFilter(IConfigFilter)</c>;
/// the built-in <see cref="NacosConfigEncryptionFilter"/> is an implementation of it.
/// </para>
/// <para>
/// Unlike the Java interface there is no <c>next</c> chain argument. A filter mutates the context
/// and returns; the client then invokes the next filter. A filter that must stop the operation
/// throws, which surfaces to the caller on a read or publish and is logged and skipped on a pushed
/// change.
/// </para>
/// <para>
/// Filters must be safe for concurrent use: one client serves many callers, so several invocations
/// can overlap.
/// </para>
/// </remarks>
public interface INacosConfigFilter
{
    /// <summary>Gets the name that identifies the filter in the chain.</summary>
    /// <remarks>
    /// Names are compared ordinally. Adding a second filter with an existing name is ignored, so
    /// the first registration wins — the same rule the official client applies.
    /// </remarks>
    string Name { get; }

    /// <summary>Gets the position of the filter in the chain; lower runs first.</summary>
    int Order { get; }

    /// <summary>Transforms the value in <paramref name="context"/>.</summary>
    /// <param name="context">The mutable value.</param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes when the filter is done.</returns>
    ValueTask InvokeAsync(NacosConfigFilterContext context, CancellationToken ct);
}

/// <summary>
/// Encrypts and decrypts configuration content and the key that protects it.
/// </summary>
/// <remarks>
/// <para>
/// This is the .NET counterpart of the Java <c>EncryptionPluginService</c> SPI. The data key is
/// generated per publish, used to encrypt the content, and then itself encrypted by
/// <see cref="EncryptSecretKey"/> before it is stored next to the content. Decryption reverses the
/// two steps.
/// </para>
/// <para>
/// The algorithm name is what <see cref="NacosConfigEncryptionFilter"/> matches against the data id:
/// a data id such as <c>cipher-AES-application.json</c> selects the provider whose
/// <see cref="AlgorithmName"/> is <c>AES</c>.
/// </para>
/// </remarks>
public interface INacosConfigEncryptionProvider
{
    /// <summary>Gets the algorithm name that selects this provider.</summary>
    string AlgorithmName { get; }

    /// <summary>Generates a new data key for one publish.</summary>
    /// <returns>The plaintext data key.</returns>
    string GenerateSecretKey();

    /// <summary>Encrypts content with a data key.</summary>
    /// <param name="secretKey">The plaintext data key.</param>
    /// <param name="content">The plaintext content.</param>
    /// <returns>The ciphertext.</returns>
    string Encrypt(string secretKey, string content);

    /// <summary>Decrypts content with a data key.</summary>
    /// <param name="secretKey">The plaintext data key.</param>
    /// <param name="content">The ciphertext.</param>
    /// <returns>The plaintext content.</returns>
    string Decrypt(string secretKey, string content);

    /// <summary>Protects a data key so it can be stored next to the content.</summary>
    /// <param name="secretKey">The plaintext data key.</param>
    /// <returns>The encrypted data key.</returns>
    string EncryptSecretKey(string secretKey);

    /// <summary>Recovers a data key that was stored next to the content.</summary>
    /// <param name="encryptedSecretKey">The encrypted data key.</param>
    /// <returns>The plaintext data key.</returns>
    string DecryptSecretKey(string encryptedSecretKey);
}

/// <summary>
/// Encrypts content published under a <c>cipher-</c> data id and decrypts it on the way back.
/// </summary>
/// <remarks>
/// <para>
/// The filter is a pass-through unless the data id starts with <c>cipher-</c> (and is not exactly
/// <c>cipher-</c>) and a provider is registered for the algorithm named by the data id's second
/// <c>-</c> segment. For example, <c>cipher-AES-application.json</c> uses the provider whose
/// <see cref="INacosConfigEncryptionProvider.AlgorithmName"/> is <c>AES</c>. This reproduces the
/// official Java <c>ConfigEncryptionFilter</c>, including its rule that an unknown algorithm leaves
/// the value alone rather than failing the operation.
/// </para>
/// <para>
/// On a read the filter replaces the content with its plaintext but deliberately leaves
/// <see cref="NacosConfigFilterContext.EncryptedDataKey"/> as the stored (encrypted) key. The Java
/// filter overwrites it with the plaintext key internally, which is not observable there but would
/// expose the key through <see cref="NacosConfigResult.EncryptedDataKey"/> here.
/// </para>
/// </remarks>
public sealed class NacosConfigEncryptionFilter : INacosConfigFilter
{
    /// <summary>The name under which the filter registers itself.</summary>
    public const string FilterName = "Lolitacoding.Nacos.Config.NacosConfigEncryptionFilter";

    private const string CipherDataIdPrefix = "cipher-";

    private readonly INacosConfigEncryptionProvider[] _providers;
    private readonly ILogger? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigEncryptionFilter"/> class.
    /// </summary>
    /// <param name="providers">The providers, searched in order for a matching algorithm.</param>
    /// <param name="logger">The logger, or <see langword="null"/> to stay silent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="providers"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The list contains a <see langword="null"/> provider or one without an algorithm name.
    /// </exception>
    public NacosConfigEncryptionFilter(
        IReadOnlyList<INacosConfigEncryptionProvider> providers,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = new INacosConfigEncryptionProvider[providers.Count];

        for (int i = 0; i < providers.Count; i++)
        {
            INacosConfigEncryptionProvider provider = providers[i]
                ?? throw new ArgumentException(
                    "The encryption provider list contains a null entry.", nameof(providers));

            if (string.IsNullOrWhiteSpace(provider.AlgorithmName))
            {
                throw new ArgumentException(
                    "Every encryption provider must have a non-empty AlgorithmName.",
                    nameof(providers));
            }

            _providers[i] = provider;
        }

        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => FilterName;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public ValueTask InvokeAsync(NacosConfigFilterContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (context.Content is null || !IsCipherDataId(context.Key.DataId))
        {
            return ValueTask.CompletedTask;
        }

        string? algorithm = ParseAlgorithmName(context.Key.DataId);
        INacosConfigEncryptionProvider? provider = FindProvider(algorithm);

        if (provider is null)
        {
            if (_logger is not null)
            {
                NacosConfigLog.EncryptionProviderMissing(
                    _logger, context.Key.ToString(), algorithm);
            }

            return ValueTask.CompletedTask;
        }

        if (context.Stage == NacosConfigFilterStage.Publish)
        {
            Encrypt(context, provider);
        }
        else if (context.Stage == NacosConfigFilterStage.Read)
        {
            Decrypt(context, provider);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Determines whether a data id opts into encryption.
    /// </summary>
    /// <param name="dataId">The data id.</param>
    /// <returns><see langword="true"/> when the id carries the cipher prefix and an algorithm.</returns>
    private static bool IsCipherDataId(string dataId) =>
        dataId.StartsWith(CipherDataIdPrefix, StringComparison.Ordinal)
        && !string.Equals(dataId, CipherDataIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Reads the algorithm from the data id.
    /// </summary>
    /// <param name="dataId">The data id, which starts with <c>cipher-</c>.</param>
    /// <returns>The text between the first and second <c>-</c>, or <see langword="null"/>.</returns>
    /// <remarks>
    /// This is the same segment the Java handler takes with
    /// <c>Stream.of(dataId.split("-")).skip(1).findFirst()</c>. A data id such as
    /// <c>cipher-AES-application.json</c> therefore selects <c>AES</c>.
    /// </remarks>
    private static string? ParseAlgorithmName(string dataId)
    {
        int first = dataId.IndexOf('-');
        if (first < 0)
        {
            return null;
        }

        int second = dataId.IndexOf('-', first + 1);

        return second < 0 ? dataId[(first + 1)..] : dataId[(first + 1)..second];
    }

    /// <summary>
    /// Finds the provider for an algorithm name.
    /// </summary>
    /// <param name="algorithm">The algorithm name.</param>
    /// <returns>The provider, or <see langword="null"/> when none matches.</returns>
    private INacosConfigEncryptionProvider? FindProvider(string? algorithm)
    {
        if (string.IsNullOrEmpty(algorithm))
        {
            return null;
        }

        foreach (INacosConfigEncryptionProvider provider in _providers)
        {
            if (string.Equals(provider.AlgorithmName, algorithm, StringComparison.Ordinal))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// Encrypts a value on its way to the server.
    /// </summary>
    /// <param name="context">The value to encrypt.</param>
    /// <param name="provider">The provider selected by the data id.</param>
    private static void Encrypt(
        NacosConfigFilterContext context,
        INacosConfigEncryptionProvider provider)
    {
        string content = context.Content!;
        string secretKey = provider.GenerateSecretKey();
        string encryptedContent = provider.Encrypt(secretKey, content);

        // Mirrors the Java filter: a provider that returns nothing usable leaves the value alone
        // rather than publishing an empty document.
        if (!string.IsNullOrWhiteSpace(encryptedContent)
            && !string.Equals(encryptedContent, content, StringComparison.Ordinal))
        {
            context.Content = encryptedContent;
        }

        string encryptedKey = provider.EncryptSecretKey(secretKey);

        if (!string.IsNullOrWhiteSpace(encryptedKey)
            && !string.Equals(encryptedKey, context.EncryptedDataKey, StringComparison.Ordinal))
        {
            context.EncryptedDataKey = encryptedKey;
        }
        else if (string.IsNullOrWhiteSpace(context.EncryptedDataKey)
            && string.IsNullOrWhiteSpace(encryptedKey))
        {
            context.EncryptedDataKey = string.Empty;
        }
    }

    /// <summary>
    /// Decrypts a value that arrived from the server or from a snapshot.
    /// </summary>
    /// <param name="context">The value to decrypt.</param>
    /// <param name="provider">The provider selected by the data id.</param>
    private static void Decrypt(
        NacosConfigFilterContext context,
        INacosConfigEncryptionProvider provider)
    {
        string content = context.Content!;
        string secretKey = provider.DecryptSecretKey(context.EncryptedDataKey ?? string.Empty);
        string decryptedContent = provider.Decrypt(secretKey, content);

        if (!string.IsNullOrWhiteSpace(decryptedContent)
            && !string.Equals(decryptedContent, content, StringComparison.Ordinal))
        {
            context.Content = decryptedContent;
        }

        // EncryptedDataKey is intentionally left as the stored key; see the type remarks.
    }
}
