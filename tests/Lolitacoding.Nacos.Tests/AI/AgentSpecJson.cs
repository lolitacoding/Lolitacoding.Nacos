using System.Text.Json;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Builds the JSON an agent-specification endpoint serves.
/// </summary>
/// <remarks>
/// The shape mirrors what a 3.2.4 server answers: the resource map is keyed by an identifier derived
/// from the type and the name, and each resource carries its own name, type, content, free-form
/// metadata and the identifier that names its path. Tests that care about one member vary it and
/// leave the rest to these defaults.
/// </remarks>
internal static class AgentSpecJson
{
    /// <summary>The manifest the tests publish unless they say otherwise.</summary>
    public const string DefaultContent =
        """{"description":"A worker","worker":{"suggested_name":"worker"}}""";

    /// <summary>
    /// Builds one specification.
    /// </summary>
    /// <param name="name">The specification name.</param>
    /// <param name="description">The description.</param>
    /// <param name="content">The <c>manifest.json</c> content.</param>
    /// <param name="soul">The content of the one resource file.</param>
    /// <param name="includeResource">Whether the specification carries its resource file.</param>
    /// <returns>The specification as JSON.</returns>
    public static string Build(
        string name = "worker",
        string description = "A worker",
        string content = DefaultContent,
        string soul = "You are a worker.",
        bool includeResource = true)
    {
        var specification = new Dictionary<string, object?>
        {
            ["namespaceId"] = "public",
            ["name"] = name,
            ["description"] = description,
            ["bizTags"] = """["probe"]""",
            ["content"] = content,
        };

        if (includeResource)
        {
            specification["resource"] = new Dictionary<string, object?>
            {
                ["config_SOUL__md"] = new Dictionary<string, object?>
                {
                    ["name"] = "SOUL.md",
                    ["type"] = "config",
                    ["content"] = soul,
                    ["metadata"] = new Dictionary<string, object?> { ["uniformId"] = 1 },
                    ["resourceIdentifier"] = "config::SOUL.md",
                },
            };
        }

        return JsonSerializer.Serialize(specification);
    }
}
