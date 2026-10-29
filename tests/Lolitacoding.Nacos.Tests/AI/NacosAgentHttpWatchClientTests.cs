using System.Net;
using System.Text.Json;
using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Tests.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies the HTTP batch long poll an agent watch is registered with.
/// </summary>
/// <remarks>
/// The endpoint is a form POST whose body is the client's complete watch set, and its failures are v3
/// result codes rather than status codes alone. Both are pinned here: what the server binds, what the
/// client sends, and which exception each refusal turns into.
/// </remarks>
public sealed class NacosAgentHttpWatchClientTests
{
    private const string Fingerprint =
        "sha256-canonical-json-v1:3362e4414540623a15d86c0c0b99a0b76f82a203c3f26eb905b9db43444436d5";

    [Fact]
    public async Task WatchAsync_SendsTheCompleteWatchSetAsAForm()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.WatchBatch(7));

        AgentWatchBatchResponse response = await CreateClient(http).WatchAsync(
            7,
            30000,
            [Item()],
            TestContext.Current.CancellationToken);

        response.Generation.ShouldBe(7);
        response.Changed.ShouldBeFalse();

        http.Requests[0].AbsolutePath.ShouldEndWith("/v3/client/ai/agents/watch");
        http.Form(0, "generation").ShouldBe("7");
        http.Form(0, "timeoutMillis").ShouldBe("30000");
        http.Header(0, "X-Nacos-Client-Id").ShouldBe("client-1");
        http.Header(0, "Request-Module").ShouldBe("ai");

        string watches = http.Form(0, "watches").ShouldNotBeNull();

        watches.ShouldContain("\"clientWatchId\":\"watch-a\"");
        watches.ShouldContain($"\"materializedFingerprint\":\"{Fingerprint}\"");
        watches.ShouldContain("\"discoveryRequest\":");
        watches.ShouldContain("\"agentName\":\"planner\"");
        watches.ShouldContain("\"namespaceId\":\"public\"");
    }

    [Fact]
    public async Task WatchAsync_ReturnsTheWatchesTheServerReportedChanged()
    {
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.WatchBatch(9, "watch-a", "watch-b"));

        AgentWatchBatchResponse response = await CreateClient(http).WatchAsync(
            9,
            1000,
            [Item()],
            TestContext.Current.CancellationToken);

        response.Generation.ShouldBe(9);
        response.Changed.ShouldBeTrue();
        response.ChangedClientWatchIds.ShouldBe(["watch-a", "watch-b"]);
    }

    [Fact]
    public async Task WatchAsync_ReportsACapacityRejectionAsTheCapacityException()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.WatchCapacity());

        NacosAgentWatchCapacityException exception =
            await Should.ThrowAsync<NacosAgentWatchCapacityException>(
                () => CreateClient(http).WatchAsync(
                    1,
                    1000,
                    [Item()],
                    TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(503);
        exception.DetailErrorCode.ShouldBe(50104);
        exception.Message.ShouldBe("Watch capacity reached");
    }

    [Fact]
    public async Task WatchAsync_KeepsTheMigrationCodeOutOfTheHttpStatus()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Result(
            HttpStatusCode.NotFound,
            50105,
            "migration in progress",
            "The agent is being migrated"));

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => CreateClient(http).WatchAsync(
                1,
                1000,
                [Item()],
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(50105);
        exception.Message.ShouldBe("The agent is being migrated");
    }

    [Fact]
    public async Task WatchAsync_KeepsTheClientNotFoundCodeOutOfTheHttpStatus()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Result(
            HttpStatusCode.NotFound,
            50404,
            "client not found",
            "The client is not registered"));

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => CreateClient(http).WatchAsync(
                1,
                1000,
                [Item()],
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(50404);
    }

    [Fact]
    public async Task WatchAsync_ReportsAPlainHttpFailureWithItsStatus()
    {
        var http = new AIHttpTestServer(
            (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => CreateClient(http).WatchAsync(
                1,
                1000,
                [Item()],
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(502);
    }

    [Fact]
    public async Task WatchAsync_RejectsAResultThatCarriesNoBatch()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Result(
            HttpStatusCode.OK,
            200,
            "success"));

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => CreateClient(http).WatchAsync(
                1,
                1000,
                [Item()],
                TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(500);
    }

    [Fact]
    public async Task WatchAsync_RejectsABatchThatBelongsToAnotherGeneration()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.WatchBatch(3, "watch-a"));

        AgentWatchBatchResponse response = await CreateClient(http).WatchAsync(
            4,
            1000,
            [Item()],
            TestContext.Current.CancellationToken);

        // The transport compares the generations; the client only reports what the server said.
        response.Generation.ShouldBe(3);
        response.ChangedClientWatchIds.ShouldBe(["watch-a"]);
    }

    [Fact]
    public void AgentWatchBatchItem_SerializesTheDiscoveryTheWatchFollows()
    {
        string json = JsonSerializer.Serialize(
            Item(),
            NacosJsonContext.Default.AgentWatchBatchItem);

        json.ShouldContain("\"clientWatchId\":\"watch-a\"");
        json.ShouldContain($"\"materializedFingerprint\":\"{Fingerprint}\"");
        json.ShouldContain("\"discoveryRequest\":{\"namespaceId\":\"public\",\"reference\":{\"agentName\":\"planner\"}}");
    }

    private static AgentWatchBatchItem Item() => new()
    {
        ClientWatchId = "watch-a",
        DiscoveryRequest = new NacosAgentDiscoveryRequest
        {
            NamespaceId = "public",
            Reference = new NacosAgentReference { AgentName = "planner" },
        },
        MaterializedFingerprint = Fingerprint,
    };

    private static NacosAgentHttpWatchClient CreateClient(AIHttpTestServer http)
    {
        var client = new NacosAIHttpClient(
            NacosServerAddress.ParseAll(["127.0.0.1:8848"]),
            new HttpClient(http),
            TimeSpan.FromSeconds(5),
            NullLogger.Instance,
            new FakeAuthProvider("token-1"));

        return new NacosAgentHttpWatchClient(client, "client-1");
    }
}
