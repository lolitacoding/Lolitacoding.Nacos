using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies the A2A agent card client's wire contract and its guard rails against a scripted AI
/// service.
/// </summary>
/// <remarks>
/// The card is the A2A protocol's document, so what is worth pinning down is that the client sends it
/// unchanged: the test asserts the request the server received rather than a projection of it. The
/// two operations that are not reads — publishing a card and registering an endpoint — are checked
/// for the fields the server acts on, because a default that silently differs from the official
/// client's would make an agent callable at the wrong place.
/// </remarks>
public sealed class NacosAgentCardClientTests
{
    [Fact]
    public async Task GetAgentCardAsync_AsksForTheNamedSelectorsInTheConfiguredNamespace()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        server.OnCard = _ => AiTest.Card(AiTest.CreateCard("planner", "1.2.3"));

        await client.AgentCard.GetAgentCardAsync(
            "planner",
            "1.2.3",
            NacosA2a.EndpointTypeUrl,
            TestContext.Current.CancellationToken);

        QueryAgentCardRequest request = server.Cards[0];
        request.AgentName.ShouldBe("planner");
        request.Version.ShouldBe("1.2.3");
        request.RegistrationType.ShouldBe(NacosA2a.EndpointTypeUrl);
        request.NamespaceId.ShouldBe("staging");

        // The card is read on the unary channel method, like every other AI request.
        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1]).ShouldBe("QueryAgentCardRequest");
    }

    [Fact]
    public async Task GetAgentCardAsync_LeavesTheSelectorsUnsetForTheLatestCard()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnCard = _ => AiTest.Card(AiTest.CreateCard());

        await client.AgentCard.GetAgentCardAsync("planner", ct: TestContext.Current.CancellationToken);

        // A client that asked for the latest version by name would need to know which label the
        // server keeps; leaving both selectors unset lets the server answer with what it serves.
        server.Cards[0].Version.ShouldBeNull();
        server.Cards[0].RegistrationType.ShouldBeNull();
    }

    [Fact]
    public async Task GetAgentCardAsync_ReturnsTheCardAsTheServerServesIt()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnCard = _ => AiTest.Card(new NacosAgentCardDetail
        {
            Name = "planner",
            Version = "1.2.3",
            Description = "Plans things.",
            Url = "https://agents.example/planner",
            RegistrationType = NacosA2a.EndpointTypeUrl,
            LatestVersion = true,
            Capabilities = new NacosAgentCapabilities { Streaming = true },
            Skills = [new NacosAgentSkill { Id = "plan", Name = "Plan a trip" }],
        });

        NacosAgentCardDetail card =
            await client.AgentCard.GetAgentCardAsync("planner", ct: TestContext.Current.CancellationToken);

        card.Name.ShouldBe("planner");
        card.Version.ShouldBe("1.2.3");
        card.Url.ShouldBe("https://agents.example/planner");
        card.RegistrationType.ShouldBe(NacosA2a.EndpointTypeUrl);
        card.Capabilities!.Streaming.ShouldBe(true);
        card.Skills!.Single().Id.ShouldBe("plan");
    }

    [Fact]
    public async Task GetAgentCardAsync_ReportsASuccessfulReplyWithNoCardAsAContractFailure()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        // A missing agent is reported as a failure result code, which the transport turns into an
        // exception. A success without a card therefore means the reply did not match the contract.
        server.OnCard = _ => new QueryAgentCardResponse();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.AgentCard.GetAgentCardAsync("planner", ct: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("planner");
    }

    [Fact]
    public async Task GetAgentCardAsync_RefusesAnAgentWhenTheServerSaysItHasNoAgentRegistry()
    {
        var server = new AiTestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAiServerAbility.Agent] = false,
        });
        await using NacosAiClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.AgentCard.GetAgentCardAsync("planner", ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(501);

        // The refusal is the client's, so no request was sent.
        server.Cards.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAgentCardAsync_StillTriesWhenTheServerSaysNothingAboutTheAgentRegistry()
    {
        // An absent ability is not a stated "no": a server that does not mention the registry may
        // still serve it, and the request's own answer is the more precise report.
        var server = new AiTestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAiServerAbility.Mcp] = true,
        });
        await using NacosAiClient client = server.CreateClient();

        server.OnCard = _ => AiTest.Card(AiTest.CreateCard());

        await client.AgentCard.GetAgentCardAsync("planner", ct: TestContext.Current.CancellationToken);

        server.Cards.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReleaseAgentCardAsync_RegistersTheCardAsAServiceAndLeavesTheLatestPointerAlone()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        var card = new NacosAgentCard
        {
            Name = "planner",
            Version = "1.0.0",
            Description = "Plans things.",
        };

        await client.AgentCard.ReleaseAgentCardAsync(
            card, ct: TestContext.Current.CancellationToken);

        ReleaseAgentCardRequest request = server.CardReleases[0];
        request.AgentName.ShouldBe("planner");
        request.NamespaceId.ShouldBe("staging");

        // A service registration is the default because an agent published from inside a
        // Nacos-registered process has no address to publish.
        request.RegistrationType.ShouldBe(NacosA2a.EndpointTypeService);

        // The latest pointer only moves when the caller asks for it, so a staged card does not
        // redirect callers before it is ready.
        request.SetAsLatest.ShouldBeFalse();

        request.AgentCard.ShouldNotBeNull();
        request.AgentCard.Version.ShouldBe("1.0.0");
        request.AgentCard.Description.ShouldBe("Plans things.");

        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1]).ShouldBe("ReleaseAgentCardRequest");
    }

    [Fact]
    public async Task ReleaseAgentCardAsync_PassesTheRegistrationTypeAndTheLatestFlagThrough()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        var card = new NacosAgentCard
        {
            Name = "planner",
            Version = "2.0.0",
            Url = "https://agents.example/planner",
        };

        await client.AgentCard.ReleaseAgentCardAsync(
            card,
            NacosA2a.EndpointTypeUrl,
            setAsLatest: true,
            ct: TestContext.Current.CancellationToken);

        ReleaseAgentCardRequest request = server.CardReleases[0];
        request.RegistrationType.ShouldBe(NacosA2a.EndpointTypeUrl);
        request.SetAsLatest.ShouldBeTrue();
        request.AgentCard!.Url.ShouldBe("https://agents.example/planner");
    }

    [Fact]
    public async Task ReleaseAgentCardAsync_RefusesACardWithoutANameOrAVersion()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(
            () => client.AgentCard.ReleaseAgentCardAsync(
                new NacosAgentCard { Version = "1.0.0" }, ct: TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ArgumentException>(
            () => client.AgentCard.ReleaseAgentCardAsync(
                new NacosAgentCard { Name = "planner" }, ct: TestContext.Current.CancellationToken));

        // The name and the version are the card's identity, so neither reaches the server without
        // the other.
        server.CardReleases.ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisterAgentEndpointAsync_SendsTheEndpointAsGiven()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        var endpoint = new NacosAgentEndpoint
        {
            Address = "10.0.0.7",
            Port = 9000,
            Path = "/a2a",
            SupportTls = true,
            Version = "1.0.0",
        };

        await client.AgentCard.RegisterAgentEndpointAsync(
            "planner", endpoint, TestContext.Current.CancellationToken);

        AgentEndpointRequest request = server.AgentEndpoints[0];
        request.AgentName.ShouldBe("planner");
        request.NamespaceId.ShouldBe("staging");
        request.Type.ShouldBe(NacosAiRemote.RegisterEndpoint);

        NacosAgentEndpoint sent = request.Endpoint.ShouldNotBeNull();
        sent.Address.ShouldBe("10.0.0.7");
        sent.Port.ShouldBe(9000);
        sent.Path.ShouldBe("/a2a");
        sent.SupportTls.ShouldBeTrue();
        sent.Version.ShouldBe("1.0.0");

        // The defaults are the A2A binding's own, so an endpoint built from an address and a port is
        // already a JSONRPC endpoint over HTTP.
        sent.Transport.ShouldBe(NacosA2a.DefaultTransport);
        sent.Protocol.ShouldBe(NacosA2a.DefaultProtocol);

        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1]).ShouldBe("AgentEndpointRequest");
    }

    [Fact]
    public async Task DeregisterAgentEndpointAsync_SendsTheEndpointWithTheDeregisterOperation()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        var endpoint = new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" };

        await client.AgentCard.DeregisterAgentEndpointAsync(
            "planner", endpoint, TestContext.Current.CancellationToken);

        AgentEndpointRequest request = server.AgentEndpoints[0];
        request.Type.ShouldBe(NacosAiRemote.DeregisterEndpoint);

        // The version travels with the removal: it is what the server resolves the instance by, so
        // dropping it would leave the registration behind.
        request.Endpoint!.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task RegisterAgentEndpointAsync_RefusesAnEndpointTheServerWouldRefuse()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        // The version names the service the registration becomes an instance of, so an endpoint
        // without one has nothing to register into.
        await Should.ThrowAsync<ArgumentException>(
            () => client.AgentCard.RegisterAgentEndpointAsync(
                "planner",
                new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000 },
                TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ArgumentException>(
            () => client.AgentCard.RegisterAgentEndpointAsync(
                "planner",
                new NacosAgentEndpoint { Port = 9000, Version = "1.0.0" },
                TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ArgumentException>(
            () => client.AgentCard.RegisterAgentEndpointAsync(
                "planner",
                new NacosAgentEndpoint { Address = "10.0.0.7", Port = 0, Version = "1.0.0" },
                TestContext.Current.CancellationToken));

        // Nothing reached the server, so the guard is the client's own.
        server.AgentEndpoints.ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisterAgentEndpointAsync_ReplaysTheEndpointAfterAReconnect()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        await client.AgentCard.RegisterAgentEndpointAsync(
            "planner",
            new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" },
            TestContext.Current.CancellationToken);

        await server.DropAndReconnectAsync();

        // The registration is a Naming instance owned by the connection that made it, so the server
        // drops it with the connection and the client has to publish it again. The replay runs on
        // the reconnect event, so wait for it rather than for the reconnect itself.
        await AiTestServer.WaitForAsync(() => server.AgentEndpoints.Count == 2);

        IReadOnlyList<AgentEndpointRequest> endpoints = server.AgentEndpoints;

        endpoints.Count.ShouldBe(2);
        endpoints[1].Type.ShouldBe(NacosAiRemote.RegisterEndpoint);
        endpoints[1].Endpoint!.Address.ShouldBe("10.0.0.7");
    }

    [Fact]
    public async Task RegisterAgentEndpointAsync_DoesNotReplayARegistrationTheServerRefused()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnAgentEndpoint = _ => AiTest.AgentEndpointFailure(500, "refused");

        await Should.ThrowAsync<NacosServerException>(
            () => client.AgentCard.RegisterAgentEndpointAsync(
                "planner",
                new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" },
                TestContext.Current.CancellationToken));

        await server.DropAndReconnectAsync();

        // The replay is asynchronous, so the absence assertion needs a window in which a replay
        // could have arrived.
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        // The caller saw the failure, so republishing later would report a state it never asked for.
        server.AgentEndpoints.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DeregisterAgentEndpointAsync_DropsTheRecordSoItIsNotReplayed()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        var endpoint = new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" };

        await client.AgentCard.RegisterAgentEndpointAsync(
            "planner", endpoint, TestContext.Current.CancellationToken);
        await client.AgentCard.DeregisterAgentEndpointAsync(
            "planner", endpoint, TestContext.Current.CancellationToken);

        await server.DropAndReconnectAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);

        // Only the original registration and the removal: a reconnect must not bring back an
        // endpoint that was taken away.
        server.AgentEndpoints.Count.ShouldBe(2);
        server.AgentEndpoints[1].Type.ShouldBe(NacosAiRemote.DeregisterEndpoint);
    }
}
