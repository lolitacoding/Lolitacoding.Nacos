using System.Text.Json.Nodes;
using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the MCP registry end to end against a real Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// What is worth checking against the real thing is the part the server decides rather than the
/// client: that a release creates a version the registry serves back, that the version it created is
/// the one the caller named, that the tool list is stored and handed back unchanged, and that a read
/// of a name that was never released is reported as an error rather than as an empty server.
/// </para>
/// <para>
/// The container is started from an image of its own. The registry these exercise arrived after the
/// server the rest of the suite is pinned to — a 3.0.2 server answers both a query and a release with
/// <c>RequestHandler Not Found</c> — so <see cref="NacosIntegrationImages.AiImage"/> names the oldest
/// release that serves it.
/// </para>
/// <para>
/// The release is a <c>stdio</c> one because that shape carries its whole description with it: a
/// server reached over the network describes itself through an endpoint, which the registry resolves
/// through the naming module, and nothing about that resolution is MCP-specific.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AiIntegrationTests
{
    /// <summary>The version every release in this class creates.</summary>
    private const string Version = "1.0.0";

    [Fact]
    public async Task ReleaseAndQuery_RoundTripsThroughARealServer()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = NacosHttp.UniqueId("mcp");

        var specification = new NacosMcpServer
        {
            Name = name,
            Protocol = NacosMcpProtocol.Stdio,
            Description = "An MCP server released by the integration suite.",
            VersionDetail = new NacosMcpVersionDetail { Version = Version, IsLatest = true },
            LocalServerConfig = new JsonObject
            {
                ["command"] = "uvx",
                ["args"] = new JsonArray("mcp-server-everything"),
            },
        };

        var tools = new NacosMcpToolSpecification
        {
            Tools =
            [
                new NacosMcpTool
                {
                    Name = "echo",
                    Description = "Echoes its argument.",
                    InputSchema = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
                    },
                },
            ],
        };

        string? id = await client.Ai.Mcp.ReleaseServerAsync(specification, tools, ct: ct);

        id.ShouldNotBeNullOrWhiteSpace();

        // The registry serves a release asynchronously in the sense that the reply says the write was
        // accepted rather than that it is readable, so the read is repeated until it is.
        NacosMcpServerDetail detail = await NacosEventually.BecomesAsync(
            () => client.Ai.Mcp.GetServerAsync(name, ct: ct),
            value => value.VersionDetail?.Version == Version,
            ct);

        detail.Name.ShouldBe(name);
        detail.Protocol.ShouldBe(NacosMcpProtocol.Stdio);
        detail.Description.ShouldBe("An MCP server released by the integration suite.");
        detail.VersionDetail!.IsLatest.ShouldBe(true);

        // Every version the server holds is reported, which is what lets a caller pin an older one.
        detail.AllVersions!.ShouldContain(version => version.Version == Version);

        // The tool list travels as the MCP protocol's own object, so getting it back unchanged is
        // what shows the client stored and read it without rewriting it.
        IList<NacosMcpTool> releasedTools = detail.ToolSpec!.Tools!;

        releasedTools.ShouldContain(tool => tool.Name == "echo");

        NacosMcpTool echoed = releasedTools.Single(tool => tool.Name == "echo");

        echoed.InputSchema!["properties"]!["text"]!["type"]!.GetValue<string>().ShouldBe("string");

        // A read that names the version returns it as the one currently served rather than only as
        // a member of the version list.
        NacosMcpServerDetail pinned = await client.Ai.Mcp.GetServerAsync(name, Version, ct);

        pinned.VersionDetail!.Version.ShouldBe(Version);
    }

    [Fact]
    public async Task GetServerAsync_ReportsAnUnknownNameAsAServerError()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;

        // A release first, so that the error below is the registry's answer to an unknown name rather
        // than a server that does not serve the registry at all: those are different failures and only
        // the second one would make this test pass for the wrong reason.
        string released = NacosHttp.UniqueId("mcp-present");

        await client.Ai.Mcp.ReleaseServerAsync(
            new NacosMcpServer
            {
                Name = released,
                Protocol = NacosMcpProtocol.Stdio,
                VersionDetail = new NacosMcpVersionDetail { Version = Version },
                LocalServerConfig = new JsonObject { ["command"] = "uvx" },
            },
            ct: ct);

        // Missing is an error rather than an empty answer, so a caller cannot mistake a typo in a
        // name for a server that exists with nothing in it. The server reports the generic
        // not-found code (the MCP registry's own code travels in the message), which is the same
        // code a missing configuration is reported with.
        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Ai.Mcp.GetServerAsync(NacosHttp.UniqueId("mcp-absent"), ct: ct));

        exception.ErrorCode.ShouldBe(404);
        exception.Message.ShouldContain("not found");
    }

    private static NacosClientFactory CreateClient(NacosContainer container) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                Reconnect = new NacosReconnectOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(500),
                    MaxDelay = TimeSpan.FromSeconds(5),
                    JitterFactor = 0.1,
                },
            },
            httpClient: null,
            channelFactory: null,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance);
}
