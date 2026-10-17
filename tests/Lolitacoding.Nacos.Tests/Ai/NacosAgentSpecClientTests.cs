using System.Net;
using Lolitacoding.Nacos.Ai;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies how an agent specification is read, and what is made of an answer that is not one.
/// </summary>
/// <remarks>
/// The transport is the one a skill uses, but the body is a JSON document inside the API's result
/// envelope rather than an archive, so the tests cover the envelope as well as the request: a
/// success carries the specification, a failure carries a code, and a success that carries nothing is
/// a reply that did not match the contract rather than an empty specification.
/// </remarks>
public sealed class NacosAgentSpecClientTests
{
    [Fact]
    public async Task GetAgentSpecAsync_ReturnsTheSpecificationWithTheResolvedVersionAndHash()
    {
        var http = new AiHttpTestServer(
            (_, _) => AiHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.2"));

        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        NacosAgentSpec spec = await client.AgentSpec.GetAgentSpecAsync(
            "worker", ct: TestContext.Current.CancellationToken);

        spec.Name.ShouldBe("worker");
        spec.Content.ShouldBe(AgentSpecJson.DefaultContent);
        spec.Version.ShouldBe("0.0.2");
        spec.Md5.ShouldBe("hash-1");

        NacosAgentSpecResource resource = spec.Resource.ShouldNotBeNull()["config_SOUL__md"];
        resource.ResourceIdentifier.ShouldBe("config::SOUL.md");
    }

    [Fact]
    public async Task GetAgentSpecAsync_AsksForTheSelectorsTheCallerNamed()
    {
        var http = new AiHttpTestServer(
            (_, _) => AiHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.2"));

        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        await client.AgentSpec.GetAgentSpecAsync(
            "worker", version: "0.0.2", label: "stable", ct: TestContext.Current.CancellationToken);

        http.Requests[0].AbsolutePath.ShouldEndWith("/v3/client/ai/agentspecs");
        http.Query(0, "namespaceId").ShouldBe("public");
        http.Query(0, "name").ShouldBe("worker");
        http.Query(0, "version").ShouldBe("0.0.2");
        http.Query(0, "label").ShouldBe("stable");

        // A plain read wants the content, so there is nothing to be answered with "not modified".
        http.Query(0, "md5").ShouldBeNull();
    }

    [Fact]
    public async Task GetAgentSpecAsync_LeavesBothSelectorsUnsetForTheLatestVersion()
    {
        var http = new AiHttpTestServer(
            (_, _) => AiHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1"));

        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        await client.AgentSpec.GetAgentSpecAsync("worker", ct: TestContext.Current.CancellationToken);

        http.Query(0, "version").ShouldBeNull();
        http.Query(0, "label").ShouldBeNull();
    }

    [Fact]
    public async Task GetAgentSpecAsync_RejectsABlankName()
    {
        var http = new AiHttpTestServer((_, _) => AiHttpTestServer.AgentSpec(AgentSpecJson.Build()));
        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(
            () => client.AgentSpec.GetAgentSpecAsync(" ", ct: TestContext.Current.CancellationToken));

        http.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAgentSpecAsync_ReportsAnUnknownNameWithTheServersDetail()
    {
        var http = new AiHttpTestServer(
            (_, _) => AiHttpTestServer.NotFound("AgentSpec not found: worker"));

        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.AgentSpec.GetAgentSpecAsync("worker", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(404);
        exception.Message.ShouldBe("AgentSpec not found: worker");
    }

    [Fact]
    public async Task GetAgentSpecAsync_ReportsAVersionThatIsNotOnline()
    {
        var http = new AiHttpTestServer(
            (_, _) => AiHttpTestServer.NotFound("AgentSpec version not online: worker"));

        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.AgentSpec.GetAgentSpecAsync(
                "worker", version: "0.0.9", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(404);
        exception.Message.ShouldBe("AgentSpec version not online: worker");
    }

    [Fact]
    public async Task GetAgentSpecAsync_ReportsAnAuthRejectionAsAnAuthFailure()
    {
        var http = new AiHttpTestServer((_, _) => AiHttpTestServer.Result(
            HttpStatusCode.Forbidden, 403, "user not found!"));

        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        NacosAuthException exception = await Should.ThrowAsync<NacosAuthException>(
            () => client.AgentSpec.GetAgentSpecAsync("worker", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(403);
    }

    [Fact]
    public async Task GetAgentSpecAsync_ReportsAnUnexpectedStatus()
    {
        var http = new AiHttpTestServer(
            (_, _) => AiHttpTestServer.Result(HttpStatusCode.BadGateway, 500, "upstream failed"));

        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.AgentSpec.GetAgentSpecAsync("worker", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(502);
        exception.Message.ShouldBe("upstream failed");
    }

    [Fact]
    public async Task GetAgentSpecAsync_ReportsAnEnvelopeThatCarriedNoSpecification()
    {
        // A successful status whose envelope has no data is a reply that did not match the contract,
        // not an empty specification: reporting it as one would hide a broken server.
        var http = new AiHttpTestServer((_, _) => AiHttpTestServer.Result(
            HttpStatusCode.OK, 20004, "resource not found"));

        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.AgentSpec.GetAgentSpecAsync("worker", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(20004);
        exception.Message.ShouldBe("resource not found");
    }

    [Fact]
    public async Task GetAgentSpecAsync_ReportsABodyThatIsNotAnEnvelope()
    {
        var http = new AiHttpTestServer((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>"),
        });

        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        await Should.ThrowAsync<NacosServerException>(
            () => client.AgentSpec.GetAgentSpecAsync("worker", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAgentSpecAsync_DoesNotOpenTheAiConnection()
    {
        var http = new AiHttpTestServer(
            (_, _) => AiHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1"));

        AiTestServer server = CreateServer(http);
        await using NacosAiClient client = server.CreateClient();

        await client.AgentSpec.GetAgentSpecAsync("worker", ct: TestContext.Current.CancellationToken);

        // Served over HTTP only, so a caller that only reads specifications never opens a gRPC
        // stream it would not use.
        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAgentSpecAsync_RejectsAReadAfterTheClientIsDisposed()
    {
        var http = new AiHttpTestServer((_, _) => AiHttpTestServer.AgentSpec(AgentSpecJson.Build()));
        AiTestServer server = CreateServer(http);
        NacosAiClient client = server.CreateClient();

        await client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => client.AgentSpec.GetAgentSpecAsync("worker", ct: TestContext.Current.CancellationToken));
    }

    private static AiTestServer CreateServer(AiHttpTestServer http) => new(httpHandler: http);
}
