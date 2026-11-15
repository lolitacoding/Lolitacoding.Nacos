using System.Net;
using System.Text.Json;
using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies the HTTP side of AI endpoint publication: the requests the server reads, the transport a
/// publication sticks to, and the heartbeat and replay the coordinator runs.
/// </summary>
/// <remarks>
/// <para>
/// An HTTP publication is the one AI registration whose lifetime the client has to maintain. A gRPC
/// registration belongs to the connection that made it, so a reconnect replays it; an HTTP one
/// belongs to a client the server identifies by a header, so the client heartbeats it and replays it
/// when the server says it has forgotten the client.
/// </para>
/// <para>
/// The tests build the publication managers directly rather than through <see cref="NacosAIClient"/>.
/// That is what makes the coordinator's cycle drivable: it can be run once, on demand, instead of
/// waiting for a timer, and both modules can be observed in the same cycle.
/// </para>
/// </remarks>
public sealed class NacosAIEndpointPublicationTests
{
    private const string McpEndpointPath = "/v3/client/ai/mcp/endpoints";
    private const string McpHeartbeatPath = "/v3/client/ai/mcp/endpoints/heartbeat";
    private const string AgentEndpointPath = "/v3/client/ai/agents/endpoints";
    private const string ClientId = "test-client";

    [Fact]
    public async Task RegisterAsync_OverHttp_PostsTheFormAndTheStatefulHeaders()
    {
        AIHttpTestServer http = Liveness();
        using var coordinator = CreateCoordinator();
        await using NacosMcpEndpointPublication publication = CreateMcpPublication(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await publication.RegisterAsync(
            "weather", "10.0.0.7", 8080, "1.2.3", TestContext.Current.CancellationToken);

        http.RequestCount.ShouldBe(1);
        http.Method(0).ShouldBe(HttpMethod.Post);
        http.Requests[0].AbsolutePath.ShouldEndWith(McpEndpointPath);

        http.Form(0, "namespaceId").ShouldBe("staging");
        http.Form(0, "mcpName").ShouldBe("weather");
        http.Form(0, "address").ShouldBe("10.0.0.7");
        http.Form(0, "port").ShouldBe("8080");
        http.Form(0, "version").ShouldBe("1.2.3");

        // The server binds the publication to the client id and routes the write by the module, so
        // both are part of the contract rather than optional metadata.
        http.Header(0, "X-Nacos-Client-Id").ShouldBe(ClientId);
        http.Header(0, "Request-Module").ShouldBe("AI");
    }

    [Fact]
    public async Task RegisterAsync_OverHttp_OmitsTheVersionWhenNoneWasGiven()
    {
        AIHttpTestServer http = Liveness();
        using var coordinator = CreateCoordinator();
        await using NacosMcpEndpointPublication publication = CreateMcpPublication(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await publication.RegisterAsync(
            "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken);

        // An absent version publishes the endpoint for every version of the server; an empty string
        // would be a different request rather than the same one.
        http.Form(0, "version").ShouldBeNull();
    }

    [Fact]
    public async Task DeregisterAsync_OverHttp_DeletesWithQueryFields()
    {
        AIHttpTestServer http = Liveness();
        using var coordinator = CreateCoordinator();
        await using NacosMcpEndpointPublication publication = CreateMcpPublication(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await publication.RegisterAsync(
            "weather", "10.0.0.7", 8080, "1.2.3", TestContext.Current.CancellationToken);

        await publication.DeregisterAsync(
            "weather", "10.0.0.7", 8080, TestContext.Current.CancellationToken);

        http.RequestCount.ShouldBe(2);
        http.Method(1).ShouldBe(HttpMethod.Delete);
        http.Requests[1].AbsolutePath.ShouldEndWith(McpEndpointPath);

        // A DELETE binds from the query string, so the fields that a POST sends as a form travel as
        // parameters here.
        http.Query(1, "namespaceId").ShouldBe("staging");
        http.Query(1, "mcpName").ShouldBe("weather");
        http.Query(1, "address").ShouldBe("10.0.0.7");
        http.Query(1, "port").ShouldBe("8080");
        http.Query(1, "version").ShouldBeNull();
    }

    [Fact]
    public async Task HeartbeatAsync_UsesTheModulePathThatOwnsThePublication()
    {
        AIHttpTestServer http = Liveness();
        using var coordinator = CreateCoordinator();
        await using NacosMcpEndpointPublication publication = CreateMcpPublication(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await publication.RegisterAsync(
            "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken);

        NacosClientLivenessInfo? liveness = await publication.HeartbeatAsync(
            TestContext.Current.CancellationToken);

        http.RequestCount.ShouldBe(2);
        http.Method(1).ShouldBe(HttpMethod.Put);
        http.Requests[1].AbsolutePath.ShouldEndWith(McpHeartbeatPath);

        // The intervals are what the coordinator schedules the next cycle by, so they have to be read
        // from the answer rather than assumed.
        liveness.ShouldNotBeNull();
        liveness!.HeartbeatIntervalMillis.ShouldBe(10_000);
        liveness.UnhealthyTimeoutMillis.ShouldBe(30_000);
        liveness.ExpireTimeoutMillis.ShouldBe(60_000);
    }

    [Fact]
    public async Task RegisterAsync_InAutoMode_FallsBackToHttpAfterTheConnectionFailed()
    {
        AIHttpTestServer http = Liveness();
        var time = new FakeTimeProvider();
        var fallback = new NacosAITransportFallback(time);
        using var coordinator = CreateCoordinator();
        await using NacosMcpEndpointPublication publication = CreateMcpPublication(
            http,
            () => fallback.GrpcIsWorthTrying()
                ? NacosAIPublicationTransport.Grpc
                : NacosAIPublicationTransport.Http,
            coordinator,
            fallback.RecordGrpcFailure);

        // The first publication picks gRPC, because the window is open, and the unreachable
        // connection is what closes it.
        await Should.ThrowAsync<NacosConnectionException>(
            () => publication.RegisterAsync(
                "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken));

        http.RequestCount.ShouldBe(0);

        // The next publication sees the closed window and publishes over HTTP instead, so a process
        // whose gRPC is down can still publish endpoints.
        await publication.RegisterAsync(
            "weather", "10.0.0.8", 8080, version: null, TestContext.Current.CancellationToken);

        http.RequestCount.ShouldBe(1);
        http.Method(0).ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task RegisterAsync_InAutoMode_ReturnsToGrpcWhenTheWindowExpires()
    {
        AIHttpTestServer http = Liveness();
        var time = new FakeTimeProvider();
        var fallback = new NacosAITransportFallback(time);
        using var coordinator = CreateCoordinator();
        await using NacosMcpEndpointPublication publication = CreateMcpPublication(
            http,
            () => fallback.GrpcIsWorthTrying()
                ? NacosAIPublicationTransport.Grpc
                : NacosAIPublicationTransport.Http,
            coordinator,
            fallback.RecordGrpcFailure);

        await Should.ThrowAsync<NacosConnectionException>(
            () => publication.RegisterAsync(
                "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken));

        time.Advance(TimeSpan.FromSeconds(31));

        // The window is a pause rather than a permanent move: once it expires, a new publication
        // tries gRPC again, which is what picks a recovered connection up without a restart.
        await Should.ThrowAsync<NacosConnectionException>(
            () => publication.RegisterAsync(
                "weather", "10.0.0.8", 8080, version: null, TestContext.Current.CancellationToken));

        http.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task DeregisterAsync_KeepsTheTransportThatOwnedTheRegistration()
    {
        AIHttpTestServer http = Liveness();
        var time = new FakeTimeProvider();
        var fallback = new NacosAITransportFallback(time);
        using var coordinator = CreateCoordinator();
        await using NacosMcpEndpointPublication publication = CreateMcpPublication(
            http,
            () => fallback.GrpcIsWorthTrying()
                ? NacosAIPublicationTransport.Grpc
                : NacosAIPublicationTransport.Http,
            coordinator,
            fallback.RecordGrpcFailure);

        await Should.ThrowAsync<NacosConnectionException>(
            () => publication.RegisterAsync(
                "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken));

        await publication.RegisterAsync(
            "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken);

        // The window expires, so a new publication would pick gRPC again — but this endpoint is owned
        // by HTTP, and the removal has to reach the record the registration created.
        time.Advance(TimeSpan.FromSeconds(31));

        await publication.DeregisterAsync(
            "weather", "10.0.0.7", 8080, TestContext.Current.CancellationToken);

        http.RequestCount.ShouldBe(2);
        http.Method(1).ShouldBe(HttpMethod.Delete);
        http.Requests[1].AbsolutePath.ShouldEndWith(McpEndpointPath);
    }

    [Fact]
    public async Task RestoreAsync_DoesNotReplayAnHttpPublication()
    {
        AIHttpTestServer http = Liveness();
        using var coordinator = CreateCoordinator();
        await using NacosMcpEndpointPublication publication = CreateMcpPublication(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await publication.RegisterAsync(
            "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken);

        await publication.RestoreAsync(TestContext.Current.CancellationToken);

        // A reconnect says nothing about an HTTP publication: it is kept alive by its heartbeat, and
        // replaying it would register a second copy rather than restore the first.
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task DisposeAsync_RemovesTheHttpPublicationsItOwns()
    {
        AIHttpTestServer http = Liveness();
        using var coordinator = CreateCoordinator();
        NacosMcpEndpointPublication publication = CreateMcpPublication(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await publication.RegisterAsync(
            "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken);

        await publication.DisposeAsync();

        // Sending the removal is what makes the endpoint disappear at once instead of after the
        // server's expiry timeout.
        http.RequestCount.ShouldBe(2);
        http.Method(1).ShouldBe(HttpMethod.Delete);
    }

    [Fact]
    public async Task RegisterAsync_RejectsAPublicationAfterDispose()
    {
        AIHttpTestServer http = Liveness();
        using var coordinator = CreateCoordinator();
        NacosMcpEndpointPublication publication = CreateMcpPublication(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await publication.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => publication.RegisterAsync(
                "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegisterAsync_OverHttp_PostsTheAgentBatchAsJson()
    {
        AIHttpTestServer http = Liveness();
        using var coordinator = CreateCoordinator();
        await using NacosAgentEndpointPublicationManager manager = CreateAgentManager(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await manager.RegisterAsync(
            new NacosAgentEndpointBatch
            {
                AgentName = "planner",
                Protocol = "jsonrpc",
                RuntimeVersion = "2.0.0",
                VersionRange = "[1.0.0,3.0.0)",
                Endpoints =
                [
                    new NacosAgentRuntimeEndpoint
                    {
                        Uri = "https://10.0.0.7:8080",
                        Transport = "JSONRPC",
                        Weight = 0.5,
                    },
                ],
            },
            TestContext.Current.CancellationToken);

        http.RequestCount.ShouldBe(1);
        http.Method(0).ShouldBe(HttpMethod.Post);
        http.Requests[0].AbsolutePath.ShouldEndWith(AgentEndpointPath);

        http.Form(0, "namespaceId").ShouldBe("staging");
        http.Form(0, "agentName").ShouldBe("planner");
        http.Form(0, "protocol").ShouldBe("jsonrpc");
        http.Form(0, "runtimeVersion").ShouldBe("2.0.0");
        http.Form(0, "versionRange").ShouldBe("[1.0.0,3.0.0)");

        // The batch is one JSON document in a field of its own, not one JSON body: the server binds
        // the other fields literally and parses this one.
        using JsonDocument endpoints = JsonDocument.Parse(http.Form(0, "endpoints")!);
        endpoints.RootElement.GetArrayLength().ShouldBe(1);
        endpoints.RootElement[0].GetProperty("uri").GetString().ShouldBe("https://10.0.0.7:8080");
        endpoints.RootElement[0].GetProperty("transport").GetString().ShouldBe("JSONRPC");
    }

    [Fact]
    public async Task DeregisterAsync_OverHttp_DeletesTheAgentProtocol()
    {
        AIHttpTestServer http = Liveness();
        using var coordinator = CreateCoordinator();
        await using NacosAgentEndpointPublicationManager manager = CreateAgentManager(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await manager.RegisterAsync(
            new NacosAgentEndpointBatch
            {
                AgentName = "planner",
                Protocol = "jsonrpc",
                RuntimeVersion = "2.0.0",
                Endpoints = [new NacosAgentRuntimeEndpoint { Uri = "https://10.0.0.7:8080" }],
            },
            TestContext.Current.CancellationToken);

        await manager.DeregisterAsync(
            "planner", "jsonrpc", TestContext.Current.CancellationToken);

        http.RequestCount.ShouldBe(2);
        http.Method(1).ShouldBe(HttpMethod.Delete);
        http.Requests[1].AbsolutePath.ShouldEndWith(AgentEndpointPath);
        http.Query(1, "namespaceId").ShouldBe("staging");
        http.Query(1, "agentName").ShouldBe("planner");
        http.Query(1, "protocol").ShouldBe("jsonrpc");
    }

    [Fact]
    public async Task RegisterAsync_RejectsABatchBeyondThePublicationLimit()
    {
        AIHttpTestServer http = Liveness();
        using var coordinator = CreateCoordinator();
        await using NacosAgentEndpointPublicationManager manager = CreateAgentManager(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        // The server enforces the same limit; checking it first turns a refusal into a failure the
        // caller sees before the batch is sent. The check is against what the client already
        // published, so the first batch has to fill the client up to the limit.
        await manager.RegisterAsync(
            new NacosAgentEndpointBatch
            {
                AgentName = "planner",
                Protocol = "jsonrpc",
                RuntimeVersion = "2.0.0",
                Endpoints = Enumerable.Range(0, 100)
                    .Select(index => new NacosAgentRuntimeEndpoint
                    {
                        Uri = string.Concat("https://10.0.0.", index.ToString()),
                    })
                    .ToList(),
            },
            TestContext.Current.CancellationToken);

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => manager.RegisterAsync(
                new NacosAgentEndpointBatch
                {
                    AgentName = "planner",
                    Protocol = "grpc",
                    RuntimeVersion = "2.0.0",
                    Endpoints = Enumerable.Range(0, 101)
                        .Select(index => new NacosAgentRuntimeEndpoint
                        {
                            Uri = string.Concat("https://10.0.1.", index.ToString()),
                        })
                        .ToList(),
                },
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(50103);
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task RegisterAsync_DropsAPublicationTheServerRefusedForCapacity()
    {
        AIHttpTestServer http = new((_, _) => AIHttpTestServer.Result(
            HttpStatusCode.BadRequest,
            50103,
            "Agent Endpoint publication capacity reached",
            "Agent Endpoint publication limit reached"));

        using var coordinator = CreateCoordinator();
        await using NacosAgentEndpointPublicationManager manager = CreateAgentManager(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await Should.ThrowAsync<NacosServerException>(
            () => manager.RegisterAsync(
                new NacosAgentEndpointBatch
                {
                    AgentName = "planner",
                    Protocol = "jsonrpc",
                    RuntimeVersion = "2.0.0",
                    Endpoints = [new NacosAgentRuntimeEndpoint { Uri = "https://10.0.0.7:8080" }],
                },
                TestContext.Current.CancellationToken));

        // A capacity refusal will be repeated, so replaying it would only log the same failure
        // forever.
        manager.HasHttpPublication.ShouldBeFalse();
    }

    [Fact]
    public async Task MaintainAsync_HeartbeatsThroughTheFirstModuleThatOwnsAPublication()
    {
        AIHttpTestServer http = Liveness();
        var time = new FakeTimeProvider();
        using var coordinator = CreateCoordinator();
        await using NacosMcpEndpointPublication mcp = CreateMcpPublication(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);
        await using NacosAgentEndpointPublicationManager agent = CreateAgentManager(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await mcp.RegisterAsync(
            "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken);

        await agent.RegisterAsync(
            new NacosAgentEndpointBatch
            {
                AgentName = "planner",
                Protocol = "jsonrpc",
                RuntimeVersion = "2.0.0",
                Endpoints = [new NacosAgentRuntimeEndpoint { Uri = "https://10.0.0.7:8080" }],
            },
            TestContext.Current.CancellationToken);

        await coordinator.MaintainAsync(TestContext.Current.CancellationToken);

        // The server keeps one liveness record per client, so one heartbeat refreshes both modules;
        // it goes through the first module that owns a live publication.
        http.RequestCount.ShouldBe(3);
        http.Method(2).ShouldBe(HttpMethod.Put);
        http.Requests[2].AbsolutePath.ShouldEndWith(McpHeartbeatPath);
    }

    [Fact]
    public async Task MaintainAsync_ReplaysEveryModuleWhenTheServerForgotTheClient()
    {
        var heartbeats = 0;

        AIHttpTestServer http = new((_, request) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                heartbeats++;

                return AIHttpTestServer.Result(
                    HttpStatusCode.NotFound,
                    50404,
                    "HTTP client not found",
                    "HTTP Client does not exist or owns no AI Endpoint publication.");
            }

            return AIHttpTestServer.Liveness();
        });

        var time = new FakeTimeProvider();
        using var coordinator = CreateCoordinator();
        await using NacosMcpEndpointPublication mcp = CreateMcpPublication(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);
        await using NacosAgentEndpointPublicationManager agent = CreateAgentManager(
            http,
            () => NacosAIPublicationTransport.Http,
            coordinator);

        await mcp.RegisterAsync(
            "weather", "10.0.0.7", 8080, version: null, TestContext.Current.CancellationToken);

        await agent.RegisterAsync(
            new NacosAgentEndpointBatch
            {
                AgentName = "planner",
                Protocol = "jsonrpc",
                RuntimeVersion = "2.0.0",
                Endpoints = [new NacosAgentRuntimeEndpoint { Uri = "https://10.0.0.7:8080" }],
            },
            TestContext.Current.CancellationToken);

        await coordinator.MaintainAsync(TestContext.Current.CancellationToken);

        // A lost client means every module's publication is gone rather than one endpoint, so all of
        // them are marked before any is sent: replaying one first would recreate the client and make
        // the other module look healthy while its endpoint is still missing.
        heartbeats.ShouldBe(1);
        http.RequestCount.ShouldBe(5);

        http.Method(3).ShouldBe(HttpMethod.Post);
        http.Requests[3].AbsolutePath.ShouldEndWith(McpEndpointPath);

        http.Method(4).ShouldBe(HttpMethod.Post);
        http.Requests[4].AbsolutePath.ShouldEndWith(AgentEndpointPath);
    }

    private static AIHttpTestServer Liveness() =>
        new((_, _) => AIHttpTestServer.Liveness());

    private static NacosAIHttpPublicationCoordinator CreateCoordinator() =>
        new(TimeProvider.System, NullLogger.Instance);

    private static NacosMcpEndpointPublication CreateMcpPublication(
        HttpMessageHandler http,
        Func<NacosAIPublicationTransport> selectOwner,
        NacosAIHttpPublicationCoordinator coordinator,
        Action? grpcConnectionFailed = null) =>
        new(
            (_, _) => throw new NacosConnectionException("The AI connection is not reachable."),
            new NacosAIHttpClient(
                [AITestServer.Server],
                new HttpClient(http),
                TimeSpan.FromSeconds(2),
                NullLogger.Instance,
                clientId: ClientId),
            selectOwner,
            coordinator,
            grpcConnectionFailed ?? (() => { }),
            TimeSpan.FromSeconds(2),
            "staging",
            NullLogger.Instance);

    private static NacosAgentEndpointPublicationManager CreateAgentManager(
        HttpMessageHandler http,
        Func<NacosAIPublicationTransport> selectOwner,
        NacosAIHttpPublicationCoordinator coordinator,
        Action? grpcConnectionFailed = null) =>
        new(
            _ => throw new NacosConnectionException("The AI connection is not reachable."),
            new NacosAIHttpClient(
                [AITestServer.Server],
                new HttpClient(http),
                TimeSpan.FromSeconds(2),
                NullLogger.Instance,
                clientId: ClientId),
            selectOwner,
            coordinator,
            grpcConnectionFailed ?? (() => { }),
            TimeSpan.FromSeconds(2),
            "staging",
            NullLogger.Instance);
}
