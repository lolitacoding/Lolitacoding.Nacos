using System.Globalization;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Lolitacoding.Nacos.Extensions.Configuration;

/// <summary>
/// Turns the raw text of a Nacos configuration into the flat key/value form that
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> expects.
/// </summary>
/// <remarks>
/// <para>
/// Nacos stores a configuration as opaque text and only labels its type; it is the client that
/// decides how to read it. Structured documents are therefore flattened with the standard
/// <c>:</c> separator and numeric array indices, so that a JSON body of
/// <c>{"App":{"Greeting":"hi"}}</c> is reachable as <c>App:Greeting</c>.
/// </para>
/// <para>
/// A document that cannot be understood as one of the structured formats is kept verbatim under
/// its data id, so <c>configuration[dataId]</c> always returns something usable even for a plain
/// text value.
/// </para>
/// </remarks>
internal static class NacosConfigurationParser
{
    /// <summary>
    /// The separator that separates nested keys in an
    /// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.
    /// </summary>
    private const char KeySeparator = ':';

    /// <summary>
    /// Parses configuration content into flat keys.
    /// </summary>
    /// <param name="dataId">The data id, used as the format hint of last resort.</param>
    /// <param name="contentType">The content type the server reported, when known.</param>
    /// <param name="content">The raw content.</param>
    /// <returns>
    /// A map of flat keys to values. The raw content is always included under
    /// <paramref name="dataId"/> so a consumer can retrieve the document as authored.
    /// </returns>
    public static Dictionary<string, string?> Parse(string dataId, string? contentType, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ArgumentNullException.ThrowIfNull(content);

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [dataId] = content,
        };

        switch (ResolveFormat(dataId, contentType, content))
        {
            case NacosConfigurationFormat.Json:
                FlattenJson(values, prefix: string.Empty, content);
                break;

            case NacosConfigurationFormat.Yaml:
                FlattenYaml(values, prefix: string.Empty, content);
                break;

            case NacosConfigurationFormat.Properties:
                FlattenProperties(values, content);
                break;

            default:
                // Plain text: the data id key added above is the whole result.
                break;
        }

        return values;
    }

    /// <summary>
    /// Decides how to read a configuration from its type, its data id and its content.
    /// </summary>
    /// <param name="dataId">The data id.</param>
    /// <param name="contentType">The content type the server reported, when known.</param>
    /// <param name="content">The content, used as a last-resort sniff.</param>
    /// <returns>The format to use.</returns>
    /// <remarks>
    /// The server's content type is authoritative when present, because it is what an operator
    /// explicitly chose when publishing. The data id suffix is the next best signal, since Nacos
    /// conventions use it even when no type was set. Sniffing the content is only used when
    /// neither is decisive.
    /// </remarks>
    internal static NacosConfigurationFormat ResolveFormat(
        string dataId,
        string? contentType,
        string content)
    {
        if (TryParseFormat(contentType, out NacosConfigurationFormat fromType))
        {
            return fromType;
        }

        if (TryParseFormat(Path.GetExtension(dataId), out NacosConfigurationFormat fromExtension))
        {
            return fromExtension;
        }

        ReadOnlySpan<char> trimmed = content.AsSpan().TrimStart();

        if (!trimmed.IsEmpty && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            return NacosConfigurationFormat.Json;
        }

        return NacosConfigurationFormat.Text;
    }

    /// <summary>
    /// Maps a type label or file extension to a format.
    /// </summary>
    /// <param name="hint">The label or extension, which may have a leading dot.</param>
    /// <param name="format">The resolved format.</param>
    /// <returns><see langword="true"/> when the hint named a known format.</returns>
    private static bool TryParseFormat(string? hint, out NacosConfigurationFormat format)
    {
        format = NacosConfigurationFormat.Text;

        if (string.IsNullOrWhiteSpace(hint))
        {
            return false;
        }

        ReadOnlySpan<char> normalized = hint.AsSpan().Trim();

        if (normalized.StartsWith("."))
        {
            normalized = normalized[1..];
        }

        if (normalized.Equals("json", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            format = NacosConfigurationFormat.Json;
            return true;
        }

        if (normalized.Equals("yaml", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yml", StringComparison.OrdinalIgnoreCase))
        {
            format = NacosConfigurationFormat.Yaml;
            return true;
        }

        if (normalized.Equals("properties", StringComparison.OrdinalIgnoreCase))
        {
            format = NacosConfigurationFormat.Properties;
            return true;
        }

        if (normalized.Equals("text", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("txt", StringComparison.OrdinalIgnoreCase))
        {
            format = NacosConfigurationFormat.Text;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Flattens a JSON document into the target map.
    /// </summary>
    /// <param name="target">The map to write into.</param>
    /// <param name="prefix">The key prefix for the current nesting level.</param>
    /// <param name="content">The JSON text.</param>
    /// <remarks>
    /// A malformed document is left unflattened rather than throwing: the raw content is already
    /// present under the data id, and refusing to start because one optional document is
    /// malformed would be a worse failure mode than ignoring the structured view of it.
    /// </remarks>
    private static void FlattenJson(Dictionary<string, string?> target, string prefix, string content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            FlattenJsonElement(target, prefix, document.RootElement);
        }
        catch (JsonException)
        {
            // Deliberately ignored; see the remarks.
        }
    }

    /// <summary>
    /// Flattens a single JSON element and its descendants.
    /// </summary>
    /// <param name="target">The map to write into.</param>
    /// <param name="prefix">The key prefix.</param>
    /// <param name="element">The element to flatten.</param>
    private static void FlattenJsonElement(
        Dictionary<string, string?> target,
        string prefix,
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    FlattenJsonElement(target, Combine(prefix, property.Name), property.Value);
                }

                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string key = index.ToString(CultureInfo.InvariantCulture);
                    FlattenJsonElement(target, Combine(prefix, key), item);
                    index++;
                }

                break;

            case JsonValueKind.String:
                target[prefix] = element.GetString();
                break;

            case JsonValueKind.Number:
                // The raw text is preserved so an integer stays an integer and a decimal keeps its
                // precision, rather than round-tripping through a double.
                target[prefix] = element.GetRawText();
                break;

            case JsonValueKind.True:
                target[prefix] = "true";
                break;

            case JsonValueKind.False:
                target[prefix] = "false";
                break;

            case JsonValueKind.Null:
                target[prefix] = null;
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Flattens a YAML document into the target map.
    /// </summary>
    /// <param name="target">The map to write into.</param>
    /// <param name="prefix">The key prefix for the current nesting level.</param>
    /// <param name="content">The YAML text.</param>
    private static void FlattenYaml(Dictionary<string, string?> target, string prefix, string content)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(content);
            stream.Load(reader);

            if (stream.Documents.Count == 0)
            {
                return;
            }

            FlattenYamlNode(target, prefix, stream.Documents[0].RootNode);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // As with JSON, malformed content degrades to the raw value rather than failing.
        }
    }

    /// <summary>
    /// Flattens a single YAML node and its descendants.
    /// </summary>
    /// <param name="target">The map to write into.</param>
    /// <param name="prefix">The key prefix.</param>
    /// <param name="node">The node to flatten.</param>
    private static void FlattenYamlNode(Dictionary<string, string?> target, string prefix, YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
                {
                    string key = entry.Key is YamlScalarNode scalarKey
                        ? scalarKey.Value ?? string.Empty
                        : entry.Key.ToString();

                    FlattenYamlNode(target, Combine(prefix, key), entry.Value);
                }

                break;

            case YamlSequenceNode sequence:
                int index = 0;
                foreach (YamlNode item in sequence.Children)
                {
                    string key = index.ToString(CultureInfo.InvariantCulture);
                    FlattenYamlNode(target, Combine(prefix, key), item);
                    index++;
                }

                break;

            case YamlScalarNode scalar:
                target[prefix] = scalar.Value;
                break;

            default:
                target[prefix] = node.ToString();
                break;
        }
    }

    /// <summary>
    /// Flattens a Java-style properties document into the target map.
    /// </summary>
    /// <param name="target">The map to write into.</param>
    /// <param name="content">The properties text.</param>
    /// <remarks>
    /// Both <c>=</c> and <c>:</c> separate a key from its value because both are accepted by
    /// Nacos and by the Java tooling that usually produces these files.
    /// </remarks>
    private static void FlattenProperties(Dictionary<string, string?> target, string content)
    {
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();

            if (line.Length == 0 || line[0] is '#' or '!')
            {
                continue;
            }

            int separator = line.IndexOfAny(['=', ':']);
            if (separator <= 0)
            {
                // A line without a separator is not a property; skip it rather than inventing one.
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            if (key.Length > 0)
            {
                target[key] = value;
            }
        }
    }

    /// <summary>
    /// Joins a parent key and a child key.
    /// </summary>
    /// <param name="prefix">The parent key, possibly empty.</param>
    /// <param name="key">The child key.</param>
    /// <returns>The combined key.</returns>
    private static string Combine(string prefix, string key) =>
        prefix.Length == 0 ? key : string.Concat(prefix, KeySeparator, key);
}

/// <summary>
/// The structured formats the provider knows how to flatten.
/// </summary>
internal enum NacosConfigurationFormat
{
    /// <summary>Unstructured text, kept verbatim under its data id.</summary>
    Text,

    /// <summary>A JSON document.</summary>
    Json,

    /// <summary>A YAML document.</summary>
    Yaml,

    /// <summary>A Java-style properties document.</summary>
    Properties,
}
