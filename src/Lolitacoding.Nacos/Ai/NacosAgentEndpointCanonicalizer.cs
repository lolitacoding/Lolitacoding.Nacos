using System.Globalization;
using System.Text;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// Canonicalises the endpoint values a RAD discovery result carries.
/// </summary>
/// <remarks>
/// <para>
/// The HTTP watch sends the fingerprint of the state it already holds, and the server compares it with
/// the fingerprint it computes itself. The two only agree if both sides canonicalise the same values
/// in the same way, so this type is a line-for-line port of the Java
/// <c>EndpointCanonicalizer</c>: the URI is rewritten with a lower-case scheme, an IDN/compressed
/// host and an explicit port, and the endpoint defaults are materialised before anything is hashed.
/// </para>
/// <para>
/// The URI is parsed by hand rather than with <see cref="Uri"/>. <see cref="Uri"/> normalises a path
/// and percent-encodes non-ASCII characters, while the Java implementation keeps the raw path and
/// query exactly as the server sent them; using the framework parser would change the fingerprint of
/// any endpoint whose path contains a non-ASCII character.
/// </para>
/// </remarks>
internal static class NacosAgentEndpointCanonicalizer
{
    /// <summary>The longest URI an endpoint may carry, in Unicode code points.</summary>
    private const int MaxUriLength = 2048;

    /// <summary>The largest weight an endpoint may carry.</summary>
    private const double MaxWeight = 10000D;

    /// <summary>The maximum number of metadata entries an endpoint may carry.</summary>
    private const int MaxMetadataEntries = 32;

    /// <summary>The maximum length of a metadata key, in Unicode code points.</summary>
    private const int MaxMetadataKeyLength = 64;

    /// <summary>The maximum length of a metadata value, in Unicode code points.</summary>
    private const int MaxMetadataValueLength = 256;

    /// <summary>The maximum length of an endpoint transport name.</summary>
    private const int MaxTransportLength = 32;

    /// <summary>
    /// Canonicalises one endpoint, validating it the way the server does before hashing it.
    /// </summary>
    /// <param name="endpoint">The endpoint to canonicalise.</param>
    /// <returns>A detached endpoint with canonical values.</returns>
    /// <exception cref="ArgumentException">The endpoint is not a valid public endpoint.</exception>
    public static NacosAgentRuntimeEndpoint Canonicalize(NacosAgentRuntimeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        string uri = CanonicalizeUri(endpoint.Uri);
        ValidateTransport(endpoint.Transport);

        if (endpoint.Priority is null)
        {
            throw new ArgumentException("Endpoint priority must not be null.", nameof(endpoint));
        }

        if (endpoint.Weight is null)
        {
            throw new ArgumentException("Endpoint weight must not be null.", nameof(endpoint));
        }

        if (endpoint.Healthy is null)
        {
            throw new ArgumentException("Endpoint healthy must not be null.", nameof(endpoint));
        }

        if (endpoint.Enabled is null)
        {
            throw new ArgumentException("Endpoint enabled must not be null.", nameof(endpoint));
        }

        int priority = endpoint.Priority.Value;

        if (priority < 0)
        {
            throw new ArgumentException("Endpoint priority must not be negative.", nameof(endpoint));
        }

        double weight = endpoint.Weight.Value;

        if (double.IsNaN(weight) || double.IsInfinity(weight) || weight < 0D || weight > MaxWeight)
        {
            throw new ArgumentException(
                "Endpoint weight must be between 0 and 10000.",
                nameof(endpoint));
        }

        ValidateMetadata(endpoint.Metadata);

        var result = new NacosAgentRuntimeEndpoint
        {
            Uri = uri,
            Transport = endpoint.Transport,
            Priority = priority,
            Weight = weight == 0D ? 0D : weight,
            Healthy = endpoint.Healthy,
            Enabled = endpoint.Enabled,
        };

        if (endpoint.Bindings is not null)
        {
            result.Bindings = new List<NacosAgentVersionBinding>(endpoint.Bindings.Count);

            foreach (NacosAgentVersionBinding? binding in endpoint.Bindings)
            {
                result.Bindings.Add(binding is null
                    ? null!
                    : new NacosAgentVersionBinding
                    {
                        RuntimeVersion = binding.RuntimeVersion,
                        VersionRange = binding.VersionRange,
                    });
            }
        }

        if (endpoint.Metadata is { Count: > 0 })
        {
            result.Metadata = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string key in endpoint.Metadata.Keys.Order(StringComparer.Ordinal))
            {
                result.Metadata[key] = endpoint.Metadata[key];
            }
        }

        return result;
    }

    /// <summary>
    /// Canonicalises an endpoint URI.
    /// </summary>
    /// <param name="value">The URI the server reported.</param>
    /// <returns>
    /// The URI with a lower-case scheme, a canonical host and an explicit effective port.
    /// </returns>
    /// <exception cref="ArgumentException">The URI is not a valid public endpoint URI.</exception>
    public static string CanonicalizeUri(string? value)
    {
        if (string.IsNullOrEmpty(value) || CodePointCount(value) > MaxUriLength)
        {
            throw InvalidUri(value);
        }

        ValidateCharacters(value);

        int schemeEnd = value.IndexOf(':', StringComparison.Ordinal);

        if (schemeEnd <= 0 || !IsValidScheme(value.AsSpan(0, schemeEnd)))
        {
            throw InvalidUri(value);
        }

        // An absolute hierarchical URI is the only shape an endpoint may use. Anything else —
        // mailto:, //host, /relative — has no authority to canonicalise.
        if (value.Length < schemeEnd + 3
            || value[schemeEnd + 1] != '/'
            || value[schemeEnd + 2] != '/')
        {
            throw InvalidUri(value);
        }

        string scheme = value[..schemeEnd].ToLowerInvariant();
        int authorityStart = schemeEnd + 3;
        int authorityEnd = IndexOfAny(value, authorityStart, '/', '?', '#');

        if (authorityEnd < 0)
        {
            authorityEnd = value.Length;
        }

        string rawAuthority = value[authorityStart..authorityEnd];
        Authority authority = ParseAuthority(rawAuthority, value);
        string host = authority.IsIpv6
            ? NormalizeIpv6(authority.Host, value)
            : NormalizeNonIpv6(authority.Host, value);
        int port = authority.Port is null ? DefaultPort(scheme, value) : ParsePort(authority.Port, value);
        string formattedHost = authority.IsIpv6 ? string.Concat("[", host, "]") : host;

        var canonical = new StringBuilder(value.Length + 8)
            .Append(scheme)
            .Append("://")
            .Append(formattedHost)
            .Append(':')
            .Append(port.ToString(CultureInfo.InvariantCulture));

        int pathStart = authorityEnd;
        int pathEnd = IndexOfAny(value, pathStart, '?', '#');

        if (pathEnd < 0)
        {
            pathEnd = value.Length;
        }

        if (pathEnd > pathStart)
        {
            canonical.Append(value, pathStart, pathEnd - pathStart);
        }

        if (pathEnd < value.Length && value[pathEnd] == '?')
        {
            int queryEnd = value.IndexOf('#', pathEnd);

            if (queryEnd < 0)
            {
                queryEnd = value.Length;
            }

            canonical.Append('?').Append(value, pathEnd + 1, queryEnd - pathEnd - 1);
        }

        if (value.Contains('#', StringComparison.Ordinal))
        {
            throw InvalidUri(value);
        }

        if (CodePointCount(canonical.ToString()) > MaxUriLength)
        {
            throw InvalidUri(value);
        }

        return canonical.ToString();
    }

    /// <summary>
    /// Rejects characters the URI grammar does not allow, and malformed percent escapes.
    /// </summary>
    /// <param name="value">The URI.</param>
    /// <exception cref="ArgumentException">The URI contains a character it cannot contain.</exception>
    private static void ValidateCharacters(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];

            if (current <= 0x20
                || current == 0x7F
                || current is '"' or '<' or '>' or '\\' or '^' or '`' or '{' or '|' or '}')
            {
                throw InvalidUri(value);
            }

            if (current == '%')
            {
                if (i + 2 >= value.Length
                    || !char.IsAsciiHexDigit(value[i + 1])
                    || !char.IsAsciiHexDigit(value[i + 2]))
                {
                    throw InvalidUri(value);
                }

                i += 2;
            }
        }
    }

    /// <summary>
    /// Determines whether a string is a syntactically valid scheme.
    /// </summary>
    /// <param name="scheme">The candidate scheme.</param>
    /// <returns><see langword="true"/> when the scheme matches RFC 3986.</returns>
    private static bool IsValidScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.IsEmpty || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (char current in scheme[1..])
        {
            if (!char.IsAsciiLetterOrDigit(current)
                && current is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the first occurrence of one of three characters.
    /// </summary>
    /// <param name="value">The string to search.</param>
    /// <param name="start">The index to start at.</param>
    /// <param name="first">The first candidate.</param>
    /// <param name="second">The second candidate.</param>
    /// <param name="third">The third candidate.</param>
    /// <returns>The index, or <c>-1</c> when none occurs.</returns>
    private static int IndexOfAny(string value, int start, char first, char second, char third)
    {
        for (int i = start; i < value.Length; i++)
        {
            char current = value[i];

            if (current == first || current == second || current == third)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the first occurrence of one of two characters.
    /// </summary>
    /// <param name="value">The string to search.</param>
    /// <param name="start">The index to start at.</param>
    /// <param name="first">The first candidate.</param>
    /// <param name="second">The second candidate.</param>
    /// <returns>The index, or <c>-1</c> when neither occurs.</returns>
    private static int IndexOfAny(string value, int start, char first, char second)
    {
        for (int i = start; i < value.Length; i++)
        {
            char current = value[i];

            if (current == first || current == second)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Splits a raw authority into host and optional port.
    /// </summary>
    /// <param name="rawAuthority">The authority, without the leading slashes.</param>
    /// <param name="originalUri">The URI being parsed, for the error message.</param>
    /// <returns>The parsed authority.</returns>
    /// <exception cref="ArgumentException">The authority is malformed.</exception>
    private static Authority ParseAuthority(string rawAuthority, string originalUri)
    {
        if (rawAuthority.Length == 0 || rawAuthority.Contains('@', StringComparison.Ordinal))
        {
            throw InvalidUri(originalUri);
        }

        if (rawAuthority[0] == '[')
        {
            int closingBracket = rawAuthority.IndexOf(']', StringComparison.Ordinal);

            if (closingBracket < 0 || closingBracket == 1)
            {
                throw InvalidUri(originalUri);
            }

            string suffix = rawAuthority[(closingBracket + 1)..];
            string? port = null;

            if (suffix.Length > 0)
            {
                if (suffix[0] != ':' || suffix.Length == 1)
                {
                    throw InvalidUri(originalUri);
                }

                port = suffix[1..];
            }

            return new Authority(rawAuthority[1..closingBracket], port, IsIpv6: true);
        }

        int colon = rawAuthority.LastIndexOf(':');

        if (colon >= 0)
        {
            if (colon == 0
                || colon != rawAuthority.IndexOf(':', StringComparison.Ordinal)
                || colon == rawAuthority.Length - 1)
            {
                throw InvalidUri(originalUri);
            }

            return new Authority(rawAuthority[..colon], rawAuthority[(colon + 1)..], IsIpv6: false);
        }

        return new Authority(rawAuthority, null, IsIpv6: false);
    }

    /// <summary>
    /// Canonicalises a host that is not an IPv6 literal.
    /// </summary>
    /// <param name="host">The host as written.</param>
    /// <param name="originalUri">The URI being parsed, for the error message.</param>
    /// <returns>The canonical host.</returns>
    private static string NormalizeNonIpv6(string host, string originalUri)
    {
        if (IsIpv4Candidate(host))
        {
            return NormalizeIpv4(host, originalUri);
        }

        try
        {
            var idn = new IdnMapping
            {
                UseStd3AsciiRules = true,
            };

            string asciiHost = idn.GetAscii(host);

            if (asciiHost.Length == 0)
            {
                throw InvalidUri(originalUri);
            }

            return asciiHost.ToLowerInvariant();
        }
        catch (ArgumentException ex)
        {
            throw InvalidUri(originalUri, ex);
        }
    }

    /// <summary>
    /// Determines whether a host is written as four dotted decimal octets.
    /// </summary>
    /// <param name="host">The host.</param>
    /// <returns><see langword="true"/> when it is an IPv4 candidate.</returns>
    private static bool IsIpv4Candidate(string host)
    {
        int dots = 0;

        foreach (char current in host)
        {
            if (current == '.')
            {
                dots++;
            }
            else if (!char.IsAsciiDigit(current))
            {
                return false;
            }
        }

        return dots == 3;
    }

    /// <summary>
    /// Canonicalises a dotted decimal IPv4 address.
    /// </summary>
    /// <param name="host">The host as written.</param>
    /// <param name="originalUri">The URI being parsed, for the error message.</param>
    /// <returns>The address with leading zeros removed from each octet.</returns>
    private static string NormalizeIpv4(string host, string originalUri)
    {
        string[] octets = host.Split('.');

        if (octets.Length != 4)
        {
            throw InvalidUri(originalUri);
        }

        var result = new StringBuilder(host.Length);

        for (int i = 0; i < octets.Length; i++)
        {
            if (octets[i].Length == 0 || octets[i].Length > 3)
            {
                throw InvalidUri(originalUri);
            }

            int value = 0;

            foreach (char current in octets[i])
            {
                if (!char.IsAsciiDigit(current))
                {
                    throw InvalidUri(originalUri);
                }

                value = (value * 10) + (current - '0');
            }

            if (value > 255)
            {
                throw InvalidUri(originalUri);
            }

            if (i > 0)
            {
                result.Append('.');
            }

            result.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    /// <summary>
    /// Canonicalises an IPv6 literal.
    /// </summary>
    /// <param name="host">The address without brackets.</param>
    /// <param name="originalUri">The URI being parsed, for the error message.</param>
    /// <returns>The compressed, lower-case address.</returns>
    private static string NormalizeIpv6(string host, string originalUri)
    {
        if (host.Contains('%', StringComparison.Ordinal))
        {
            throw InvalidUri(originalUri);
        }

        int doubleColon = host.IndexOf("::", StringComparison.Ordinal);

        if (doubleColon >= 0 && doubleColon != host.LastIndexOf("::", StringComparison.Ordinal))
        {
            throw InvalidUri(originalUri);
        }

        List<int> words;

        if (doubleColon >= 0)
        {
            List<int> left = ParseIpv6Section(host[..doubleColon], originalUri);
            List<int> right = ParseIpv6Section(host[(doubleColon + 2)..], originalUri);
            int zeroCount = 8 - left.Count - right.Count;

            if (zeroCount < 1)
            {
                throw InvalidUri(originalUri);
            }

            for (int i = 0; i < zeroCount; i++)
            {
                left.Add(0);
            }

            left.AddRange(right);
            words = left;
        }
        else
        {
            words = ParseIpv6Section(host, originalUri);

            if (words.Count != 8)
            {
                throw InvalidUri(originalUri);
            }
        }

        return FormatIpv6(words);
    }

    /// <summary>
    /// Parses one side of an IPv6 address into 16-bit words.
    /// </summary>
    /// <param name="section">The section to parse.</param>
    /// <param name="originalUri">The URI being parsed, for the error message.</param>
    /// <returns>The words in the section.</returns>
    private static List<int> ParseIpv6Section(string section, string originalUri)
    {
        var words = new List<int>();

        if (section.Length == 0)
        {
            return words;
        }

        string[] parts = section.Split(':');

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];

            if (part.Length == 0)
            {
                throw InvalidUri(originalUri);
            }

            if (part.Contains('.', StringComparison.Ordinal))
            {
                if (i != parts.Length - 1)
                {
                    throw InvalidUri(originalUri);
                }

                string ipv4 = NormalizeIpv4(part, originalUri);
                string[] octets = ipv4.Split('.');
                words.Add((int.Parse(octets[0], CultureInfo.InvariantCulture) << 8)
                    | int.Parse(octets[1], CultureInfo.InvariantCulture));
                words.Add((int.Parse(octets[2], CultureInfo.InvariantCulture) << 8)
                    | int.Parse(octets[3], CultureInfo.InvariantCulture));
            }
            else
            {
                if (part.Length > 4 || !IsHex(part))
                {
                    throw InvalidUri(originalUri);
                }

                words.Add(int.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }

        return words;
    }

    /// <summary>
    /// Determines whether a string consists only of hexadecimal digits.
    /// </summary>
    /// <param name="value">The string.</param>
    /// <returns><see langword="true"/> when every character is a hex digit.</returns>
    private static bool IsHex(string value)
    {
        foreach (char current in value)
        {
            if (!char.IsAsciiHexDigit(current))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Formats IPv6 words the way RFC 5952 asks for.
    /// </summary>
    /// <param name="words">The eight 16-bit words.</param>
    /// <returns>The compressed address.</returns>
    private static string FormatIpv6(List<int> words)
    {
        int bestStart = -1;
        int bestLength = 0;

        for (int i = 0; i < words.Count;)
        {
            if (words[i] != 0)
            {
                i++;
                continue;
            }

            int end = i;

            while (end < words.Count && words[end] == 0)
            {
                end++;
            }

            int length = end - i;

            if (length >= 2 && length > bestLength)
            {
                bestStart = i;
                bestLength = length;
            }

            i = end;
        }

        if (bestStart < 0)
        {
            return JoinIpv6Words(words, 0, words.Count);
        }

        string prefix = JoinIpv6Words(words, 0, bestStart);
        string suffix = JoinIpv6Words(words, bestStart + bestLength, words.Count);

        return string.Concat(prefix, "::", suffix);
    }

    /// <summary>
    /// Joins a range of IPv6 words with colons.
    /// </summary>
    /// <param name="words">The words.</param>
    /// <param name="start">The first index.</param>
    /// <param name="end">The index after the last word.</param>
    /// <returns>The joined range.</returns>
    private static string JoinIpv6Words(List<int> words, int start, int end)
    {
        var result = new StringBuilder();

        for (int i = start; i < end; i++)
        {
            if (result.Length > 0)
            {
                result.Append(':');
            }

            result.Append(words[i].ToString("x", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    /// <summary>
    /// Returns the port a scheme uses when the URI does not name one.
    /// </summary>
    /// <param name="scheme">The lower-case scheme.</param>
    /// <param name="originalUri">The URI being parsed, for the error message.</param>
    /// <returns>The default port.</returns>
    private static int DefaultPort(string scheme, string originalUri) => scheme switch
    {
        "http" or "ws" => 80,
        "https" or "wss" => 443,
        _ => throw InvalidUri(originalUri),
    };

    /// <summary>
    /// Parses an explicit port.
    /// </summary>
    /// <param name="value">The port as written.</param>
    /// <param name="originalUri">The URI being parsed, for the error message.</param>
    /// <returns>The port.</returns>
    private static int ParsePort(string value, string originalUri)
    {
        foreach (char current in value)
        {
            if (!char.IsAsciiDigit(current))
            {
                throw InvalidUri(originalUri);
            }
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port < 1
            || port > 65535)
        {
            throw InvalidUri(originalUri);
        }

        return port;
    }

    /// <summary>
    /// Validates an endpoint transport name.
    /// </summary>
    /// <param name="transport">The transport.</param>
    /// <exception cref="ArgumentException">The transport is missing or malformed.</exception>
    private static void ValidateTransport(string? transport)
    {
        if (string.IsNullOrEmpty(transport)
            || transport.Length > MaxTransportLength
            || !char.IsAsciiLetterOrDigit(transport[0]))
        {
            throw new ArgumentException(
                string.Concat("Invalid transport: ", transport ?? "null"),
                nameof(transport));
        }

        foreach (char current in transport.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(current) && current != '-')
            {
                throw new ArgumentException(
                    string.Concat("Invalid transport: ", transport),
                    nameof(transport));
            }
        }
    }

    /// <summary>
    /// Validates endpoint metadata the way the server does.
    /// </summary>
    /// <param name="metadata">The metadata, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException">A key or value is invalid.</exception>
    private static void ValidateMetadata(Dictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return;
        }

        if (metadata.Count > MaxMetadataEntries)
        {
            throw new ArgumentException(
                "Endpoint metadata exceeds 32 entries.",
                nameof(metadata));
        }

        foreach (KeyValuePair<string, string> entry in metadata)
        {
            if (string.IsNullOrEmpty(entry.Key)
                || CodePointCount(entry.Key) > MaxMetadataKeyLength)
            {
                throw new ArgumentException(
                    string.Concat("Invalid Endpoint metadata key: ", entry.Key ?? "null"),
                    nameof(metadata));
            }

            if (entry.Value is null || CodePointCount(entry.Value) > MaxMetadataValueLength)
            {
                throw new ArgumentException(
                    string.Concat("Invalid Endpoint metadata value for key: ", entry.Key),
                    nameof(metadata));
            }
        }
    }

    /// <summary>
    /// Counts Unicode code points, treating a surrogate pair as one.
    /// </summary>
    /// <param name="value">The string.</param>
    /// <returns>The number of code points.</returns>
    internal static int CodePointCount(string value)
    {
        int count = 0;

        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i])
                && i + 1 < value.Length
                && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }

            count++;
        }

        return count;
    }

    /// <summary>
    /// Builds the error a malformed URI raises.
    /// </summary>
    /// <param name="value">The URI.</param>
    /// <param name="cause">The underlying parser failure, when there is one.</param>
    /// <returns>The exception.</returns>
    private static ArgumentException InvalidUri(string? value, Exception? cause = null)
        => new(string.Concat("Invalid Endpoint URI: ", value ?? "null"), cause);

    /// <summary>
    /// The host and optional port of an authority.
    /// </summary>
    /// <param name="Host">The host, without IPv6 brackets.</param>
    /// <param name="Port">The port as written, or <see langword="null"/>.</param>
    /// <param name="IsIpv6">Whether the host is an IPv6 literal.</param>
    private readonly record struct Authority(string Host, string? Port, bool IsIpv6);
}
