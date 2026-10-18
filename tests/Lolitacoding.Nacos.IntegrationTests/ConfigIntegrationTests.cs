using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the configuration client end to end against a real Nacos 3.x server.
/// </summary>
/// <remarks>
/// PLAN.md's acceptance criterion for M2 is a full publish → read → change-push → remove cycle,
/// and that a listener survives a reconnect. Both are properties of the interaction with the
/// server — the push path in particular cannot be exercised by a scripted in-memory server,
/// because it depends on the server actually deciding to notify.
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class ConfigIntegrationTests
{
    [Fact]
    public async Task PublishReadRemove_RoundTripsThroughARealServer()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using NacosClientFactory client = CreateClient(container);
        await client.ConnectAsync(TestContext.Current.CancellationToken);

        string dataId = NacosHttp.UniqueId("config-it");
        var key = new NacosConfigKey(dataId, "DEFAULT_GROUP");
        string content = NacosHttp.ConfigContent("published");

        await client.Config.PublishConfigAsync(key, content, "text", ct: TestContext.Current.CancellationToken);

        // A publish answers before the value is readable, so the read waits for it rather than
        // assuming the server applied it already.
        NacosConfigResult read = await NacosEventually.BecomesAsync(
            () => client.Config.GetConfigAsync(key, TestContext.Current.CancellationToken),
            result => result.Exists,
            TestContext.Current.CancellationToken);

        read.Exists.ShouldBeTrue();
        read.Content.ShouldBe(content);
        read.FromLocalCache.ShouldBeFalse();

        await client.Config.RemoveConfigAsync(key, TestContext.Current.CancellationToken);

        NacosConfigResult removed = await NacosEventually.BecomesAsync(
            () => client.Config.GetConfigAsync(key, TestContext.Current.CancellationToken),
            result => !result.Exists,
            TestContext.Current.CancellationToken);

        removed.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task Listen_ReceivesAChangePushedByTheServer()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using NacosClientFactory client = CreateClient(container);
        await client.ConnectAsync(TestContext.Current.CancellationToken);

        string dataId = NacosHttp.UniqueId("config-listen-it");
        var key = new NacosConfigKey(dataId, "DEFAULT_GROUP");

        await client.Config.PublishConfigAsync(
            key, NacosHttp.ConfigContent("before"), "text", ct: TestContext.Current.CancellationToken);

        // The listener's first element is read back from the server, so the publish has to be applied
        // first for the listener to initialise from a value rather than from "not published yet".
        await NacosEventually.BecomesAsync(
            () => client.Config.GetConfigAsync(key, TestContext.Current.CancellationToken),
            result => result.Exists,
            TestContext.Current.CancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));

        await using IAsyncEnumerator<NacosConfigChange> enumerator =
            client.Config.ListenAsync(key, cts.Token).GetAsyncEnumerator(cts.Token);

        // The first element is the current value, so a consumer initialises from one code path.
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Content.ShouldNotBeNull();

        string updated = NacosHttp.ConfigContent("after");

        // The listener is registered with the server just after its current value is yielded, and the
        // server decides when to push, so the change is repeated until it is pushed. That is what
        // leaves the assertion about the push path rather than about the registration window.
        await NacosEventually.PushedAsync(
            ct => client.Config.PublishConfigAsync(key, updated, "text", ct: ct),
            enumerator,
            change => change.Content == updated,
            cts.Token);

        enumerator.Current.Content.ShouldBe(updated);
    }

    [Fact]
    public async Task Listen_SurvivesAServerRestart()
    {
        // The acceptance criterion for M2: after a reconnect the listener is still effective. The
        // server discards the registration with the connection, so this proves it was replayed.
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using NacosClientFactory client = CreateClient(container);
        await client.ConnectAsync(TestContext.Current.CancellationToken);

        string dataId = NacosHttp.UniqueId("config-reconnect-it");
        var key = new NacosConfigKey(dataId, "DEFAULT_GROUP");

        await client.Config.PublishConfigAsync(
            key, NacosHttp.ConfigContent("initial"), "text", ct: TestContext.Current.CancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(3));

        await using IAsyncEnumerator<NacosConfigChange> enumerator =
            client.Config.ListenAsync(key, cts.Token).GetAsyncEnumerator(cts.Token);

        (await enumerator.MoveNextAsync()).ShouldBeTrue();

        await container.StopAsync(cts.Token);

        string updated = NacosHttp.ConfigContent("after-restart");

        // The value is published once the server is serving traffic again; the change must still
        // reach the listener that was registered on the connection that was destroyed. The restart
        // returns only once the server reports ready, so the publish can follow it.
        Task restart = Task.Run(() => container.StartAsync(cts.Token), cts.Token);

        // The listener's push handler is registered only when the enumeration is resumed, and after
        // the server returns the client reconnects on its own backoff. Waiting for a read to succeed
        // before the registration keeps it from racing that reconnect: a request made while one is in
        // progress is refused rather than queued, a gap the protocol notes record, not a property
        // this test is about. Repeating the publish then covers the window between the first value
        // and the registration, in which a change is not pushed to this subscriber.
        await NacosEventually.PushedAsync(
            async ct =>
            {
                await restart.ConfigureAwait(false);

                await NacosEventually.BecomesAsync(
                    () => client.Config.GetConfigAsync(key, ct),
                    _ => true,
                    ct,
                    tolerateFailures: true).ConfigureAwait(false);

                await client.Config.PublishConfigAsync(key, updated, "text", ct: ct).ConfigureAwait(false);
            },
            enumerator,
            change => change.Content == updated,
            cts.Token);

        await restart;

        enumerator.Current.Content.ShouldBe(updated);
    }

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
