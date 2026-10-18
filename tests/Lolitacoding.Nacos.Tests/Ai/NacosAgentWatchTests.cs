using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Lolitacoding.Nacos.Ai;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Messages;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies how a RAD agent watch behaves against a scripted AI service.
/// </summary>
/// <remarks>
/// A watch is the push half of discovery, and almost everything that can go wrong with it is about
/// who decides that the answer changed. These tests pin that down: the state is read when the server
/// says to and not on a timer, a hint that carries the state the client already holds costs no read,
/// the hint is answered so the server stops re-sending it, and the watch survives a reconnect and a
/// read that fails.
/// </remarks>
public sealed class NacosAgentWatchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task WatchAgentAsync_YieldsTheCurrentStateFirst()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult("planner", "1.0.0"));

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        NacosAgentDiscoveryResult? state = await watch.NextAsync(Timeout);

        // The current state is published first, so a consumer initialises from the same path that
        // handles later changes instead of one path for the present and one for the future.
        state.ShouldNotBeNull();
        state.AgentName.ShouldBe("planner");
        state.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task WatchAgentAsync_SendsTheWatchWithoutAFingerprint()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        server.AgentSubscribes.Count.ShouldBe(1);

        AgentSubscribeRpcRequest sent = server.AgentSubscribes[0];
        sent.DiscoveryRequest!.NamespaceId.ShouldBe("public");
        sent.DiscoveryRequest.Reference!.AgentName.ShouldBe("planner");
        sent.ClientWatchId.ShouldNotBeNullOrEmpty();

        // Only the server decides what a change is, so the client cannot claim a fingerprint it has
        // never been given. Sending none is what asks the server to state the current one.
        sent.MaterializedFingerprint.ShouldBeNull();
    }

    [Fact]
    public async Task WatchAgentAsync_YieldsTheStateAgainWhenTheServerHintsAChange()
    {
        var server = WatchServer();

        server.OnAgentDiscovery = _ => server.AgentDiscoveries.Count <= 1
            ? AiTest.AgentDiscovery(AiTest.AgentResult("planner", "1.0.0", "before"))
            : AiTest.AgentDiscovery(AiTest.AgentResult("planner", "2.0.0", "after"));

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        NacosAgentDiscoveryResult? first = await watch.NextAsync(Timeout);
        first!.Description.ShouldBe("before");

        server.PushAgentHint(
            WatchKey(server),
            NacosAiWatchEvent.Invalidate,
            "fp-2");

        NacosAgentDiscoveryResult? second = await watch.NextAsync(Timeout);

        second.ShouldNotBeNull();
        second.Version.ShouldBe("2.0.0");
        second.Description.ShouldBe("after");
    }

    [Fact]
    public async Task WatchAgentAsync_IgnoresAHintThatCarriesTheFingerprintItAlreadyHolds()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        // The subscription answer reported the state the client now holds, so a hint carrying that
        // same fingerprint says nothing changed and must not cost a read.
        server.PushAgentHint(WatchKey(server), NacosAiWatchEvent.Invalidate, "fp-initial");

        await Task.Delay(200, TestContext.Current.CancellationToken);

        server.AgentDiscoveries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task WatchAgentAsync_ReportsAnAgentThatIsNotPublishedAsUnavailable()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscoveryFailure(404, "Agent not found: planner");

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        // An agent that is not published yet is a state worth reporting — the server pushes a hint
        // when it appears — rather than a failure that ends the watch.
        NacosAgentDiscoveryResult? state = await watch.NextAsync(Timeout);

        state.ShouldBeNull();
    }

    [Fact]
    public async Task WatchAgentAsync_AcknowledgesAHintItAccepted()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        server.PushAgentHint(WatchKey(server), NacosAiWatchEvent.Revalidate, "fp-2");

        await AiTestServer.WaitForAsync(() => LastHintReply(server) is not null);

        AgentDiscoveryNotifyResponse reply = LastHintReply(server)!;
        reply.WatchKey.ShouldBe(WatchKey(server));
        reply.Accepted.ShouldBeTrue();
    }

    [Fact]
    public async Task WatchAgentAsync_DeclinesAHintForAWatchItDoesNotOwn()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        // A hint for a watch that is not registered — a removal and a push crossing on the wire —
        // is declined, which is how the server is told to stop sending it.
        server.PushAgentHint("watch-nobody", NacosAiWatchEvent.Invalidate, "fp-9");

        await AiTestServer.WaitForAsync(() => LastHintReply(server) is not null);

        AgentDiscoveryNotifyResponse reply = LastHintReply(server)!;
        reply.WatchKey.ShouldBe("watch-nobody");
        reply.Accepted.ShouldBeFalse();
        reply.ResultCode.ShouldBe(NacosResponse.FailureCode);
    }

    [Fact]
    public async Task WatchAgentAsync_EndsWhenTheServerTerminatesTheWatchWithAReason()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        server.PushAgentHint(WatchKey(server), NacosAiWatchEvent.Terminated, errorCode: 50105);

        // The server has decided the watch is over, so the enumeration ends with the reason rather
        // than parking on a state that will never arrive.
        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => watch.NextAsync(Timeout));

        exception.ErrorCode.ShouldBe(50105);
    }

    [Fact]
    public async Task WatchAgentAsync_ReportsAGoneAgentAsUnavailableAndKeepsWatching()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        string watchKey = WatchKey(server);

        // A deletion is a state rather than the end of the interest in the agent: the watch is offered
        // again, so the caller sees the agent come back instead of having to start over.
        server.PushAgentHint(watchKey, NacosAiWatchEvent.Terminated, errorCode: 50100);

        (await watch.NextAsync(Timeout)).ShouldBeNull();

        await AiTestServer.WaitForAsync(() => server.AgentSubscribes.Count == 2);
    }

    [Fact]
    public async Task WatchAgentAsync_ReportsAnAgentThatIsNotPublishedWhenTheWatchIsRefused()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());
        server.OnAgentSubscribe = _ => AiTest.AgentWatchRefused(50100, "Agent not found: planner");

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        // A watch cannot be installed on an agent that does not exist, but the caller is told the
        // agent is not served rather than left waiting for a first element that never comes.
        (await watch.NextAsync(Timeout)).ShouldBeNull();
    }

    [Fact]
    public async Task WatchAgentAsync_ReportsAnAbsentAgentUnderTheCodeARealServerAnswersWith()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        // The gRPC error mapper reports the v2 business code rather than the status, so this is what
        // an absent agent actually arrives as. The watch has to read it as "not published" for an
        // agent added later to be seen.
        server.OnAgentSubscribe = _ => AiTest.AgentWatchRefused(20004, "resource not found");

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        (await watch.NextAsync(Timeout)).ShouldBeNull();
    }

    [Fact]
    public async Task WatchAgentAsync_DoesNotRepeatAnAbsenceItAlreadyReported()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());
        server.OnAgentSubscribe = _ => AiTest.AgentWatchRefused(20004, "resource not found");

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        (await watch.NextAsync(Timeout)).ShouldBeNull();

        // The watch is offered again while the agent is away, and every offer is refused the same way.
        // Reporting the absence each time would give a caller that acts on every element a stream of
        // the same news and hide the state it is waiting for.
        await AiTestServer.WaitForAsync(() => server.AgentSubscribes.Count >= 3);

        await Task.Delay(200, TestContext.Current.CancellationToken);

        watch.TryNext().ShouldBeFalse();
    }

    [Fact]
    public async Task WatchAgentAsync_EndsWhenTheWatchIsRefusedForAResourceItHasNoRightTo()
    {
        var server = WatchServer();
        server.OnAgentSubscribe = _ => AiTest.AgentWatchRefused(403, "No right to read the agent.");

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        // A refusal that asking again cannot change is reported once, rather than retried quietly
        // until the caller gives up on a watch that was never going to work.
        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => watch.NextAsync(Timeout));

        exception.ErrorCode.ShouldBe(403);
        server.AgentSubscribes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task WatchAgentAsync_DoesNotRepeatTheStateWhenARevalidationFindsTheSameAnswer()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult("planner", "1.0.0", "same"));

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        // A revalidation carries no state, so the client has to read to learn what the server could
        // not say; a read that finds the same answer has nothing for the caller to act on.
        server.PushAgentHint(WatchKey(server), NacosAiWatchEvent.Revalidate);

        await AiTestServer.WaitForAsync(() => server.AgentDiscoveries.Count == 2);

        await Task.Delay(200, TestContext.Current.CancellationToken);

        watch.TryNext().ShouldBeFalse();
    }

    [Fact]
    public async Task WatchAgentAsync_DeclinesATerminationThatStatesNoReason()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        // A termination with no reason cannot be told apart from a malformed hint, so it is declined
        // and the watch is left running rather than being ended by something unexplained.
        server.PushAgentHint(WatchKey(server), NacosAiWatchEvent.Terminated);

        await AiTestServer.WaitForAsync(() => LastHintReply(server) is not null);

        LastHintReply(server)!.Accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task WatchAgentAsync_SharesOneWatchBetweenCallersFollowingTheSameAgent()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var first = new WatchSubscription(Watch(client, "planner"));
        await using var second = new WatchSubscription(Watch(client, "planner"));

        await first.NextAsync(Timeout);
        await second.NextAsync(Timeout);

        // Two callers asking the same question are one watch with two readers, because the wire
        // watch is what costs the server, not the reading of it.
        server.AgentSubscribes.Count.ShouldBe(1);
        server.AgentDiscoveries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task WatchAgentAsync_RemovesTheWatchWhenTheLastCallerStops()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        string watchKey = WatchKey(server);

        await watch.DisposeAsync();

        await AiTestServer.WaitForAsync(() => server.AgentUnsubscribes.Count == 1);

        server.AgentUnsubscribes[0].WatchKey.ShouldBe(watchKey);
    }

    [Fact]
    public async Task WatchAgentAsync_ReportsAServerWithoutTheWatchAbilityAsNotImplementingIt()
    {
        var server = WatchServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAiServerAbility.RadV1] = true,
        });

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => watch.NextAsync(Timeout));

        exception.ErrorCode.ShouldBe(501);

        // A server that cannot take the hint push is refused rather than watched silently: hints
        // that are never delivered would make the watch stop reporting changes without saying so.
        server.AgentSubscribes.ShouldBeEmpty();
    }

    [Fact]
    public async Task WatchAgentAsync_ReportsAServerThatRefusesTheWatchAbilityAsNotImplementingIt()
    {
        var server = WatchServer(new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAiServerAbility.RadV1] = true,
            [NacosAiServerAbility.RadWatchV1] = false,
        });

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            () => watch.NextAsync(Timeout));

        exception.ErrorCode.ShouldBe(501);
        server.AgentSubscribes.ShouldBeEmpty();
    }

    [Fact]
    public async Task WatchAgentAsync_ReinstallsTheWatchAfterAReconnect()
    {
        var server = WatchServer();

        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(
            AiTest.AgentResult("planner", "1.0.0", server.AgentDiscoveries.Count <= 1 ? "before" : "after"));

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        await server.DropAndReconnectAsync();

        await AiTestServer.WaitForAsync(() => server.AgentSubscribes.Count == 2);

        // The watch died with the connection, so it is installed again — and the state is read again
        // rather than assumed, because it may have moved on while the client was disconnected.
        await AiTestServer.WaitForAsync(() => server.AgentDiscoveries.Count == 2);

        NacosAgentDiscoveryResult? state = await watch.NextAsync(Timeout);
        state!.Description.ShouldBe("after");
    }

    [Fact]
    public async Task WatchAgentAsync_AdvertisesTheWatchAbilityToTheServer()
    {
        var server = WatchServer();
        server.OnAgentDiscovery = _ => AiTest.AgentDiscovery(AiTest.AgentResult());

        await using NacosAiClient client = server.CreateClient(FastOptions());

        await using var watch = new WatchSubscription(Watch(client, "planner"));

        await watch.NextAsync(Timeout);

        // The server refuses to bind a watch on a connection that does not claim it can take a hint,
        // so the flag is what makes the subscription possible rather than an extra.
        ConnectionSetupRequest setup = PayloadCodec.Decode<ConnectionSetupRequest>(server.Stream.Written[0])!;
        setup.AbilityTable.ShouldNotBeNull();
        setup.AbilityTable!["radWatchV1"].ShouldBeTrue();
    }

    /// <summary>Builds the watch key the scripted server issues to the first subscriber.</summary>
    /// <param name="server">The server.</param>
    /// <returns>The watch key.</returns>
    private static string WatchKey(AiTestServer server) =>
        string.Concat("watch-", server.AgentSubscribes[0].ClientWatchId);

    /// <summary>Reads the last reply the client sent for a pushed hint.</summary>
    /// <param name="server">The server.</param>
    /// <returns>The reply, or <see langword="null"/> when none was sent.</returns>
    private static AgentDiscoveryNotifyResponse? LastHintReply(AiTestServer server) =>
        server.Stream.Written
            .Where(payload => PayloadCodec.ReadTypeName(payload) == "AgentDiscoveryNotifyResponse")
            .Select(payload => PayloadCodec.Decode<AgentDiscoveryNotifyResponse>(payload)!)
            .LastOrDefault();

    /// <summary>
    /// Builds the options the watch tests run with.
    /// </summary>
    /// <returns>The options.</returns>
    /// <remarks>
    /// The retry interval doubles as the backoff base, so a short one keeps a retried read from
    /// slowing a test down.
    /// </remarks>
    private static NacosOptions FastOptions() => new()
    {
        ServerAddresses = ["127.0.0.1:8848"],
        AppName = "ai-tests",
        Ai = new NacosAiOptions { CacheRefreshInterval = TimeSpan.FromMilliseconds(20) },
    };

    /// <summary>
    /// Starts a watch that ends with the test.
    /// </summary>
    /// <param name="client">The AI client.</param>
    /// <param name="agentName">The agent name.</param>
    /// <returns>The watch.</returns>
    private static IAsyncEnumerable<NacosAgentDiscoveryResult?> Watch(NacosAiClient client, string agentName) =>
        client.Agent.WatchAgentAsync(
            new NacosAgentReference { AgentName = agentName },
            ct: TestContext.Current.CancellationToken);

    /// <summary>
    /// Builds a server that speaks the RAD contract and takes the watch hint push.
    /// </summary>
    /// <param name="abilities">The ability table to state instead, when a test is about negotiation.</param>
    /// <returns>The server.</returns>
    private static AiTestServer WatchServer(Dictionary<string, bool>? abilities = null) =>
        new(abilities ?? new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [NacosAiServerAbility.RadV1] = true,
            [NacosAiServerAbility.RadWatchV1] = true,
        });

    /// <summary>
    /// Runs a watch on a background pump so a test can wait for one state at a time.
    /// </summary>
    /// <remarks>
    /// The pump owns the enumerator, which is what makes waiting on a state that never comes safe:
    /// the pump can be cancelled and disposed as a unit instead of abandoning a move that is still
    /// in flight. A failure is held and re-raised on the reader's thread, so a watch the server
    /// cannot take is reported as the error it was rather than as a timeout.
    /// </remarks>
    private sealed class WatchSubscription : IAsyncDisposable
    {
        private readonly ChannelReader<NacosAgentDiscoveryResult?> _reader;
        private readonly CancellationTokenSource _cts = new();
        private Exception? _error;

        public WatchSubscription(IAsyncEnumerable<NacosAgentDiscoveryResult?> source)
        {
            var queue = Channel.CreateUnbounded<NacosAgentDiscoveryResult?>();
            _reader = queue.Reader;

            Completion = Task.Run(async () =>
            {
                try
                {
                    await foreach (NacosAgentDiscoveryResult? state in source.WithCancellation(_cts.Token))
                    {
                        queue.Writer.TryWrite(state);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when the watch is disposed.
                }
                catch (Exception ex)
                {
                    _error = ex;
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
        /// <returns>The state, or <see langword="null"/> when the agent is not served.</returns>
        /// <exception cref="TimeoutException">No state arrived in time.</exception>
        public async Task<NacosAgentDiscoveryResult?> NextAsync(TimeSpan timeout)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);

            timeoutSource.CancelAfter(timeout);

            try
            {
                return await _reader.ReadAsync(timeoutSource.Token);
            }
            catch (ChannelClosedException) when (_error is not null)
            {
                ExceptionDispatchInfo.Capture(_error).Throw();
                throw;
            }
            catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The expected state did not arrive in time.");
            }
        }

        /// <summary>
        /// Reports whether a state is already waiting, without waiting for one.
        /// </summary>
        /// <returns><see langword="true"/> when a state was ready to be read.</returns>
        public bool TryNext() => _reader.TryRead(out _);

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
