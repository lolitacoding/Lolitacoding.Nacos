using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lolitacoding.Nacos.Ai;

/// <summary>
/// Computes the fingerprint a RAD watch sends for the state it already holds.
/// </summary>
/// <remarks>
/// <para>
/// The HTTP watch batch is self-describing: every item carries the fingerprint of the discovery result
/// the caller materialised. The server recomputes the fingerprint of the current projection and only
/// reports the watch as changed when the two differ, so this type has to reproduce the server's
/// canonical form byte for byte. It is a port of the Java <c>AgentDiscoveryCanonicalizer</c> and
/// deliberately uses the same frame, the same object-key ordering, the same string escaping and the
/// same number normalisation.
/// </para>
/// <para>
/// Only the fields that describe the public snapshot take part. Management state such as
/// <c>enabled</c>, the endpoint set's <c>lastUpdatedTime</c> and the call interface's
/// <c>endpointSourceOrder</c> is excluded, because the server's projection does not consider it part
/// of the answer. Arrays keep their contract order: reordering two call interfaces is a different
/// snapshot even though reordering two object keys is not.
/// </para>
/// </remarks>
internal static class NacosAgentDiscoveryCanonicalizer
{
    /// <summary>The algorithm identifier every watch fingerprint starts with.</summary>
    public const string AlgorithmId = "sha256-canonical-json-v1";

    /// <summary>The namespace a result belongs to when it names none.</summary>
    private const string DefaultNamespace = "public";

    /// <summary>
    /// Computes the fingerprint of a complete discovery result.
    /// </summary>
    /// <param name="result">The result the caller holds.</param>
    /// <returns>The algorithm identifier, a colon, and the lower-case SHA-256 digest.</returns>
    /// <exception cref="ArgumentException">The result cannot be canonicalised.</exception>
    public static string Fingerprint(NacosAgentDiscoveryResult result)
    {
        string canonicalJson = CanonicalResultJson(result);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));

        return string.Concat(AlgorithmId, ":", Convert.ToHexStringLower(digest));
    }

    /// <summary>
    /// Writes the canonical JSON form of a discovery result.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <returns>The canonical JSON.</returns>
    /// <exception cref="ArgumentException">The result cannot be canonicalised.</exception>
    public static string CanonicalResultJson(NacosAgentDiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var frame = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["agentName"] = result.AgentName,
            ["callInterfaces"] = CallInterfaceFrames(result.CallInterfaces),
            ["contentDigest"] = result.ContentDigest,
            ["namespaceId"] = string.IsNullOrEmpty(result.NamespaceId)
                ? DefaultNamespace
                : result.NamespaceId,
            ["version"] = result.Version,
        };

        if (result.Description is not null)
        {
            frame["description"] = result.Description;
        }

        if (result.Tags is { Count: > 0 })
        {
            frame["tags"] = result.Tags.Order(StringComparer.Ordinal).ToList();
        }

        return ToCanonicalJson(frame);
    }

    /// <summary>
    /// Writes a value in the canonical JSON form.
    /// </summary>
    /// <param name="value">The value: a map, list, string, boolean, number or <see langword="null"/>.</param>
    /// <returns>The canonical JSON.</returns>
    /// <exception cref="ArgumentException">The value is not representable as canonical JSON.</exception>
    public static string ToCanonicalJson(object? value)
    {
        var builder = new StringBuilder();
        WriteJson(builder, value);

        return builder.ToString();
    }

    /// <summary>
    /// Normalises a number the way <c>BigDecimal.stripTrailingZeros().toPlainString()</c> does.
    /// </summary>
    /// <param name="raw">The number as JSON or as <see cref="double"/> printed it.</param>
    /// <returns>The canonical decimal form.</returns>
    /// <exception cref="ArgumentException">The text is not a finite JSON number.</exception>
    internal static string NormalizeNumber(string raw)
    {
        ArgumentException.ThrowIfNullOrEmpty(raw);

        int index = 0;
        bool negative = false;

        if (raw[index] == '-')
        {
            negative = true;
            index++;
        }
        else if (raw[index] == '+')
        {
            index++;
        }

        int integerStart = index;

        while (index < raw.Length && char.IsAsciiDigit(raw[index]))
        {
            index++;
        }

        if (index == integerStart)
        {
            throw new ArgumentException(
                string.Concat("Invalid JSON number: ", raw),
                nameof(raw));
        }

        var digits = new StringBuilder(raw[integerStart..index]);
        int scale = 0;

        if (index < raw.Length && raw[index] == '.')
        {
            index++;
            int fractionStart = index;

            while (index < raw.Length && char.IsAsciiDigit(raw[index]))
            {
                index++;
            }

            if (index == fractionStart)
            {
                throw new ArgumentException(
                    string.Concat("Invalid JSON number: ", raw),
                    nameof(raw));
            }

            digits.Append(raw, fractionStart, index - fractionStart);
            scale = index - fractionStart;
        }

        if (index < raw.Length && raw[index] is 'e' or 'E')
        {
            index++;
            bool negativeExponent = false;

            if (index < raw.Length && raw[index] is '+' or '-')
            {
                negativeExponent = raw[index] == '-';
                index++;
            }

            int exponentStart = index;

            while (index < raw.Length && char.IsAsciiDigit(raw[index]))
            {
                index++;
            }

            if (index == exponentStart
                || !int.TryParse(
                    raw.AsSpan(exponentStart, index - exponentStart),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int exponent))
            {
                throw new ArgumentException(
                    string.Concat("Invalid JSON number: ", raw),
                    nameof(raw));
            }

            scale -= negativeExponent ? -exponent : exponent;
        }

        if (index != raw.Length)
        {
            throw new ArgumentException(
                string.Concat("Invalid JSON number: ", raw),
                nameof(raw));
        }

        int firstNonZero = 0;

        while (firstNonZero < digits.Length && digits[firstNonZero] == '0')
        {
            firstNonZero++;
        }

        if (firstNonZero == digits.Length)
        {
            return "0";
        }

        digits.Remove(0, firstNonZero);

        while (digits.Length > 0 && digits[^1] == '0')
        {
            digits.Length--;
            scale--;
        }

        var result = new StringBuilder(digits.Length + Math.Abs(scale) + 3);

        if (negative)
        {
            result.Append('-');
        }

        if (scale <= 0)
        {
            result.Append(digits);

            for (int i = 0; i < -scale; i++)
            {
                result.Append('0');
            }
        }
        else if (digits.Length > scale)
        {
            result.Append(digits, 0, digits.Length - scale);
            result.Append('.');
            result.Append(digits, digits.Length - scale, scale);
        }
        else
        {
            result.Append("0.");

            for (int i = 0; i < scale - digits.Length; i++)
            {
                result.Append('0');
            }

            result.Append(digits);
        }

        return result.ToString();
    }

    /// <summary>
    /// Builds the frame of one call interface.
    /// </summary>
    /// <param name="callInterfaces">The interfaces, or <see langword="null"/> for none.</param>
    /// <returns>The frames.</returns>
    private static List<object?> CallInterfaceFrames(List<NacosAgentCallInterface>? callInterfaces)
    {
        var result = new List<object?>(callInterfaces?.Count ?? 0);

        if (callInterfaces is null)
        {
            return result;
        }

        foreach (NacosAgentCallInterface? callInterface in callInterfaces)
        {
            if (callInterface is null)
            {
                throw new ArgumentException("A discovery result must not contain a null call interface.");
            }

            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["descriptorMediaType"] = callInterface.DescriptorMediaType,
                ["endpointSets"] = EndpointSetFrames(callInterface.EndpointSets),
                ["nativeDescriptor"] = CanonicalJsonValue(callInterface.NativeDescriptor),
                ["protocol"] = callInterface.Protocol,
                ["protocolVersion"] = callInterface.ProtocolVersion,
            });
        }

        return result;
    }

    /// <summary>
    /// Builds the frames of one interface's endpoint sets.
    /// </summary>
    /// <param name="endpointSets">The sets, or <see langword="null"/> for none.</param>
    /// <returns>The frames.</returns>
    private static List<object?> EndpointSetFrames(List<NacosAgentEndpointSet>? endpointSets)
    {
        var result = new List<object?>(endpointSets?.Count ?? 0);

        if (endpointSets is null)
        {
            return result;
        }

        foreach (NacosAgentEndpointSet? endpointSet in endpointSets)
        {
            if (endpointSet is null)
            {
                throw new ArgumentException("A discovery result must not contain a null endpoint set.");
            }

            if (endpointSet.Source is null)
            {
                throw new ArgumentException("An endpoint set must name its source.");
            }

            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["endpoints"] = EndpointFrames(endpointSet.Endpoints),
                ["source"] = endpointSet.Source,
                ["sourceRevision"] = endpointSet.SourceRevision,
            });
        }

        return result;
    }

    /// <summary>
    /// Builds the frames of one endpoint set's endpoints.
    /// </summary>
    /// <param name="endpoints">The endpoints, or <see langword="null"/> for none.</param>
    /// <returns>The frames.</returns>
    private static List<object?> EndpointFrames(List<NacosAgentRuntimeEndpoint>? endpoints)
    {
        var result = new List<object?>(endpoints?.Count ?? 0);

        if (endpoints is null)
        {
            return result;
        }

        foreach (NacosAgentRuntimeEndpoint? endpoint in endpoints)
        {
            if (endpoint is null)
            {
                throw new ArgumentException("A discovery result must not contain a null endpoint.");
            }

            NacosAgentRuntimeEndpoint canonical = NacosAgentEndpointCanonicalizer.Canonicalize(endpoint);

            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["bindings"] = BindingFrames(canonical.Bindings),
                ["healthy"] = canonical.Healthy,
                ["metadata"] = canonical.Metadata is { Count: > 0 }
                    ? canonical.Metadata
                    : new Dictionary<string, string>(StringComparer.Ordinal),
                ["priority"] = canonical.Priority,
                ["transport"] = canonical.Transport,
                ["uri"] = canonical.Uri,
                ["weight"] = canonical.Weight,
            });
        }

        return result;
    }

    /// <summary>
    /// Builds the frames of one endpoint's version bindings.
    /// </summary>
    /// <param name="bindings">The bindings, or <see langword="null"/> for none.</param>
    /// <returns>The frames.</returns>
    private static List<object?> BindingFrames(List<NacosAgentVersionBinding>? bindings)
    {
        var result = new List<object?>(bindings?.Count ?? 0);

        if (bindings is null)
        {
            return result;
        }

        foreach (NacosAgentVersionBinding? binding in bindings)
        {
            if (binding is null)
            {
                throw new ArgumentException("A discovery result must not contain a null version binding.");
            }

            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["runtimeVersion"] = binding.RuntimeVersion,
                ["versionRange"] = binding.VersionRange,
            });
        }

        return result;
    }

    /// <summary>
    /// Converts a descriptor value into the canonical JSON object graph.
    /// </summary>
    /// <param name="descriptor">The descriptor, or <see langword="null"/>.</param>
    /// <returns>The graph value.</returns>
    private static object? CanonicalJsonValue(JsonElement? descriptor)
    {
        if (descriptor is null || descriptor.Value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return CanonicalJsonValue(descriptor.Value);
    }

    /// <summary>
    /// Converts one JSON element into the canonical JSON object graph.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The graph value.</returns>
    private static object? CanonicalJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => new CanonicalNumber(element.GetRawText()),
        JsonValueKind.Array => CanonicalArray(element),
        JsonValueKind.Object => CanonicalObject(element),
        _ => throw new ArgumentException("The native descriptor contains a value that is not JSON."),
    };

    /// <summary>
    /// Converts a JSON array into the canonical JSON object graph.
    /// </summary>
    /// <param name="element">The array.</param>
    /// <returns>The list.</returns>
    private static List<object?> CanonicalArray(JsonElement element)
    {
        var result = new List<object?>(element.GetArrayLength());

        foreach (JsonElement item in element.EnumerateArray())
        {
            result.Add(CanonicalJsonValue(item));
        }

        return result;
    }

    /// <summary>
    /// Converts a JSON object into the canonical JSON object graph.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <returns>The map.</returns>
    private static Dictionary<string, object?> CanonicalObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            result[property.Name] = CanonicalJsonValue(property.Value);
        }

        return result;
    }

    /// <summary>
    /// Writes one value as canonical JSON.
    /// </summary>
    /// <param name="builder">The builder receiving the text.</param>
    /// <param name="value">The value.</param>
    private static void WriteJson(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;

            case string text:
                WriteString(builder, text);
                break;

            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;

            case int number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;

            case long number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;

            case double number:
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new ArgumentException("Canonical JSON cannot contain a non-finite number.");
                }

                builder.Append(NormalizeNumber(
                    number == 0D
                        ? "0"
                        : number.ToString("R", CultureInfo.InvariantCulture)));
                break;

            case CanonicalNumber number:
                builder.Append(NormalizeNumber(number.Raw));
                break;

            case IReadOnlyDictionary<string, object?> map:
                WriteMap(builder, map);
                break;

            case IReadOnlyDictionary<string, string> map:
                WriteMap(builder, map);
                break;

            case System.Collections.IEnumerable sequence:
                WriteSequence(builder, sequence);
                break;

            default:
                throw new ArgumentException(
                    string.Concat(
                        "Canonical JSON contains an unsupported value: ",
                        value.GetType().FullName));
        }
    }

    /// <summary>
    /// Writes a map with its keys in ordinal order.
    /// </summary>
    /// <param name="builder">The builder receiving the text.</param>
    /// <param name="map">The map.</param>
    private static void WriteMap(
        StringBuilder builder,
        IReadOnlyDictionary<string, object?> map)
    {
        builder.Append('{');
        bool first = true;

        foreach (string key in map.Keys.Order(StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            WriteString(builder, key);
            builder.Append(':');
            WriteJson(builder, map[key]);
        }

        builder.Append('}');
    }

    /// <summary>
    /// Writes a map with its keys in ordinal order.
    /// </summary>
    /// <param name="builder">The builder receiving the text.</param>
    /// <param name="map">The map.</param>
    private static void WriteMap(
        StringBuilder builder,
        IReadOnlyDictionary<string, string> map)
    {
        builder.Append('{');
        bool first = true;

        foreach (string key in map.Keys.Order(StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            WriteString(builder, key);
            builder.Append(':');
            WriteString(builder, map[key]);
        }

        builder.Append('}');
    }

    /// <summary>
    /// Writes a sequence.
    /// </summary>
    /// <param name="builder">The builder receiving the text.</param>
    /// <param name="sequence">The sequence.</param>
    private static void WriteSequence(StringBuilder builder, System.Collections.IEnumerable sequence)
    {
        builder.Append('[');
        bool first = true;

        foreach (object? item in sequence)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            WriteJson(builder, item);
        }

        builder.Append(']');
    }

    /// <summary>
    /// Writes a string with the escapes the canonical form requires.
    /// </summary>
    /// <param name="builder">The builder receiving the text.</param>
    /// <param name="value">The string.</param>
    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');

        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];

            switch (current)
            {
                case '"':
                    builder.Append("\\\"");
                    break;

                case '\\':
                    builder.Append("\\\\");
                    break;

                case '\b':
                    builder.Append("\\b");
                    break;

                case '\f':
                    builder.Append("\\f");
                    break;

                case '\n':
                    builder.Append("\\n");
                    break;

                case '\r':
                    builder.Append("\\r");
                    break;

                case '\t':
                    builder.Append("\\t");
                    break;

                default:
                    if (current < 0x20)
                    {
                        WriteUnicodeEscape(builder, current);
                    }
                    else if (char.IsHighSurrogate(current))
                    {
                        if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                        {
                            builder.Append(current).Append(value[++i]);
                        }
                        else
                        {
                            WriteUnicodeEscape(builder, current);
                        }
                    }
                    else if (char.IsLowSurrogate(current))
                    {
                        WriteUnicodeEscape(builder, current);
                    }
                    else
                    {
                        builder.Append(current);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// Writes one UTF-16 code unit as a lowercase <c>\uXXXX</c> escape.
    /// </summary>
    /// <param name="builder">The builder receiving the text.</param>
    /// <param name="value">The code unit.</param>
    private static void WriteUnicodeEscape(StringBuilder builder, char value)
        => builder.Append("\\u").Append(((int)value).ToString("x4", CultureInfo.InvariantCulture));

    /// <summary>
    /// A number that still carries its JSON spelling, so it can be normalised exactly.
    /// </summary>
    /// <param name="Raw">The number as written.</param>
    private readonly record struct CanonicalNumber(string Raw);
}
