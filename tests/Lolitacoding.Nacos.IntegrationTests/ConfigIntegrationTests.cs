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

        string dataId = NacosHttp.UniqueId("config-it");
        var key = new NacosConfigKey(dataId, "DEFAULT_GROUP");
        string content = NacosHttp.ConfigContent("published");

        await client.Config.PublishConfigAsync(key, content, "text", ct: TestContext.Current.CancellationToken);

        NacosConfigResult read = await client.Config.GetConfigAsync(key, TestContext.Current.CancellationToken);
        read.Exists.ShouldBeTrue();
        read.Content.ShouldBe(content);
        read.FromLocalCache.ShouldBeFalse();

        await client.Config.RemoveConfigAsync(key, TestContext.Current.CancellationToken);

        NacosConfigResult removed = await client.Config.GetConfigAsync(key, TestContext.Current.CancellationToken);
        removed.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task Listen_ReceivesAChangePushedByTheServer()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using NacosClientFactory client = CreateClient(container);

        string dataId = NacosHttp.UniqueId("config-listen-it");
        var key = new NacosConfigKey(dataId, "DEFAULT_GROUP");

        await client.Config.PublishConfigAsync(
            key, NacosHttp.ConfigContent("before"), "text", ct: TestContext.Current.CancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMinutes(1));

        await using IAsyncEnumerator<NacosConfigChange> enumerator =
            client.Config.ListenAsync(key, cts.Token).GetAsyncEnumerator(cts.Token);

        // The first element is the current value, so a consumer initialises from one code path.
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        enumerator.Current.Content.ShouldNotBeNull();

        string updated = NacosHttp.ConfigContent("after");
        await client.Config.PublishConfigAsync(key, updated, "text", ct: cts.Token);

        // The server decides when to push, so the assertion waits for it rather than assuming.
        while (await enumerator.MoveNextAsync())
        {
            if (enumerator.Current.Content == updated)
            {
                break;
            }
        }

        enumerator.Current.Content.ShouldBe(updated);
    }

    [Fact]
    public async Task Listen_SurvivesAServerRestart()
    {
        // The acceptance criterion for M2: after a reconnect the listener is still effective. The
        // server discards the registration with the connection, so this proves it was replayed.
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using NacosClientFactory client = CreateClient(container);

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
        // reach the listener that was registered on the connection that was destroyed.
        Task publish = Task.Run(
            async () =>
            {
                await container.StartAsync(cts.Token);
                await WaitForReadyAsync(container, cts.Token);
                await client.Config.PublishConfigAsync(key, updated, "text", ct: cts.Token);
            },
            cts.Token);

        while (await enumerator.MoveNextAsync())
        {
            if (enumerator.Current.Content == updated)
            {
                break;
            }
        }

        await publish;

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

    private static async Task WaitForReadyAsync(NacosContainer container, CancellationToken ct)
    {
        using var http = new HttpClient();

        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(
                    string.Concat(container.HttpAddress, "/nacos/v1/console/health/readiness"), ct);

                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The server is still starting.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException("The Nacos server did not become ready in time.");
    }
}
