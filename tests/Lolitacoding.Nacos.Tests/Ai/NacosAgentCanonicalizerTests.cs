using System.Text.Json;
using Lolitacoding.Nacos.Ai;
using Shouldly;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies the canonical form an HTTP agent watch is registered with.
/// </summary>
/// <remarks>
/// The batch watch compares fingerprints rather than state, so the client has to reproduce the
/// server's canonical JSON exactly. These tests pin the Java implementation's known vector, the
/// members that are excluded, the ordering rules, the number and string normalisation, and the URI
/// forms the endpoint canonicaliser accepts.
/// </remarks>
public sealed class NacosAgentCanonicalizerTests
{
    [Fact]
    public void Fingerprint_MatchesTheVectorTheJavaImplementationProduces()
    {
        var result = new NacosAgentDiscoveryResult
        {
            AgentName = "AgentA",
            NamespaceId = "public",
            Version = "1.0.0",
            ContentDigest = string.Concat("sha256:", new string('a', 64)),
        };

        NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result).ShouldBe(
            string.Concat(
                "{\"agentName\":\"AgentA\",\"callInterfaces\":[],\"contentDigest\":\"sha256:",
                new string('a', 64),
                "\",\"namespaceId\":\"public\",\"version\":\"1.0.0\"}"));

        NacosAgentDiscoveryCanonicalizer.Fingerprint(result).ShouldBe(
            "sha256-canonical-json-v1:3362e4414540623a15d86c0c0b99a0b76f82a203c3f26eb905b9db43444436d5");
    }

    [Fact]
    public void CanonicalResultJson_OmitsAnAbsentDescriptionAndEmptyTags()
    {
        NacosAgentDiscoveryResult result = Result();
        result.Description = null;
        result.Tags = [];

        string canonical = NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result);

        canonical.ShouldNotContain("\"description\"");
        canonical.ShouldNotContain("\"tags\"");

        result.Tags = null;

        NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result).ShouldBe(canonical);
    }

    [Fact]
    public void CanonicalResultJson_SortsTagsWithoutDeduplicatingThem()
    {
        NacosAgentDiscoveryResult result = Result();
        result.Tags = ["planner", "a2a", "planner"];

        NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result).ShouldContain(
            "\"tags\":[\"a2a\",\"planner\",\"planner\"]");
    }

    [Fact]
    public void CanonicalResultJson_DefaultsAnAbsentNamespaceToPublic()
    {
        NacosAgentDiscoveryResult result = Result();
        result.NamespaceId = null;

        NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result).ShouldContain(
            "\"namespaceId\":\"public\"");

        result.NamespaceId = string.Empty;

        NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result).ShouldContain(
            "\"namespaceId\":\"public\"");
    }

    [Fact]
    public void CanonicalResultJson_KeepsTheContractOrderOfCallInterfaces()
    {
        NacosAgentDiscoveryResult result = Result();
        result.CallInterfaces =
        [
            new NacosAgentCallInterface { Protocol = "a2a" },
            new NacosAgentCallInterface { Protocol = "mcp" },
        ];

        string canonical = NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result);

        canonical.IndexOf("\"protocol\":\"a2a\"", StringComparison.Ordinal)
            .ShouldBeLessThan(canonical.IndexOf("\"protocol\":\"mcp\"", StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalResultJson_SortsMetadataAndNormalisesNumbers()
    {
        NacosAgentDiscoveryResult result = Result();
        result.CallInterfaces =
        [
            new NacosAgentCallInterface
            {
                Protocol = "a2a",
                EndpointSets =
                [
                    new NacosAgentEndpointSet
                    {
                        Source = "RUNTIME",
                        SourceRevision = "rev",
                        LastUpdatedTime = 123,
                        Endpoints =
                        [
                            new NacosAgentRuntimeEndpoint
                            {
                                Uri = "HTTPS://Example.COM/a2a",
                                Transport = "JSONRPC",
                                Priority = 0,
                                Weight = 1.5,
                                Healthy = true,
                                Enabled = false,
                                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["z"] = "last",
                                    ["a"] = "first",
                                },
                                Bindings =
                                [
                                    new NacosAgentVersionBinding
                                    {
                                        RuntimeVersion = "1.0.0",
                                        VersionRange = ">=1.0.0",
                                    },
                                ],
                            },
                        ],
                    },
                ],
            },
        ];

        string canonical = NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result);

        canonical.ShouldContain(
            "{\"bindings\":[{\"runtimeVersion\":\"1.0.0\",\"versionRange\":\">=1.0.0\"}],"
            + "\"healthy\":true,\"metadata\":{\"a\":\"first\",\"z\":\"last\"},\"priority\":0,"
            + "\"transport\":\"JSONRPC\",\"uri\":\"https://example.com:443/a2a\",\"weight\":1.5}");

        // Management state is not part of the answer, so a change to it is not a change to the
        // projection the watch follows.
        canonical.ShouldNotContain("lastUpdatedTime");
        canonical.ShouldNotContain("enabled");
        canonical.ShouldContain("\"sourceRevision\":\"rev\"");
    }

    [Fact]
    public void CanonicalResultJson_SortsNativeDescriptorKeysAndNormalisesItsNumbers()
    {
        using var descriptor = JsonDocument.Parse(
            "{\"b\":1.500,\"a\":[2e3,true,null],\"nested\":{\"z\":\"s\",\"y\":-0.0}}");

        NacosAgentDiscoveryResult result = Result();
        result.CallInterfaces =
        [
            new NacosAgentCallInterface
            {
                Protocol = "a2a",
                NativeDescriptor = descriptor.RootElement.Clone(),
            },
        ];

        NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result).ShouldContain(
            "\"nativeDescriptor\":{\"a\":[2000,true,null],\"b\":1.5,"
            + "\"nested\":{\"y\":0,\"z\":\"s\"}}");
    }

    [Fact]
    public void CanonicalResultJson_EscapesLoneSurrogates()
    {
        NacosAgentDiscoveryResult result = Result();
        result.Description = "a\uD800b";

        NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result).ShouldContain(
            "\"description\":\"a\\ud800b\"");
    }

    [Fact]
    public void CanonicalResultJson_RejectsANullCallInterface()
    {
        NacosAgentDiscoveryResult result = Result();
        result.CallInterfaces = [null!];

        Should.Throw<ArgumentException>(
            () => NacosAgentDiscoveryCanonicalizer.CanonicalResultJson(result));
    }

    [Theory]
    [InlineData("1.500", "1.5")]
    [InlineData("100", "100")]
    [InlineData("1e3", "1000")]
    [InlineData("1E-7", "0.0000001")]
    [InlineData("-0.0", "0")]
    [InlineData("0.0010", "0.001")]
    [InlineData("12.3400", "12.34")]
    public void NormalizeNumber_MatchesBigDecimalPlainString(string raw, string expected) =>
        NacosAgentDiscoveryCanonicalizer.NormalizeNumber(raw).ShouldBe(expected);

    [Theory]
    [InlineData("HTTPS://Example.COM/a2a?mode=sync", "https://example.com:443/a2a?mode=sync")]
    [InlineData("https://B\u00DCCHER.example/a2a", "https://xn--bcher-kva.example:443/a2a")]
    [InlineData("http://192.168.001.010/a2a", "http://192.168.1.10:80/a2a")]
    [InlineData(
        "https://[2001:0DB8:0:0:0:0:0:1]/a2a",
        "https://[2001:db8::1]:443/a2a")]
    [InlineData("https://[::FFFF:192.0.2.1]", "https://[::ffff:c000:201]:443")]
    [InlineData("grpc://Example.COM:9090/call", "grpc://example.com:9090/call")]
    [InlineData("https://example.com/a%20b?q=1%202", "https://example.com:443/a%20b?q=1%202")]
    public void CanonicalizeUri_MatchesTheJavaCases(string value, string expected) =>
        NacosAgentEndpointCanonicalizer.CanonicalizeUri(value).ShouldBe(expected);

    [Theory]
    [InlineData("grpc://example.com/call")]
    [InlineData("https://example.com/a2a#fragment")]
    [InlineData("https://user@example.com/a2a")]
    [InlineData("mailto:planner@example.com")]
    [InlineData("https://example.com/a b")]
    [InlineData("https://example.com/a%2")]
    [InlineData("/relative")]
    [InlineData("")]
    public void CanonicalizeUri_RejectsAnUriAnEndpointMayNotCarry(string value) =>
        Should.Throw<ArgumentException>(
            () => NacosAgentEndpointCanonicalizer.CanonicalizeUri(value));

    /// <summary>Builds the smallest result the canonicaliser accepts.</summary>
    /// <returns>The result.</returns>
    private static NacosAgentDiscoveryResult Result() => new()
    {
        AgentName = "planner",
        NamespaceId = "public",
        Version = "1.0.0",
        ContentDigest = "sha256:digest",
    };
}
