using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the AI failover cache against a real server, including the case it exists for.
/// </summary>
/// <remarks>
/// <para>
/// The interesting part cannot be shown without a real server, because the copy on disk is only ever
/// written from what the server answered and only ever read when the server cannot be asked. So the
/// fixture reads a prompt while the server is up, stops the server, and starts a fresh client against
/// the same directory: the subscription that has nothing in memory and no reachable server can only
/// be answered from the file the previous client left behind.
/// </para>
/// <para>
/// The container is stopped rather than unreachable by construction so that the write and the read
/// exercise the same address and the same client configuration, which is what a restart during an
/// outage actually looks like.
/// </para>
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AiLocalCacheIntegrationTests
{
    /// <summary>The version the fixture publishes.</summary>
    private const string Version = "1.0.0";

    [Fact]
    public async Task SubscribePromptAsync_ServesTheLocalCopyAfterTheServerIsGone()
    {
        await using NacosContainer container =
            await NacosContainer.StartAsync(NacosIntegrationImages.AiImage);

        CancellationToken ct = TestContext.Current.CancellationToken;
        string promptKey = NacosHttp.UniqueId("prompt-failover");
        string cacheDirectory = CreateCacheDirectory();

        try
        {
            using var http = new HttpClient();
            string token = await container.CreateAccessTokenAsync(ct);

            await NacosHttp.PublishPromptAsync(
                http, container.HttpAddress, promptKey, Version, "Hello {{name}}", null, token, ct);

            await using (NacosClientFactory first = CreateClient(container, cacheDirectory))
            {
                // A publish answers before the version is servable, so the read is the wait. It is a
                // plain read rather than a subscription, which is deliberate: the copy under test has
                // to be written by the subscription below, not by this fixture.
                await NacosEventually.BecomesAsync(
                    () => first.Ai.Prompt.GetPromptAsync(promptKey, ct: ct),
                    prompt => prompt.Version == Version,
                    ct);

                await using IAsyncEnumerator<NacosPrompt?> subscribed = first.Ai.Prompt
                    .SubscribePromptAsync(promptKey, ct: ct)
                    .GetAsyncEnumerator(ct);

                (await subscribed.MoveNextAsync()).ShouldBeTrue();
                subscribed.Current!.Template.ShouldBe("Hello {{name}}");
            }

            await container.StopAsync(ct);

            // A new client, the same directory, and a server that is no longer there: the in-memory
            // cache is empty and the connection cannot be opened, so the only thing that can answer
            // this subscription is the file the first client wrote.
            await using NacosClientFactory restarted = CreateClient(container, cacheDirectory);

            await using IAsyncEnumerator<NacosPrompt?> restored = restarted.Ai.Prompt
                .SubscribePromptAsync(promptKey, ct: ct)
                .GetAsyncEnumerator(ct);

            (await restored.MoveNextAsync()).ShouldBeTrue();
            restored.Current!.PromptKey.ShouldBe(promptKey);
            restored.Current.Template.ShouldBe("Hello {{name}}");

            // A resource the server never served has no copy either, so an unknown key is still the
            // error it was: the failover source is the previous answer, not an empty one.
            await Should.ThrowAsync<NacosConnectionException>(async () =>
            {
                await foreach (NacosPrompt? _ in restarted.Ai.Prompt.SubscribePromptAsync(
                    NacosHttp.UniqueId("prompt-failover-absent"), ct: ct))
                {
                    // The iteration never reaches here.
                }
            });
        }
        finally
        {
            TryDelete(cacheDirectory);
        }
    }

    /// <summary>
    /// Builds a client that caches AI resources under a caller-named directory.
    /// </summary>
    /// <param name="container">The container to point at.</param>
    /// <param name="cacheDirectory">The directory the failover files live in.</param>
    /// <returns>The client.</returns>
    private static NacosClientFactory CreateClient(NacosContainer container, string cacheDirectory) =>
        NacosClientFactory.Create(
            new NacosOptions
            {
                ServerAddresses = [container.HttpAddress],
                ConnectionTimeout = TimeSpan.FromSeconds(15),
                LocalCacheDirectory = cacheDirectory,
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
    /// Creates the empty directory a test's failover files are written to.
    /// </summary>
    /// <returns>The directory.</returns>
    private static string CreateCacheDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "lolitacoding-nacos-ai-cache", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(directory);

        return directory;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}
