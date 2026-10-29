using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the agent-specification registry end to end against a real Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// What the server decides, rather than the client, is what is worth checking here: which version a
/// selector resolves to, that the manifest and the resource files are stored and handed back
/// unchanged, and that the version and hash travel in the headers rather than in the body. This
/// resource resolves a label before a version — the other way round from a prompt — so the selector
/// test asserts on that order explicitly.
/// </para>
/// <para>
/// The fixture publishes through the HTTP admin API: a first version is uploaded as a package whose
/// <c>manifest.json</c> names the worker, and later versions are forked from it and edited. The
/// registry numbers the uploaded version itself, which is why the first one is asserted as
/// <c>0.0.1</c> rather than passed in.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AgentSpecIntegrationTests
{
    /// <summary>The version the registry gives the first uploaded version.</summary>
    private const string FirstVersion = "0.0.1";

    /// <summary>The version the fixture publishes second, which the latest label moves to.</summary>
    private const string SecondVersion = "0.0.2";

    [Fact]
    public async Task PublishAndRead_RoundTripsThroughARealServer()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AIImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentSpecName = NacosHttp.UniqueId("spec");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishAgentSpecAsync(
            http,
            container.HttpAddress,
            agentSpecName,
            FirstVersion,
            "A worker that does the thing.",
            "You are the worker.",
            basedOnVersion: null,
            accessToken: token,
            ct: ct);

        // A publish answers before the version is servable, and a read that gets there first is
        // answered the way an unknown name is. The resource map is part of what the version serves —
        // it is populated from the stored manifest a moment later — so the wait is on the state the
        // assertions below are about, not just on the version being resolvable.
        NacosAgentSpec spec = await NacosEventually.BecomesAsync(
            () => client.AI.AgentSpec.GetAgentSpecAsync(agentSpecName, ct: ct),
            value => value.Version == FirstVersion && value.Resource is { Count: > 0 },
            ct);

        spec.Name.ShouldBe(agentSpecName);
        spec.Description.ShouldBe("A worker that does the thing.");
        spec.Md5.ShouldNotBeNullOrWhiteSpace();

        // The manifest is handed back exactly as it was stored, in the body rather than parsed.
        spec.Content.ShouldNotBeNull();
        spec.Content.ShouldContain(agentSpecName);

        // The resource map is keyed by the registry's identifier, and the identifier is what names
        // the path; the name is only the file's own name.
        NacosAgentSpecResource resource = spec.Resource.ShouldNotBeNull().Values.Single();

        resource.ResourceIdentifier.ShouldBe("config::SOUL.md");
        resource.Name.ShouldBe("SOUL.md");
        resource.Type.ShouldBe("config");
        resource.Content.ShouldBe("You are the worker.");
    }

    [Fact]
    public async Task GetAgentSpecAsync_ResolvesALabelBeforeAVersion()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AIImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentSpecName = NacosHttp.UniqueId("spec-pin");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishAgentSpecAsync(
            http,
            container.HttpAddress,
            agentSpecName,
            FirstVersion,
            "Version one.",
            "You are version one.",
            basedOnVersion: null,
            accessToken: token,
            ct: ct);

        await NacosHttp.PublishAgentSpecAsync(
            http,
            container.HttpAddress,
            agentSpecName,
            SecondVersion,
            "Version two.",
            "You are version two.",
            basedOnVersion: FirstVersion,
            accessToken: token,
            ct: ct);

        // The labels are written as a whole map, and the reserved latest label is included so
        // binding stable does not drop it.
        await NacosHttp.BindAgentSpecLabelsAsync(
            http,
            container.HttpAddress,
            agentSpecName,
            $$"""{"stable":"{{FirstVersion}}","latest":"{{SecondVersion}}"}""",
            token,
            ct);

        // A read that names no selector resolves through latest, which the second publish moved.
        NacosAgentSpec latest = await NacosEventually.BecomesAsync(
            () => client.AI.AgentSpec.GetAgentSpecAsync(agentSpecName, ct: ct),
            value => value.Version == SecondVersion,
            ct);

        latest.Description.ShouldBe("Version two.");

        // A label wins over a version on this resource, which is the opposite order to a prompt's.
        NacosAgentSpec both = await client.AI.AgentSpec.GetAgentSpecAsync(
            agentSpecName, version: SecondVersion, label: "stable", ct: ct);

        both.Version.ShouldBe(FirstVersion);
        both.Description.ShouldBe("Version one.");

        // Naming the version alone pins it, and naming the label alone resolves through it.
        NacosAgentSpec pinned = await client.AI.AgentSpec.GetAgentSpecAsync(
            agentSpecName, version: SecondVersion, ct: ct);

        pinned.Version.ShouldBe(SecondVersion);

        NacosAgentSpec labelled = await client.AI.AgentSpec.GetAgentSpecAsync(
            agentSpecName, label: "stable", ct: ct);

        labelled.Version.ShouldBe(FirstVersion);
    }

    [Fact]
    public async Task GetAgentSpecAsync_ReportsAnUnknownNameAndAnUnknownVersion()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AIImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentSpecName = NacosHttp.UniqueId("spec-absent");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishAgentSpecAsync(
            http,
            container.HttpAddress,
            agentSpecName,
            FirstVersion,
            "Present.",
            "You are present.",
            basedOnVersion: null,
            accessToken: token,
            ct: ct);

        // A release first, so that the version error is the registry's answer to a version rather
        // than to a name that does not exist at all. The two are different failures.
        NacosServerException unknownName = await Should.ThrowAsync<NacosServerException>(
            () => client.AI.AgentSpec.GetAgentSpecAsync(NacosHttp.UniqueId("spec-nothing"), ct: ct));

        unknownName.ErrorCode.ShouldBe(404);

        NacosServerException unknownVersion = await Should.ThrowAsync<NacosServerException>(
            () => client.AI.AgentSpec.GetAgentSpecAsync(agentSpecName, "9.9.9", ct: ct));

        unknownVersion.ErrorCode.ShouldBe(404);
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_ReportsANewVersion()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AIImage);
        await using NacosClientFactory client = CreateClient(container, TimeSpan.FromMilliseconds(500));

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentSpecName = NacosHttp.UniqueId("spec-watch");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishAgentSpecAsync(
            http,
            container.HttpAddress,
            agentSpecName,
            FirstVersion,
            "Version one.",
            "You are version one.",
            basedOnVersion: null,
            accessToken: token,
            ct: ct);

        await using IAsyncEnumerator<NacosAgentSpec?> changes =
            client.AI.AgentSpec.SubscribeAgentSpecAsync(agentSpecName, ct: ct).GetAsyncEnumerator(ct);

        (await changes.MoveNextAsync()).ShouldBeTrue();
        changes.Current.ShouldNotBeNull();
        changes.Current.Version.ShouldBe(FirstVersion);

        await NacosHttp.PublishAgentSpecAsync(
            http,
            container.HttpAddress,
            agentSpecName,
            SecondVersion,
            "Version two.",
            "You are version two.",
            basedOnVersion: FirstVersion,
            accessToken: token,
            ct: ct);

        // Nacos pushes no change for this resource, so the subscription is the refresh loop noticing
        // that the version a read without a selector resolves to has moved.
        await NacosEventually.ReportsAsync(changes, spec => spec?.Version == SecondVersion, ct);

        changes.Current!.Description.ShouldBe("Version two.");
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_ReportsAWithdrawal()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AIImage);
        await using NacosClientFactory client = CreateClient(container, TimeSpan.FromMilliseconds(500));

        CancellationToken ct = TestContext.Current.CancellationToken;
        string agentSpecName = NacosHttp.UniqueId("spec-withdraw");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishAgentSpecAsync(
            http,
            container.HttpAddress,
            agentSpecName,
            FirstVersion,
            "Going away.",
            "You are going away.",
            basedOnVersion: null,
            accessToken: token,
            ct: ct);

        await using IAsyncEnumerator<NacosAgentSpec?> changes =
            client.AI.AgentSpec.SubscribeAgentSpecAsync(agentSpecName, ct: ct).GetAsyncEnumerator(ct);

        (await changes.MoveNextAsync()).ShouldBeTrue();
        changes.Current.ShouldNotBeNull();
        changes.Current.Version.ShouldBe(FirstVersion);

        await NacosHttp.OfflineAgentSpecAsync(
            http, container.HttpAddress, agentSpecName, FirstVersion, token, ct);

        // A specification that is gone is reported rather than waited out, which is what the null
        // element is: a subscriber that was never told would keep loading content that no longer
        // exists.
        await NacosEventually.ReportsAsync(changes, spec => spec is null, ct);

        changes.Current.ShouldBeNull();
    }

    private static NacosClientFactory CreateClient(
        NacosContainer container,
        TimeSpan? cacheRefreshInterval = null) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                AI = new NacosAIOptions
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
