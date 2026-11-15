using System.Text.Json.Nodes;
using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the MCP registry's HTTP transport end to end against a real Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// The scripted handler in the unit tests agrees with whatever the client builds, so only the real
/// endpoint proves that the path, the query names, the form fields and the result envelope are the
/// server's. What is worth checking here is therefore the same thing the gRPC integration tests
/// check — that a release creates a version the registry serves back, with the tool list unchanged —
/// but over the client OpenAPI rather than the AI connection.
/// </para>
/// <para>
/// The container is started from an image of its own. The HTTP endpoints of the MCP registry are
/// marked <c>@Since("3.3.0")</c> on the server, while the gRPC handlers exist from 3.0.3 on, so a
/// server that serves <see cref="AIIntegrationTests"/> would answer these with a 404.
/// </para>
/// <para>
/// The draft flag is deliberately not exercised here. A stock 3.3.0-RC server refuses a draft
/// release with a conflict until its lifecycle cutover has run, so the only thing an integration test
/// could observe is that migration gate rather than the client. What the client sends is pinned by
/// the unit tests, and the gRPC path's ability check exists for the same reason.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class McpHttpIntegrationTests
{
    /// <summary>The version every release in this class creates.</summary>
    private const string Version = "1.0.0";

    [Fact]
    public async Task ReleaseAndQuery_OverHttp_RoundTripsThroughARealServer()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.McpHttpImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = NacosHttp.UniqueId("mcp-http");

        var specification = new NacosMcpServer
        {
            Name = name,
            Protocol = NacosMcpProtocol.Stdio,
            Description = "An MCP server released over HTTP by the integration suite.",
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

        string? id = await client.AI.Mcp.ReleaseServerAsync(specification, tools, ct: ct);

        id.ShouldNotBeNullOrWhiteSpace();

        // A release answers before the version is servable, so the read is repeated until it is.
        NacosMcpServerDetail detail = await NacosEventually.BecomesAsync(
            () => client.AI.Mcp.GetServerAsync(name, ct: ct),
            value => value.VersionDetail?.Version == Version && value.ToolSpec?.Tools is { Count: > 0 },
            ct);

        detail.Name.ShouldBe(name);
        detail.Protocol.ShouldBe(NacosMcpProtocol.Stdio);
        detail.Description.ShouldBe("An MCP server released over HTTP by the integration suite.");
        detail.VersionDetail!.IsLatest.ShouldBe(true);

        // The tool list travels as the MCP protocol's own object, so getting it back unchanged is
        // what shows the form's JSON field and the gRPC message carry the same contract.
        NacosMcpTool echoed = detail.ToolSpec!.Tools!.Single(tool => tool.Name == "echo");

        echoed.InputSchema!["properties"]!["text"]!["type"]!.GetValue<string>().ShouldBe("string");
    }

    [Fact]
    public async Task RegisterAndDeregisterEndpoint_OverHttp_RoundTripsThroughARealServer()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.McpHttpImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = NacosHttp.UniqueId("mcp-http-endpoint");

        const int port = 18080;
        const string exportPath = "/mcp/ref";

        // A server reached over the network is published as a naming service, and the read names the
        // one Nacos created on its behalf. The endpoint registered below is an instance of it.
        await client.AI.Mcp.ReleaseServerAsync(RemoteServer(name, exportPath), ct: ct);

        NacosMcpServerDetail released = await NacosEventually.BecomesAsync(
            () => client.AI.Mcp.GetServerAsync(name, Version, ct),
            value => value.RemoteServerConfig?.ServiceRef is not null,
            ct);

        released.RemoteServerConfig!.ServiceRef!.ServiceName.ShouldBe($"{name}::{Version}");

        // The registration is a stateful HTTP write: the server binds it to the client id and expires
        // it unless the client heartbeats. What this test proves is that the write reaches the same
        // naming service the gRPC registration would, and that the read projects it back.
        await client.AI.Mcp.RegisterServerEndpointAsync(name, "127.0.0.1", port, Version, ct);

        NacosMcpServerDetail registered = await NacosEventually.BecomesAsync(
            () => client.AI.Mcp.GetServerAsync(name, Version, ct),
            value => ContainsEndpoint(value, port),
            ct);

        NacosMcpEndpointInfo endpoint =
            registered.BackendEndpoints!.Single(candidate => candidate.Port == port);

        endpoint.Address.ShouldBe("127.0.0.1");

        // The path is the one the release exported the server under, not a property of the endpoint
        // registration: Nacos projects the server's description onto every instance it resolves.
        endpoint.Path.ShouldBe(exportPath);

        await client.AI.Mcp.DeregisterServerEndpointAsync(name, "127.0.0.1", port, ct);

        NacosMcpServerDetail removed = await NacosEventually.BecomesAsync(
            () => client.AI.Mcp.GetServerAsync(name, Version, ct),
            value => !ContainsEndpoint(value, port),
            ct);

        // Asserted rather than assumed: the wait gives up quietly, so a removal that never reached the
        // server would otherwise pass as one that did.
        ContainsEndpoint(removed, port).ShouldBeFalse();
    }

    [Fact]
    public async Task GetServerAsync_OverHttp_MapsAnUnknownNameToTheNotFoundCode()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.McpHttpImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = NacosHttp.UniqueId("mcp-http-absent");

        // A release first, so that the error below is the registry's answer to an unknown name rather
        // than a server that does not serve the HTTP endpoints at all: those are different failures
        // and only the second one would make this test pass for the wrong reason.
        string present = NacosHttp.UniqueId("mcp-http-present");

        await client.AI.Mcp.ReleaseServerAsync(StdioServer(present), ct: ct);

        await NacosEventually.BecomesAsync(
            () => client.AI.Mcp.GetServerAsync(present, ct: ct),
            value => value.VersionDetail?.Version == Version,
            ct);

        // The subscription cache decides what a missing version means by the error code, not by the
        // transport, so the real endpoint's 404 has to arrive as the same code the gRPC result
        // carries. The unit tests script the status code; only the server proves which one it sends.
        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.AI.Mcp.GetServerAsync(name, ct: ct));

        exception.ErrorCode.ShouldBe(404);
    }

    private static NacosClientFactory CreateClient(NacosContainer container) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),

                // Http means Http: the AI connection is never opened, which is the property that makes
                // the registry usable behind an ingress that only exposes the client OpenAPI.
                AI = new NacosAIOptions { TransportMode = NacosAITransportMode.Http },
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

    /// <summary>
    /// Builds a <c>stdio</c> server specification.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <returns>The specification.</returns>
    /// <remarks>
    /// A <c>stdio</c> server carries its whole description with it, so the registry has nothing to
    /// resolve for it and the release needs no endpoint specification.
    /// </remarks>
    private static NacosMcpServer StdioServer(string name) => new()
    {
        Name = name,
        Protocol = NacosMcpProtocol.Stdio,
        Description = "An MCP server released over HTTP by the integration suite.",
        VersionDetail = new NacosMcpVersionDetail { Version = Version, IsLatest = true },
        LocalServerConfig = new JsonObject { ["command"] = "uvx" },
    };

    /// <summary>
    /// Builds a server specification for a server reached over the network.
    /// </summary>
    /// <param name="name">The server name.</param>
    /// <param name="exportPath">The path the server is exported under.</param>
    /// <returns>The specification.</returns>
    /// <remarks>
    /// No endpoint specification is given, which is what makes Nacos publish the backend as a naming
    /// service of its own and name it in the read. An endpoint can then be registered against it, and
    /// that registration is the stateful write this transport adds.
    /// </remarks>
    private static NacosMcpServer RemoteServer(string name, string exportPath) => new()
    {
        Name = name,
        Protocol = NacosMcpProtocol.Sse,
        FrontProtocol = NacosMcpProtocol.Sse,
        Description = "An MCP server reached over the network.",
        VersionDetail = new NacosMcpVersionDetail { Version = Version, IsLatest = true },
        RemoteServerConfig = new NacosMcpRemoteServiceConfig
        {
            ExportPath = exportPath,
            FrontEndpointConfigList = [],
        },
    };

    /// <summary>
    /// Reports whether the server resolves an endpoint at a port.
    /// </summary>
    /// <param name="detail">The detail the server returned.</param>
    /// <param name="port">The port the endpoint was registered at.</param>
    /// <returns><see langword="true"/> when the endpoint is there.</returns>
    private static bool ContainsEndpoint(NacosMcpServerDetail detail, int port) =>
        detail.BackendEndpoints?.Any(endpoint => endpoint.Port == port) == true;
}
