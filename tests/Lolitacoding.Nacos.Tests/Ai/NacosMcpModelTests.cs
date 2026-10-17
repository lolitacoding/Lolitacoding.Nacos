using System.Text;
using System.Text.Json.Nodes;
using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies the wire form of the MCP models.
/// </summary>
/// <remarks>
/// These models are the protocol contract rather than a client-side projection: Nacos stores the
/// tool list, the resource list and the version detail verbatim, so the member names are what the
/// registry, the console and every other SDK agree on. The names that do not follow the camel-case
/// policy — the MCP <c>_meta</c> member, the official registry's snake-case date fields, the
/// uppercase capability values — are the ones that would otherwise break silently, so they are
/// asserted on the encoded body rather than through the models.
/// </remarks>
public sealed class NacosMcpModelTests
{
    [Fact]
    public void NacosMcpTool_TravelsItsMetaMemberUnderTheUnderscoreNameTheProtocolRequires()
    {
        JsonNode tool = ServersTool(Encode(new QueryMcpServerResponse
        {
            McpServerDetailInfo = new NacosMcpServerDetail
            {
                Name = "weather",
                ToolSpec = new NacosMcpToolSpecification
                {
                    Tools =
                    [
                        new NacosMcpTool
                        {
                            Name = "forecast",
                            Meta = new JsonObject { ["catalog"] = "internal" },
                        },
                    ],
                },
            },
        }));

        // The underscore is part of the MCP protocol's extension convention; "meta" would be a
        // different member as far as an MCP client is concerned.
        tool["_meta"]!["catalog"]!.GetValue<string>().ShouldBe("internal");
        tool["meta"].ShouldBeNull();
    }

    [Fact]
    public void NacosMcpVersionDetail_TravelsTheSnakeCaseNamesTheOfficialRegistryUses()
    {
        JsonNode version = ServersDetail(Encode(new QueryMcpServerResponse
        {
            McpServerDetailInfo = new NacosMcpServerDetail
            {
                Name = "weather",
                VersionDetail = new NacosMcpVersionDetail
                {
                    Version = "1.2.3",
                    ReleaseDate = "2026-01-02",
                    IsLatest = true,
                },
            },
        }))["versionDetail"]!;

        // Nacos stores the official registry's version object verbatim, and it uses snake case there
        // even though every other field in this payload is camel case.
        version["version"]!.GetValue<string>().ShouldBe("1.2.3");
        version["release_date"]!.GetValue<string>().ShouldBe("2026-01-02");
        version["is_latest"]!.GetValue<bool>().ShouldBeTrue();
        version["releaseDate"].ShouldBeNull();
    }

    [Fact]
    public void NacosMcpCapability_TravelsTheUpperCaseNameTheServerStores()
    {
        JsonNode capabilities = ServersDetail(Encode(new QueryMcpServerResponse
        {
            McpServerDetailInfo = new NacosMcpServerDetail
            {
                Name = "weather",
                Capabilities = [NacosMcpCapability.Tool, NacosMcpCapability.Resource],
            },
        }))["capabilities"]!;

        capabilities[0]!.GetValue<string>().ShouldBe("TOOL");
        capabilities[1]!.GetValue<string>().ShouldBe("RESOURCE");
    }

    [Fact]
    public void NacosMcpIcon_TravelsTheMediaTypeAndThemeValuesRatherThanTheirNames()
    {
        JsonNode icon = ServersDetail(Encode(new QueryMcpServerResponse
        {
            McpServerDetailInfo = new NacosMcpServerDetail
            {
                Name = "weather",
                Icons =
                [
                    new NacosMcpIcon
                    {
                        Src = "https://example.test/weather.png",
                        MimeType = NacosMcpIconMimeType.ImagePng,
                        Theme = NacosMcpIconTheme.Dark,
                        Sizes = ["48x48"],
                    },
                ],
            },
        }))["icons"]![0]!;

        icon["src"]!.GetValue<string>().ShouldBe("https://example.test/weather.png");
        icon["mimeType"]!.GetValue<string>().ShouldBe("image/png");
        icon["theme"]!.GetValue<string>().ShouldBe("dark");
        icon["sizes"]![0]!.GetValue<string>().ShouldBe("48x48");
    }

    [Fact]
    public void NacosMcpKeyValueInput_TravelsTheRegistryInputMemberNames()
    {
        JsonNode header = ServersDetail(Encode(new QueryMcpServerResponse
        {
            McpServerDetailInfo = new NacosMcpServerDetail
            {
                Name = "weather",
                BackendEndpoints =
                [
                    new NacosMcpEndpointInfo
                    {
                        Protocol = "http",
                        Address = "10.0.0.7",
                        Port = 8080,
                        Headers =
                        [
                            new NacosMcpKeyValueInput
                            {
                                Name = "Authorization",
                                IsRequired = true,
                                IsSecret = true,
                            },
                        ],
                    },
                ],
            },
        }))["backendEndpoints"]![0]!["headers"]![0]!;

        // The registry names these after the Java field, which keeps the "is" prefix the rest of the
        // payload drops.
        header["name"]!.GetValue<string>().ShouldBe("Authorization");
        header["isRequired"]!.GetValue<bool>().ShouldBeTrue();
        header["isSecret"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void NacosMcpServer_OmitsTheOptionalMembersTheCallerDidNotSupply()
    {
        JsonNode server = JsonNode.Parse(Encode(new ReleaseMcpServerRequest
        {
            NamespaceId = "public",
            McpName = "weather",
            ServerSpecification = new NacosMcpServer
            {
                Name = "weather",
                Version = "1.2.3",
            },
        }))!["serverSpecification"]!;

        // Nacos serialises with Include.NON_NULL, so an unset optional member is absent rather than
        // null. A caller that never mentioned a repository must not be taken as having cleared one.
        foreach (string member in new[]
        {
            "description", "repository", "packages", "icons", "remoteServerConfig", "localServerConfig",
        })
        {
            server[member].ShouldBeNull(member);
        }

        // The two members that do have defaults travel as the server's own defaults.
        server["enabled"]!.GetValue<bool>().ShouldBeTrue();
        server["status"]!.GetValue<string>().ShouldBe("active");
    }

    [Fact]
    public void NacosMcpServerDetail_RoundTripsThroughTheWire()
    {
        var detail = new NacosMcpServerDetail
        {
            NamespaceId = "public",
            Id = "mcp-id-1",
            Name = "weather",
            Protocol = NacosMcpProtocol.Streamable,
            FrontProtocol = NacosMcpProtocol.Sse,
            Description = "Weather tools",
            Enabled = true,
            Status = NacosMcpStatus.Deprecated,
            Version = "1.2.3",
            WebsiteUrl = "https://example.test/weather",
            VersionDetail = new NacosMcpVersionDetail
            {
                Version = "1.2.3",
                ReleaseDate = "2026-01-02",
                IsLatest = true,
            },
            AllVersions =
            [
                new NacosMcpVersionDetail { Version = "1.2.2", IsLatest = false },
                new NacosMcpVersionDetail { Version = "1.2.3", IsLatest = true },
            ],
            Capabilities = [NacosMcpCapability.Tool],
            Repository = new NacosMcpRepository
            {
                Url = "https://github.com/example/weather",
                Source = "github",
                Id = "example/weather",
                Subfolder = "mcp",
            },
            LocalServerConfig = new JsonObject { ["command"] = "npx" },
            RemoteServerConfig = new NacosMcpRemoteServiceConfig
            {
                ServiceRef = new NacosMcpServiceRef
                {
                    NamespaceId = "public",
                    GroupName = "DEFAULT_GROUP",
                    ServiceName = "weather",
                    TransportProtocol = "http",
                },
                ExportPath = "/mcp",
            },
            ToolSpec = new NacosMcpToolSpecification
            {
                SpecificationType = "normal",
                Tools = [new NacosMcpTool { Name = "forecast" }],
                ToolsMeta = new Dictionary<string, NacosMcpToolMeta>(StringComparer.Ordinal)
                {
                    ["forecast"] = new NacosMcpToolMeta { Enabled = false },
                },
                SecuritySchemes =
                [
                    new NacosMcpSecurityScheme { Id = "default", Type = "http", Scheme = "bearer" },
                ],
                Extensions = new JsonObject { ["vendor"] = "example" },
            },
            ResourceSpec = new NacosMcpResourceSpecification
            {
                Resources = [new JsonObject { ["uri"] = "file:///weather.json" }],
                Extensions = new JsonObject { ["vendor"] = "example" },
            },
            BackendEndpoints =
            [
                new NacosMcpEndpointInfo
                {
                    Protocol = "http",
                    Address = "10.0.0.7",
                    Port = 8080,
                    Path = "/mcp",
                },
            ],
            FrontendEndpoints =
            [
                new NacosMcpEndpointInfo
                {
                    Protocol = "https",
                    Address = "gateway.test",
                    Port = 443,
                    Path = "/mcp",
                },
            ],
        };

        QueryMcpServerResponse decoded = PayloadCodec.Decode<QueryMcpServerResponse>(
            PayloadCodec.Encode(new QueryMcpServerResponse { McpServerDetailInfo = detail }))!;

        decoded.McpServerDetailInfo.ShouldNotBeNull();
        NacosMcpServerDetail result = decoded.McpServerDetailInfo!;

        result.ShouldSatisfyAllConditions(
            value => value.Id.ShouldBe("mcp-id-1"),
            value => value.FrontProtocol.ShouldBe(NacosMcpProtocol.Sse),
            value => value.Status.ShouldBe(NacosMcpStatus.Deprecated),
            value => value.WebsiteUrl.ShouldBe("https://example.test/weather"),
            value => value.VersionDetail!.IsLatest.ShouldBe(true),
            value => value.VersionDetail!.ReleaseDate.ShouldBe("2026-01-02"),
            value => value.AllVersions!.Count.ShouldBe(2),
            value => value.Capabilities!.ShouldBe([NacosMcpCapability.Tool]),
            value => value.Repository!.Subfolder.ShouldBe("mcp"),
            value => value.LocalServerConfig!["command"]!.GetValue<string>().ShouldBe("npx"),
            value => value.RemoteServerConfig!.ServiceRef!.ServiceName.ShouldBe("weather"),
            value => value.RemoteServerConfig!.ExportPath.ShouldBe("/mcp"),
            value => value.ToolSpec!.Tools![0].Name.ShouldBe("forecast"),
            value => value.ToolSpec!.ToolsMeta!["forecast"].Enabled.ShouldBeFalse(),
            value => value.ToolSpec!.SecuritySchemes![0].Scheme.ShouldBe("bearer"),
            value => value.ResourceSpec!.Resources![0]!["uri"]!.GetValue<string>().ShouldBe("file:///weather.json"),
            value => value.BackendEndpoints![0].Port.ShouldBe(8080),
            value => value.FrontendEndpoints![0].Address.ShouldBe("gateway.test"));
    }

    private static string Encode(NacosRequest request) =>
        Encoding.UTF8.GetString(PayloadCodec.ReadBodyBytes(PayloadCodec.Encode(request, null)));

    private static string Encode(NacosResponse response) =>
        Encoding.UTF8.GetString(PayloadCodec.ReadBodyBytes(PayloadCodec.Encode(response)));

    private static JsonNode ServersDetail(string json) =>
        JsonNode.Parse(json)!["mcpServerDetailInfo"]!;

    private static JsonNode ServersTool(string json) =>
        ServersDetail(json)["toolSpec"]!["tools"]![0]!;
}
