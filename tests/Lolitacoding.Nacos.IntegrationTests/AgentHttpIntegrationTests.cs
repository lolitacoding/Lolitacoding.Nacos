using System.Text.Json;
using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the RAD agent registry's HTTP transport end to end against a real Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// The scripted handler in the unit tests agrees with whatever the client builds, so only the real
/// endpoint proves that the path, the query names, the form fields whose values are JSON documents
/// and the result envelope are the server's. What is worth checking here is the same thing the gRPC
/// integration tests check — that a published version is what a discovery resolves and a search finds
/// — but over the client OpenAPI rather than the AI connection.
/// </para>
/// <para>
/// The container is started from the 3.3 image: the agent client endpoints are marked
/// <c>@Since("3.3.0")</c> on the server, so a 3.2 server answers them the way it answers a payload
/// type it has no handler for.
/// </para>
/// <para>
/// The stock 3.3.0-RC server gates these endpoints behind <c>AgentClientMigrationGuard</c>, which
/// admits them only while <c>nacos.ai.a2a.compatibility.mode</c> is <c>CANONICAL</c> — the default.
/// A deployment left on a historical A2A mode answers with a conflict instead, which the client
/// reports as the server's error code rather than retrying.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AgentHttpIntegrationTests
{
    /// <summary>The version every publish in this class creates.</summary>
    private const string Version = "1.0.0";

    [Fact]
    public async Task PublishDiscoverAndSearch_OverHttp_RoundTripsThroughARealServer()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AgentHttpImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("rad-http");

        // The publish is repeated because the server reads the version back before it answers, and
        // that read can miss the write it just applied. AutoSubmit makes a repeat of an existing
        // version online, which is what the discovery and search below need to find.
        NacosAgentVersionDetail published = await NacosEventually.WritesAsync(
            () => client.AI.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest
                {
                    AgentName = agentName,
                    Version = Version,
                    DisplayName = "Planner",
                    Description = "Plans things over HTTP.",
                    Author = "integration-tests",
                    Tags = ["travel"],
                    CallInterfaces = [CallInterface()],
                    AutoSubmit = true,
                },
                ct),
            ct);

        published.Version.ShouldBe(Version);
        published.Status.ShouldBe(NacosAIStatus.VersionOnline);

        NacosAgentDiscoveryResult discovered = await NacosEventually.BecomesAsync(
            () => client.AI.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = agentName }, ct: ct),
            value => value.Version == Version && value.CallInterfaces is { Count: > 0 },
            ct);

        discovered.AgentName.ShouldBe(agentName);
        discovered.Version.ShouldBe(Version);
        discovered.ContentDigest.ShouldNotBeNullOrWhiteSpace();
        discovered.CallInterfaces.ShouldNotBeNull().Single().Protocol.ShouldBe("a2a");

        // A search reads the shared AI resource index rather than the registry itself, and the server
        // applies that projection on a background task, so the wait spans the task rather than only
        // the write. The query parameters are what this test is proving: the real endpoint binds them
        // the way the client sends them, repeated values included.
        NacosAgentPage<NacosAgentSummary> page = await NacosEventually.BecomesAsync(
            () => client.AI.Agent.SearchAgentsAsync(
                new NacosAgentSearchRequest
                {
                    AgentNameContains = agentName,
                    TagsAll = ["travel"],
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
    public async Task DiscoverAgentAsync_OverHttp_MapsAnUnknownAgentToTheNotFoundCode()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AgentHttpImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;

        // Publish one agent first, so that the error below is the registry's answer to an unknown
        // name rather than a server that does not serve the HTTP endpoints at all: those are
        // different failures and only the second one would make this test pass for the wrong reason.
        string present = NacosHttp.UniqueId("rad-http-present");

        await NacosEventually.WritesAsync(
            () => client.AI.Agent.PublishAgentAsync(
                new NacosAgentPublishRequest
                {
                    AgentName = present,
                    Version = Version,
                    AutoSubmit = true,
                    CallInterfaces = [CallInterface()],
                },
                ct),
            ct);

        await NacosEventually.BecomesAsync(
            () => client.AI.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = present }, ct: ct),
            value => value.Version == Version,
            ct);

        // The client OpenAPI reports a missing agent as HTTP 404, where the gRPC result carries the
        // AI module's own not-found business code. A watch decides what an absent agent means by the
        // code, so the transport's status has to arrive as the code the caller expects.
        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.AI.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = NacosHttp.UniqueId("rad-http-absent") },
                ct: ct));

        exception.ErrorCode.ShouldBe(404);
    }

    /// <summary>
    /// Builds the call interface a test publishes.
    /// </summary>
    /// <returns>The interface.</returns>
    /// <remarks>
    /// A publish must carry either call interfaces or a base version to copy from, and never both, so
    /// the interface is what makes a fresh version publishable at all. The registry validates it as a
    /// descriptor, so it has to say what the descriptor is and carry one.
    /// </remarks>
    private static NacosAgentCallInterface CallInterface() => new()
    {
        Protocol = "a2a",
        ProtocolVersion = "0.2.5",
        DescriptorMediaType = "application/json",
        NativeDescriptor = JsonSerializer.SerializeToElement(new { name = "planner" }),
        EndpointSourceOrder = ["RUNTIME", "DECLARED"],
    };

    private static NacosClientFactory CreateClient(NacosContainer container) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),

                // Http means Http for this registry: the AI connection is never opened, which is the
                // property that makes the agent registry usable behind an ingress that only exposes
                // the client OpenAPI.
                AI = new NacosAIOptions { AgentTransportMode = NacosAITransportMode.Http },
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
