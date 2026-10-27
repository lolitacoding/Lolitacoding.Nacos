using System.Threading.Channels;
using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies how an agent card subscription behaves against a scripted AI service.
/// </summary>
/// <remarks>
/// A card has no content hash, so the subscription is a plain poll: every interval reads the card and
/// compares the document. These tests pin that down — the read carries no conditional field, an
/// unchanged document is not reported, and a card that stops being served is reported as a null
/// element rather than waited out.
/// </remarks>
public sealed class NacosAgentCardSubscriptionTests
{
    /// <summary>The refresh interval the tests run with, short enough to be quick and long enough to be ordered.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SubscribeAgentCardAsync_YieldsTheCardTheServerServes()
    {
        var server = new AiTestServer();

        server.OnCard = _ => AiTest.Card(AiTest.CreateCard("planner", "1.0.0", "Plans things."));

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new CardSubscription(Subscribe(client.AgentCard, "planner"));

        NacosAgentCardDetail? first = await subscription.NextAsync(Timeout);

        // The current state is published first so a consumer can initialise from the same path that
        // handles later changes.
        first.ShouldNotBeNull();
        first.Name.ShouldBe("planner");
        first.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task SubscribeAgentCardAsync_AsksForTheSelectorsTheCallerNamed()
    {
        var server = new AiTestServer();

        server.OnCard = _ => AiTest.Card(AiTest.CreateCard());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var subscription = new CardSubscription(
            Subscribe(client.AgentCard, "planner", version: "1.0.0", registrationType: NacosA2a.EndpointTypeUrl));

        await subscription.NextAsync(Timeout);

        server.Cards[0].AgentName.ShouldBe("planner");
        server.Cards[0].Version.ShouldBe("1.0.0");
        server.Cards[0].RegistrationType.ShouldBe(NacosA2a.EndpointTypeUrl);
    }

    [Fact]
    public async Task SubscribeAgentCardAsync_YieldsTheCardAgainWhenTheServerChanges()
    {
        var server = new AiTestServer();

        // The server serves whatever the test has published so far. Counting reads would make the
        // answer depend on whether the read that opens the subscription or the first read of its
        // refresh loop is served first — that is a race, not a change.
        int state = 0;

        server.OnCard = _ => AiTest.Card(Volatile.Read(ref state) switch
        {
            0 => AiTest.CreateCard("planner", "1.0.0", "before"),
            1 => AiTest.CreateCard("planner", "2.0.0", "after"),
            _ => AiTest.CreateCard("planner", "3.0.0", "later"),
        });

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new CardSubscription(Subscribe(client.AgentCard, "planner"));

        NacosAgentCardDetail? first = await subscription.NextAsync(Timeout);
        first!.Description.ShouldBe("before");

        Volatile.Write(ref state, 1);

        NacosAgentCardDetail? second = await subscription.NextAsync(Timeout);
        second!.Description.ShouldBe("after");
        second.Version.ShouldBe("2.0.0");

        // A second change is reported the same way, so the subscription is still reading rather
        // than having stopped after the first one it saw.
        Volatile.Write(ref state, 2);

        NacosAgentCardDetail? third = await subscription.NextAsync(Timeout);
        third!.Description.ShouldBe("later");
        third.Version.ShouldBe("3.0.0");
    }

    [Fact]
    public async Task SubscribeAgentCardAsync_ReportsOnlyARealChange()
    {
        var server = new AiTestServer();

        // The server serves one document until the test has seen it, then a different one. The
        // switch is a flag rather than a read count: the read a subscription makes when it opens and
        // the first read of its refresh loop can be served in either order, and counting reads would
        // turn that ordering into a failure about which document arrived first.
        bool changed = false;

        server.OnCard = _ => AiTest.Card(
            Volatile.Read(ref changed)
                ? AiTest.CreateCard("planner", "2.0.0", "after")
                : AiTest.CreateCard("planner", "1.0.0", "before"));

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new CardSubscription(Subscribe(client.AgentCard, "planner"));

        NacosAgentCardDetail? first = await subscription.NextAsync(Timeout);

        first!.Version.ShouldBe("1.0.0");
        first.Description.ShouldBe("before");

        Volatile.Write(ref changed, true);

        NacosAgentCardDetail? second = await subscription.NextAsync(Timeout);

        // The next element is the new document, never a repeat of the first: the opening read and
        // the refresh loop compare against the same cache, so only one of them can announce a state.
        second!.Version.ShouldBe("2.0.0");
        second.Description.ShouldBe("after");
    }

    [Fact]
    public async Task SubscribeAgentCardAsync_ReportsAWithdrawalAsANullElement()
    {
        var server = new AiTestServer();

        // A flag rather than a read count: the read that opens the subscription and the first read
        // of its refresh loop can be served in either order, so counting them would make the first
        // element depend on which one arrived first.
        bool withdrawn = false;

        server.OnCard = _ => Volatile.Read(ref withdrawn)
            ? AiTest.CardFailure(404, "Agent not found: planner")
            : AiTest.Card(AiTest.CreateCard("planner"));

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new CardSubscription(Subscribe(client.AgentCard, "planner"));

        NacosAgentCardDetail? first = await subscription.NextAsync(Timeout);

        first.ShouldNotBeNull();

        Volatile.Write(ref withdrawn, true);

        NacosAgentCardDetail? second = await subscription.NextAsync(Timeout);

        // A subscriber that was never told would keep calling an agent that is no longer published.
        second.ShouldBeNull();
    }

    [Fact]
    public async Task SubscribeAgentCardAsync_ServesASecondSubscriberOfTheSameSelectorFromTheCache()
    {
        var server = new AiTestServer();

        server.OnCard = _ => AiTest.Card(AiTest.CreateCard("planner"));

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var first = new CardSubscription(Subscribe(client.AgentCard, "planner"));
        await first.NextAsync(Timeout);

        int readsAfterFirst = server.Cards.Count;

        await using var second = new CardSubscription(Subscribe(client.AgentCard, "planner"));
        NacosAgentCardDetail? seeded = await second.NextAsync(Timeout);

        // The selector is already cached, so a second subscriber costs no read of its own.
        seeded.ShouldNotBeNull();
        server.Cards.Count.ShouldBe(readsAfterFirst);
    }

    /// <summary>
    /// Builds the options the subscription tests run with.
    /// </summary>
    /// <returns>The options.</returns>
    private static NacosOptions FastOptions() => new()
    {
        ServerAddresses = ["127.0.0.1:8848"],
        AppName = "ai-tests",
        Ai = new NacosAiOptions { CacheRefreshInterval = Interval },
    };

    /// <summary>
    /// Starts a subscription that ends with the test.
    /// </summary>
    /// <param name="registry">The registry to subscribe through.</param>
    /// <param name="agentName">The agent name.</param>
    /// <param name="version">The card version, or <see langword="null"/> for none.</param>
    /// <param name="registrationType">The registration type, or <see langword="null"/> for none.</param>
    /// <returns>The subscription.</returns>
    private static IAsyncEnumerable<NacosAgentCardDetail?> Subscribe(
        INacosAgentCardRegistry registry,
        string agentName,
        string? version = null,
        string? registrationType = null) =>
        registry.SubscribeAgentCardAsync(
            agentName,
            version,
            registrationType,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Runs a subscription on a background pump so a test can wait for one change at a time.
    /// </summary>
    /// <remarks>
    /// The pump owns the enumerator. That is what makes waiting on a change that never comes safe:
    /// the pump can be cancelled and disposed as a unit instead of abandoning a move that is still
    /// in flight. A <see langword="null"/> element is a state rather than the end of the stream, so
    /// the queue carries nullable elements and only completion ends the reader.
    /// </remarks>
    private sealed class CardSubscription : IAsyncDisposable
    {
        private readonly ChannelReader<NacosAgentCardDetail?> _reader;
        private readonly CancellationTokenSource _cts = new();

        public CardSubscription(IAsyncEnumerable<NacosAgentCardDetail?> source)
        {
            var queue = Channel.CreateUnbounded<NacosAgentCardDetail?>();
            _reader = queue.Reader;

            Completion = Task.Run(async () =>
            {
                try
                {
                    await foreach (NacosAgentCardDetail? card in source.WithCancellation(_cts.Token))
                    {
                        queue.Writer.TryWrite(card);
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

        /// <summary>Waits for the next state.</summary>
        /// <param name="timeout">How long to wait.</param>
        /// <returns>The card, or <see langword="null"/> when it is not served.</returns>
        /// <exception cref="TimeoutException">No state arrived in time.</exception>
        public async Task<NacosAgentCardDetail?> NextAsync(TimeSpan timeout)
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
                // The wait is what this helper is for, and the test asserts on it, so a timeout is
                // reported as one rather than as a cancellation.
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
                // The pump ends by cancellation when the test ends.
            }

            _cts.Dispose();
        }
    }
}
