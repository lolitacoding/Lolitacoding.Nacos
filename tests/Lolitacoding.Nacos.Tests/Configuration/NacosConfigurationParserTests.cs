using Lolitacoding.Nacos.Extensions.Configuration;

namespace Lolitacoding.Nacos.Tests.Configuration;

/// <summary>
/// Verifies how raw Nacos configuration is turned into flat configuration keys.
/// </summary>
public sealed class NacosConfigurationParserTests
{
    [Fact]
    public void Parse_FlattensNestedJsonObjects()
    {
        const string Json = """
            {
              "App": { "Greeting": "hello", "Retries": 3, "Enabled": true, "Missing": null },
              "Logging": { "LogLevel": { "Default": "Information" } }
            }
            """;

        Dictionary<string, string?> values = NacosConfigurationParser.Parse("app.json", "json", Json);

        values["App:Greeting"].ShouldBe("hello");
        values["App:Retries"].ShouldBe("3");
        values["App:Enabled"].ShouldBe("true");
        values["App:Missing"].ShouldBeNull();
        values["Logging:LogLevel:Default"].ShouldBe("Information");
    }

    [Fact]
    public void Parse_FlattensArraysWithNumericIndices()
    {
        const string Json = """{ "Hosts": ["a", "b"] }""";

        Dictionary<string, string?> values = NacosConfigurationParser.Parse("app.json", "json", Json);

        values["Hosts:0"].ShouldBe("a");
        values["Hosts:1"].ShouldBe("b");
    }

    [Fact]
    public void Parse_KeepsTheRawDocumentUnderItsDataId()
    {
        // A consumer that wants the document as authored — rather than a flattened view — must be
        // able to ask for it by data id.
        const string Json = """{ "App": { "Greeting": "hello" } }""";

        Dictionary<string, string?> values = NacosConfigurationParser.Parse("app.json", "json", Json);

        values["app.json"].ShouldBe(Json);
    }

    [Fact]
    public void Parse_FlattensPropertiesWithEqualsAndColon()
    {
        const string Properties = """
            # a comment
            ! another comment
            app.greeting=hello
            app.timeout : 30

            """;

        Dictionary<string, string?> values =
            NacosConfigurationParser.Parse("app.properties", "properties", Properties);

        values["app.greeting"].ShouldBe("hello");
        values["app.timeout"].ShouldBe("30");
    }

    [Fact]
    public void Parse_FlattensYamlMappingsAndSequences()
    {
        const string Yaml = """
            App:
              Greeting: hello
              Ports:
                - 8080
                - 8081
            """;

        Dictionary<string, string?> values = NacosConfigurationParser.Parse("app.yaml", "yaml", Yaml);

        values["App:Greeting"].ShouldBe("hello");
        values["App:Ports:0"].ShouldBe("8080");
        values["App:Ports:1"].ShouldBe("8081");
    }

    [Fact]
    public void Parse_KeepsPlainTextVerbatim()
    {
        const string Text = "just some text";

        Dictionary<string, string?> values = NacosConfigurationParser.Parse("app.txt", "text", Text);

        values.Count.ShouldBe(1);
        values["app.txt"].ShouldBe(Text);
    }

    [Fact]
    public void Parse_DoesNotThrowOnMalformedJson()
    {
        // A malformed document must not take the process down; the raw value is still available.
        const string Broken = "{ not json";

        Dictionary<string, string?> values = NacosConfigurationParser.Parse("app.json", "json", Broken);

        values["app.json"].ShouldBe(Broken);
        values.Count.ShouldBe(1);
    }

    [Fact]
    public void Parse_DoesNotThrowOnMalformedYaml()
    {
        const string Broken = "key: [unterminated";

        Dictionary<string, string?> values = NacosConfigurationParser.Parse("app.yaml", "yaml", Broken);

        values["app.yaml"].ShouldBe(Broken);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("application/json")]
    [InlineData(".json")]
    [InlineData("JSON")]
    public void ResolveFormat_ReadsTheContentType(string contentType)
    {
        NacosConfigurationParser
            .ResolveFormat("ignored", contentType, "irrelevant")
            .ShouldBe(NacosConfigurationFormat.Json);
    }

    [Theory]
    [InlineData("app.yaml", "Yaml")]
    [InlineData("app.yml", "Yaml")]
    [InlineData("app.properties", "Properties")]
    [InlineData("app.json", "Json")]
    public void ResolveFormat_FallsBackToTheDataIdSuffix(string dataId, string expected)
    {
        NacosConfigurationParser
            .ResolveFormat(dataId, contentType: null, content: "x")
            .ToString()
            .ShouldBe(expected);
    }

    [Fact]
    public void ResolveFormat_SniffsJsonWhenNothingElseSaysSo()
    {
        NacosConfigurationParser
            .ResolveFormat("no-extension", contentType: null, content: "  {\"a\":1}")
            .ShouldBe(NacosConfigurationFormat.Json);
    }

    [Fact]
    public void ResolveFormat_PrefersTheContentTypeOverTheSuffix()
    {
        // An operator who explicitly publishes a document as YAML is taken at their word, even if
        // the data id ends in .json.
        NacosConfigurationParser
            .ResolveFormat("app.json", "yaml", "a: 1")
            .ShouldBe(NacosConfigurationFormat.Yaml);
    }
}
