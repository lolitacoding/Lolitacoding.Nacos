using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies the MCP endpoint publication against a scripted AI service.
/// </summary>
/// <remarks>
/// An endpoint is registered through the AI module but stored as a Naming instance, so it belongs to
/// the connection that registered it. The tests therefore cover the request the server reads and the
/// replay that keeps the endpoint published across a reconnect, which is the part a caller cannot see
/// from a single call.
/// </remarks>
public sealed class NacosMcpEndpointTests
{
    private const string EndpointRequestType = "McpServerEndpointRequest";

    [Fact]
    public async Task RegisterServerEndpointAsync_SendsTheEndpointTheServerReads()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        await client.Mcp.RegisterServerEndpointAsync(
            "weather", "10.0.0.7", 8080, "1.2.3", TestContext.Current.CancellationToken);

        McpServerEndpointRequest request = server.Endpoints[0];
        request.McpName.ShouldBe("weather");
        request.Address.ShouldBe("10.0.0.7");
        request.Port.ShouldBe(8080);
        request.Version.ShouldBe("1.2.3");
        request.NamespaceId.ShouldBe("staging");
        request.Type.ShouldBe(NacosAIRemote.RegisterEndpoint);

        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1]).ShouldBe(EndpointRequestType);
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_LeavesTheVersionUnsetWhenNoneIsGiven()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await client.Mcp.RegisterServerEndpointAsync(
            "weather", "10.0.0.7", 8080, ct: TestContext.Current.CancellationToken);

        // An unset version registers the endpoint for every version of the server, so an empty
        // string would be a different request rather than the same one.
        server.Endpoints[0].Version.ShouldBeNull();
    }

    [Fact]
    public async Task DeregisterServerEndpointAsync_SendsTheDeregisterOperation()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await client.Mcp.RegisterServerEndpointAsync(
            "weather", "10.0.0.7", 8080, "1.2.3", TestContext.Current.CancellationToken);

        await client.Mcp.DeregisterServerEndpointAsync(
            "weather", "10.0.0.7", 8080, TestContext.Current.CancellationToken);

        server.Endpoints.Count.ShouldBe(2);
        server.Endpoints[1].Type.ShouldBe(NacosAIRemote.DeregisterEndpoint);
        server.Endpoints[1].Address.ShouldBe("10.0.0.7");
        server.Endpoints[1].Port.ShouldBe(8080);

        // The version travels with the removal because Nacos resolves the service to remove the
        // instance from by it: without it, a server whose latest version has moved on would be asked
        // to remove the instance from a service that never held it, and the endpoint would stay.
        server.Endpoints[1].Version.ShouldBe("1.2.3");
    }

    [Fact]
    public async Task DeregisterServerEndpointAsync_LeavesTheVersionUnsetForAnEndpointItDidNotPublish()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        // Nothing was published under this endpoint, so there is no version to repeat, and the
        // server is left to resolve the one it serves.
        await client.Mcp.DeregisterServerEndpointAsync(
            "weather", "10.0.0.7", 8080, TestContext.Current.CancellationToken);

        server.Endpoints[0].Type.ShouldBe(NacosAIRemote.DeregisterEndpoint);
        server.Endpoints[0].Version.ShouldBeNull();
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_RejectsAPortOutsideTheRange()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.Mcp.RegisterServerEndpointAsync(
                "weather", "10.0.0.7", 0, ct: TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => client.Mcp.RegisterServerEndpointAsync(
                "weather", "10.0.0.7", 65536, ct: TestContext.Current.CancellationToken));

        // The validation is local, so a request that could never be served does not reach the server.
        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_RejectsABlankAddress()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(
            () => client.Mcp.RegisterServerEndpointAsync(
                "weather", "  ", 8080, ct: TestContext.Current.CancellationToken));

        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_FailsWhenTheServerSaysItHasNoMcpRegistry()
    {
        var server = new AITestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.Mcp] = false,
        });

        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.RegisterServerEndpointAsync(
                "weather", "10.0.0.7", 8080, ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(501);
        server.Endpoints.ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_SurfacesTheErrorTheServerReported()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnEndpoint = _ => AITest.EndpointFailure(20004, "MCP server not found.");

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.RegisterServerEndpointAsync(
                "weather", "10.0.0.7", 8080, ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(20004);
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_ReplaysTheEndpointAfterAReconnect()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await client.Mcp.RegisterServerEndpointAsync(
            "weather", "10.0.0.7", 8080, "1.2.3", TestContext.Current.CancellationToken);

        server.CountRequests(EndpointRequestType).ShouldBe(1);

        await server.DropAndReconnectAsync();

        await AITestServer.WaitForAsync(() => server.CountRequests(EndpointRequestType) == 2);

        // The endpoint was registered on a connection that is gone, so the server dropped it with
        // that connection; replaying it is what keeps the published server reachable.
        McpServerEndpointRequest replayed = PayloadCodec.Decode<McpServerEndpointRequest>(
            server.Channel.UnaryRequests.Last(
                payload => PayloadCodec.ReadTypeName(payload) == EndpointRequestType))!;

        replayed.Type.ShouldBe(NacosAIRemote.RegisterEndpoint);
        replayed.Address.ShouldBe("10.0.0.7");
        replayed.Port.ShouldBe(8080);
        replayed.Version.ShouldBe("1.2.3");
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_DoesNotReplayAnEndpointTheServerRefused()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnEndpoint = _ => AITest.EndpointFailure(20004, "MCP server not found.");

        await Should.ThrowAsync<NacosServerException>(
            () => client.Mcp.RegisterServerEndpointAsync(
                "weather", "10.0.0.7", 8080, ct: TestContext.Current.CancellationToken));

        // The endpoint would be accepted now, which makes the assertion meaningful: the absence of a
        // replay is the client's doing rather than the server's.
        server.OnEndpoint = request => AITest.Endpoint(request.Type!);

        await server.DropAndReconnectAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        server.CountRequests(EndpointRequestType).ShouldBe(1);
    }

    [Fact]
    public async Task DeregisterServerEndpointAsync_StopsReplayingTheEndpoint()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await client.Mcp.RegisterServerEndpointAsync(
            "weather", "10.0.0.7", 8080, ct: TestContext.Current.CancellationToken);

        await client.Mcp.DeregisterServerEndpointAsync(
            "weather", "10.0.0.7", 8080, TestContext.Current.CancellationToken);

        await server.DropAndReconnectAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // A registration and a removal were sent on the first connection; nothing is replayed on the
        // second because the endpoint is no longer owned.
        server.CountRequests(EndpointRequestType).ShouldBe(2);
        server.Channel.UnaryRequests
            .Count(payload => PayloadCodec.ReadTypeName(payload) == EndpointRequestType)
            .ShouldBe(0);
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_ReplaysEveryEndpointOfAServer()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await client.Mcp.RegisterServerEndpointAsync(
            "weather", "10.0.0.7", 8080, ct: TestContext.Current.CancellationToken);

        await client.Mcp.RegisterServerEndpointAsync(
            "weather", "10.0.0.8", 8080, ct: TestContext.Current.CancellationToken);

        await server.DropAndReconnectAsync();

        await AITestServer.WaitForAsync(() => server.CountRequests(EndpointRequestType) == 4);

        // Both endpoints are owned, so both must come back; keying the record by server name alone
        // would lose one of them.
        server.Channel.UnaryRequests
            .Count(payload => PayloadCodec.ReadTypeName(payload) == EndpointRequestType)
            .ShouldBe(2);
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_FailsAfterDispose()
    {
        var server = new AITestServer();
        NacosAIClient client = server.CreateClient();

        await client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => client.Mcp.RegisterServerEndpointAsync(
                "weather", "10.0.0.7", 8080, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RegisterServerEndpointAsync_OpensTheAIConnectionOnlyWhenFirstUsed()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.Factory.RequestedAddresses.ShouldBeEmpty();

        await client.Mcp.RegisterServerEndpointAsync(
            "weather", "10.0.0.7", 8080, ct: TestContext.Current.CancellationToken);

        server.Factory.RequestedAddresses.Count.ShouldBe(1);
    }
}
