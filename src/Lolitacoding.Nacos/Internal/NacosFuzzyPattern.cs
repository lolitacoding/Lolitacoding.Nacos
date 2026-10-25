using System.Text;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// The pattern language and group-key codec the fuzzy watch protocol uses.
/// </summary>
/// <remarks>
/// <para>
/// A watch pattern is <c>namespaceId&gt;&gt;groupPattern&gt;&gt;resourcePattern</c>. A resource
/// pattern (a data id for configuration, a service name for naming) understands five forms:
/// <c>*</c>, <c>prefix*</c>, <c>*suffix</c>, <c>*text*</c> and a literal. A <c>*</c> in the middle
/// of a segment — <c>a*b</c> — matches nothing, which is what the official matcher does: it only
/// strips asterisks from the ends.
/// </para>
/// <para>
/// A group key is <c>dataId+group[+tenant]</c> with <c>+</c> escaped as <c>%2B</c> and <c>%</c> as
/// <c>%25</c>. That is the whole codec: no other percent escape is legal, and the parser rejects
/// one rather than guessing.
/// </para>
/// </remarks>
internal static class NacosFuzzyPattern
{
    /// <summary>The namespace a blank namespace is treated as.</summary>
    private const string DefaultNamespace = "public";

    /// <summary>
    /// Builds the pattern the server expects for a pair of resource and group patterns.
    /// </summary>
    /// <param name="namespaceId">The namespace the watch is scoped to.</param>
    /// <param name="groupPattern">The group pattern.</param>
    /// <param name="resourcePattern">The data id or service name pattern.</param>
    /// <returns>The combined pattern.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="groupPattern"/> or <paramref name="resourcePattern"/> is blank.
    /// </exception>
    public static string Generate(string? namespaceId, string groupPattern, string resourcePattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupPattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePattern);

        string scope = string.IsNullOrWhiteSpace(namespaceId) ? DefaultNamespace : namespaceId;

        return string.Concat(
            scope,
            Protocol.Messages.FuzzyWatchProtocol.PatternSplitter,
            groupPattern,
            Protocol.Messages.FuzzyWatchProtocol.PatternSplitter,
            resourcePattern);
    }

    /// <summary>
    /// Reports whether a resource falls inside a watch pattern.
    /// </summary>
    /// <param name="groupKeyPattern">The pattern to test.</param>
    /// <param name="namespaceId">The resource's namespace.</param>
    /// <param name="group">The resource's group.</param>
    /// <param name="resource">The data id or service name.</param>
    /// <returns><see langword="true"/> when every segment matches.</returns>
    public static bool Matches(
        string groupKeyPattern,
        string? namespaceId,
        string group,
        string resource)
    {
        ArgumentNullException.ThrowIfNull(groupKeyPattern);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(resource);

        string[] segments = groupKeyPattern.Split(
            Protocol.Messages.FuzzyWatchProtocol.PatternSplitter, StringSplitOptions.None);

        // A pattern that is not exactly three segments cannot have been produced by the protocol.
        // Treating it as "matches nothing" keeps a malformed push from throwing on the read loop.
        if (segments.Length != 3)
        {
            return false;
        }

        string scope = string.IsNullOrWhiteSpace(namespaceId) ? DefaultNamespace : namespaceId;

        return string.Equals(segments[0], scope, StringComparison.Ordinal)
            && SegmentMatches(segments[1], group)
            && SegmentMatches(segments[2], resource);
    }

    /// <summary>
    /// Matches one pattern segment against one value.
    /// </summary>
    /// <param name="pattern">The segment pattern.</param>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value matches.</returns>
    public static bool SegmentMatches(string pattern, string value)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);

        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(pattern, value, StringComparison.Ordinal);
        }

        if (pattern == "*")
        {
            return true;
        }

        // The official matcher strips every asterisk, not just the leading and trailing ones, so
        // "a*b" is not a glob: it falls through to the final "no match" because it neither starts
        // nor ends with one.
        string literal = pattern.Replace("*", string.Empty, StringComparison.Ordinal);

        if (pattern.StartsWith('*') && pattern.EndsWith('*'))
        {
            return value.Contains(literal, StringComparison.Ordinal);
        }

        if (pattern.StartsWith('*'))
        {
            return value.EndsWith(literal, StringComparison.Ordinal);
        }

        if (pattern.EndsWith('*'))
        {
            return value.StartsWith(literal, StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Encodes a configuration's coordinates as the protocol's group key.
    /// </summary>
    /// <param name="dataId">The data id.</param>
    /// <param name="group">The group.</param>
    /// <param name="tenant">The namespace, or <see langword="null"/> when there is none.</param>
    /// <returns>The group key.</returns>
    public static string EncodeGroupKey(string dataId, string group, string? tenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        var builder = new StringBuilder(dataId.Length + group.Length + 8);

        AppendEscaped(builder, dataId);
        builder.Append('+');
        AppendEscaped(builder, group);

        if (!string.IsNullOrEmpty(tenant))
        {
            builder.Append('+');
            AppendEscaped(builder, tenant);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Decodes a group key into its coordinates.
    /// </summary>
    /// <param name="groupKey">The group key.</param>
    /// <param name="dataId">The decoded data id.</param>
    /// <param name="group">The decoded group.</param>
    /// <param name="tenant">The decoded namespace, or <see langword="null"/> when absent.</param>
    /// <returns><see langword="false"/> when the key is malformed.</returns>
    /// <remarks>
    /// A malformed key is reported rather than thrown because it arrives from the server: one bad
    /// push must not take down the read loop that carries every other notification.
    /// </remarks>
    public static bool TryParseGroupKey(
        string? groupKey,
        out string dataId,
        out string group,
        out string? tenant)
    {
        dataId = string.Empty;
        group = string.Empty;
        tenant = null;

        if (string.IsNullOrEmpty(groupKey))
        {
            return false;
        }

        var segments = new List<string>(3);
        var current = new StringBuilder(groupKey.Length);

        for (int i = 0; i < groupKey.Length; i++)
        {
            char c = groupKey[i];

            if (c == '+')
            {
                if (segments.Count == 2)
                {
                    // A fourth segment is not part of the protocol.
                    return false;
                }

                segments.Add(current.ToString());
                current.Clear();
                continue;
            }

            if (c == '%')
            {
                if (i + 2 >= groupKey.Length)
                {
                    return false;
                }

                char high = groupKey[i + 1];
                char low = groupKey[i + 2];

                if (high == '2' && low == 'B')
                {
                    current.Append('+');
                }
                else if (high == '2' && low == '5')
                {
                    current.Append('%');
                }
                else
                {
                    return false;
                }

                i += 2;
                continue;
            }

            current.Append(c);
        }

        segments.Add(current.ToString());

        if (segments.Count < 2
            || string.IsNullOrWhiteSpace(segments[0])
            || string.IsNullOrWhiteSpace(segments[1]))
        {
            return false;
        }

        dataId = segments[0];
        group = segments[1];

        if (segments.Count == 3)
        {
            tenant = segments[2];
        }

        return true;
    }

    /// <summary>
    /// Appends one component with the two escapes the codec defines.
    /// </summary>
    /// <param name="builder">The builder to append to.</param>
    /// <param name="value">The component.</param>
    private static void AppendEscaped(StringBuilder builder, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '+':
                    builder.Append("%2B");
                    break;
                case '%':
                    builder.Append("%25");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
    }
}
