using System.IO.Compression;
using System.Runtime.CompilerServices;
using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// The skill registry, served over the AI module's HTTP endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Skills and agent specifications are the two AI resources the official client serves only over
/// HTTP: its gRPC client answers both with <c>SERVER_NOT_IMPLEMENTED</c>, and the server exposes them
/// as OpenAPI endpoints that stream a body rather than as messages on the AI connection. This client
/// does the same, so reading a skill needs a reachable HTTP endpoint and does not open the AI
/// connection at all.
/// </para>
/// <para>
/// A read names a version, or a label that Nacos moves between versions, or neither and takes the
/// version labelled <c>latest</c>. Unlike a prompt, an unknown label does not fall back to latest:
/// the server treats a label that does not resolve as a missing version, so a typo is reported rather
/// than silently answered with something else.
/// </para>
/// <para>
/// A subscription is a cached copy refreshed on an interval, like every other AI resource, because
/// Nacos pushes no skill changes either. The read is conditional on the hash of the cached archive.
/// </para>
/// </remarks>
internal sealed class NacosSkillClient : INacosSkillRegistry, IAsyncDisposable
{
    /// <summary>The path of the skill download endpoint.</summary>
    private const string SkillPath = "/v3/client/ai/skills";

    /// <summary>The header that carries the hash of the published content.</summary>
    private const string Md5Header = "X-Nacos-Skill-Md5";

    /// <summary>The header that carries the version the server resolved.</summary>
    private const string ResolvedVersionHeader = "X-Nacos-Skill-Resolved-Version";

    /// <summary>The smallest byte sequence that can be a ZIP archive.</summary>
    private const int MinimumZipSize = 30;

    /// <summary>The error code Nacos uses for a resource or version that does not exist.</summary>
    private const int NotFoundErrorCode = 404;

    /// <summary>The error code Nacos uses for a conditional read whose hash still matches.</summary>
    private const int NotModifiedErrorCode = 304;

    private readonly NacosAiHttpClient _http;
    private readonly NacosOptions _options;
    private readonly ILogger<NacosSkillClient> _logger;
    private readonly NacosAiResourceCache<NacosSkill> _cache;

    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosSkillClient"/> class.
    /// </summary>
    /// <param name="http">The HTTP endpoint the registry is served over.</param>
    /// <param name="options">The client options.</param>
    /// <param name="timeProvider">The clock the subscription refresh interval is measured with.</param>
    /// <param name="logger">The logger.</param>
    public NacosSkillClient(
        NacosAiHttpClient http,
        NacosOptions options,
        TimeProvider timeProvider,
        ILogger<NacosSkillClient> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _options = options;
        _logger = logger;

        _cache = new NacosAiResourceCache<NacosSkill>(new NacosAiResourceCacheOptions<NacosSkill>
        {
            Query = QueryAsync,
            IsSame = IsSame,
            Hash = static skill => skill.Md5,
            Changed = (skill, name) => NacosAiLog.SkillChanged(_logger, name, skill.Version),
            Withdrawn = name => NacosAiLog.SkillWithdrawn(_logger, name),
            RefreshFailed = (name, version, label, reason) =>
                NacosAiLog.SkillRefreshFailed(_logger, name, version, label, reason),
            RefreshInterval = options.Ai.CacheRefreshInterval,
            TimeProvider = timeProvider,
            Logger = logger,
        });
    }

    /// <inheritdoc />
    public async Task<NacosSkill> GetSkillAsync(
        string skillName,
        string? version = null,
        string? label = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The read is unconditional: a caller that asked for the skill wants its archive, so there
        // is nothing to be answered with "not modified".
        return await QueryAsync(skillName, version, label, md5: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosSkill?> SubscribeSkillAsync(
        string skillName,
        string? version = null,
        string? label = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);

        // Unlike a prompt subscription this deliberately does not touch the AI connection: a skill is
        // served over HTTP only, so an application that only follows skills never opens a gRPC
        // stream it would not use.
        await foreach (NacosSkill? skill in _cache.SubscribeAsync(skillName, version, label, ct).ConfigureAwait(false))
        {
            yield return skill;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _cache.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one skill selector from the registry.
    /// </summary>
    /// <param name="skillName">The skill name.</param>
    /// <param name="version">The version, or <see langword="null"/> to resolve one.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <param name="md5">The hash of the archive the caller already holds, or <see langword="null"/>.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The resolved skill.</returns>
    /// <exception cref="NacosServerException">
    /// The skill or version does not exist (the not-found code), the hash still matches (the
    /// not-modified code), the archive is not a safe ZIP, or the request failed for any other reason.
    /// </exception>
    /// <remarks>
    /// This is the raw read that both <see cref="GetSkillAsync"/> and the subscription refresh loop
    /// use. The endpoint reports a missing skill and a matching hash as status codes rather than as a
    /// missing field, so both callers decide what they mean: a refresh treats the first as a
    /// withdrawal and the second as nothing to do, while a read surfaces both.
    /// </remarks>
    private async Task<NacosSkill> QueryAsync(
        string skillName,
        string? version,
        string? label,
        string? md5,
        CancellationToken ct)
    {
        var query = new List<KeyValuePair<string, string?>>(5)
        {
            new("namespaceId", _options.Namespace),
            new("name", skillName),
        };

        AddIfPresent(query, "version", version);
        AddIfPresent(query, "label", label);
        AddIfPresent(query, "md5", md5);

        NacosAiHttpResponse response = await _http.GetAsync(SkillPath, query, ct).ConfigureAwait(false);

        switch ((int)response.StatusCode)
        {
            case 200:
                break;

            case NotModifiedErrorCode:
                throw new NacosServerException(
                    NotModifiedErrorCode,
                    string.Concat("The skill '", skillName, "' has not changed."));

            case NotFoundErrorCode:
                throw new NacosServerException(
                    NotFoundErrorCode,
                    response.ErrorMessage
                        ?? string.Concat("The Nacos server does not serve skill '", skillName, "'."));

            case 403:
                throw new NacosAuthException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat("The Nacos server refused the read of skill '", skillName, "'."));

            default:
                throw new NacosServerException(
                    (int)response.StatusCode,
                    response.ErrorMessage
                        ?? string.Concat(
                            "The Nacos server answered the read of skill '",
                            skillName,
                            "' with HTTP ",
                            ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "."));
        }

        ValidateArchive(response.Body, skillName);

        return new NacosSkill
        {
            Name = skillName,
            Version = response.Header(ResolvedVersionHeader),
            Md5 = response.Header(Md5Header),
            Archive = response.Body,
        };
    }

    /// <summary>
    /// Adds a query parameter when it carries a value.
    /// </summary>
    /// <param name="query">The parameters collected so far.</param>
    /// <param name="name">The parameter name.</param>
    /// <param name="value">The value, or <see langword="null"/> to omit the parameter.</param>
    private static void AddIfPresent(
        List<KeyValuePair<string, string?>> query,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add(new KeyValuePair<string, string?>(name, value));
        }
    }

    /// <summary>
    /// Checks that a body really is a ZIP archive, and one that is safe to extract.
    /// </summary>
    /// <param name="archive">The bytes the server returned.</param>
    /// <param name="skillName">The skill they were requested for, for the error message.</param>
    /// <exception cref="NacosServerException">
    /// The body is not a ZIP archive, or an entry names a path outside the archive's directory.
    /// </exception>
    /// <remarks>
    /// The archive is handed to the caller as bytes, so the caller is the one that extracts it. A body
    /// that is not a ZIP at all — an HTML error page served with 200 by an intermediary, say — would
    /// otherwise reach an extractor as a corrupt file, and an entry named <c>../x</c> would escape the
    /// directory it was extracted into. Both are refused here, which is the same two checks the
    /// official client makes.
    /// </remarks>
    private static void ValidateArchive(byte[] archive, string skillName)
    {
        if (archive.Length < MinimumZipSize
            || archive[0] != 0x50
            || archive[1] != 0x4B
            || archive[2] != 0x03
            || archive[3] != 0x04)
        {
            throw new NacosServerException(
                string.Concat(
                    "The Nacos server returned a body for skill '",
                    skillName,
                    "' that is not a ZIP archive."));
        }

        try
        {
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                ValidateEntryPath(entry.FullName, skillName);
            }
        }
        catch (InvalidDataException ex)
        {
            throw new NacosServerException(
                string.Concat(
                    "The Nacos server returned a corrupt ZIP archive for skill '",
                    skillName,
                    "'."),
                ex);
        }
    }

    /// <summary>
    /// Checks that an archive entry stays inside the archive's directory.
    /// </summary>
    /// <param name="path">The entry's full name.</param>
    /// <param name="skillName">The skill the archive belongs to, for the error message.</param>
    /// <exception cref="NacosServerException">The entry escapes the directory it belongs in.</exception>
    private static void ValidateEntryPath(string path, string skillName)
    {
        if (path.Contains('\0', StringComparison.Ordinal)
            || path.Contains("..", StringComparison.Ordinal))
        {
            throw UnsafeArchive(skillName, path);
        }

        string normalized = path.Replace('\\', '/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        bool hasDrivePrefix = normalized.Length >= 2
            && char.IsAsciiLetter(normalized[0])
            && normalized[1] == ':';

        if (normalized.StartsWith('/') || hasDrivePrefix)
        {
            throw UnsafeArchive(skillName, path);
        }
    }

    /// <summary>
    /// Builds the error reported for an archive entry that escapes its directory.
    /// </summary>
    /// <param name="skillName">The skill the archive belongs to.</param>
    /// <param name="path">The offending entry name.</param>
    /// <returns>The exception to throw.</returns>
    private static NacosServerException UnsafeArchive(string skillName, string path) =>
        new(string.Concat(
            "The ZIP archive of skill '",
            skillName,
            "' contains an entry that escapes its directory: '",
            path,
            "'."));

    /// <summary>
    /// Decides whether two reads are the same state.
    /// </summary>
    /// <param name="left">The cached skill.</param>
    /// <param name="right">The skill just read.</param>
    /// <returns><see langword="true"/> when the two are the same state.</returns>
    /// <remarks>
    /// The archive is compared byte for byte rather than by the hash the response reported, because a
    /// server that could not compute the hash still serves content: two different archives that both
    /// arrived without a hash would then look like one state and the change would never be announced.
    /// </remarks>
    private static bool IsSame(NacosSkill? left, NacosSkill? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Archive.Span.SequenceEqual(right.Archive.Span);
    }
}
