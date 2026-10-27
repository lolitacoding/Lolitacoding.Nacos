using System.Collections.Concurrent;
using System.Diagnostics;
using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Protocol.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies the coordinator that keeps one HTTP batch long poll open for the whole watch set.
/// </summary>
/// <remarks>
/// The transport is where the batch's failure rules live, so the tests here are about the watch set
/// and the answers rather than about the wire: what a change does to the next batch, which failure
/// ends one watch, and which failure ends the binding. The binding itself is a scripted stand-in so
/// that a long poll can be left unanswered and the timing can be short.
/// </remarks>
public sealed class NacosAgentHttpWatchTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string FingerprintA = "sha256-canonical-json-v1:aaaa";
    private const string FingerprintB = "sha256-canonical-json-v1:bbbb";
    private const string FingerprintC = "sha256-canonical-json-v1:cccc";

    [Fact]
    public async Task Start_SendsTheCompleteWatchSetWithTheFingerprintsTheClientHolds()
    {
        var binding = new FakeBinding(call => Pending(call.Token));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        FakeBinding.Call first = await binding.WaitForCallAsync(1);

        harness.Transport.Start("watch-b", Request("writer"), FingerprintB);
        FakeBinding.Call second = await binding.WaitForCallAsync(2);

        // A watch set change makes the in-flight poll stale, so the second request carries the whole
        // set rather than the addition alone.
        second.Generation.ShouldBeGreaterThan(first.Generation);
        second.TimeoutMillis.ShouldBe(30000);
        second.Watches.Select(watch => watch.ClientWatchId).ShouldBe(["watch-a", "watch-b"]);
        second.Watches.Select(watch => watch.MaterializedFingerprint)
            .ShouldBe([FingerprintA, FingerprintB]);
    }

    [Fact]
    public async Task Update_ReplacesTheFingerprintAndStartsANewPoll()
    {
        var binding = new FakeBinding(call => Pending(call.Token));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        await binding.WaitForCallAsync(1);

        harness.Transport.Update("watch-a", FingerprintB);
        FakeBinding.Call second = await binding.WaitForCallAsync(2);

        second.Watches.Single().MaterializedFingerprint.ShouldBe(FingerprintB);
    }

    [Fact]
    public async Task Update_OfAWatchThatIsNotRegisteredStartsNoPoll()
    {
        var binding = new FakeBinding(call => Pending(call.Token));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        await binding.WaitForCallAsync(1);

        harness.Transport.Update("watch-b", FingerprintB);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        binding.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AChangedWatch_IsLeftOutUntilItsNewFingerprintIsHandedBack()
    {
        var binding = new FakeBinding(call => call.Index == 0
            ? Task.FromResult(Changed(call.Generation, "watch-a"))
            : Pending(call.Token));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        await binding.WaitForCallAsync(1);
        await WaitUntilAsync(() => !harness.Invalidated.IsEmpty, "the watch was reported changed");

        harness.Invalidated.ShouldContain("watch-a");

        // The server reported a change against the fingerprint the client sent, so sending the same
        // fingerprint again would report the same change again. The watch leaves the batch until the
        // manager has read the new state and handed back its fingerprint.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        binding.Calls.Count.ShouldBe(1);

        harness.Transport.Update("watch-a", FingerprintB);
        FakeBinding.Call second = await binding.WaitForCallAsync(2);

        second.Watches.Single().MaterializedFingerprint.ShouldBe(FingerprintB);
    }

    [Fact]
    public async Task AChangedAnswer_OnlyReportsTheWatchesThatAreStillRegistered()
    {
        var binding = new FakeBinding(call => call.Index == 1
            ? Task.FromResult(Changed(call.Generation, "watch-a", "watch-b"))
            : Pending(call.Token));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        harness.Transport.Start("watch-b", Request("writer"), FingerprintB);

        await WaitUntilAsync(() => !harness.Invalidated.IsEmpty, "a watch was reported changed");
        await WaitUntilAsync(() => harness.Invalidated.Count >= 2, "both watches were reported changed");
        harness.Invalidated.OrderBy(id => id, StringComparer.Ordinal)
            .ShouldBe(["watch-a", "watch-b"]);
    }

    [Fact]
    public async Task Stop_RemovesTheWatchAndStartsANewPollWithoutIt()
    {
        var binding = new FakeBinding(call => Pending(call.Token));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        harness.Transport.Start("watch-b", Request("writer"), FingerprintB);
        await binding.WaitForCallAsync(2);

        harness.Transport.Stop("watch-a");
        FakeBinding.Call third = await binding.WaitForCallAsync(3);

        third.Watches.Select(watch => watch.ClientWatchId).ShouldBe(["watch-b"]);
    }

    [Fact]
    public async Task Stop_OfAWatchThatIsNotRegisteredStartsNoPoll()
    {
        var binding = new FakeBinding(call => Pending(call.Token));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        await binding.WaitForCallAsync(1);

        harness.Transport.Stop("watch-b");

        await Task.Delay(50, TestContext.Current.CancellationToken);
        binding.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ACapacityRejection_DropsTheNewestAdditionAndReportsItUnavailable()
    {
        var binding = new FakeBinding(call => call.Index == 1
            ? Task.FromException<AgentWatchBatchResponse>(
                new NacosAgentWatchCapacityException(503, "Watch capacity reached"))
            : Pending(call.Token));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        harness.Transport.Start("watch-b", Request("writer"), FingerprintB);

        await WaitUntilAsync(() => !harness.Unavailable.IsEmpty, "the newest addition was rejected");

        harness.Unavailable.Single().ShouldBe("watch-b");
        harness.WireUnavailable.ShouldBeEmpty();
        harness.Transport.IsAvailable.ShouldBeTrue();

        // Only the addition that overflowed the set is dropped; the watches the server already had
        // stay registered and are polled again without it.
        FakeBinding.Call next = await binding.WaitForCallAsync(3);

        next.Watches.Select(watch => watch.ClientWatchId).ShouldBe(["watch-a"]);
    }

    [Fact]
    public async Task ACapacityRejection_WithoutAPendingAddition_AbandonsTheBinding()
    {
        var binding = new FakeBinding(call => call.Index == 0
            ? Task.FromResult(Unchanged(call.Generation))
            : Task.FromException<AgentWatchBatchResponse>(
                new NacosAgentWatchCapacityException(503, "Watch capacity reached")));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);

        await WaitUntilAsync(() => !harness.WireUnavailable.IsEmpty, "the binding was abandoned");

        harness.Unavailable.ShouldBeEmpty();
        harness.Transport.IsAvailable.ShouldBeFalse();
        binding.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AMigrationRejection_EndsEveryWatchWithoutAbandoningTheBinding()
    {
        var binding = new FakeBinding(call => Task.FromException<AgentWatchBatchResponse>(
            new NacosServerException(50105, "The agent is being migrated")));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);

        await WaitUntilAsync(() => !harness.Unavailable.IsEmpty, "the watch was ended");

        harness.Unavailable.Single().ShouldBe("watch-a");

        // The agent is protected by the migration rather than the binding being unusable, so another
        // watch may still be registered.
        harness.WireUnavailable.ShouldBeEmpty();
        harness.Transport.IsAvailable.ShouldBeTrue();
        binding.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ThreeConsecutiveFailures_AbandonTheBinding()
    {
        var binding = new FakeBinding(call => Task.FromException<AgentWatchBatchResponse>(
            new NacosServerException(500, "The server failed the request")));
        await using var harness = new Harness(
            binding,
            retryDelay: TimeSpan.FromMilliseconds(5),
            failuresBeforeFallback: 3);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);

        await WaitUntilAsync(() => !harness.WireUnavailable.IsEmpty, "the binding was abandoned");

        harness.WireUnavailable.Single().ErrorCode.ShouldBe(500);
        harness.Transport.IsAvailable.ShouldBeFalse();
        binding.Calls.Count.ShouldBe(3);
    }

    [Fact]
    public async Task AFailureThatRetryingCannotFix_AbandonsTheBindingAtOnce()
    {
        var binding = new FakeBinding(call => Task.FromException<AgentWatchBatchResponse>(
            new NacosServerException(404, "The endpoint is not mapped")));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);

        await WaitUntilAsync(() => !harness.WireUnavailable.IsEmpty, "the binding was abandoned");

        harness.Transport.IsAvailable.ShouldBeFalse();
        binding.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ATransientFailure_RetriesTheSameWatchSet()
    {
        var binding = new FakeBinding(call => call.Index == 0
            ? Task.FromException<AgentWatchBatchResponse>(
                new NacosServerException(500, "The server failed the request"))
            : Pending(call.Token));
        await using var harness = new Harness(binding, retryDelay: TimeSpan.FromMilliseconds(5));

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);

        FakeBinding.Call second = await binding.WaitForCallAsync(2);

        // The watch set did not change, so the retry carries the same generation; only a set change
        // moves it.
        second.Generation.ShouldBe(binding.Calls[0].Generation);
        second.Watches.Single().MaterializedFingerprint.ShouldBe(FingerprintA);
        harness.WireUnavailable.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAnswerForAnOutdatedGeneration_IsRetriedRatherThanApplied()
    {
        var binding = new FakeBinding(call => call.Index == 0
            ? Task.FromResult(Changed(call.Generation + 1, "watch-a"))
            : Pending(call.Token));
        await using var harness = new Harness(binding, retryDelay: TimeSpan.FromMilliseconds(5));

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);

        FakeBinding.Call second = await binding.WaitForCallAsync(2);

        // An answer that names another generation may describe identifiers the client no longer
        // holds, so acting on it would invalidate the wrong watch.
        harness.Invalidated.ShouldBeEmpty();
        second.Generation.ShouldBe(binding.Calls[0].Generation);
    }

    [Fact]
    public async Task Start_AfterTheBindingWasAbandoned_IsRefused()
    {
        var binding = new FakeBinding(call => Task.FromException<AgentWatchBatchResponse>(
            new NacosServerException(501, "The server does not implement the endpoint")));
        await using var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        await WaitUntilAsync(() => !harness.WireUnavailable.IsEmpty, "the binding was abandoned");

        NacosServerException exception = Should.Throw<NacosServerException>(
            () => harness.Transport.Start("watch-b", Request("writer"), FingerprintB));

        exception.ErrorCode.ShouldBe(501);
    }

    [Fact]
    public async Task Dispose_CancelsTheInFlightPoll()
    {
        var binding = new FakeBinding(call => Pending(call.Token));
        var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        FakeBinding.Call first = await binding.WaitForCallAsync(1);

        await harness.DisposeAsync();

        first.Token.IsCancellationRequested.ShouldBeTrue();
        harness.Transport.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var binding = new FakeBinding(call => Pending(call.Token));
        var harness = new Harness(binding);

        harness.Transport.Start("watch-a", Request("planner"), FingerprintA);
        await binding.WaitForCallAsync(1);

        await harness.DisposeAsync();
        await harness.DisposeAsync();
    }

    /// <summary>
    /// Builds the discovery a watch follows.
    /// </summary>
    /// <param name="agentName">The agent name.</param>
    /// <returns>The discovery.</returns>
    private static NacosAgentDiscoveryRequest Request(string agentName) => new()
    {
        NamespaceId = "public",
        Reference = new NacosAgentReference { AgentName = agentName },
    };

    /// <summary>Builds an answer that reports the given watches changed.</summary>
    /// <param name="generation">The generation answered.</param>
    /// <param name="changed">The changed watch identifiers.</param>
    /// <returns>The answer.</returns>
    private static AgentWatchBatchResponse Changed(long generation, params string[] changed) => new()
    {
        Generation = generation,
        Changed = true,
        ChangedClientWatchIds = [.. changed],
    };

    /// <summary>Builds an answer that reports no change.</summary>
    /// <param name="generation">The generation answered.</param>
    /// <returns>The answer.</returns>
    private static AgentWatchBatchResponse Unchanged(long generation) => new()
    {
        Generation = generation,
        Changed = false,
        ChangedClientWatchIds = [],
    };

    /// <summary>Builds a long poll that stays unanswered until it is cancelled.</summary>
    /// <returns>The answer, which never arrives.</returns>
    private static async Task<AgentWatchBatchResponse> Pending(CancellationToken ct)
    {
        await Task.Delay(System.Threading.Timeout.Infinite, ct).ConfigureAwait(false);

        throw new UnreachableException("A pending long poll cannot complete.");
    }

    /// <summary>
    /// Waits until a condition holds.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="because">What is being waited for, for the timeout message.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5, TestContext.Current.CancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(string.Concat("Timed out waiting until ", because, "."));
    }

    /// <summary>
    /// The transport under test together with the callbacks it made.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private int _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="Harness"/> class.
        /// </summary>
        /// <param name="binding">The scripted binding.</param>
        /// <param name="retryDelay">How long a failed poll waits before it is tried again.</param>
        /// <param name="failuresBeforeFallback">
        /// How many consecutive failures the binding is given before it is abandoned.
        /// </param>
        public Harness(
            FakeBinding binding,
            TimeSpan? retryDelay = null,
            int failuresBeforeFallback = 3)
        {
            Transport = new NacosAgentHttpWatchTransport(
                binding,
                TimeProvider.System,
                NullLogger.Instance,
                Invalidated.Enqueue,
                (clientWatchId, exception) => Unavailable.Enqueue(clientWatchId),
                WireUnavailable.Enqueue,
                TimeSpan.FromSeconds(30),
                retryDelay ?? TimeSpan.FromMilliseconds(5),
                failuresBeforeFallback);
        }

        /// <summary>Gets the transport.</summary>
        public NacosAgentHttpWatchTransport Transport { get; }

        /// <summary>Gets the watches the binding reported changed.</summary>
        public ConcurrentQueue<string> Invalidated { get; } = new();

        /// <summary>Gets the watches the binding reported gone.</summary>
        public ConcurrentQueue<string> Unavailable { get; } = new();

        /// <summary>Gets the failures that ended the binding.</summary>
        public ConcurrentQueue<NacosException> WireUnavailable { get; } = new();

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await Transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A scripted stand-in for the HTTP watch binding.
    /// </summary>
    private sealed class FakeBinding : INacosAgentHttpWatchBinding
    {
        private readonly Func<Call, Task<AgentWatchBatchResponse>> _respond;
        private readonly object _gate = new();
        private readonly List<Call> _calls = [];

        /// <summary>
        /// Initializes a new instance of the <see cref="FakeBinding"/> class.
        /// </summary>
        /// <param name="respond">Produces the answer to one long poll.</param>
        public FakeBinding(Func<Call, Task<AgentWatchBatchResponse>> respond) => _respond = respond;

        /// <summary>Gets the long polls the transport sent, in order.</summary>
        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        /// <summary>
        /// Waits until the transport sent the given number of long polls.
        /// </summary>
        /// <param name="count">How many polls to wait for.</param>
        /// <returns>The last poll that was sent.</returns>
        public async Task<Call> WaitForCallAsync(int count)
        {
            await WaitUntilAsync(
                () => Calls.Count >= count,
                string.Concat("the binding was called ", count.ToString(), " time(s)"))
                .ConfigureAwait(false);

            return Calls[count - 1];
        }

        /// <inheritdoc />
        public async Task<AgentWatchBatchResponse> WatchAsync(
            long generation,
            int timeoutMillis,
            IReadOnlyList<AgentWatchBatchItem> watches,
            CancellationToken ct)
        {
            Call call;

            lock (_gate)
            {
                call = new Call(_calls.Count, generation, timeoutMillis, [.. watches], ct);
                _calls.Add(call);
            }

            // The answer is always delivered off the caller's thread so that the transport observes
            // the same ordering it would with a real HTTP request.
            await Task.Yield();

            return await _respond(call).ConfigureAwait(false);
        }

        /// <summary>
        /// One long poll the transport sent.
        /// </summary>
        /// <param name="Index">The zero-based index of the poll.</param>
        /// <param name="Generation">The generation the poll was built for.</param>
        /// <param name="TimeoutMillis">How long the poll asked the server to wait.</param>
        /// <param name="Watches">The watch set sent.</param>
        /// <param name="Token">The token that cancels the poll.</param>
        public sealed record Call(
            int Index,
            long Generation,
            int TimeoutMillis,
            IReadOnlyList<AgentWatchBatchItem> Watches,
            CancellationToken Token);
    }
}
