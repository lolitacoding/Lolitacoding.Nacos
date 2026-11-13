using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.AI;

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
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        server.OnCard = _ => AITest.Card(AITest.CreateCard("planner", "1.2.3"));

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
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnCard = _ => AITest.Card(AITest.CreateCard());

        await client.AgentCard.GetAgentCardAsync("planner", ct: TestContext.Current.CancellationToken);

        // A client that asked for the latest version by name would need to know which label the
        // server keeps; leaving both selectors unset lets the server answer with what it serves.
        server.Cards[0].Version.ShouldBeNull();
        server.Cards[0].RegistrationType.ShouldBeNull();
    }

    [Fact]
    public async Task GetAgentCardAsync_ReturnsTheCardAsTheServerServesIt()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnCard = _ => AITest.Card(new NacosAgentCardDetail
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
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

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
        var server = new AITestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.Agent] = false,
        });
        await using NacosAIClient client = server.CreateClient();

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
        var server = new AITestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.Mcp] = true,
        });
        await using NacosAIClient client = server.CreateClient();

        server.OnCard = _ => AITest.Card(AITest.CreateCard());

        await client.AgentCard.GetAgentCardAsync("planner", ct: TestContext.Current.CancellationToken);

        server.Cards.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReleaseAgentCardAsync_RegistersTheCardAsAServiceAndLeavesTheLatestPointerAlone()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient(new NacosOptions
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
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

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
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

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
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient(new NacosOptions
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
        request.Type.ShouldBe(NacosAIRemote.RegisterEndpoint);

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
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        var endpoint = new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" };

        await client.AgentCard.DeregisterAgentEndpointAsync(
            "planner", endpoint, TestContext.Current.CancellationToken);

        AgentEndpointRequest request = server.AgentEndpoints[0];
        request.Type.ShouldBe(NacosAIRemote.DeregisterEndpoint);

        // The version travels with the removal: it is what the server resolves the instance by, so
        // dropping it would leave the registration behind.
        request.Endpoint!.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task RegisterAgentEndpointAsync_RefusesAnEndpointTheServerWouldRefuse()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

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
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await client.AgentCard.RegisterAgentEndpointAsync(
            "planner",
            new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" },
            TestContext.Current.CancellationToken);

        await server.DropAndReconnectAsync();

        // The registration is a Naming instance owned by the connection that made it, so the server
        // drops it with the connection and the client has to publish it again. The replay runs on
        // the reconnect event, so wait for it rather than for the reconnect itself.
        await AITestServer.WaitForAsync(() => server.AgentEndpoints.Count == 2);

        IReadOnlyList<AgentEndpointRequest> endpoints = server.AgentEndpoints;

        endpoints.Count.ShouldBe(2);
        endpoints[1].Type.ShouldBe(NacosAIRemote.RegisterEndpoint);
        endpoints[1].Endpoint!.Address.ShouldBe("10.0.0.7");
    }

    [Fact]
    public async Task RegisterAgentEndpointAsync_DoesNotReplayARegistrationTheServerRefused()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnAgentEndpoint = _ => AITest.AgentEndpointFailure(500, "refused");

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
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

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
        server.AgentEndpoints[1].Type.ShouldBe(NacosAIRemote.DeregisterEndpoint);
    }

    [Fact]
    public async Task RegisterAgentEndpointsAsync_SendsOneRequestCarryingTheWholeSet()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient(new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "ai-tests",
            Namespace = "staging",
        });

        await client.AgentCard.RegisterAgentEndpointsAsync(
            "planner",
            [
                new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" },
                new NacosAgentEndpoint
                {
                    Address = "10.0.0.7",
                    Port = 9001,
                    Path = "/grpc",
                    Transport = "GRPC",
                    Version = "1.0.0",
                },
            ],
            TestContext.Current.CancellationToken);

        // One request, not two: the point of the batch is that the set is published atomically.
        server.AgentEndpoints.ShouldBeEmpty();

        BatchAgentEndpointRequest request = server.AgentEndpointBatches.ShouldHaveSingleItem();
        request.AgentName.ShouldBe("planner");
        request.NamespaceId.ShouldBe("staging");
        request.Type.ShouldBe(NacosAIRemote.BatchRegisterEndpoint);

        IReadOnlyList<NacosAgentEndpoint> sent = request.Endpoints.ShouldNotBeNull();
        sent.Count.ShouldBe(2);
        sent[0].Port.ShouldBe(9000);
        sent[1].Transport.ShouldBe("GRPC");

        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1])
            .ShouldBe("BatchAgentEndpointRequest");
    }

    [Fact]
    public async Task RegisterAgentEndpointsAsync_RejectsAnEmptyBatch()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        // "Register nothing" is a removal, and it has its own call; failing here says so before a
        // round trip.
        await Should.ThrowAsync<ArgumentException>(
            () => client.AgentCard.RegisterAgentEndpointsAsync(
                "planner", [], TestContext.Current.CancellationToken));

        server.AgentEndpointBatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisterAgentEndpointsAsync_RejectsEndpointsOfDifferentVersions()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        // The endpoints become instances of one service, named after the agent and the version, so a
        // mixed batch has no single service to register into. The server refuses it too.
        await Should.ThrowAsync<ArgumentException>(
            () => client.AgentCard.RegisterAgentEndpointsAsync(
                "planner",
                [
                    new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" },
                    new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9001, Version = "2.0.0" },
                ],
                TestContext.Current.CancellationToken));

        server.AgentEndpointBatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task RegisterAgentEndpointsAsync_ReplaysTheBatchAsOneRequest()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await client.AgentCard.RegisterAgentEndpointsAsync(
            "planner",
            [
                new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" },
                new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9001, Version = "1.0.0" },
            ],
            TestContext.Current.CancellationToken);

        await server.DropAndReconnectAsync();
        await AITestServer.WaitForAsync(() => server.AgentEndpointBatches.Count == 2);

        // Replaying the set as one request is what makes the batch cheaper than a loop of single
        // registrations, and it keeps the set atomic across the reconnect.
        server.AgentEndpoints.ShouldBeEmpty();
        server.AgentEndpointBatches[1].Endpoints.ShouldNotBeNull().Count.ShouldBe(2);
        server.AgentEndpointBatches[1].Type.ShouldBe(NacosAIRemote.BatchRegisterEndpoint);
    }

    [Fact]
    public async Task RegisterAgentEndpointsAsync_KeepsTheReplacedSetWhenTheServerRefusesTheBatch()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        await client.AgentCard.RegisterAgentEndpointsAsync(
            "planner",
            [new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" }],
            TestContext.Current.CancellationToken);

        server.OnAgentEndpointBatch = _ => AITest.AgentEndpointFailure(500, "refused");

        await Should.ThrowAsync<NacosServerException>(
            () => client.AgentCard.RegisterAgentEndpointsAsync(
                "planner",
                [new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9001, Version = "1.0.0" }],
                TestContext.Current.CancellationToken));

        await server.DropAndReconnectAsync();
        await AITestServer.WaitForAsync(() => server.AgentEndpointBatches.Count == 3);

        // The refused batch was never applied, so it must not be replayed; the set it was meant to
        // replace is still the server's state and is what a reconnect has to publish.
        IReadOnlyList<NacosAgentEndpoint> replayed =
            server.AgentEndpointBatches[2].Endpoints.ShouldNotBeNull();
        replayed.ShouldHaveSingleItem().Port.ShouldBe(9000);
    }

    [Fact]
    public async Task RegisterAgentEndpointsAsync_WithdrawsTheSingleRegistrationsItReplaces()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        var replaced = new NacosAgentEndpoint
        {
            Address = "10.0.0.8",
            Port = 9002,
            Version = "1.0.0",
        };

        await client.AgentCard.RegisterAgentEndpointAsync(
            "planner", replaced, TestContext.Current.CancellationToken);

        await client.AgentCard.RegisterAgentEndpointsAsync(
            "planner",
            [new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" }],
            TestContext.Current.CancellationToken);

        await server.DropAndReconnectAsync();
        await AITestServer.WaitForAsync(() => server.AgentEndpointBatches.Count == 2);

        // The server replaced this publisher's whole set, so replaying the withdrawn endpoint would
        // resurrect a registration the caller replaced.
        server.AgentEndpoints.Count.ShouldBe(1);
        server.AgentEndpoints[0].Type.ShouldBe(NacosAIRemote.RegisterEndpoint);
    }

    [Fact]
    public async Task DeregisterAgentEndpointAsync_DropsTheEndpointFromTheReplayedBatch()
    {
        var server = new AITestServer();
        await using NacosAIClient client = server.CreateClient();

        var kept = new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9000, Version = "1.0.0" };
        var removed = new NacosAgentEndpoint { Address = "10.0.0.7", Port = 9001, Version = "1.0.0" };

        await client.AgentCard.RegisterAgentEndpointsAsync(
            "planner", [kept, removed], TestContext.Current.CancellationToken);

        await client.AgentCard.DeregisterAgentEndpointAsync(
            "planner", removed, TestContext.Current.CancellationToken);

        await server.DropAndReconnectAsync();
        await AITestServer.WaitForAsync(() => server.AgentEndpointBatches.Count == 2);

        // The batch is replayed as one request, so the removed endpoint has to leave the recorded
        // set or the replay would put it back.
        IReadOnlyList<NacosAgentEndpoint> replayed =
            server.AgentEndpointBatches[1].Endpoints.ShouldNotBeNull();
        replayed.Count.ShouldBe(1);
        replayed[0].Port.ShouldBe(9000);
    }
}
