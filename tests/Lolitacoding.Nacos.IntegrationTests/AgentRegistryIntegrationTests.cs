using System.Text.Json;
using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the RAD agent registry end to end against a real Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// This is the 3.3 agent contract, so the suite runs it against the 3.3 image rather than the AI one.
/// What the server decides is what is worth checking: that a published version is the one a discovery
/// resolves, and that a registered endpoint batch is what a discovery reports — the batch is the unit
/// the registry replaces, so publishing one is what makes an agent callable at a runtime address.
/// </para>
/// <para>
/// The fixture publishes through the client alone. The first version of an agent is submitted as it is
/// published and the default pipeline puts it online, so a discovery resolves it without an approval
/// step; a later version of an existing agent is a draft until it is submitted.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AgentRegistryIntegrationTests
{
    [Fact]
    public async Task PublishSearchAndDiscover_RoundTripsThroughARealServer()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AgentImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("rad");

        // The publish is repeated through WritesAsync because the server reads the version back
        // before it answers, and that read can miss the write it just applied — 3.0.2 answers
        // "Agent Version cannot be read after creation" for it. An identical request is answered
        // with the existing version rather than creating a second one, so the repeat is safe.
        NacosAgentVersionDetail published = await NacosEventually.WritesAsync(
            () => client.Ai.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest
                {
                    AgentName = agentName,
                    Version = "1.0.0",
                    DisplayName = "Planner",
                    Description = "Plans things.",
                    Author = "integration-tests",
                    Tags = ["travel"],
                    CallInterfaces = [CallInterface()],
                },
                ct),
            ct);

        // The first version of an agent is submitted as it is published, and the default pipeline
        // puts it online, so a discovery has something to resolve without an approval step.
        published.Version.ShouldBe("1.0.0");
        published.Status.ShouldBe(NacosAiStatus.VersionOnline);

        NacosAgentDiscoveryResult discovered = await NacosEventually.BecomesAsync(
            () => client.Ai.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = agentName }, ct: ct),
            value => value.Version == "1.0.0" && value.CallInterfaces is { Count: > 0 },
            ct);

        discovered.AgentName.ShouldBe(agentName);
        discovered.Version.ShouldBe("1.0.0");
        discovered.ContentDigest.ShouldNotBeNullOrWhiteSpace();
        discovered.CallInterfaces.ShouldNotBeNull().Single().Protocol.ShouldBe("a2a");
    }

    [Fact]
    public async Task SearchAgentsAsync_FindsThePublishedAgent()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AgentImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("rad-search");

        await NacosEventually.WritesAsync(
            () => client.Ai.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest
                {
                    AgentName = agentName,
                    Version = "1.0.0",
                    Description = "Searchable.",
                    Tags = ["travel"],
                    CallInterfaces = [CallInterface()],
                },
                ct),
            ct);

        // A search reads the shared AI resource index, not the registry itself: the server projects
        // an agent into it only once its latest version is online, and a background task applies the
        // projection on a short poll. The wait therefore spans that task, which is why it is given
        // longer than a read that only waits for the publish to be applied.
        NacosAgentPage<NacosAgentSummary> page = await NacosEventually.BecomesAsync(
            () => client.Ai.Agent.SearchAgentsAsync(
                new NacosAgentSearchRequest
                {
                    AgentNameContains = agentName,
                    PageNo = 1,
                    PageSize = 10,
                },
                ct),
            value => value.TotalCount > 0,
            ct,
            timeout: TimeSpan.FromMinutes(2));

        page.PageItems.ShouldNotBeNull()
            .ShouldContain(agent => agent.AgentName == agentName);
    }

    [Fact]
    public async Task DiscoverAgentAsync_ReportsAnUnknownAgent()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AgentImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;

        // The gRPC error mapper reports the v2 business code rather than the HTTP status, so an agent
        // that was never published arrives as the AI module's own resource-not-found. The watch
        // decides what to do about an absent agent from that code, which is why it is pinned here.
        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Ai.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = NacosHttp.UniqueId("rad-absent") }, ct: ct));

        exception.ErrorCode.ShouldBe(20004);
    }

    [Fact]
    public async Task RegisterEndpointsAsync_BecomesWhatADiscoveryReports()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AgentImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("rad-endpoints");

        await NacosEventually.WritesAsync(
            () => client.Ai.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest
                {
                    AgentName = agentName,
                    Version = "1.0.0",
                    Description = "Callable.",
                    CallInterfaces = [CallInterface()],
                },
                ct),
            ct);

        await NacosEventually.BecomesAsync(
            () => client.Ai.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = agentName }, ct: ct),
            value => value.Version == "1.0.0",
            ct);

        await client.Ai.Agent.RegisterEndpointsAsync(
            new NacosAgentEndpointBatch
            {
                AgentName = agentName,
                Protocol = "a2a",

                // The batch says which agent versions its endpoints serve, and an endpoint without a
                // binding inherits it. A registration that names neither has no version to bind to,
                // which the registry refuses.
                RuntimeVersion = "1.0.0",
                VersionRange = "[1.0.0,2.0.0)",
                Endpoints =
                [
                    new NacosAgentRuntimeEndpoint
                    {
                        Uri = "http://10.0.0.7:9000/a2a",
                        Transport = "JSONRPC",
                    },
                ],
            },
            ct);

        // The registered endpoint is what a caller is meant to reach, so it has to be what the
        // discovery reports rather than something only the publisher can see.
        NacosAgentDiscoveryResult discovered = await NacosEventually.BecomesAsync(
            () => client.Ai.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = agentName },
                ct: ct),
            value => RuntimeUris(value).Contains("http://10.0.0.7:9000/a2a"),
            ct);

        RuntimeUris(discovered).ShouldContain("http://10.0.0.7:9000/a2a");
    }

    [Fact]
    public async Task DeregisterEndpointsAsync_RemovesThemFromWhatADiscoveryReports()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AgentImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("rad-withdraw");

        await NacosEventually.WritesAsync(
            () => client.Ai.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest
                {
                    AgentName = agentName,
                    Version = "1.0.0",
                    CallInterfaces = [CallInterface()],
                },
                ct),
            ct);

        await client.Ai.Agent.RegisterEndpointsAsync(
            new NacosAgentEndpointBatch
            {
                AgentName = agentName,
                Protocol = "a2a",

                // The batch says which agent versions its endpoints serve, and an endpoint without a
                // binding inherits it. A registration that names neither has no version to bind to,
                // which the registry refuses.
                RuntimeVersion = "1.0.0",
                VersionRange = "[1.0.0,2.0.0)",
                Endpoints =
                [
                    new NacosAgentRuntimeEndpoint
                    {
                        Uri = "http://10.0.0.8:9001/a2a",
                        Transport = "JSONRPC",
                    },
                ],
            },
            ct);

        await NacosEventually.BecomesAsync(
            () => client.Ai.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = agentName }, ct: ct),
            value => RuntimeUris(value).Contains("http://10.0.0.8:9001/a2a"),
            ct);

        await client.Ai.Agent.DeregisterEndpointsAsync(agentName, "a2a", ct);

        // The removal is by protocol because the endpoints a process registered are only meaningful
        // as a set, so what a discovery reports afterwards has none of them.
        NacosAgentDiscoveryResult discovered = await NacosEventually.BecomesAsync(
            () => client.Ai.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = agentName },
                ct: ct),
            value => !RuntimeUris(value).Contains("http://10.0.0.8:9001/a2a"),
            ct);

        RuntimeUris(discovered).ShouldNotContain("http://10.0.0.8:9001/a2a");
    }

    /// <summary>
    /// Builds the call interface a test publishes.
    /// </summary>
    /// <returns>The interface.</returns>
    /// <remarks>
    /// A publish must carry either call interfaces or a base version to copy from, and never both, so
    /// the interface is what makes a fresh version publishable at all.
    /// </remarks>
    private static NacosAgentCallInterface CallInterface() => new()
    {
        Protocol = "a2a",
        ProtocolVersion = "0.2.5",

        // The registry validates a call interface as a descriptor: it has to say what the descriptor
        // is and carry one, so neither can be left out.
        DescriptorMediaType = "application/json",
        NativeDescriptor = JsonSerializer.SerializeToElement(new { name = "planner" }),

        // Publishing needs the order the sources are consulted in; a discovery result is the one that
        // must not carry it, because by then the order has been applied.
        EndpointSourceOrder = ["RUNTIME", "DECLARED"],
    };

    /// <summary>
    /// Collects the runtime endpoint URIs a discovery reported.
    /// </summary>
    /// <param name="result">The discovery result.</param>
    /// <returns>The URIs.</returns>
    private static List<string> RuntimeUris(NacosAgentDiscoveryResult result) =>
        [.. (result.CallInterfaces ?? [])
            .SelectMany(callInterface => callInterface.EndpointSets ?? [])
            .Where(set => string.Equals(set.Source, "RUNTIME", StringComparison.Ordinal))
            .SelectMany(set => set.Endpoints ?? [])
            .Select(endpoint => endpoint.Uri)
            .OfType<string>()];

    private static NacosClientFactory CreateClient(NacosContainer container) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),
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
}
