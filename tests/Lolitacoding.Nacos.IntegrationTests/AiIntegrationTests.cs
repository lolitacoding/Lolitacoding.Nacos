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
/// <para>
/// The endpoint tests are the exception, and they are the reason the four operations are not all
/// written the same way: an endpoint is registered as an instance of a naming service, so what is
/// worth checking is that the registry names the service, that the instance shows up in the read,
/// and that removing it takes it away again.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AiIntegrationTests
{
    /// <summary>The version every release in this class creates.</summary>
    private const string Version = "1.0.0";

    /// <summary>The version the subscription test releases second.</summary>
    private const string SecondVersion = "2.0.0";

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
        // accepted rather than that it is readable, so the read is repeated until it is. A read that
        // gets there first is answered the way an unknown name is — the server has accepted the
        // release but not published it yet — so a failing read is repeated over rather than reported.
        NacosMcpServerDetail detail = await NacosEventually.BecomesAsync(
            () => client.Ai.Mcp.GetServerAsync(name, ct: ct),
            value => value.VersionDetail?.Version == Version,
            ct,
            tolerateFailures: true);

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

    [Fact]
    public async Task SubscribeServerAsync_ReportsANewerVersionOfARealServer()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);

        // The interval is short because it is the only thing that makes a subscription fire, and a
        // test that waited ten seconds for it would be a slow way to prove nothing.
        await using NacosClientFactory client =
            CreateClient(container, TimeSpan.FromMilliseconds(500));

        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = NacosHttp.UniqueId("mcp-watch");

        await client.Ai.Mcp.ReleaseServerAsync(StdioServer(name, Version), ct: ct);

        await using IAsyncEnumerator<NacosMcpServerDetail> changes =
            client.Ai.Mcp.SubscribeServerAsync(name, ct: ct).GetAsyncEnumerator(ct);

        // The release is repeated until the subscription reports it: the subscription is registered
        // with the server only after its current value has been yielded, so a change made in that
        // window would never be seen, and a repeated release of an existing version is refused
        // rather than harmful.
        await NacosEventually.PushedAsync(
            changeCt => client.Ai.Mcp.ReleaseServerAsync(StdioServer(name, SecondVersion), ct: changeCt),
            changes,
            detail => detail.VersionDetail?.Version == SecondVersion,
            ct);

        // The subscription asked for no version, so what it is served is whatever Nacos now labels
        // the latest — which is the version the release just made current.
        changes.Current.VersionDetail!.IsLatest.ShouldBe(true);
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_PublishesAnEndpointTheServerResolves()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string name = NacosHttp.UniqueId("mcp-endpoint");

        const int pinnedPort = 18080;
        const int latestPort = 18081;
        const string exportPath = "/mcp/ref";

        await client.Ai.Mcp.ReleaseServerAsync(RemoteServer(name, exportPath), ct: ct);

        // A server reached over the network is published as a naming service, and the read names the
        // one Nacos created on its behalf. The endpoint registered below is an instance of it. Every
        // read in this test follows a write, and a read that races one is answered as if the server
        // were unknown, so a failing read is repeated over rather than reported.
        NacosMcpServerDetail released = await NacosEventually.BecomesAsync(
            () => client.Ai.Mcp.GetServerAsync(name, Version, ct),
            value => value.RemoteServerConfig?.ServiceRef is not null,
            ct,
            tolerateFailures: true);

        released.RemoteServerConfig!.ServiceRef!.ServiceName.ShouldBe($"{name}::{Version}");

        await client.Ai.Mcp.RegisterServerEndpointAsync(name, "127.0.0.1", pinnedPort, Version, ct);

        NacosMcpServerDetail registered = await NacosEventually.BecomesAsync(
            () => client.Ai.Mcp.GetServerAsync(name, Version, ct),
            value => ContainsEndpoint(value, pinnedPort),
            ct,
            tolerateFailures: true);

        NacosMcpEndpointInfo endpoint =
            registered.BackendEndpoints!.Single(candidate => candidate.Port == pinnedPort);

        endpoint.Address.ShouldBe("127.0.0.1");

        // The path is the one the release exported the server under, not a property of the endpoint
        // registration: Nacos projects the server's description onto every instance it resolves.
        endpoint.Path.ShouldBe(exportPath);

        await client.Ai.Mcp.DeregisterServerEndpointAsync(name, "127.0.0.1", pinnedPort, ct);

        NacosMcpServerDetail removed = await NacosEventually.BecomesAsync(
            () => client.Ai.Mcp.GetServerAsync(name, Version, ct),
            value => !ContainsEndpoint(value, pinnedPort),
            ct,
            tolerateFailures: true);

        // Asserted rather than assumed: the wait gives up quietly, so a removal that never reaches the
        // server would otherwise pass as one that did.
        ContainsEndpoint(removed, pinnedPort).ShouldBeFalse();

        // An endpoint registered without a version serves every version, which Nacos resolves the
        // same way a read without a version does.
        await client.Ai.Mcp.RegisterServerEndpointAsync(name, "127.0.0.1", latestPort, ct: ct);

        NacosMcpServerDetail withoutVersion = await NacosEventually.BecomesAsync(
            () => client.Ai.Mcp.GetServerAsync(name, ct: ct),
            value => ContainsEndpoint(value, latestPort),
            ct,
            tolerateFailures: true);

        ContainsEndpoint(withoutVersion, latestPort).ShouldBeTrue();

        await client.Ai.Mcp.DeregisterServerEndpointAsync(name, "127.0.0.1", latestPort, ct);
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_ReportsAnUnknownNameAsAServerError()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;

        // A server that resolves no backend service cannot take an endpoint, so the failure is the
        // registry's answer to an unknown name rather than to a server that does not serve the
        // registry at all: those are different failures and only the second one would make this
        // test pass for the wrong reason.
        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Ai.Mcp.RegisterServerEndpointAsync(
                NacosHttp.UniqueId("mcp-endpoint-absent"), "127.0.0.1", 18082, ct: ct));

        exception.ErrorCode.ShouldBe(404);
    }

    private static NacosClientFactory CreateClient(
        NacosContainer container,
        TimeSpan? cacheRefreshInterval = null) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                Ai = new NacosAiOptions
                {
                    CacheRefreshInterval = cacheRefreshInterval ?? TimeSpan.FromSeconds(10),
                },
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
    /// <param name="version">The version to release.</param>
    /// <returns>The specification.</returns>
    /// <remarks>
    /// A <c>stdio</c> server carries its whole description with it, so the registry has nothing to
    /// resolve for it.
    /// </remarks>
    private static NacosMcpServer StdioServer(string name, string version) => new()
    {
        Name = name,
        Protocol = NacosMcpProtocol.Stdio,
        Description = "An MCP server released by the integration suite.",
        VersionDetail = new NacosMcpVersionDetail { Version = version, IsLatest = true },
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
    /// service of its own and name it in the read.
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
