using System.Net;
using System.Threading.Channels;
using Lolitacoding.Nacos.AI;

namespace Lolitacoding.Nacos.Tests.AI;

/// <summary>
/// Verifies how a skill subscription behaves against a scripted HTTP endpoint.
/// </summary>
/// <remarks>
/// A skill subscription runs the same refresh loop a prompt subscription does, over a different
/// transport, and the archive is what makes it different: the change is detected by comparing ZIP
/// bytes rather than a serialized document, and a withdrawal is a null element rather than a stale
/// copy kept silently. The null is a state here, so the tests assert on it as one.
/// </remarks>
public sealed class NacosSkillSubscriptionTests
{
    /// <summary>The refresh interval the tests run with, short enough to be quick and long enough to be ordered.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly byte[] V1 = AIHttpTestServer.CreateArchive(("hello/SKILL.md", "v1"));

    private static readonly byte[] V2 = AIHttpTestServer.CreateArchive(("hello/SKILL.md", "v2"));

    [Fact]
    public async Task SubscribeSkillAsync_YieldsTheSkillTheServerServes()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"));
        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription = new SkillSubscription(Subscribe(client.Skill, "hello"));

        NacosSkill? first = await subscription.NextAsync(Timeout);

        // The current state is published first so a consumer can initialise from the same path that
        // handles later changes.
        first.ShouldNotBeNull();
        first.Version.ShouldBe("1.0.0");
        first.Md5.ShouldBe("hash-1");
        first.Archive.ToArray().ShouldBe(V1);
    }

    [Fact]
    public async Task SubscribeSkillAsync_AsksForTheSelectorsTheCallerNamed()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"));
        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription =
            new SkillSubscription(Subscribe(client.Skill, "hello", version: "1.0.0"));

        await subscription.NextAsync(Timeout);

        http.Query(0, "name").ShouldBe("hello");
        http.Query(0, "version").ShouldBe("1.0.0");
        http.Query(0, "label").ShouldBeNull();
    }

    [Fact]
    public async Task SubscribeSkillAsync_LeavesBothSelectorsUnsetForTheLatestVersion()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"));
        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription = new SkillSubscription(Subscribe(client.Skill, "hello"));

        await subscription.NextAsync(Timeout);

        // A client that asked for the label by name would have to know which one the server uses;
        // leaving both unset lets the server answer with whatever version it currently serves.
        http.Query(0, "version").ShouldBeNull();
        http.Query(0, "label").ShouldBeNull();
    }

    [Fact]
    public async Task SubscribeSkillAsync_YieldsTheSkillAgainWhenTheArchiveChanges()
    {
        var http = new AIHttpTestServer((index, _) => index == 0
            ? AIHttpTestServer.Zip(V1, "hash-1", "1.0.0")
            : AIHttpTestServer.Zip(V2, "hash-2", "1.1.0"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription = new SkillSubscription(Subscribe(client.Skill, "hello"));

        NacosSkill? first = await subscription.NextAsync(Timeout);
        NacosSkill? second = await subscription.NextAsync(Timeout);

        first!.Version.ShouldBe("1.0.0");
        second!.Version.ShouldBe("1.1.0");
        second.Archive.ToArray().ShouldBe(V2);
    }

    [Fact]
    public async Task SubscribeSkillAsync_SendsTheCachedHashSoAnUnchangedSkillCostsNoBody()
    {
        var http = new AIHttpTestServer((index, _) => index switch
        {
            0 => AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"),
            1 => AIHttpTestServer.NotModified("hash-1", "1.0.0"),
            _ => AIHttpTestServer.Zip(V2, "hash-2", "1.1.0"),
        });

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription = new SkillSubscription(Subscribe(client.Skill, "hello"));

        NacosSkill? first = await subscription.NextAsync(Timeout);
        NacosSkill? second = await subscription.NextAsync(Timeout);

        // The change is discovered only after the unchanged read, which proves the "not modified"
        // answer was not mistaken for a change.
        first!.Version.ShouldBe("1.0.0");
        second!.Version.ShouldBe("1.1.0");

        // The seeding read is unconditional; every later read carries the hash of the copy that is
        // cached, which is what makes the server answer rather than resend.
        http.Query(0, "md5").ShouldBeNull();
        http.Query(1, "md5").ShouldBe("hash-1");
    }

    [Fact]
    public async Task SubscribeSkillAsync_ReportsAWithdrawalAsANullElement()
    {
        var http = new AIHttpTestServer((index, _) => index == 0
            ? AIHttpTestServer.Zip(V1, "hash-1", "1.0.0")
            : AIHttpTestServer.NotFound("Skill not found: hello"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription = new SkillSubscription(Subscribe(client.Skill, "hello"));

        NacosSkill? first = await subscription.NextAsync(Timeout);
        NacosSkill? second = await subscription.NextAsync(Timeout);

        // A subscriber that was never told would keep loading content that is gone.
        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task SubscribeSkillAsync_StartsWithANullWhenTheSkillIsNotPublishedYet()
    {
        var http = new AIHttpTestServer((index, _) => index == 0
            ? AIHttpTestServer.NotFound("Skill not found: hello")
            : AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription = new SkillSubscription(Subscribe(client.Skill, "hello"));

        NacosSkill? first = await subscription.NextAsync(Timeout);
        NacosSkill? second = await subscription.NextAsync(Timeout);

        // The first element is the state the subscription starts from, so a skill that does not
        // exist yet is reported as absent rather than the subscription failing or staying silent.
        first.ShouldBeNull();
        second!.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task SubscribeSkillAsync_AnnouncesAWithdrawalOnlyOnce()
    {
        var http = new AIHttpTestServer((index, _) => index == 0
            ? AIHttpTestServer.Zip(V1, "hash-1", "1.0.0")
            : AIHttpTestServer.NotFound("Skill not found: hello"));

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription = new SkillSubscription(Subscribe(client.Skill, "hello"));

        NacosSkill? first = await subscription.NextAsync(Timeout);
        NacosSkill? withdrawn = await subscription.NextAsync(Timeout);

        first.ShouldNotBeNull();
        withdrawn.ShouldBeNull();

        // A skill that is still missing is not a state change, so the subscriber is not told again
        // on every refresh; it is told once, when the state actually became absent.
        await Should.ThrowAsync<TimeoutException>(async () =>
        {
            await subscription.NextAsync(TimeSpan.FromMilliseconds(200));
        });
    }

    [Fact]
    public async Task SubscribeSkillAsync_SurvivesARefreshThatFails()
    {
        var http = new AIHttpTestServer((index, _) => index switch
        {
            0 => AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"),
            1 => AIHttpTestServer.Result(HttpStatusCode.InternalServerError, 500, "boom"),
            _ => AIHttpTestServer.Zip(V2, "hash-2", "1.1.0"),
        });

        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription = new SkillSubscription(Subscribe(client.Skill, "hello"));

        NacosSkill? first = await subscription.NextAsync(Timeout);
        NacosSkill? second = await subscription.NextAsync(Timeout);

        // The failed read is not a state change, so the cached archive is untouched and the loop
        // keeps looking rather than the subscription ending.
        first!.Version.ShouldBe("1.0.0");
        second!.Version.ShouldBe("1.1.0");
    }

    [Fact]
    public async Task SubscribeSkillAsync_ServesASecondSubscriberOfTheSameSelectorFromTheCache()
    {
        // The default interval is long enough that nothing refreshes during the test, so a second
        // read can only have come from the cache.
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"));
        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient();

        await using var first = new SkillSubscription(Subscribe(client.Skill, "hello"));
        await first.NextAsync(Timeout);

        await using var second = new SkillSubscription(Subscribe(client.Skill, "hello"));
        NacosSkill? served = await second.NextAsync(Timeout);

        served!.Version.ShouldBe("1.0.0");
        http.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task SubscribeSkillAsync_KeepsAVersionAndALabelThatShareAStringApart()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"));
        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient();

        await using var byVersion =
            new SkillSubscription(Subscribe(client.Skill, "hello", version: "stable"));

        await using var byLabel =
            new SkillSubscription(Subscribe(client.Skill, "hello", label: "stable"));

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
    public async Task SubscribeSkillAsync_StopsReadingWhenTheLastSubscriberLeaves()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"));
        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient(FastOptions());

        var subscription = new SkillSubscription(Subscribe(client.Skill, "hello"));
        await subscription.NextAsync(Timeout);

        await subscription.DisposeAsync();

        int readsAfterLeaving = http.RequestCount;

        // Several intervals are enough for the loop to have read again had it not been stopped.
        await Task.Delay(Interval * 5, TestContext.Current.CancellationToken);

        http.RequestCount.ShouldBe(readsAfterLeaving);
    }

    [Fact]
    public async Task SubscribeSkillAsync_EndsWhenTheClientIsDisposed()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"));
        AITestServer server = new(httpHandler: http);
        NacosAIClient client = server.CreateClient(FastOptions());

        await using var subscription = new SkillSubscription(Subscribe(client.Skill, "hello"));
        await subscription.NextAsync(Timeout);

        await client.DisposeAsync();

        // Completing the subscription is what ends a consumer's enumeration rather than leaving it
        // waiting for a change that can no longer arrive.
        await subscription.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SubscribeSkillAsync_RejectsABlankName()
    {
        var http = new AIHttpTestServer((_, _) => AIHttpTestServer.Zip(V1, "hash-1", "1.0.0"));
        AITestServer server = new(httpHandler: http);
        await using NacosAIClient client = server.CreateClient();

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await foreach (NacosSkill? _ in
                client.Skill.SubscribeSkillAsync("  ", ct: TestContext.Current.CancellationToken))
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
    /// <param name="registry">The skill registry to subscribe through.</param>
    /// <param name="skillName">The skill name.</param>
    /// <param name="version">The version, or <see langword="null"/> for none.</param>
    /// <param name="label">The label, or <see langword="null"/> for none.</param>
    /// <returns>The subscription.</returns>
    private static IAsyncEnumerable<NacosSkill?> Subscribe(
        INacosSkillRegistry registry,
        string skillName,
        string? version = null,
        string? label = null) =>
        registry.SubscribeSkillAsync(
            skillName,
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
    private sealed class SkillSubscription : IAsyncDisposable
    {
        private readonly ChannelReader<NacosSkill?> _reader;
        private readonly CancellationTokenSource _cts = new();

        public SkillSubscription(IAsyncEnumerable<NacosSkill?> source)
        {
            var queue = Channel.CreateUnbounded<NacosSkill?>();
            _reader = queue.Reader;

            Completion = Task.Run(async () =>
            {
                try
                {
                    await foreach (NacosSkill? skill in source.WithCancellation(_cts.Token))
                    {
                        queue.Writer.TryWrite(skill);
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
        /// <returns>The skill, or <see langword="null"/> when it is not served.</returns>
        /// <exception cref="TimeoutException">No state arrived in time.</exception>
        public async Task<NacosSkill?> NextAsync(TimeSpan timeout)
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
