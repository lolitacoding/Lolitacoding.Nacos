using System.Text.Json;
using System.Threading.Channels;
using Lolitacoding.Nacos.AI;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies the on-disk failover cache the AI subscriptions fall back to.
/// </summary>
/// <remarks>
/// A subscription already survives a control-plane outage in memory. What is verified here is the
/// step beyond that: a process that restarts into an outage starts from the copy it last read rather
/// than from nothing, and it does so without the server — which is the only case in which a disk
/// copy can matter, because a reachable server is always asked first.
/// </remarks>
public sealed class NacosAILocalCacheTests : IDisposable
{
    /// <summary>The refresh interval the tests that need a refresh run with.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _root = TestPaths.CreateScratchDirectory("ai-local-cache");

    public void Dispose() => TestPaths.TryDelete(_root);

    private NacosAISnapshotStore Store => new(_root);

    // ------------------------------------------------------------------ the cache's decision

    [Fact]
    public async Task SubscribeAsync_WritesTheResourceItReadToDisk()
    {
        NacosAISnapshotStore store = Store;
        NacosAIResourceCache<NacosPrompt> cache = PromptCache(
            store,
            (_, _, _, _, _) => Task.FromResult(new NacosPrompt { PromptKey = "greeting", Template = "Hello" }));

        await using var subscription = new Subscription<NacosPrompt?>(Subscribe(cache, "greeting"));

        (await subscription.NextAsync(Timeout))!.Template.ShouldBe("Hello");

        // The copy is written under the selector the caller named, which is what the next process
        // looks it up by.
        (await LoadPromptAsync(store, "greeting::latest"))!.Template.ShouldBe("Hello");
    }

    [Fact]
    public async Task SubscribeAsync_ServesTheSnapshotWhenTheReadCannotReachTheServer()
    {
        NacosAISnapshotStore store = Store;
        await SavePromptAsync(store, "greeting::latest", "cached");

        NacosAIResourceCache<NacosPrompt> cache = PromptCache(
            store,
            (_, _, _, _, _) => throw new NacosConnectionException("server is down"));

        await using var subscription = new Subscription<NacosPrompt?>(Subscribe(cache, "greeting"));

        // A read that cannot reach the server is answered from the copy that was cached, which is
        // the difference between a process that starts during an outage and one that cannot.
        (await subscription.NextAsync(Timeout))!.Template.ShouldBe("cached");
    }

    [Fact]
    public async Task SubscribeAsync_FailsWhenTheServerCannotBeReachedAndNothingWasCached()
    {
        NacosAIResourceCache<NacosPrompt> cache = PromptCache(
            Store,
            (_, _, _, _, _) => throw new NacosConnectionException("server is down"));

        // With no copy to fall back to there is no answer to give, and the failure is the caller's
        // rather than a silent empty subscription.
        await Should.ThrowAsync<NacosConnectionException>(async () =>
        {
            await foreach (NacosPrompt? _ in cache.SubscribeAsync(
                "greeting", null, null, TestContext.Current.CancellationToken))
            {
                // The iteration never reaches here.
            }
        });
    }

    [Fact]
    public async Task SubscribeAsync_RemovesTheSnapshotWhenTheResourceStopsBeingServed()
    {
        NacosAISnapshotStore store = Store;
        int reads = 0;

        NacosAIResourceCache<NacosPrompt> cache = PromptCache(
            store,
            (_, _, _, _, _) => Interlocked.Increment(ref reads) == 1
                ? Task.FromResult(new NacosPrompt { PromptKey = "greeting", Template = "Hello" })
                : throw new NacosServerException(404, "Prompt not found: greeting"),
            Interval);

        await using var subscription = new Subscription<NacosPrompt?>(Subscribe(cache, "greeting"));

        (await subscription.NextAsync(Timeout))!.Template.ShouldBe("Hello");
        (await subscription.NextAsync(Timeout)).ShouldBeNull();

        // A withdrawal that was left on disk would be resurrected by the next outage, which is the
        // wrong direction for a failover source to fail in.
        (await LoadPromptAsync(store, "greeting::latest")).ShouldBeNull();
    }

    [Fact]
    public async Task RestoreAsync_SeedsTheSelectorSoTheServerIsNotRead()
    {
        NacosAISnapshotStore store = Store;
        await SavePromptAsync(store, "greeting::latest", "cached");

        int reads = 0;

        NacosAIResourceCache<NacosPrompt> cache = PromptCache(store, (_, _, _, _, _) =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(new NacosPrompt());
        });

        (await cache.RestoreAsync("greeting", null, null, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        await using var subscription = new Subscription<NacosPrompt?>(Subscribe(cache, "greeting"));

        (await subscription.NextAsync(Timeout))!.Template.ShouldBe("cached");

        // This is what makes the fallback cost nothing: the copy answers the subscription, so the
        // read that would have needed the unreachable server is never made.
        reads.ShouldBe(0);
    }

    [Fact]
    public async Task RestoreAsync_ReportsNothingWhenTheSelectorWasNeverCached()
    {
        var cache = PromptCache(Store, (_, _, _, _, _) => Task.FromResult(new NacosPrompt()));

        (await cache.RestoreAsync("greeting", null, null, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task RestoreAsync_ReportsNothingWhenTheClientCachesInMemoryOnly()
    {
        var cache = PromptCache(null, (_, _, _, _, _) => Task.FromResult(new NacosPrompt()));

        // Without a store there is nothing to restore from, and the caller learns that instead of
        // being handed a copy that does not exist.
        (await cache.RestoreAsync("greeting", null, null, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task McpSubscribeAsync_WritesTheVersionItReadToDisk()
    {
        NacosAISnapshotStore store = Store;
        NacosMcpServerCache cache = McpCache(
            store,
            (_, _, _) => Task.FromResult(AITest.Server("weather", "1.0.0")));

        await using var subscription = new Subscription<NacosMcpServerDetail>(Subscribe(cache, "weather", "1.0.0"));

        (await subscription.NextAsync(Timeout)).VersionDetail!.Version.ShouldBe("1.0.0");

        (await LoadMcpAsync(store, "weather::1.0.0")).ShouldNotBeNull();
    }

    [Fact]
    public async Task McpSubscribeAsync_ServesTheSnapshotWhenTheReadCannotReachTheServer()
    {
        NacosAISnapshotStore store = Store;

        await store.SaveAsync(
            NacosAISnapshotStore.McpKind,
            "weather::1.0.0",
            AITest.Server("weather", "1.0.0"),
            NacosJsonContext.Default.NacosMcpServerDetail,
            TestContext.Current.CancellationToken);

        NacosMcpServerCache cache = McpCache(
            store,
            (_, _, _) => throw new NacosConnectionException("server is down"));

        await using var subscription = new Subscription<NacosMcpServerDetail>(Subscribe(cache, "weather", "1.0.0"));

        (await subscription.NextAsync(Timeout)).VersionDetail!.Version.ShouldBe("1.0.0");
    }

    // ------------------------------------------------------------------ the client's wiring

    [Fact]
    public async Task SubscribePromptAsync_WritesTheCopyItReadToDisk()
    {
        var server = new AITestServer();
        NacosAISnapshotStore store = Store;

        await using NacosAIClient client = server.CreateClient(snapshots: store);

        server.OnPrompt = _ => AITest.Prompt(AITest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await using var subscription = new Subscription<NacosPrompt?>(
            client.Prompt.SubscribePromptAsync("greeting", ct: TestContext.Current.CancellationToken));

        (await subscription.NextAsync(Timeout))!.Template.ShouldBe("Hello");

        // The store a client was built with is what its subscriptions cache against, so a restart
        // with the same store finds this copy.
        (await LoadPromptAsync(store, "greeting::latest"))!.Template.ShouldBe("Hello");
    }

    [Fact]
    public async Task SubscribeMcpServerAsync_WritesTheCopyItReadToDisk()
    {
        var server = new AITestServer();
        NacosAISnapshotStore store = Store;

        await using NacosAIClient client = server.CreateClient(snapshots: store);

        server.OnQuery = _ => AITest.Detail(AITest.Server("weather", "1.0.0"));

        await using var subscription = new Subscription<NacosMcpServerDetail>(
            client.Mcp.SubscribeServerAsync("weather", ct: TestContext.Current.CancellationToken));

        (await subscription.NextAsync(Timeout)).VersionDetail!.Version.ShouldBe("1.0.0");

        (await LoadMcpAsync(store, "weather::latest")).ShouldNotBeNull();
    }

    [Fact]
    public async Task SubscribePromptAsync_StartsFromTheDiskCopyAfterARestartWhileTheServerIsUnreachable()
    {
        NacosAISnapshotStore store = Store;

        // The first process reads the prompt while the server is up, which caches it.
        var server = new AITestServer();
        server.OnPrompt = _ => AITest.Prompt(AITest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await using (NacosAIClient first = server.CreateClient(snapshots: store))
        {
            await using var subscription = new Subscription<NacosPrompt?>(
                first.Prompt.SubscribePromptAsync("greeting", ct: TestContext.Current.CancellationToken));

            (await subscription.NextAsync(Timeout))!.Template.ShouldBe("Hello");
        }

        // The second process starts while the server is gone, so nothing but the disk copy can
        // answer it.
        await using NacosAIClient restarted = UnreachableClient(store);

        await using var restored = new Subscription<NacosPrompt?>(
            restarted.Prompt.SubscribePromptAsync("greeting", ct: TestContext.Current.CancellationToken));

        (await restored.NextAsync(Timeout))!.Template.ShouldBe("Hello");
    }

    [Fact]
    public async Task SubscribeMcpServerAsync_StartsFromTheDiskCopyAfterARestartWhileTheServerIsUnreachable()
    {
        NacosAISnapshotStore store = Store;

        var server = new AITestServer();
        server.OnQuery = _ => AITest.Detail(AITest.Server("weather", "1.0.0"));

        await using (NacosAIClient first = server.CreateClient(snapshots: store))
        {
            await using var subscription = new Subscription<NacosMcpServerDetail>(
                first.Mcp.SubscribeServerAsync("weather", ct: TestContext.Current.CancellationToken));

            (await subscription.NextAsync(Timeout)).VersionDetail!.Version.ShouldBe("1.0.0");
        }

        await using NacosAIClient restarted = UnreachableClient(store);

        await using var restored = new Subscription<NacosMcpServerDetail>(
            restarted.Mcp.SubscribeServerAsync("weather", ct: TestContext.Current.CancellationToken));

        // A subscription that named no version is answered from the copy the previous process read
        // for the latest one.
        (await restored.NextAsync(Timeout)).VersionDetail!.Version.ShouldBe("1.0.0");
    }

    // ------------------------------------------------------------------ helpers

    private static NacosAIResourceCache<NacosPrompt> PromptCache(
        NacosAISnapshotStore? store,
        Func<string, string?, string?, string?, CancellationToken, Task<NacosPrompt>> query,
        TimeSpan? interval = null) =>
        new(new NacosAIResourceCacheOptions<NacosPrompt>
        {
            Query = query,
            IsSame = (left, right) => Serialize(left) == Serialize(right),

            // No hash is sent, so every refresh is an unconditional read and the test's scripted
            // query decides what the answer means.
            Hash = static _ => null,
            Changed = static (_, _) => { },
            Withdrawn = static _ => { },
            RefreshFailed = static (_, _, _, _) => { },
            RefreshInterval = interval ?? TimeSpan.FromMinutes(1),
            TimeProvider = TimeProvider.System,
            Logger = NullLogger.Instance,
            Snapshot = NacosAISnapshot<NacosPrompt>.Create(
                store, NacosAISnapshotStore.PromptKind, NacosJsonContext.Default.NacosPrompt),
        });

    private static NacosMcpServerCache McpCache(
        NacosAISnapshotStore? store,
        Func<string, string?, CancellationToken, Task<NacosMcpServerDetail>> query) =>
        new(
            query,
            TimeSpan.FromMinutes(1),
            TimeProvider.System,
            NullLogger.Instance,
            NacosAISnapshot<NacosMcpServerDetail>.Create(
                store, NacosAISnapshotStore.McpKind, NacosJsonContext.Default.NacosMcpServerDetail));

    /// <summary>
    /// Builds the client a restarted process would have while the control plane is unreachable.
    /// </summary>
    /// <param name="store">The store the previous process cached into.</param>
    /// <returns>A client that cannot reach its server.</returns>
    /// <remarks>
    /// Port 1 is used because nothing can be listening on it without elevation, so the connection is
    /// refused at once instead of waiting out a timeout. The server address is deliberately the only
    /// unreachable part: everything else about the client is the production default.
    /// </remarks>
    private static NacosAIClient UnreachableClient(NacosAISnapshotStore store)
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:1"],
            AppName = "ai-tests",
            ConnectionTimeout = TimeSpan.FromSeconds(2),
        };

        return new NacosAIClient(
            options,
            [NacosServerAddress.Parse("127.0.0.1:1")],
            new GrpcNacosChannelFactory(options.RequestTimeout, options.Tls),
            new HttpClient(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            random: new Random(1234),
            snapshots: store);
    }

    private static IAsyncEnumerable<NacosPrompt?> Subscribe(
        NacosAIResourceCache<NacosPrompt> cache,
        string promptKey) =>
        cache.SubscribeAsync(promptKey, null, null, TestContext.Current.CancellationToken);

    private static IAsyncEnumerable<NacosMcpServerDetail> Subscribe(
        NacosMcpServerCache cache,
        string name,
        string? version) =>
        cache.SubscribeAsync(name, version, TestContext.Current.CancellationToken);

    private static string Serialize(NacosPrompt? prompt) =>
        JsonSerializer.Serialize(prompt, NacosJsonContext.Default.NacosPrompt);

    private static Task SavePromptAsync(NacosAISnapshotStore store, string key, string template) =>
        store.SaveAsync(
            NacosAISnapshotStore.PromptKind,
            key,
            new NacosPrompt { PromptKey = "greeting", Template = template },
            NacosJsonContext.Default.NacosPrompt,
            TestContext.Current.CancellationToken);

    private static Task<NacosPrompt?> LoadPromptAsync(NacosAISnapshotStore store, string key) =>
        store.LoadAsync(
            NacosAISnapshotStore.PromptKind,
            key,
            NacosJsonContext.Default.NacosPrompt,
            TestContext.Current.CancellationToken);

    private static Task<NacosMcpServerDetail?> LoadMcpAsync(NacosAISnapshotStore store, string key) =>
        store.LoadAsync(
            NacosAISnapshotStore.McpKind,
            key,
            NacosJsonContext.Default.NacosMcpServerDetail,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Runs a subscription on a background pump so a test can wait for one change at a time.
    /// </summary>
    /// <remarks>
    /// The pump owns the enumerator, which is what makes waiting on a change that never comes safe:
    /// the pump can be disposed as a unit instead of abandoning a move that is still in flight.
    /// </remarks>
    private sealed class Subscription<T> : IAsyncDisposable
    {
        private readonly ChannelReader<T> _reader;
        private readonly CancellationTokenSource _cts = new();

        public Subscription(IAsyncEnumerable<T> source)
        {
            var queue = Channel.CreateUnbounded<T>();
            _reader = queue.Reader;

            Completion = Task.Run(async () =>
            {
                try
                {
                    await foreach (T item in source.WithCancellation(_cts.Token))
                    {
                        queue.Writer.TryWrite(item);
                    }

                    queue.Writer.TryComplete();
                }
                catch (OperationCanceledException)
                {
                    // Expected when the subscription is disposed.
                    queue.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    queue.Writer.TryComplete(ex);
                }
            });
        }

        public Task Completion { get; }

        public async Task<T> NextAsync(TimeSpan timeout)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);

            timeoutSource.CancelAfter(timeout);

            try
            {
                return await _reader.ReadAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The expected state did not arrive in time.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();

            try
            {
                await Completion;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            _cts.Dispose();
        }
    }
}


