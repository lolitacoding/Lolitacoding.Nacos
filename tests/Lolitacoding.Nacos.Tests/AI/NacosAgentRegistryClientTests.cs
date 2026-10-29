using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies the RAD agent registry client's wire contract and its guard rails against a scripted AI
/// service.
/// </summary>
/// <remarks>
/// The RAD contract is the one AI surface that refuses to guess: it needs the server to state the
/// ability it implements, because a server that speaks only the A2A card contract needs a different
/// payload for the same intent. Most of what is worth pinning down here is therefore the negotiation,
/// together with the fields the server acts on — the batch that replaces a publisher's endpoints, and
/// the protocol a removal is scoped to.
/// </remarks>
public sealed class NacosAgentRegistryClientTests
{
    [Fact]
    public async Task PublishAgentAsync_SendsTheRequestInTheConfiguredNamespace()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient(Namespaced("staging"));

        var request = new NacosAgentPublishRequest
        {
            AgentName = "planner",
            Version = "1.0.0",
            DisplayName = "Planner",
            Author = "ci",
            AutoSubmit = true,
        };

        NacosAgentVersionDetail detail =
            await client.Agent.PublishAgentAsync(request, TestContext.Current.CancellationToken);

        AgentPublishRpcRequest sent = server.AgentPublishes[0];
        sent.NamespaceId.ShouldBe("staging");
        sent.PublishRequest.ShouldNotBeNull();
        sent.PublishRequest.AgentName.ShouldBe("planner");
        sent.PublishRequest.Version.ShouldBe("1.0.0");
        sent.PublishRequest.AutoSubmit.ShouldBeTrue();

        // The answer is the registry's own record of the version, which is the only place the status
        // and labels it assigned appear.
        detail.Version.ShouldBe("1.0.0");
        detail.Status.ShouldBe("reviewing");

        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1]).ShouldBe("AgentPublishRpcRequest");
    }

    [Fact]
    public async Task PublishAgentAsync_RefusesARequestWithoutANameOrAVersion()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(
            () => client.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest { Version = "1.0.0" },
                TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ArgumentException>(
            () => client.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest { AgentName = "planner" },
                TestContext.Current.CancellationToken));

        // The pair is the version's identity, so neither is sent without the other.
        server.AgentPublishes.ShouldBeEmpty();
    }

    [Fact]
    public async Task PublishAgentAsync_ReportsASuccessfulReplyWithNoDetailAsAContractFailure()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnAgentPublish = _ => new AgentPublishRpcResponse();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest { AgentName = "planner", Version = "1.0.0" },
                TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("planner");
    }

    [Fact]
    public async Task SearchAgentsAsync_SendsTheQueryAndReturnsThePage()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient(Namespaced("staging"));

        server.OnAgentSearch = _ => AITest.AgentPage(
            new NacosAgentSummary { AgentName = "planner", Status = "enable" },
            new NacosAgentSummary { AgentName = "researcher", Status = "enable" });

        var request = new NacosAgentSearchRequest
        {
            AgentNameContains = "plan",
            TagsAll = ["travel"],
            ProtocolsAny = ["a2a"],
            PageNo = 2,
            PageSize = 10,
        };

        NacosAgentPage<NacosAgentSummary> page =
            await client.Agent.SearchAgentsAsync(request, TestContext.Current.CancellationToken);

        AgentSearchRpcRequest sent = server.AgentSearches[0];
        sent.NamespaceId.ShouldBe("staging");
        sent.SearchRequest.ShouldNotBeNull();
        sent.SearchRequest.AgentNameContains.ShouldBe("plan");
        sent.SearchRequest.TagsAll.ShouldBe(["travel"]);
        sent.SearchRequest.ProtocolsAny.ShouldBe(["a2a"]);
        sent.SearchRequest.PageNo.ShouldBe(2);

        page.TotalCount.ShouldBe(2);
        page.PageItems.ShouldNotBeNull().Count.ShouldBe(2);

        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1]).ShouldBe("AgentSearchRpcRequest");
    }

    [Fact]
    public async Task SearchAgentsAsync_ReportsASuccessfulReplyWithNoPageAsAContractFailure()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnAgentSearch = _ => new AgentSearchResponse();

        await Should.ThrowAsync<NacosServerException>(
            () => client.Agent.SearchAgentsAsync(
                new NacosAgentSearchRequest(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoverAgentAsync_SendsTheReferenceAndTheFilter()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient(Namespaced("staging"));

        server.OnAgentDiscovery = _ => AITest.AgentDiscovery(new NacosAgentDiscoveryResult
        {
            NamespaceId = "staging",
            AgentName = "planner",
            Version = "1.0.0",
            ContentDigest = "sha256-canonical-json-v1:abc",
        });

        NacosAgentDiscoveryResult result = await client.Agent.DiscoverAgentAsync(
            new NacosAgentReference { AgentName = "planner", Label = "stable" },
            new NacosAgentDiscoveryFilter
            {
                Protocols = ["a2a"],
                Transports = ["JSONRPC"],
                EndpointSources = ["RUNTIME"],
            },
            TestContext.Current.CancellationToken);

        // The namespace travels inside the discovery request, because the same request is what a
        // watch subscribes with and a watch has to describe its target completely.
        AgentDiscoveryRpcRequest sent = server.AgentDiscoveries[0];
        sent.DiscoveryRequest.ShouldNotBeNull();
        sent.DiscoveryRequest.NamespaceId.ShouldBe("staging");
        sent.DiscoveryRequest.Reference.ShouldNotBeNull();
        sent.DiscoveryRequest.Reference.AgentName.ShouldBe("planner");
        sent.DiscoveryRequest.Reference.Label.ShouldBe("stable");
        sent.DiscoveryRequest.Filter.ShouldNotBeNull();
        sent.DiscoveryRequest.Filter.Transports.ShouldBe(["JSONRPC"]);

        result.Version.ShouldBe("1.0.0");
        result.ContentDigest.ShouldBe("sha256-canonical-json-v1:abc");

        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1]).ShouldBe("AgentDiscoveryRpcRequest");
    }

    [Fact]
    public async Task DiscoverAgentAsync_LeavesTheFilterUnsetWhenTheCallerNamesNone()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnAgentDiscovery = _ => AITest.AgentDiscovery(
            new NacosAgentDiscoveryResult { AgentName = "planner" });

        await client.Agent.DiscoverAgentAsync(
            new NacosAgentReference { AgentName = "planner" },
            ct: TestContext.Current.CancellationToken);

        server.AgentDiscoveries[0].DiscoveryRequest!.Filter.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAgentAsync_ReportsASuccessfulReplyWithNoResultAsAContractFailure()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient();

        server.OnAgentDiscovery = _ => new AgentDiscoveryResponse();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = "planner" },
                ct: TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("planner");
    }

    [Fact]
    public async Task RegisterEndpointsAsync_SendsTheBatch()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient(Namespaced("staging"));

        var batch = new NacosAgentEndpointBatch
        {
            AgentName = "planner",
            Protocol = "a2a",
            RuntimeVersion = "build-42",
            VersionRange = "[1.0.0,2.0.0)",
            Endpoints =
            [
                new NacosAgentRuntimeEndpoint { Uri = "http://10.0.0.7:9000/a2a", Transport = "JSONRPC" },
            ],
        };

        await client.Agent.RegisterEndpointsAsync(batch, TestContext.Current.CancellationToken);

        AgentEndpointRegisterRpcRequest sent = server.AgentEndpointRegisters[0];
        sent.NamespaceId.ShouldBe("staging");
        sent.RegistrationBatch.ShouldNotBeNull();
        sent.RegistrationBatch.AgentName.ShouldBe("planner");
        sent.RegistrationBatch.Protocol.ShouldBe("a2a");
        sent.RegistrationBatch.RuntimeVersion.ShouldBe("build-42");
        sent.RegistrationBatch.Endpoints.ShouldNotBeNull().Single().Uri.ShouldBe("http://10.0.0.7:9000/a2a");

        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1])
            .ShouldBe("AgentEndpointRegisterRpcRequest");
    }

    [Fact]
    public async Task RegisterEndpointsAsync_RefusesABatchThatWouldWithdrawRatherThanPublish()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient();

        // A batch replaces what the same publisher registered before, so an empty one would remove
        // the endpoints instead of publishing any.
        await Should.ThrowAsync<ArgumentException>(
            () => client.Agent.RegisterEndpointsAsync(
                new NacosAgentEndpointBatch { AgentName = "planner", Protocol = "a2a" },
                TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ArgumentException>(
            () => client.Agent.RegisterEndpointsAsync(
                new NacosAgentEndpointBatch
                {
                    Protocol = "a2a",
                    Endpoints = [new NacosAgentRuntimeEndpoint { Uri = "http://10.0.0.7:9000" }],
                },
                TestContext.Current.CancellationToken));

        server.AgentEndpointRegisters.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeregisterEndpointsAsync_ScopesTheRemovalToTheAgentAndProtocol()
    {
        var server = RadServer();
        await using NacosAIClient client = server.CreateClient(Namespaced("staging"));

        await client.Agent.DeregisterEndpointsAsync(
            "planner", "a2a", TestContext.Current.CancellationToken);

        AgentEndpointDeregisterRpcRequest sent = server.AgentEndpointDeregisters[0];
        sent.NamespaceId.ShouldBe("staging");
        sent.AgentName.ShouldBe("planner");
        sent.Protocol.ShouldBe("a2a");

        PayloadCodec.ReadTypeName(server.Channel.UnaryRequests[^1])
            .ShouldBe("AgentEndpointDeregisterRpcRequest");
    }

    [Fact]
    public async Task DiscoverAgentAsync_RefusesAnAgentThatStatesItHasNoRadRegistry()
    {
        var server = RadServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.RadV1] = false,
        });
        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = "planner" },
                ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(501);
        server.AgentDiscoveries.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAgentAsync_ReportsAnA2aOnlyServerAsNotImplementingRad()
    {
        // A server that states the older agent ability is a 3.1 or 3.2 one: it has the card contract
        // and nothing else, which is a refusal rather than an unknown.
        var server = RadServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.Agent] = true,
        });
        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = "planner" },
                ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(501);
        exception.Message.ShouldContain("3.3");
        server.AgentDiscoveries.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAgentAsync_RefusesToGuessWhenTheServerStatesNeitherAbility()
    {
        // Neither ability stated means the client cannot tell which agent contract the server speaks,
        // and the two need different payloads. Sending one and hoping is what this avoids.
        var server = RadServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.Mcp] = true,
        });
        await using NacosAIClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = "planner" },
                ct: TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(500);
        server.AgentDiscoveries.ShouldBeEmpty();
    }

    /// <summary>
    /// Builds a server that states the RAD ability, so the client's negotiation lets the call through.
    /// </summary>
    /// <param name="abilities">The ability table to state instead, when a test is about negotiation.</param>
    /// <returns>The server.</returns>
    private static AITestServer RadServer(Dictionary<string, bool>? abilities = null) =>
        new(abilities ?? new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAIServerAbility.RadV1] = true,
        });

    /// <summary>Builds the options a test needs to observe the namespace that was sent.</summary>
    /// <param name="namespace">The namespace.</param>
    /// <returns>The options.</returns>
    private static NacosOptions Namespaced(string @namespace) => new()
    {
        ServerAddresses = ["127.0.0.1:8848"],
        AppName = "ai-tests",
        Namespace = @namespace,
        ConnectionTimeout = TimeSpan.FromSeconds(5),
        Reconnect = new NacosReconnectOptions
        {
            InitialDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(2),
            JitterFactor = 0,
        },
    };
}
