using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the A2A agent card registry end to end against a real Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// What the server decides, rather than the client, is what is worth checking here: that a published
/// card is served back with the members it was published with, that the latest pointer is what a read
/// without a version resolves, and that an endpoint registered against a service-backed card is what
/// the card's address is assembled from. That last one is the reason a <c>SERVICE</c> card carries no
/// address of its own, so it is the assertion that proves the endpoint registration reached the
/// registry rather than merely being accepted.
/// </para>
/// <para>
/// The fixture publishes through the client itself: releasing a card is a client operation, unlike a
/// skill or an agent specification, whose admin API has no client-side counterpart.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AgentCardIntegrationTests
{
    [Fact]
    public async Task ReleaseAndRead_RoundTripsThroughARealServer()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("agent");

        var card = new NacosAgentCard
        {
            Name = agentName,
            Version = "1.0.0",
            Description = "Plans a trip.",
            ProtocolVersion = "0.2.5",
            PreferredTransport = NacosA2a.DefaultTransport,
            Url = "https://agents.example/planner",
            Capabilities = new NacosAgentCapabilities { Streaming = true },
            Skills = [new NacosAgentSkill { Id = "plan", Name = "Plan a trip", Tags = ["travel"] }],
        };

        await client.Ai.AgentCard.ReleaseAgentCardAsync(
            card, NacosA2a.EndpointTypeUrl, setAsLatest: true, ct: ct);

        NacosAgentCardDetail read = await NacosEventually.BecomesAsync(
            () => client.Ai.AgentCard.GetAgentCardAsync(agentName, ct: ct),
            value => value.Version == "1.0.0" && value.Skills is { Count: > 0 },
            ct);

        read.Name.ShouldBe(agentName);
        read.Description.ShouldBe("Plans a trip.");
        read.Url.ShouldBe("https://agents.example/planner");
        read.RegistrationType.ShouldBe(NacosA2a.EndpointTypeUrl);
        read.LatestVersion.ShouldBe(true);

        // The card is the A2A document, so what was published is what comes back.
        read.Capabilities.ShouldNotBeNull().Streaming.ShouldBe(true);
        read.Skills.ShouldNotBeNull().Single().Id.ShouldBe("plan");
        read.Skills!.Single().Tags.ShouldBe(["travel"]);
    }

    [Fact]
    public async Task GetAgentCardAsync_ReportsAnUnknownAgent()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Ai.AgentCard.GetAgentCardAsync(NacosHttp.UniqueId("agent-absent"), ct: ct));

        // A missing agent is reported as the not-found code, which is what the subscription turns
        // into the null element that announces a withdrawal.
        exception.ErrorCode.ShouldBe(404);
    }

    [Fact]
    public async Task GetAgentCardAsync_InjectsTheRegisteredEndpointIntoAServiceCard()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("agent-service");

        // A service-backed card still has to declare an interface: the registry validates the card
        // before it stores it, and the declared one is what the card falls back to while no runtime
        // endpoint is registered.
        var card = new NacosAgentCard
        {
            Name = agentName,
            Version = "1.0.0",
            Description = "A service-backed agent.",
            SupportedInterfaces = [PlaceholderInterface()],
        };

        await client.Ai.AgentCard.ReleaseAgentCardAsync(
            card, NacosA2a.EndpointTypeService, setAsLatest: true, ct: ct);

        await client.Ai.AgentCard.RegisterAgentEndpointAsync(
            agentName,
            new NacosAgentEndpoint
            {
                Address = "10.0.0.7",
                Port = 9000,
                Path = "/a2a",
                Version = "1.0.0",
                ProtocolVersion = "0.2.5",
            },
            ct);

        // A service-backed card names no address of its own: the registry assembles one from the
        // endpoints registered against it, which is what makes an agent callable at an address only
        // known at runtime.
        NacosAgentCardDetail read = await NacosEventually.BecomesAsync(
            () => client.Ai.AgentCard.GetAgentCardAsync(agentName, ct: ct),
            value => value.Url == "http://10.0.0.7:9000/a2a" && value.SupportedInterfaces is { Count: 1 },
            ct);

        read.Url.ShouldBe("http://10.0.0.7:9000/a2a");
        read.SupportedInterfaces.ShouldNotBeNull().Count.ShouldBe(1);
        read.SupportedInterfaces![0].Transport.ShouldBe(NacosA2a.DefaultTransport);
    }

    [Fact]
    public async Task DeregisterAgentEndpointAsync_RemovesTheEndpointFromTheCard()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("agent-gone");

        var card = new NacosAgentCard
        {
            Name = agentName,
            Version = "1.0.0",
            SupportedInterfaces = [PlaceholderInterface()],
        };

        await client.Ai.AgentCard.ReleaseAgentCardAsync(
            card, NacosA2a.EndpointTypeService, setAsLatest: true, ct: ct);

        var endpoint = new NacosAgentEndpoint
        {
            Address = "10.0.0.8",
            Port = 9001,
            Path = "/a2a",
            Version = "1.0.0",
            ProtocolVersion = "0.2.5",
        };

        await client.Ai.AgentCard.RegisterAgentEndpointAsync(agentName, endpoint, ct);

        await NacosEventually.BecomesAsync(
            () => client.Ai.AgentCard.GetAgentCardAsync(agentName, ct: ct),
            value => !string.IsNullOrEmpty(value.Url),
            ct);

        await client.Ai.AgentCard.DeregisterAgentEndpointAsync(agentName, endpoint, ct);

        // With no runtime endpoint left, the card falls back to the interface it declared, so the
        // registered address is gone and the placeholder is what a caller sees.
        NacosAgentCardDetail read = await NacosEventually.BecomesAsync(
            () => client.Ai.AgentCard.GetAgentCardAsync(agentName, ct: ct),
            value => value.Url == PlaceholderUrl && value.SupportedInterfaces is { Count: 1 },
            ct);

        read.SupportedInterfaces.ShouldNotBeNull().Count.ShouldBe(1);
        read.SupportedInterfaces![0].Url.ShouldBe(PlaceholderUrl);
    }

    [Fact]
    public async Task SubscribeAgentCardAsync_ReportsANewVersion()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client =
            CreateClient(container, TimeSpan.FromMilliseconds(500));

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentName = NacosHttp.UniqueId("agent-watch");

        await client.Ai.AgentCard.ReleaseAgentCardAsync(
            new NacosAgentCard
            {
                Name = agentName,
                Version = "1.0.0",
                Description = "Version one.",
                SupportedInterfaces = [PlaceholderInterface()],
            },
            NacosA2a.EndpointTypeService,
            setAsLatest: true,
            ct: ct);

        // A subscription whose first read reports the card as missing starts from null by design, so
        // the release has to be visible before the subscription is opened; otherwise the test races
        // the server's read-back and asserts against a null it asked for.
        await NacosEventually.BecomesAsync(
            () => client.Ai.AgentCard.GetAgentCardAsync(agentName, ct: ct),
            card => card.Version == "1.0.0",
            ct);

        await using IAsyncEnumerator<NacosAgentCardDetail?> changes =
            client.Ai.AgentCard.SubscribeAgentCardAsync(agentName, ct: ct).GetAsyncEnumerator(ct);

        (await changes.MoveNextAsync()).ShouldBeTrue();
        changes.Current.ShouldNotBeNull();
        changes.Current.Version.ShouldBe("1.0.0");

        await client.Ai.AgentCard.ReleaseAgentCardAsync(
            new NacosAgentCard
            {
                Name = agentName,
                Version = "2.0.0",
                Description = "Version two.",
                SupportedInterfaces = [PlaceholderInterface()],
            },
            NacosA2a.EndpointTypeService,
            setAsLatest: true,
            ct: ct);

        // Nacos pushes no card change, so the subscription is the refresh loop noticing that the
        // version a read without one resolves to has moved.
        await NacosEventually.ReportsAsync(changes, card => card?.Version == "2.0.0", ct);

        changes.Current!.Description.ShouldBe("Version two.");
    }

    /// <summary>The address a service-backed card declares while it has no runtime endpoint.</summary>
    private const string PlaceholderUrl = "http://placeholder.invalid/a2a";

    /// <summary>
    /// Builds the interface a service-backed card declares.
    /// </summary>
    /// <returns>The interface.</returns>
    /// <remarks>
    /// An interface is only valid when it names an address, a binding and a protocol version, so a
    /// card cannot be published without one even though a registered endpoint replaces it.
    /// </remarks>
    private static NacosAgentInterface PlaceholderInterface() => new()
    {
        Url = PlaceholderUrl,
        Transport = NacosA2a.DefaultTransport,
        ProtocolBinding = NacosA2a.DefaultTransport,
        ProtocolVersion = "0.2.5",
    };

    private static NacosClientFactory CreateClient(
        NacosContainer container,
        TimeSpan? cacheRefreshInterval = null) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                Ai = new NacosAiOptions
                {
                    CacheRefreshInterval = cacheRefreshInterval ?? TimeSpan.FromSeconds(10),
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
