using System.Net;
using System.Threading.Channels;
using Lolitacoding.Nacos.AI;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies how an agent-specification subscription behaves against a scripted HTTP endpoint.
/// </summary>
/// <remarks>
/// The subscription is the same poll a prompt or a skill uses, over the HTTP transport, and the
/// comparison is the payload's: the specification is a JSON document with free-form resource
/// metadata, so a change is detected by comparing the whole document. A withdrawal is a null element,
/// as it is for the other two resources that can disappear.
/// </remarks>
public sealed class NacosAgentSpecSubscriptionTests
{
    /// <summary>The refresh interval the tests run with, short enough to be quick and long enough to be ordered.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SubscribeAgentSpecAsync_YieldsTheSpecificationTheServerServes()
    {
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));

        NacosAgentSpec? first = await subscription.NextAsync(Timeout);

        // The current state is published first so a consumer can initialise from the same path that
        // handles later changes.
        first.ShouldNotBeNull();
        first.Version.ShouldBe("0.0.1");
        first.Md5.ShouldBe("hash-1");
        first.Name.ShouldBe("worker");
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_AsksForTheSelectorsTheCallerNamed()
    {
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker", version: "0.0.1"));

        await subscription.NextAsync(Timeout);

        http.Query(0, "name").ShouldBe("worker");
        http.Query(0, "version").ShouldBe("0.0.1");
        http.Query(0, "label").ShouldBeNull();
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_LeavesBothSelectorsUnsetForTheLatestVersion()
    {
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));

        await subscription.NextAsync(Timeout);

        // A client that asked for the label by name would have to know which one the server uses;
        // leaving both unset lets the server answer with whatever version it currently serves.
        http.Query(0, "version").ShouldBeNull();
        http.Query(0, "label").ShouldBeNull();
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_YieldsTheSpecificationAgainWhenTheServerChanges()
    {
        var http = new AIHttpTestServer((index, _) => index == 0
            ? AIHttpTestServer.AgentSpec(AgentSpecJson.Build(description: "before"), "hash-1", "0.0.1")
            : AIHttpTestServer.AgentSpec(AgentSpecJson.Build(description: "after"), "hash-2", "0.0.2"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));

        NacosAgentSpec? first = await subscription.NextAsync(Timeout);
        NacosAgentSpec? second = await subscription.NextAsync(Timeout);

        first!.Description.ShouldBe("before");
        second!.Description.ShouldBe("after");
        second.Version.ShouldBe("0.0.2");
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_ReadsInFullAndReportsOnlyARealChange()
    {
        var http = new AIHttpTestServer((index, _) => index switch
        {
            0 => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(description: "before"), "hash-1", "0.0.1"),
            1 => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(description: "before"), "hash-1", "0.0.1"),
            _ => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(description: "after"), "hash-2", "0.0.2"),
        });

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));

        NacosAgentSpec? first = await subscription.NextAsync(Timeout);
        NacosAgentSpec? second = await subscription.NextAsync(Timeout);

        // The change is discovered only after a read that returned the same document, which proves an
        // unchanged specification is not mistaken for a change.
        first!.Version.ShouldBe("0.0.1");
        second!.Description.ShouldBe("after");
        second.Version.ShouldBe("0.0.2");

        // No read carries a hash. A conditional read cannot report a withdrawal on this resource: the
        // server compares the hash of the version the selector resolved to and answers 304 before it
        // checks that the version is still online, so a version taken offline is "not modified"
        // forever. Every refresh therefore reads the specification in full.
        http.Query(0, "md5").ShouldBeNull();
        http.Query(1, "md5").ShouldBeNull();
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_TreatsANotModifiedAnswerAsUnchanged()
    {
        var http = new AIHttpTestServer((index, _) => index switch
        {
            0 => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(description: "before"), "hash-1", "0.0.1"),
            1 => AIHttpTestServer.AgentSpecNotModified("hash-1"),
            _ => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(description: "after"), "hash-2", "0.0.2"),
        });

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));

        NacosAgentSpec? first = await subscription.NextAsync(Timeout);
        NacosAgentSpec? second = await subscription.NextAsync(Timeout);

        // This client asks for no conditional read, but the answer is still understood: it means the
        // cached copy stands, so nothing is reported and the next read is what finds the change.
        first!.Version.ShouldBe("0.0.1");
        second!.Description.ShouldBe("after");
        second.Version.ShouldBe("0.0.2");
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_ReportsAWithdrawalAsANullElement()
    {
        var http = new AIHttpTestServer((index, _) => index == 0
            ? AIHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1")
            : AIHttpTestServer.NotFound("AgentSpec not found: worker"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));

        NacosAgentSpec? first = await subscription.NextAsync(Timeout);
        NacosAgentSpec? second = await subscription.NextAsync(Timeout);

        // A subscriber that was never told would keep loading content that is gone.
        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_StartsWithANullWhenItIsNotPublishedYet()
    {
        var http = new AIHttpTestServer((index, _) => index == 0
            ? AIHttpTestServer.NotFound("AgentSpec not found: worker")
            : AIHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));

        NacosAgentSpec? first = await subscription.NextAsync(Timeout);
        NacosAgentSpec? second = await subscription.NextAsync(Timeout);

        // The first element is the state the subscription starts from, so a specification that does
        // not exist yet is reported as absent rather than the subscription failing or staying silent.
        first.ShouldBeNull();
        second!.Version.ShouldBe("0.0.1");
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_AnnouncesAWithdrawalOnlyOnce()
    {
        var http = new AIHttpTestServer((index, _) => index == 0
            ? AIHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1")
            : AIHttpTestServer.NotFound("AgentSpec not found: worker"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));

        NacosAgentSpec? first = await subscription.NextAsync(Timeout);
        NacosAgentSpec? withdrawn = await subscription.NextAsync(Timeout);

        first.ShouldNotBeNull();
        withdrawn.ShouldBeNull();

        // A specification that is still missing is not a state change, so the subscriber is not told
        // again on every refresh; it is told once, when the state actually became absent.
        await Should.ThrowAsync<TimeoutException>(async () =>
        {
            await subscription.NextAsync(TimeSpan.FromMilliseconds(200));
        });
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_SurvivesARefreshThatFails()
    {
        var http = new AIHttpTestServer((index, _) => index switch
        {
            0 => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(description: "before"), "hash-1", "0.0.1"),
            1 => AIHttpTestServer.Result(HttpStatusCode.InternalServerError, 500, "boom"),
            _ => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(description: "after"), "hash-2", "0.0.2"),
        });

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));

        NacosAgentSpec? first = await subscription.NextAsync(Timeout);
        NacosAgentSpec? second = await subscription.NextAsync(Timeout);

        // The failed read is not a state change, so the cached copy is untouched and the loop keeps
        // looking rather than the subscription ending.
        first!.Description.ShouldBe("before");
        second!.Description.ShouldBe("after");
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_ServesASecondSubscriberOfTheSameSelectorFromTheCache()
    {
        // The default interval is long enough that nothing refreshes during the test, so a second
        // read can only have come from the cache.
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient();

        await using var first = new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));
        await first.NextAsync(Timeout);

        await using var second = new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));
        NacosAgentSpec? served = await second.NextAsync(Timeout);

        served!.Version.ShouldBe("0.0.1");
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_KeepsAVersionAndALabelThatShareAStringApart()
    {
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient();

        await using var byVersion =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker", version: "stable"));

        await using var byLabel =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker", label: "stable"));

        await byVersion.NextAsync(Timeout);
        await byLabel.NextAsync(Timeout);

        // A version and a label can legitimately be the same string, so they must not share a cache
        // entry: the second subscriber has to read the server rather than take the first's copy. The
        // two subscriptions are started independently, so the order the reads arrive in is not fixed
        // and only the set of selectors is asserted on.
        http.RequestCount.ShouldBe(2);

        var selectors = Enumerable.Range(0, http.RequestCount)
            .Select(index => (Version: http.Query(index, "version"), Label: http.Query(index, "label")))
            .ToList();

        selectors.Count(selector => selector.Version == "stable" && selector.Label is null).ShouldBe(1);
        selectors.Count(selector => selector.Version is null && selector.Label == "stable").ShouldBe(1);
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_StopsReadingWhenTheLastSubscriberLeaves()
    {
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        var subscription = new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));
        await subscription.NextAsync(Timeout);

        await subscription.DisposeAsync();

        int readsAfterLeaving = http.RequestCount;

        // Several intervals are enough for the loop to have read again had it not been stopped.
        await Task.Delay(Interval * 5, TestContext.Current.CancellationToken);

        http.RequestCount.ShouldBe(readsAfterLeaving);
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_EndsWhenTheClientIsDisposed()
    {
        var http = new AIHttpTestServer(
            (_, _) => AIHttpTestServer.AgentSpec(AgentSpecJson.Build(), "hash-1", "0.0.1"));

        AITestServer server = new(httpHandler: http);
        NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new AgentSpecSubscription(Subscribe(client.AgentSpec, "worker"));

        await subscription.NextAsync(Timeout);

        await client.DisposeAsync();

        // Completing the subscription is what ends a consumer's enumeration rather than leaving it
        // waiting for a change that can no longer arrive.
        await subscription.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SubscribeAgentSpecAsync_RejectsABlankName()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.AgentSpec(AgentSpecJson.Build()));
        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await foreach (NacosAgentSpec? _ in
                client.AgentSpec.SubscribeAgentSpecAsync("  ", ct: TestContext.Current.CancellationToken))
            {
                // The iteration never reaches here.
            }
        });

        http.RequestCount.ShouldBe(0);
    }

    private static NacosOptions FastOptions() => new()
    {
        ServerAddresses = ["127.0.0.1:8848"],
        AppName = "ai-tests",
        AI = new NacosAIOptions { CacheRefreshInterval = Interval },
    };

    /// <summary>
    /// Starts a subscription that ends with the test.
    /// </summary>
    /// <param name="registry">The registry to subscribe through.</param>
    /// <param name="agentSpecName">The specification name.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <returns>The subscription.</returns>
    private static IAsyncEnumerable<NacosAgentSpec?> Subscribe(
        INacosAgentSpecRegistry registry,
        string agentSpecName,
        string? version = null,
        string? label = null) =>
        registry.SubscribeAgentSpecAsync(
            agentSpecName,
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
    private sealed class AgentSpecSubscription : IAsyncDisposable
    {
        private readonly ChannelReader<NacosAgentSpec?> _reader;
        private readonly CancellationTokenSource _cts = new();

        public AgentSpecSubscription(IAsyncEnumerable<NacosAgentSpec?> source)
        {
            var queue = Channel.CreateUnbounded<NacosAgentSpec?>();
            _reader = queue.Reader;

            Completion = Task.Run(async () =>
            {
                try
                {
                    await foreach (NacosAgentSpec? spec in source.WithCancellation(_cts.Token))
                    {
                        queue.Writer.TryWrite(spec);
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
        /// <returns>The specification, or <see langword="null"/> when it is not served.</returns>
        /// <exception cref="TimeoutException">No state arrived in time.</exception>
        public async Task<NacosAgentSpec?> NextAsync(TimeSpan timeout)
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
                // The pump observed the cancellation while completing the queue.
            }

            _cts.Dispose();
        }
    }
}
