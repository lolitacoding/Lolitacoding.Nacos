using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the prompt registry end to end against a real Nacos server.
/// </summary>
/// <remarks>
/// <para>
/// What is worth checking against the real thing is the part the server decides rather than the
/// client: that a prompt resolves to the version the server currently serves, that a version and a
/// label are resolved the way the server resolves them, that the template and the variable metadata
/// are stored and handed back unchanged, and that a key that was never published is reported as an
/// error rather than as an empty prompt.
/// </para>
/// <para>
/// The fixture goes through the HTTP admin API rather than the client, because there is no client-side
/// way to publish a prompt — the client surface is the read path under test. That API needs a token
/// even on this container, whose authentication plugin is off, so the container creates its first
/// administrator for the fixture. Seeding a raw configuration in the prompt group does not work: a
/// configuration in that group is only a migration source, and a read is not answered from it.
/// </para>
/// <para>
/// The container is started from the AI image, which is newer than the server the rest of the suite is
/// pinned to: the registry these exercise does not exist on the oldest 3.x release.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class PromptIntegrationTests
{
    /// <summary>The version the fixture publishes first, and points a label at.</summary>
    private const string FirstVersion = "1.0.0";

    /// <summary>The version the fixture publishes second, which the latest label moves to.</summary>
    private const string SecondVersion = "2.0.0";

    [Fact]
    public async Task PublishAndQuery_RoundTripsThroughARealServer()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string promptKey = NacosHttp.UniqueId("prompt");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        const string variables =
            """[{"name":"name","defaultValue":"Nacos","description":"Who to greet"}]""";

        await NacosHttp.PublishPromptAsync(
            http, container.HttpAddress, promptKey, FirstVersion, "Hello {{name}} from 1.0.0", variables, token, ct);

        await NacosHttp.PublishPromptAsync(
            http, container.HttpAddress, promptKey, SecondVersion, "Hello {{name}} from 2.0.0", variables, token, ct);

        // A read that names no version resolves to the one the server labels the latest, which is the
        // version the second publish made current. The read is repeated because a publish answers
        // before the version is servable, and a read that gets there first is answered the way an
        // unknown key is.
        NacosPrompt latest = await NacosEventually.BecomesAsync(
            () => client.Ai.Prompt.GetPromptAsync(promptKey, ct: ct),
            value => value.Version == SecondVersion && value.Variables is { Count: > 0 },
            ct,
            tolerateFailures: true);

        latest.PromptKey.ShouldBe(promptKey);
        latest.Template.ShouldBe("Hello {{name}} from 2.0.0");
        latest.Md5.ShouldNotBeNullOrWhiteSpace();

        // The variable metadata the server stored travels back as the registry's own object, so the
        // assertion is on what a caller would substitute rather than on what it sent.
        NacosPromptVariable variable = latest.Variables!.Single();

        variable.ShouldSatisfyAllConditions(
            value => value.Name.ShouldBe("name"),
            value => value.DefaultValue.ShouldBe("Nacos"),
            value => value.Description.ShouldBe("Who to greet"));

        // A read that names a version resolves to that version rather than to the current one, which
        // is what lets a caller pin an older version after a newer one was published.
        NacosPrompt pinned = await client.Ai.Prompt.GetPromptAsync(promptKey, FirstVersion, ct: ct);

        pinned.Version.ShouldBe(FirstVersion);
        pinned.Template.ShouldBe("Hello {{name}} from 1.0.0");

        // A label is a pointer the server moves; binding one and reading it back is what shows the
        // client sends the label rather than guessing which version it points at.
        await NacosHttp.BindPromptLabelsAsync(
            http, container.HttpAddress, promptKey, """{"stable":"1.0.0"}""", token, ct);

        NacosPrompt labelled = await NacosEventually.BecomesAsync(
            () => client.Ai.Prompt.GetPromptAsync(promptKey, label: "stable", ct: ct),
            value => value.Version == FirstVersion,
            ct,
            tolerateFailures: true);

        labelled.Version.ShouldBe(FirstVersion);
        labelled.Template.ShouldBe("Hello {{name}} from 1.0.0");
    }

    [Fact]
    public async Task GetPromptAsync_ReportsAnUnknownKeyAsAServerError()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string published = NacosHttp.UniqueId("prompt-present");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        // A prompt first, so that the error below is the registry's answer to an unknown key rather
        // than a server that does not serve prompts at all: those are different failures and only the
        // second one would make this test pass for the wrong reason.
        await NacosHttp.PublishPromptAsync(
            http, container.HttpAddress, published, FirstVersion, "Hello {{name}}", null, token, ct);

        await NacosEventually.BecomesAsync(
            () => client.Ai.Prompt.GetPromptAsync(published, ct: ct),
            value => value.Version == FirstVersion,
            ct,
            tolerateFailures: true);

        // Missing is an error rather than an empty answer, so a caller cannot mistake a typo in a key
        // for a prompt that exists with nothing in it.
        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => client.Ai.Prompt.GetPromptAsync(NacosHttp.UniqueId("prompt-absent"), ct: ct));

        exception.ErrorCode.ShouldBe(404);
        exception.Message.ShouldContain("not found");

        // A version that was never published is a failure too, even though the key exists.
        NacosServerException unpinned = await Should.ThrowAsync<NacosServerException>(
            () => client.Ai.Prompt.GetPromptAsync(published, "9.9.9", ct: ct));

        unpinned.ErrorCode.ShouldBe(404);
    }

    [Fact]
    public async Task SubscribePromptAsync_ReportsANewerVersionOfARealPrompt()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);

        // The interval is short because it is the only thing that makes a subscription fire, and a
        // test that waited ten seconds for it would be a slow way to prove nothing.
        await using NacosClientFactory client = CreateClient(container, TimeSpan.FromMilliseconds(500));

        CancellationToken ct = TestContext.Current.CancellationToken;
        string promptKey = NacosHttp.UniqueId("prompt-watch");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishPromptAsync(
            http, container.HttpAddress, promptKey, FirstVersion, "Hello from 1.0.0", null, token, ct);

        await using IAsyncEnumerator<NacosPrompt?> changes =
            client.Ai.Prompt.SubscribePromptAsync(promptKey, ct: ct).GetAsyncEnumerator(ct);

        // The current state is the first element, and the subscription is live once it has been
        // consumed: the refresh loop starts before the seed is read, so a change made now is seen.
        (await changes.MoveNextAsync()).ShouldBeTrue();

        changes.Current.ShouldNotBeNull();
        changes.Current.Version.ShouldBe(FirstVersion);

        // Publishing a second version moves the latest label, and a subscription that named no version
        // watches whatever that label points at.
        await NacosHttp.PublishPromptAsync(
            http, container.HttpAddress, promptKey, SecondVersion, "Hello from 2.0.0", null, token, ct);

        await NacosEventually.ReportsAsync(changes, prompt => prompt?.Version == SecondVersion, ct);

        changes.Current!.Template.ShouldBe("Hello from 2.0.0");
    }

    [Fact]
    public async Task SubscribePromptAsync_ReportsARelabelledVersion()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container, TimeSpan.FromMilliseconds(500));

        CancellationToken ct = TestContext.Current.CancellationToken;
        string promptKey = NacosHttp.UniqueId("prompt-label");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishPromptAsync(
            http, container.HttpAddress, promptKey, FirstVersion, "Hello from 1.0.0", null, token, ct);

        await NacosHttp.PublishPromptAsync(
            http, container.HttpAddress, promptKey, SecondVersion, "Hello from 2.0.0", null, token, ct);

        // The subscription watches the label, which starts at the older version even though a newer
        // one is already published.
        await NacosHttp.BindPromptLabelsAsync(
            http, container.HttpAddress, promptKey, """{"stable":"1.0.0"}""", token, ct);

        await using IAsyncEnumerator<NacosPrompt?> changes =
            client.Ai.Prompt.SubscribePromptAsync(promptKey, label: "stable", ct: ct).GetAsyncEnumerator(ct);

        (await changes.MoveNextAsync()).ShouldBeTrue();

        changes.Current.ShouldNotBeNull();
        changes.Current.Version.ShouldBe(FirstVersion);

        // Moving the label is a change the subscription sees without any version being published,
        // because what it watches is the resolution rather than the version list.
        await NacosHttp.BindPromptLabelsAsync(
            http, container.HttpAddress, promptKey, """{"stable":"2.0.0"}""", token, ct);

        await NacosEventually.ReportsAsync(changes, prompt => prompt?.Version == SecondVersion, ct);

        changes.Current!.Template.ShouldBe("Hello from 2.0.0");
    }

    [Fact]
    public async Task SubscribePromptAsync_ReportsAWithdrawal()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);
        await using NacosClientFactory client = CreateClient(container, TimeSpan.FromMilliseconds(500));

        CancellationToken ct = TestContext.Current.CancellationToken;
        string promptKey = NacosHttp.UniqueId("prompt-withdraw");

        using var http = new HttpClient();
        string token = await container.CreateAccessTokenAsync(ct);

        await NacosHttp.PublishPromptAsync(
            http, container.HttpAddress, promptKey, FirstVersion, "Hello from 1.0.0", null, token, ct);

        await using IAsyncEnumerator<NacosPrompt?> changes =
            client.Ai.Prompt.SubscribePromptAsync(promptKey, ct: ct).GetAsyncEnumerator(ct);

        (await changes.MoveNextAsync()).ShouldBeTrue();
        changes.Current!.Version.ShouldBe(FirstVersion);

        await NacosHttp.DeletePromptAsync(http, container.HttpAddress, promptKey, token, ct);

        // A prompt that is gone is reported rather than waited out, which is what the null element is:
        // a subscriber that was never told would keep rendering content that no longer exists.
        await NacosEventually.ReportsAsync(changes, prompt => prompt is null, ct);

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
