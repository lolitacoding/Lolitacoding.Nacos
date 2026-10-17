using System.Threading.Channels;
using Lolitacoding.Nacos.Ai;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies how the MCP subscription behaves against a scripted AI service.
/// </summary>
/// <remarks>
/// A subscription is the one MCP operation Nacos does not serve with a single request: the client
/// re-reads the server on an interval and reports what changed. The tests therefore cover the three
/// things that defines — what the first element is, when a later element is produced, and when the
/// reading stops — rather than the shape of one request.
/// </remarks>
public sealed class NacosMcpSubscriptionTests
{
    /// <summary>The refresh interval the tests run with, short enough to be quick and long enough to be ordered.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SubscribeServerAsync_YieldsTheDetailTheServerServes()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        server.OnQuery = _ => AiTest.Detail(AiTest.Server("weather", "1.2.3"));

        await using var subscription = new McpSubscription(Subscribe(client.Mcp, "weather"));

        NacosMcpServerDetail first = await subscription.NextAsync(Timeout);

        // The current state is published first so a consumer can initialise from the same path that
        // handles later changes.
        first.Name.ShouldBe("weather");
        first.VersionDetail!.Version.ShouldBe("1.2.3");
    }

    [Fact]
    public async Task SubscribeServerAsync_AsksTheServerForTheVersionTheCallerNamed()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        server.OnQuery = _ => AiTest.Detail(AiTest.Server("weather", "1.2.3"));

        await using var subscription =
            new McpSubscription(Subscribe(client.Mcp, "weather", "1.2.3"));

        await subscription.NextAsync(Timeout);

        server.Queries[0].McpName.ShouldBe("weather");
        server.Queries[0].Version.ShouldBe("1.2.3");
    }

    [Fact]
    public async Task SubscribeServerAsync_LeavesTheVersionUnsetForTheLatestOne()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        server.OnQuery = _ => AiTest.Detail(AiTest.Server("weather", "1.2.3"));

        await using var subscription = new McpSubscription(Subscribe(client.Mcp, "weather"));

        await subscription.NextAsync(Timeout);

        // A client that asked for the label by name would have to know which one the server uses;
        // leaving the field unset lets the server answer with whatever version it currently serves.
        server.Queries[0].Version.ShouldBeNull();
    }

    [Fact]
    public async Task SubscribeServerAsync_YieldsTheDetailAgainWhenTheServerChanges()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        int queries = 0;

        server.OnQuery = _ => AiTest.Detail(AiTest.Server(
            "weather",
            "1.2.3",
            description: $"revision {Interlocked.Increment(ref queries)}"));

        await using var subscription = new McpSubscription(Subscribe(client.Mcp, "weather"));

        NacosMcpServerDetail first = await subscription.NextAsync(Timeout);
        NacosMcpServerDetail second = await subscription.NextAsync(Timeout);

        first.Description.ShouldBe("revision 1");
        second.Description.ShouldNotBe(first.Description);
    }

    [Fact]
    public async Task SubscribeServerAsync_NotifiesASubscriberPinnedToTheLatestVersion()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        int queries = 0;

        // The server flags the subscribed version as the latest one. The official client keys the
        // change by the latest label alone in that case, which leaves a subscriber that named the
        // version itself without an update; publishing to both keys is what this asserts.
        server.OnQuery = _ => AiTest.Detail(AiTest.Server(
            "weather",
            "2.0.0",
            isLatest: true,
            description: $"revision {Interlocked.Increment(ref queries)}"));

        await using var subscription =
            new McpSubscription(Subscribe(client.Mcp, "weather", "2.0.0"));

        NacosMcpServerDetail first = await subscription.NextAsync(Timeout);
        NacosMcpServerDetail second = await subscription.NextAsync(Timeout);

        first.Description.ShouldBe("revision 1");
        second.Description.ShouldNotBe(first.Description);
    }

    [Fact]
    public async Task SubscribeServerAsync_KeepsWatchingAServerThatIsNotPublishedYet()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        int queries = 0;

        server.OnQuery = _ => Interlocked.Increment(ref queries) <= 2
            ? AiTest.Failure(404, "MCP server not found.")
            : AiTest.Detail(AiTest.Server("weather", "1.2.3"));

        await using var subscription = new McpSubscription(Subscribe(client.Mcp, "weather"));

        NacosMcpServerDetail first = await subscription.NextAsync(Timeout);

        // A name that does not exist yet is not a failure: the subscription stays open and announces
        // the server when it is released, which is what a watcher is for.
        first.VersionDetail!.Version.ShouldBe("1.2.3");
    }

    [Fact]
    public async Task SubscribeServerAsync_SurvivesARefreshThatFails()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        int queries = 0;

        server.OnQuery = _ => Interlocked.Increment(ref queries) switch
        {
            1 => AiTest.Detail(AiTest.Server("weather", "1.2.3", description: "before")),
            2 => AiTest.Failure(500, "boom"),
            _ => AiTest.Detail(AiTest.Server("weather", "1.2.3", description: "after")),
        };

        await using var subscription = new McpSubscription(Subscribe(client.Mcp, "weather"));

        NacosMcpServerDetail first = await subscription.NextAsync(Timeout);
        NacosMcpServerDetail second = await subscription.NextAsync(Timeout);

        // The failed read is not a state change, so the cached copy is untouched and the loop keeps
        // looking rather than the subscription ending.
        first.Description.ShouldBe("before");
        second.Description.ShouldBe("after");
    }

    [Fact]
    public async Task SubscribeServerAsync_ServesASecondSubscriberOfTheSameVersionFromTheCache()
    {
        // The default interval is long enough that nothing refreshes during the test, so a second
        // read can only have come from the cache.
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnQuery = _ => AiTest.Detail(AiTest.Server("weather", "1.2.3"));

        await using var first = new McpSubscription(Subscribe(client.Mcp, "weather"));
        await first.NextAsync(Timeout);

        await using var second = new McpSubscription(Subscribe(client.Mcp, "weather"));
        NacosMcpServerDetail served = await second.NextAsync(Timeout);

        served.VersionDetail!.Version.ShouldBe("1.2.3");
        server.Queries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SubscribeServerAsync_StopsReadingWhenTheLastSubscriberLeaves()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        server.OnQuery = _ => AiTest.Detail(AiTest.Server("weather", "1.2.3"));

        var subscription = new McpSubscription(Subscribe(client.Mcp, "weather"));
        await subscription.NextAsync(Timeout);

        await subscription.DisposeAsync();

        int queriesAfterLeaving = server.Queries.Count;

        // Two intervals is enough for the loop to have read again had it not been stopped.
        await Task.Delay(Interval * 5, TestContext.Current.CancellationToken);

        server.Queries.Count.ShouldBe(queriesAfterLeaving);
    }

    [Fact]
    public async Task SubscribeServerAsync_EndsWhenTheClientIsDisposed()
    {
        var server = new AiTestServer();
        NacosAiClient client = server.CreateClient(FastOptions());

        server.OnQuery = _ => AiTest.Detail(AiTest.Server("weather", "1.2.3"));

        await using var subscription = new McpSubscription(Subscribe(client.Mcp, "weather"));
        await subscription.NextAsync(Timeout);

        await client.DisposeAsync();

        // Completing the subscription is what ends a consumer's enumeration rather than leaving it
        // waiting for a change that can no longer arrive.
        await subscription.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SubscribeServerAsync_RejectsABlankName()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await foreach (NacosMcpServerDetail _ in
                client.Mcp.SubscribeServerAsync("  ", ct: TestContext.Current.CancellationToken))
            {
                // The iteration never reaches here.
            }
        });

        server.Factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task SubscribeServerAsync_FailsWhenTheServerSaysItHasNoMcpRegistry()
    {
        var server = new AiTestServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAiServerAbility.Mcp] = false,
        });

        await using NacosAiClient client = server.CreateClient();

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(async () =>
        {
            await foreach (NacosMcpServerDetail _ in
                client.Mcp.SubscribeServerAsync("weather", ct: TestContext.Current.CancellationToken))
            {
                // The iteration never reaches here.
            }
        });

        exception.ErrorCode.ShouldBe(501);
        server.Queries.ShouldBeEmpty();
    }

    private static NacosOptions FastOptions() => new()
    {
        ServerAddresses = ["127.0.0.1:8848"],
        AppName = "ai-tests",
        Ai = new NacosAiOptions { CacheRefreshInterval = Interval },
    };

    /// <summary>
    /// Starts a subscription that ends with the test.
    /// </summary>
    /// <param name="registry">The MCP registry to subscribe through.</param>
    /// <param name="name">The server name.</param>
    /// <param name="version">The version, or <see langword="null"/> for the latest one.</param>
    /// <returns>The subscription.</returns>
    private static IAsyncEnumerable<NacosMcpServerDetail> Subscribe(
        INacosMcpRegistry registry,
        string name,
        string? version = null) =>
        registry.SubscribeServerAsync(name, version, TestContext.Current.CancellationToken);

    /// <summary>
    /// Runs a subscription on a background pump so a test can wait for one change at a time.
    /// </summary>
    /// <remarks>
    /// The pump owns the enumerator. That is what makes waiting on a change that never comes safe:
    /// the pump can be cancelled and disposed as a unit instead of abandoning a move that is still
    /// in flight.
    /// </remarks>
    private sealed class McpSubscription : IAsyncDisposable
    {
        private readonly ChannelReader<NacosMcpServerDetail> _reader;
        private readonly CancellationTokenSource _cts = new();

        public McpSubscription(IAsyncEnumerable<NacosMcpServerDetail> source)
        {
            var queue = Channel.CreateUnbounded<NacosMcpServerDetail>();
            _reader = queue.Reader;

            Completion = Task.Run(async () =>
            {
                try
                {
                    await foreach (NacosMcpServerDetail detail in source.WithCancellation(_cts.Token))
                    {
                        queue.Writer.TryWrite(detail);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when the subscription is disposed.
                }
                finally
                {
                    queue.Writer.TryComplete();
                }
            });
        }

        /// <summary>Gets a task that completes when the enumeration ends.</summary>
        public Task Completion { get; }

        /// <summary>Waits for the next change.</summary>
        /// <param name="timeout">How long to wait.</param>
        /// <returns>The detail.</returns>
        public async Task<NacosMcpServerDetail> NextAsync(TimeSpan timeout)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);

            timeoutSource.CancelAfter(timeout);

            return await _reader.ReadAsync(timeoutSource.Token);
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
