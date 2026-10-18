using System.Text.Json;
using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the RAD agent watch end to end against a real Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// This is the 3.3 agent watch, so the suite runs it against the 3.3 image: the server pushes the
/// hint, and what is worth checking is that the client acts on it — that a watch reports the state the
/// agent was published with, and then the state a later publish made current without the caller
/// asking again.
/// </para>
/// <para>
/// The watch is the one AI surface that depends on a server-initiated message, so a client that
/// installed the watch but never answered the push would pass every other test in this suite and fail
/// here. The pushes are therefore waited for rather than read on a schedule, and the wait is repeated
/// because the subscription is registered with the server only after the state it was opened at has
/// been read.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AgentWatchIntegrationTests
{
    [Fact]
    public async Task WatchAgentAsync_ReportsThePublishedStateAndThenAChange()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AgentImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("rad-watch");

        // The publish is a precondition — the watch is what this test is about — so a refusal that is
        // the server's own read-back timing is repeated rather than reported.
        await NacosEventually.WritesAsync(
            () => client.Ai.Agent.PublishAgentAsync(Publish(agentName, "1.0.0"), ct),
            ct);

        // The watch is opened once the first version is the one a discovery resolves, so the first
        // element it reports is a fact about the agent rather than about how far the publish got.
        await NacosEventually.BecomesAsync(
            () => client.Ai.Agent.DiscoverAgentAsync(
                new NacosAgentReference { AgentName = agentName }, ct: ct),
            value => value.Version == "1.0.0",
            ct,
            tolerateFailures: true);

        await using IAsyncEnumerator<NacosAgentDiscoveryResult?> changes =
            client.Ai.Agent.WatchAgentAsync(new NacosAgentReference { AgentName = agentName }, ct: ct)
                .GetAsyncEnumerator(ct);

        // The wait is bounded rather than open-ended, so a watch that installed itself and then never
        // reported the state fails the test instead of hanging it.
        await NacosEventually.ReportsAsync(changes, value => value?.Version == "1.0.0", ct);

        changes.Current.ShouldNotBeNull();
        changes.Current.Version.ShouldBe("1.0.0");

        // The change is applied until the watch reports it: a hint is only pushed for a change the
        // server sees after the watch was installed, and a repeated publish of a version that exists
        // is answered with the version rather than refused.
        await NacosEventually.PushedAsync(
            changeCt => client.Ai.Agent.PublishAgentAsync(Publish(agentName, "2.0.0"), changeCt),
            changes,
            value => value?.Version == "2.0.0",
            ct);

        changes.Current!.Version.ShouldBe("2.0.0");
    }

    [Fact]
    public async Task WatchAgentAsync_ReportsAnAgentThatIsNotPublished()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AgentImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("rad-absent-watch");

        await using IAsyncEnumerator<NacosAgentDiscoveryResult?> changes =
            client.Ai.Agent.WatchAgentAsync(new NacosAgentReference { AgentName = agentName }, ct: ct)
                .GetAsyncEnumerator(ct);

        // An agent that was never published is a state the watch reports rather than a failure of it,
        // because the answer to "is it there yet" is the thing the caller is waiting for. What the
        // server sends when the agent appears is the hint that turns this into a positive answer.
        await NacosEventually.ReportsAsync(changes, value => value is null, ct);

        changes.Current.ShouldBeNull();
    }

    /// <summary>
    /// Builds the publication a test writes.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The version to write.</param>
    /// <returns>The request.</returns>
    /// <remarks>
    /// A version other than the first is only submitted when it is asked to be, so <c>AutoSubmit</c>
    /// is what makes the second version one a discovery resolves — and therefore one the watch can
    /// report.
    /// </remarks>
    private static NacosAgentPublishRequest Publish(string agentName, string version) => new()
    {
        AgentName = agentName,
        Version = version,
        DisplayName = "Planner",
        Description = $"Version {version}.",
        Author = "integration-tests",
        Tags = ["travel"],
        AutoSubmit = true,
        CallInterfaces = [CallInterface()],
    };

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
                Ai = new NacosAiOptions
                {
                    // The interval is the base of the watch's retry backoff, and a short one keeps a
                    // retried read from slowing a test down.
                    CacheRefreshInterval = TimeSpan.FromMilliseconds(500),
                },
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
