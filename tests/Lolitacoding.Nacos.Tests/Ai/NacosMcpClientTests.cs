using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies the MCP registry client's wire contract and its guard rails against a scripted AI
/// service.
/// </summary>
/// <remarks>
/// The contract worth pinning down is the part the server reads literally: the module label, the
/// name and version a query carries, the specifications a release carries, and the ability flags
/// that decide whether an operation may be attempted at all. The distinction the tests are most
/// careful about is the one between a server that said it cannot serve the registry and one that
/// said nothing, because only the first is a reason to refuse.
/// </remarks>
public sealed class NacosMcpClientTests
{
    [Fact]
    public async Task GetServerAsync_OpensTheAiConnectionOnlyWhenFirstUsed()
    {
        // An application that never asks the registry for anything should not carry a connection and
        // a heartbeat for the module it does not use.
        var server = new AiTestServer();

        await using NacosAiClient client = server.CreateClient();

        server.Factory.RequestedAddresses.ShouldBeEmpty();
        server.Stream.Written.ShouldBeEmpty();

        server.OnQuery = _ => AiTest.Detail(new NacosMcpServerDetail { Name = "weather" });

        await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        server.Factory.RequestedAddresses.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetServerAsync_LabelsTheConnectionForTheAiModuleWithItsPublisherIdentity()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnQuery = _ => AiTest.Detail(new NacosMcpServerDetail { Name = "weather" });

        await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        ConnectionSetupRequest setup =
            PayloadCodec.Decode<ConnectionSetupRequest>(server.Stream.Written[0])!;

        setup.Labels![ProtocolHeaders.Module].ShouldBe("ai");

        // The server records the publisher identity with every endpoint the connection registers, so
        // the label has to be there before the handshake completes and has to survive a reconnect.
        setup.Labels[NacosAiRemote.ClientUuidLabel].ShouldNotBeNullOrEmpty();
        Guid.TryParse(setup.Labels[NacosAiRemote.ClientUuidLabel], out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetServerAsync_AsksForTheNamedVersionInTheConfiguredNamespace()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        server.OnQuery = _ => AiTest.Detail(new NacosMcpServerDetail { Name = "weather" });

        await client.Mcp.GetServerAsync("weather", "1.2.3", TestContext.Current.CancellationToken);

        QueryMcpServerRequest request = server.Queries[0];
        request.McpName.ShouldBe("weather");
        request.Version.ShouldBe("1.2.3");
        request.NamespaceId.ShouldBe("staging");

        // The query travels on the unary channel method, not on the duplex stream the setup and the
        // server's pushes use.
        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1]).ShouldBe("QueryMcpServerRequest");
    }

    [Fact]
    public async Task GetServerAsync_LeavesTheVersionUnsetForTheLatestOne()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnQuery = _ => AiTest.Detail(new NacosMcpServerDetail { Name = "weather" });

        await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        // A client that asked for "latest" by name would need to know which label the server uses;
        // leaving the field unset lets the server answer with whatever it currently serves.
        server.Queries[0].Version.ShouldBeNull();
    }

    [Fact]
    public async Task GetServerAsync_ReadsTheDetailTheServerReturns()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnQuery = _ => AiTest.Detail(new NacosMcpServerDetail
        {
            Name = "weather",
            Protocol = NacosMcpProtocol.Streamable,
            Version = "1.2.3",
            Enabled = true,
            Status = NacosMcpStatus.Active,
            Capabilities = [NacosMcpCapability.Tool, NacosMcpCapability.Resource],
            ToolSpec = new NacosMcpToolSpecification
            {
                Tools =
                [
                    new NacosMcpTool { Name = "forecast", Description = "Tomorrow's weather" },
                ],
            },
            BackendEndpoints =
            [
                new NacosMcpEndpointInfo { Protocol = "http", Address = "10.0.0.7", Port = 8080 },
            ],
        });

        NacosMcpServerDetail detail =
            await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        detail.Name.ShouldBe("weather");
        detail.Version.ShouldBe("1.2.3");
        detail.Protocol.ShouldBe("mcp-streamable");
        detail.Capabilities.ShouldBe([NacosMcpCapability.Tool, NacosMcpCapability.Resource]);
        detail.ToolSpec!.Tools!.Count.ShouldBe(1);
        detail.ToolSpec.Tools[0].Name.ShouldBe("forecast");
        detail.BackendEndpoints!.Single().ShouldSatisfyAllConditions(
            endpoint => endpoint.Address.ShouldBe("10.0.0.7"),
            endpoint => endpoint.Port.ShouldBe(8080));
    }

    [Fact]
    public async Task GetServerAsync_RejectsABlankNameBeforeSendingAnything()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(
            () => client.Mcp.GetServerAsync("  ", ct: TestContext.Current.CancellationToken));

        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetServerAsync_FailsWhenTheServerSaysItHasNoMcpRegistry()
    {
        var server = new AiTestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAiServerAbility.Mcp] = false,
        });

        await using NacosAiClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));

        // 501 is the code Nacos uses for a feature a server does not implement.
        exception.ErrorCode.ShouldBe(501);

        // Refusing locally matters because an operation the server cannot serve would otherwise
        // surface as an unknown-request-type failure from the transport.
        server.Queries.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetServerAsync_ProceedsWhenTheAbilityTableSaysNothingAboutMcp()
    {
        // A key the server did not mention means it did not state the ability, which is not the same
        // as stating it is unsupported: an older server that predates the key still serves MCP.
        var server = new AiTestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAiServerAbility.Lock] = true,
        });

        await using NacosAiClient client = server.CreateClient();

        server.OnQuery = _ => AiTest.Detail(new NacosMcpServerDetail { Name = "weather" });

        NacosMcpServerDetail detail =
            await client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken);

        detail.Name.ShouldBe("weather");
    }

    [Fact]
    public async Task GetServerAsync_SurfacesTheErrorCodeTheServerReported()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnQuery = _ => AiTest.Failure(20004, "MCP server not found.");

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(20004);
        exception.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task GetServerAsync_ReportsASuccessWithNoDetailAsAProtocolViolation()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        // A missing server is reported as a failure code, so reaching a caller with a success and no
        // detail would be a contract break rather than a missing resource.
        server.OnQuery = _ => new QueryMcpServerResponse();

        await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseServerAsync_SendsTheSpecificationsTheServerReads()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        server.OnRelease = _ => new ReleaseMcpServerResponse { McpId = "mcp-id-1" };

        var serverSpecification = new NacosMcpServer
        {
            Name = "weather",
            Protocol = NacosMcpProtocol.Streamable,
            VersionDetail = new NacosMcpVersionDetail { Version = "1.2.3" },
        };

        var tools = new NacosMcpToolSpecification
        {
            Tools = [new NacosMcpTool { Name = "forecast" }],
        };

        var resources = new NacosMcpResourceSpecification
        {
            Resources = [],
        };

        var endpoint = new NacosMcpEndpointSpec
        {
            Type = NacosMcpEndpointType.Direct,
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NacosMcpEndpointData.Address] = "10.0.0.7",
                [NacosMcpEndpointData.Port] = "8080",
            },
        };

        await client.Mcp.ReleaseServerAsync(
            serverSpecification,
            tools,
            resources,
            endpoint,
            ct: TestContext.Current.CancellationToken);

        ReleaseMcpServerRequest request = server.Releases[0];
        request.McpName.ShouldBe("weather");
        request.NamespaceId.ShouldBe("staging");
        request.CreateDraft.ShouldBeFalse();

        // The request is read back off the wire, so the specifications are compared by what the
        // server would read rather than by object identity.
        request.ServerSpecification!.Name.ShouldBe("weather");
        request.ServerSpecification.Protocol.ShouldBe(NacosMcpProtocol.Streamable);
        request.ServerSpecification.VersionDetail!.Version.ShouldBe("1.2.3");
        request.ToolSpecification!.Tools![0].Name.ShouldBe("forecast");
        request.ResourceSpecification!.Resources.ShouldBeEmpty();
        request.EndpointSpecification!.Type.ShouldBe(NacosMcpEndpointType.Direct);
        request.EndpointSpecification.Data![NacosMcpEndpointData.Address].ShouldBe("10.0.0.7");
        request.EndpointSpecification.Data[NacosMcpEndpointData.Port].ShouldBe("8080");
    }

    [Fact]
    public async Task ReleaseServerAsync_DerivesTheNameFromTheSpecification()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnRelease = _ => new ReleaseMcpServerResponse { McpId = "mcp-id-1" };

        await client.Mcp.ReleaseServerAsync(
            new NacosMcpServer
            {
                Name = "weather",
                Version = "1.2.3",
            },
            ct: TestContext.Current.CancellationToken);

        // The name is the request's routing key, and the server rejects a release whose name differs
        // from the specification's, so taking it from the specification removes the chance of a
        // mismatch.
        server.Releases[0].McpName.ShouldBe("weather");
        server.Releases[0].ServerSpecification!.Version.ShouldBe("1.2.3");
    }

    [Fact]
    public async Task ReleaseServerAsync_ReturnsTheIdentifierTheServerAssigned()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnRelease = _ => new ReleaseMcpServerResponse { McpId = "mcp-id-1" };

        string? id = await client.Mcp.ReleaseServerAsync(
            new NacosMcpServer { Name = "weather", Version = "1.2.3" },
            ct: TestContext.Current.CancellationToken);

        id.ShouldBe("mcp-id-1");
    }

    [Fact]
    public async Task ReleaseServerAsync_RejectsASpecificationWithoutAName()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(
            () => client.Mcp.ReleaseServerAsync(
                new NacosMcpServer { Version = "1.2.3" },
                ct: TestContext.Current.CancellationToken));

        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseServerAsync_RejectsASpecificationWithoutAVersion()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        // The version is what the release creates, so a specification without one would have to
        // leave the version for the server to invent — and a later release could not name it again.
        await Should.ThrowAsync<ArgumentException>(
            () => client.Mcp.ReleaseServerAsync(
                new NacosMcpServer { Name = "weather" },
                ct: TestContext.Current.CancellationToken));

        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseServerAsync_RejectsANullSpecification()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentNullException>(
            () => client.Mcp.ReleaseServerAsync(null!, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReleaseServerAsync_RejectsADraftWhenTheServerDoesNotStoreDrafts()
    {
        var server = new AiTestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAiServerAbility.Mcp] = true,
            [NacosAiServerAbility.McpDraftRelease] = false,
        });

        await using NacosAiClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.ReleaseServerAsync(
                new NacosMcpServer { Name = "weather", Version = "1.2.3" },
                createDraft: true,
                ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(501);
        server.Releases.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseServerAsync_PublishesDirectlyWhenTheDraftAbilityIsAbsentAndNoDraftWasAsked()
    {
        var server = new AiTestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAiServerAbility.Mcp] = true,
        });

        await using NacosAiClient client = server.CreateClient();

        server.OnRelease = _ => new ReleaseMcpServerResponse { McpId = "mcp-id-1" };

        await client.Mcp.ReleaseServerAsync(
            new NacosMcpServer { Name = "weather", Version = "1.2.3" },
            ct: TestContext.Current.CancellationToken);

        // An unstated draft ability is not a rejection, and the flag the server would ignore is not
        // sent anyway.
        server.Releases[0].CreateDraft.ShouldBeFalse();
    }

    [Fact]
    public async Task GetServerAsync_FailsAfterDispose()
    {
        var server = new AiTestServer();
        NacosAiClient client = server.CreateClient();

        await client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => client.Mcp.GetServerAsync("weather", ct: TestContext.Current.CancellationToken));
    }
}
