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
    public async Task Watch_ReportsTheMatchedSetAsConfigurationsAppearAndDisappear()
    {
        // A fuzzy watch tracks which configurations match a pattern, not their contents: the server
        // only pushes a change when a key enters or leaves the matched set. Content changes are the
        // exact listener's job, which is why the cycle here is create, remove and create again.
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using NacosClientFactory client = CreateClient(container);
        await client.ConnectAsync(TestContext.Current.CancellationToken);

        string prefix = NacosHttp.UniqueId("config-fuzzy-it");
        var key = new NacosConfigKey(prefix + ".json", "DEFAULT_GROUP");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(2));

        await using IAsyncEnumerator<NacosConfigWatchEvent> events =
            client.Config.WatchAsync($"{prefix}*", "DEFAULT_GROUP", cts.Token)
                .GetAsyncEnumerator(cts.Token);

        // Nothing matches the pattern yet, so the initial sync is the finish marker alone.
        await NacosEventually.ReportsAsync(
            events,
            item => item.Kind == NacosConfigWatchKind.InitialSyncCompleted,
            cts.Token);

        // Each write is repeated until the push arrives. The content differs on every attempt, and
        // the server reports a creation with the same change type as an update, so the client is
        // what turns an unknown key into an addition.
        await NacosEventually.PushedAsync(
            ct => client.Config.PublishConfigAsync(
                key, NacosHttp.ConfigContent("created"), "text", ct: ct),
            events,
            item => item.Kind == NacosConfigWatchKind.Added && item.Key?.DataId == key.DataId,
            cts.Token);

        await NacosEventually.PushedAsync(
            ct => client.Config.RemoveConfigAsync(key, ct),
            events,
            item => item.Kind == NacosConfigWatchKind.Removed && item.Key?.DataId == key.DataId,
            cts.Token);

        // The key is gone from the set, so recreating it is an addition again rather than an
        // update to something the subscription never learned about.
        await NacosEventually.PushedAsync(
            ct => client.Config.PublishConfigAsync(
                key, NacosHttp.ConfigContent("recreated"), "text", ct: ct),
            events,
            item => item.Kind == NacosConfigWatchKind.Added && item.Key?.DataId == key.DataId,
            cts.Token);

        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
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
        // before the registration keeps it from racing that reconnect: a request made while a
        // reconnect is in progress waits for the connection to come back, but only until the
        // recovery's first attempt is refused or its request budget runs out, so the retry stays.
        // Repeating the publish then covers the window between the first value and the registration,
        // in which a change is not pushed to this subscriber.
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

    [Fact]
    public async Task Reconnect_ServesTheRequestWaitingOnItAndTheChangesItMissed()
    {
        // Both halves of the reconnect window, against a real server. A request that arrives with no
        // connection to send on waits for the recovery instead of failing, and a configuration that
        // changed while nobody could be pushed to it is reported by the listener replay.
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using NacosClientFactory client = CreateReconnectWindowClient(container);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(5));

        await client.ConnectAsync(cts.Token);

        string dataId = NacosHttp.UniqueId("config-reconnect-window-it");
        var key = new NacosConfigKey(dataId, "DEFAULT_GROUP");
        string before = NacosHttp.ConfigContent("before-the-outage");

        await client.Config.PublishConfigAsync(key, before, "text", ct: cts.Token);

        // A publish answers before the value is readable, and the listener's first delivery is
        // whatever is readable when it registers, so the read is what makes the fixture certain.
        await NacosEventually.BecomesAsync(
            () => client.Config.GetConfigAsync(key, cts.Token),
            result => result.Exists,
            cts.Token);

        await using IAsyncEnumerator<NacosConfigChange> changes =
            client.Config.ListenAsync(key, cts.Token).GetAsyncEnumerator(cts.Token);

        (await changes.MoveNextAsync()).ShouldBeTrue();
        changes.Current.Content.ShouldBe(before);

        // The token survives the restart: it is signed with the identity the container was given, and
        // the account that issued it is on the container's own disk.
        string token;
        using (var http = new HttpClient())
        {
            token = await container.CreateAccessTokenAsync(cts.Token);
        }

        // The server goes away, and the client's recovery waits well past the restart before its
        // first attempt, so it is provably still disconnected while the value changes below.
        await container.StopAsync(cts.Token);

        Task restart = Task.Run(() => container.StartAsync(cts.Token), cts.Token);

        string during = NacosHttp.ConfigContent("during-the-outage");

        await restart;

        using (var http = new HttpClient())
        {
            await NacosHttp.PublishConfigAsync(
                http, container.HttpAddress, dataId, "DEFAULT_GROUP", during, token, cts.Token);
        }

        // Nothing is registered with the server at this point, so no push exists to carry the change:
        // only the recovery can recover it. The read is issued with no connection to send on, and it
        // has to wait for the connection to come back rather than fail against its local copy.
        NacosConfigResult read = await client.Config.GetConfigAsync(key, cts.Token);

        read.Content.ShouldBe(during);

        // And the change that had nobody to be pushed to is reported once the listener is replayed.
        await NacosEventually.ReportsAsync(changes, change => change.Content == during, cts.Token);

        changes.Current.Content.ShouldBe(during);
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

    /// <summary>
    /// Builds a client whose recovery first tries again long after the server is back, and whose
    /// requests are willing to wait that long.
    /// </summary>
    /// <param name="container">The container to point at.</param>
    /// <returns>The client factory.</returns>
    /// <remarks>
    /// A real server takes tens of seconds to become ready again, so a recovery that tried
    /// immediately would be refused and, by design, would stop being worth waiting for. Delaying the
    /// first attempt past the restart is what makes the reconnect window observable instead of a
    /// race: the client is provably still away when the change below is written.
    /// </remarks>
    private static NacosClientFactory CreateReconnectWindowClient(NacosContainer container) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                // The request budget is also the budget of the wait for a reconnection, so it has to
                // outlast the recovery delay below.
                RequestTimeout = TimeSpan.FromMinutes(2),
                Reconnect = new NacosReconnectOptions
                {
                    InitialDelay = TimeSpan.FromSeconds(90),
                    MaxDelay = TimeSpan.FromSeconds(90),
                    JitterFactor = 0,
                },
            },
            httpClient: null,
            channelFactory: null,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance);
}
