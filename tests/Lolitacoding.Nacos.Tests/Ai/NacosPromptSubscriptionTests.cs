using System.Threading.Channels;
using Lolitacoding.Nacos.Ai;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies how the prompt subscription behaves against a scripted AI service.
/// </summary>
/// <remarks>
/// A prompt subscription differs from an MCP one in two ways, and both are covered here. The refresh
/// is conditional, so an unchanged prompt is answered with "not modified" rather than with its
/// content; and a prompt that stops being served is reported, because a missing prompt is
/// distinguishable from one that has not appeared yet. That second difference is what makes the
/// element type nullable, so the tests assert on the nulls as states rather than as absences.
/// </remarks>
public sealed class NacosPromptSubscriptionTests
{
    /// <summary>The refresh interval the tests run with, short enough to be quick and long enough to be ordered.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SubscribePromptAsync_YieldsThePromptTheServerServes()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        server.OnPrompt = _ => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "Hello {{name}}"));

        await using var subscription = new PromptSubscription(Subscribe(client.Prompt, "greeting"));

        NacosPrompt? first = await subscription.NextAsync(Timeout);

        // The current state is published first so a consumer can initialise from the same path that
        // handles later changes.
        first.ShouldNotBeNull();
        first.Version.ShouldBe("1.0.0");
        first.Template.ShouldBe("Hello {{name}}");
    }

    [Fact]
    public async Task SubscribePromptAsync_AsksForTheSelectorsTheCallerNamed()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        server.OnPrompt = _ => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await using var subscription =
            new PromptSubscription(Subscribe(client.Prompt, "greeting", version: "1.0.0"));

        await subscription.NextAsync(Timeout);

        server.Prompts[0].PromptKey.ShouldBe("greeting");
        server.Prompts[0].Version.ShouldBe("1.0.0");
        server.Prompts[0].Label.ShouldBeNull();
    }

    [Fact]
    public async Task SubscribePromptAsync_LeavesBothSelectorsUnsetForTheLatestVersion()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        server.OnPrompt = _ => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await using var subscription = new PromptSubscription(Subscribe(client.Prompt, "greeting"));

        await subscription.NextAsync(Timeout);

        // A client that asked for the label by name would have to know which one the server uses;
        // leaving both unset lets the server answer with whatever version it currently serves.
        server.Prompts[0].Version.ShouldBeNull();
        server.Prompts[0].Label.ShouldBeNull();
    }

    [Fact]
    public async Task SubscribePromptAsync_YieldsThePromptAgainWhenTheServerChanges()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        int reads = 0;

        server.OnPrompt = _ => AiTest.Prompt(AiTest.CreatePrompt(
            "greeting",
            "1.0.0",
            $"revision {Interlocked.Increment(ref reads)}"));

        await using var subscription = new PromptSubscription(Subscribe(client.Prompt, "greeting"));

        NacosPrompt? first = await subscription.NextAsync(Timeout);
        NacosPrompt? second = await subscription.NextAsync(Timeout);

        first!.Template.ShouldBe("revision 1");
        second!.Template.ShouldNotBe(first.Template);
    }

    [Fact]
    public async Task SubscribePromptAsync_SendsTheCachedHashSoAnUnchangedPromptCostsNoBody()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        int reads = 0;

        server.OnPrompt = request => Interlocked.Increment(ref reads) switch
        {
            1 => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "v1", md5: "hash-1")),
            2 => AiTest.PromptFailure(304, "prompt data is up to date"),
            _ => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "v2", md5: "hash-2")),
        };

        await using var subscription = new PromptSubscription(Subscribe(client.Prompt, "greeting"));

        NacosPrompt? first = await subscription.NextAsync(Timeout);
        NacosPrompt? second = await subscription.NextAsync(Timeout);

        // The change is discovered only after the unchanged read, which proves the "not modified"
        // answer was not mistaken for a change: had it been, an empty prompt would have been served.
        first!.Template.ShouldBe("v1");
        second!.Template.ShouldBe("v2");

        // The seeding read is unconditional; every later read carries the hash of the copy that is
        // cached, which is what makes the server answer rather than resend.
        server.Prompts[0].Md5.ShouldBeNull();
        server.Prompts[1].Md5.ShouldBe("hash-1");
    }

    [Fact]
    public async Task SubscribePromptAsync_ReportsAWithdrawalAsANullElement()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        int reads = 0;

        server.OnPrompt = _ => Interlocked.Increment(ref reads) == 1
            ? AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "Hello"))
            : AiTest.PromptFailure(404, "Prompt not found: greeting");

        await using var subscription = new PromptSubscription(Subscribe(client.Prompt, "greeting"));

        NacosPrompt? first = await subscription.NextAsync(Timeout);
        NacosPrompt? second = await subscription.NextAsync(Timeout);

        // Unlike an MCP server, a prompt that is no longer served is reported: a subscriber that was
        // never told would keep rendering content that is gone.
        first!.Template.ShouldBe("Hello");
        second.ShouldBeNull();
    }

    [Fact]
    public async Task SubscribePromptAsync_StartsWithANullWhenThePromptIsNotPublishedYet()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        int reads = 0;

        server.OnPrompt = _ => Interlocked.Increment(ref reads) == 1
            ? AiTest.PromptFailure(404, "Prompt not found: greeting")
            : AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await using var subscription = new PromptSubscription(Subscribe(client.Prompt, "greeting"));

        NacosPrompt? first = await subscription.NextAsync(Timeout);
        NacosPrompt? second = await subscription.NextAsync(Timeout);

        // The first element is the state the subscription starts from, so a prompt that does not
        // exist yet is reported as absent rather than the subscription failing or staying silent.
        first.ShouldBeNull();
        second!.Template.ShouldBe("Hello");
    }

    [Fact]
    public async Task SubscribePromptAsync_AnnouncesAWithdrawalOnlyOnce()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        int reads = 0;

        server.OnPrompt = _ => Interlocked.Increment(ref reads) == 1
            ? AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "Hello"))
            : AiTest.PromptFailure(404, "Prompt not found: greeting");

        await using var subscription = new PromptSubscription(Subscribe(client.Prompt, "greeting"));

        NacosPrompt? first = await subscription.NextAsync(Timeout);
        NacosPrompt? withdrawn = await subscription.NextAsync(Timeout);

        first.ShouldNotBeNull();
        withdrawn.ShouldBeNull();

        // A prompt that is still missing is not a state change, so the subscriber is not told again
        // on every refresh; it is told once, when the state actually became absent.
        await Should.ThrowAsync<TimeoutException>(async () =>
        {
            await subscription.NextAsync(TimeSpan.FromMilliseconds(200));
        });
    }

    [Fact]
    public async Task SubscribePromptAsync_SurvivesARefreshThatFails()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        int reads = 0;

        server.OnPrompt = _ => Interlocked.Increment(ref reads) switch
        {
            1 => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "before")),
            2 => AiTest.PromptFailure(500, "boom"),
            _ => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "after")),
        };

        await using var subscription = new PromptSubscription(Subscribe(client.Prompt, "greeting"));

        NacosPrompt? first = await subscription.NextAsync(Timeout);
        NacosPrompt? second = await subscription.NextAsync(Timeout);

        // The failed read is not a state change, so the cached copy is untouched and the loop keeps
        // looking rather than the subscription ending.
        first!.Template.ShouldBe("before");
        second!.Template.ShouldBe("after");
    }

    [Fact]
    public async Task SubscribePromptAsync_ServesASecondSubscriberOfTheSameSelectorFromTheCache()
    {
        // The default interval is long enough that nothing refreshes during the test, so a second
        // read can only have come from the cache.
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnPrompt = _ => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await using var first = new PromptSubscription(Subscribe(client.Prompt, "greeting"));
        await first.NextAsync(Timeout);

        await using var second = new PromptSubscription(Subscribe(client.Prompt, "greeting"));
        NacosPrompt? served = await second.NextAsync(Timeout);

        served!.Template.ShouldBe("Hello");
        server.Prompts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SubscribePromptAsync_KeepsAVersionAndALabelThatShareAStringApart()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        server.OnPrompt = _ => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await using var byVersion =
            new PromptSubscription(Subscribe(client.Prompt, "greeting", version: "stable"));

        await using var byLabel =
            new PromptSubscription(Subscribe(client.Prompt, "greeting", label: "stable"));

        await byVersion.NextAsync(Timeout);
        await byLabel.NextAsync(Timeout);

        // A version and a label can legitimately be the same string, so they must not share a cache
        // entry: the second subscriber has to read the server rather than take the first's copy.
        server.Prompts.Count.ShouldBe(2);
        server.Prompts[0].Version.ShouldBe("stable");
        server.Prompts[1].Label.ShouldBe("stable");
    }

    [Fact]
    public async Task SubscribePromptAsync_StopsReadingWhenTheLastSubscriberLeaves()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient(FastOptions());

        server.OnPrompt = _ => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "Hello"));

        var subscription = new PromptSubscription(Subscribe(client.Prompt, "greeting"));
        await subscription.NextAsync(Timeout);

        await subscription.DisposeAsync();

        int readsAfterLeaving = server.Prompts.Count;

        // Several intervals are enough for the loop to have read again had it not been stopped.
        await Task.Delay(Interval * 5, TestContext.Current.CancellationToken);

        server.Prompts.Count.ShouldBe(readsAfterLeaving);
    }

    [Fact]
    public async Task SubscribePromptAsync_EndsWhenTheClientIsDisposed()
    {
        var server = new AiTestServer();
        NacosAiClient client = server.CreateClient(FastOptions());

        server.OnPrompt = _ => AiTest.Prompt(AiTest.CreatePrompt("greeting", "1.0.0", "Hello"));

        await using var subscription = new PromptSubscription(Subscribe(client.Prompt, "greeting"));
        await subscription.NextAsync(Timeout);

        await client.DisposeAsync();

        // Completing the subscription is what ends a consumer's enumeration rather than leaving it
        // waiting for a change that can no longer arrive.
        await subscription.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SubscribePromptAsync_RejectsABlankKey()
    {
        var server = new AiTestServer();
        await using NacosAiClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await foreach (NacosPrompt? _ in
                client.Prompt.SubscribePromptAsync("  ", ct: TestContext.Current.CancellationToken))
            {
                // The iteration never reaches here.
            }
        });

        server.Factory.RequestedAddresses.ShouldBeEmpty();
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
    /// <param name="registry">The prompt registry to subscribe through.</param>
    /// <param name="promptKey">The prompt key.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <returns>The subscription.</returns>
    private static IAsyncEnumerable<NacosPrompt?> Subscribe(
        INacosPromptRegistry registry,
        string promptKey,
        string? version = null,
        string? label = null) =>
        registry.SubscribePromptAsync(
            promptKey,
            version,
            label,
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
    private sealed class PromptSubscription : IAsyncDisposable
    {
        private readonly ChannelReader<NacosPrompt?> _reader;
        private readonly CancellationTokenSource _cts = new();

        public PromptSubscription(IAsyncEnumerable<NacosPrompt?> source)
        {
            var queue = Channel.CreateUnbounded<NacosPrompt?>();
            _reader = queue.Reader;

            Completion = Task.Run(async () =>
            {
                try
                {
                    await foreach (NacosPrompt? prompt in source.WithCancellation(_cts.Token))
                    {
                        queue.Writer.TryWrite(prompt);
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
        /// <returns>The prompt, or <see langword="null"/> when it is not served.</returns>
        /// <exception cref="TimeoutException">No state arrived in time.</exception>
        public async Task<NacosPrompt?> NextAsync(TimeSpan timeout)
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
                // Expected.
            }

            _cts.Dispose();
        }
    }
}
