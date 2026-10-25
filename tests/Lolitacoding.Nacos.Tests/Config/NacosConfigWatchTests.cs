using System.Text.Json;
using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Lolitacoding.Nacos.Tests.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Config;

/// <summary>
/// Verifies the configuration fuzzy watch against a scripted in-memory server.
/// </summary>
/// <remarks>
/// The watch is a state machine over three inputs: the client's own registration request, the
/// initial diff the server pushes, and the per-configuration change pushes that follow. All three
/// are exercised here; the real server's decision to push is covered by the integration tests.
/// </remarks>
public sealed class NacosConfigWatchTests
{
    private const string Group = "DEFAULT_GROUP";
    private const string Pattern = "public>>DEFAULT_GROUP>>app*";

    /// <summary>The longest a test waits for one pushed event.</summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Watch_RegistersThePatternAndReportsTheInitialDiff()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events = harness.Watch("app*", Group);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "ConfigFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        using (JsonDocument body = harness.RequestBody("ConfigFuzzyWatchRequest", 0))
        {
            JsonElement root = body.RootElement;

            root.GetProperty("groupKeyPattern").GetString().ShouldBe(Pattern);
            root.GetProperty("watchType").GetString().ShouldBe(FuzzyWatchProtocol.Watch);
            // The client has nothing yet, so the server must answer with the full set.
            root.GetProperty("initializing").GetBoolean().ShouldBeTrue();
            root.GetProperty("receivedGroupKeys").GetArrayLength().ShouldBe(0);
        }

        harness.Push(new ConfigFuzzyWatchSyncRequest
        {
            GroupKeyPattern = Pattern,
            SyncType = FuzzyWatchProtocol.InitNotify,
            TotalBatch = 1,
            CurrentBatch = 1,
            Contexts =
            [
                Add("app.one+DEFAULT_GROUP+public"),
                Add("app.two+DEFAULT_GROUP+public"),
            ],
        });

        await NextAsync(pending);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        events.Current.Key!.DataId.ShouldBe("app.one");
        events.Current.GroupKeyPattern.ShouldBe(Pattern);

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        events.Current.Key!.DataId.ShouldBe("app.two");

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.InitialSyncCompleted);
        events.Current.Key.ShouldBeNull();
    }

    [Fact]
    public async Task Watch_CompletesTheInitialSyncWhenTheServerHasNothingToDiff()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events = harness.Watch("app*", Group);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "ConfigFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        // An empty initial diff is ended by the finish marker alone.
        harness.Push(Finish(Pattern));

        await NextAsync(pending);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.InitialSyncCompleted);
    }

    [Fact]
    public async Task Watch_CompletesOnTheLastInitialBatchWhenNoFinishArrives()
    {
        // The last batch ends the initial phase on its own, so a consumer does not have to wait for
        // the finish marker the server sends once the batch has been acknowledged.
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events = harness.Watch("app*", Group);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "ConfigFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        harness.Push(Batch(Pattern, total: 2, current: 1, Add("app.one+DEFAULT_GROUP+public")));

        await NextAsync(pending);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);

        harness.Push(Batch(Pattern, total: 2, current: 2, Add("app.two+DEFAULT_GROUP+public")));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        events.Current.Key!.DataId.ShouldBe("app.two");

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.InitialSyncCompleted);
    }

    [Fact]
    public async Task Watch_ReportsResourceChangesAfterTheInitialSync()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events =
            await harness.StartInitializedWatchAsync("app.one+DEFAULT_GROUP+public");

        harness.Push(Change("app.two+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.AddConfig));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        events.Current.Key!.DataId.ShouldBe("app.two");

        // A repeated add for a key the subscription already knows must not be delivered again.
        harness.Push(Change("app.two+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.AddConfig));
        harness.Push(Change("app.one+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.ConfigChanged));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Modified);
        events.Current.Key!.DataId.ShouldBe("app.one");

        // A repeated delete for a key already removed must not be delivered either.
        harness.Push(Change("app.one+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.DeleteConfig));
        harness.Push(Change("app.one+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.DeleteConfig));
        harness.Push(Change("app.three+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.AddConfig));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Removed);
        events.Current.Key!.DataId.ShouldBe("app.one");

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        events.Current.Key!.DataId.ShouldBe("app.three");
    }

    [Fact]
    public async Task Watch_DerivesAddAndModifyFromAChangeThatTheServerCannotClassify()
    {
        // A configuration created after the initial sync reaches the client as CONFIG_CHANGED: the
        // server only knows that the content changed, not whether the key is new. Reporting it as a
        // modification would leave the key out of the set, which in turn would suppress its removal.
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events =
            await harness.StartInitializedWatchAsync("app.one+DEFAULT_GROUP+public");

        harness.Push(Change("app.two+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.ConfigChanged));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        events.Current.Key!.DataId.ShouldBe("app.two");

        // Once the key is known the same notification maps to a modification. A server does not send
        // it — it reports membership, not content — but a push it did send must not be dropped.
        harness.Push(Change("app.two+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.ConfigChanged));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Modified);
        events.Current.Key!.DataId.ShouldBe("app.two");

        harness.Push(Change("app.two+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.DeleteConfig));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Removed);
        events.Current.Key!.DataId.ShouldBe("app.two");
    }

    [Fact]
    public void Options_RejectANonPositiveResyncInterval()
    {
        // The interval is the bound on how long a lost push can leave the set wrong, so a zero or
        // negative value would either spin the timer or never repair anything.
        var options = new NacosConfigOptions { FuzzyWatchResyncInterval = TimeSpan.Zero };

        Should.Throw<NacosConfigurationException>(options.Validate);
    }

    [Fact]
    public async Task Watch_SeedsALateSubscriberFromTheSetAlreadyKnown()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> first =
            await harness.StartInitializedWatchAsync("app.one+DEFAULT_GROUP+public");

        await using IAsyncEnumerator<NacosConfigWatchEvent> second = harness.Watch("app*", Group);

        // A subscriber that joins an initialised watch is handed the current set, so it does not
        // have to wait for a change to learn what already matches.
        await NextAsync(second);
        second.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        second.Current.Key!.DataId.ShouldBe("app.one");

        await NextAsync(second);
        second.Current.Kind.ShouldBe(NacosConfigWatchKind.InitialSyncCompleted);

        // The second subscriber shares the existing registration instead of adding another one.
        harness.RequestsOfType("ConfigFuzzyWatchRequest").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Watch_ReRegistersTheDiffAfterAReconnect()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events =
            await harness.StartInitializedWatchAsync("app.one+DEFAULT_GROUP+public");

        await harness.DropAndReconnectAsync(TestContext.Current.CancellationToken);
        await harness.WaitForRequestsAsync(
            "ConfigFuzzyWatchRequest", 2, TestContext.Current.CancellationToken);

        using JsonDocument body = harness.RequestBody("ConfigFuzzyWatchRequest", 1);
        JsonElement root = body.RootElement;

        root.GetProperty("watchType").GetString().ShouldBe(FuzzyWatchProtocol.Watch);
        // The registration is already initialised, so the replay asks for a diff against the set
        // the client still holds rather than for a second initialisation.
        root.GetProperty("initializing").GetBoolean().ShouldBeFalse();

        string[] known = [.. root.GetProperty("receivedGroupKeys").EnumerateArray()
            .Select(item => item.GetString()!)];
        known.ShouldBe(["app.one+DEFAULT_GROUP+public"]);

        // The subscription is not interrupted by the reconnect and still receives pushes.
        harness.Push(Change("app.two+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.AddConfig));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        events.Current.Key!.DataId.ShouldBe("app.two");
    }

    [Fact]
    public async Task Watch_ReplaysAnUninitialisedWatchAsAnInitialisation()
    {
        // A registration that never finished its first catch-up cannot ask for a diff: a diff sync
        // carries no finish marker, so the subscription would wait for one forever. It asks for a
        // fresh initialisation instead, which is what makes the reconnect recover the phase.
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events = harness.Watch("app*", Group);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "ConfigFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        await harness.DropAndReconnectAsync(TestContext.Current.CancellationToken);
        await harness.WaitForRequestsAsync(
            "ConfigFuzzyWatchRequest", 2, TestContext.Current.CancellationToken);

        using JsonDocument body = harness.RequestBody("ConfigFuzzyWatchRequest", 1);
        body.RootElement.GetProperty("initializing").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("receivedGroupKeys").GetArrayLength().ShouldBe(0);

        harness.Push(Batch(Pattern, total: 1, current: 1, Add("app.one+DEFAULT_GROUP+public")));

        await NextAsync(pending);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        events.Current.Key!.DataId.ShouldBe("app.one");

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.InitialSyncCompleted);
    }

    [Fact]
    public async Task Watch_IgnoresTheFinishMarkerThatFollowsTheLastBatch()
    {
        // The server sends the finish marker after the last initial batch has been acknowledged, so
        // a non-empty catch-up sees both. The marker must not become a second InitialSyncCompleted:
        // that would tell a caller its set was complete twice.
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events =
            await harness.StartInitializedWatchAsync("app.one+DEFAULT_GROUP+public");

        harness.Push(Finish(Pattern));
        harness.Push(Change("app.two+DEFAULT_GROUP+public", ConfigFuzzyWatchChangeType.AddConfig));

        // The next event is the change, not a repeated marker.
        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        events.Current.Key!.DataId.ShouldBe("app.two");
    }

    [Fact]
    public async Task Watch_CancelsTheServerSideWatchWhenTheLastSubscriberLeaves()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        IAsyncEnumerator<NacosConfigWatchEvent> events =
            await harness.StartInitializedWatchAsync("app.one+DEFAULT_GROUP+public");

        await events.DisposeAsync();
        await harness.WaitForRequestsAsync(
            "ConfigFuzzyWatchRequest", 2, TestContext.Current.CancellationToken);

        using JsonDocument body = harness.RequestBody("ConfigFuzzyWatchRequest", 1);
        body.RootElement.GetProperty("watchType").GetString()
            .ShouldBe(FuzzyWatchProtocol.CancelWatch);
    }

    [Fact]
    public async Task Watch_RefusesAServerThatStatesItHasNoFuzzyWatch()
    {
        await using WatchHarness harness = new(abilities: new Dictionary<string, bool>
        {
            ["fuzzyWatch"] = false,
        });
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events = harness.Watch("app*", Group);

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            async () => await events.MoveNextAsync().AsTask()
                .WaitAsync(EventTimeout, TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(501);
        harness.RequestsOfType("ConfigFuzzyWatchRequest").ShouldBeEmpty();
    }

    [Fact]
    public async Task Watch_ConnectsBeforeItChecksWhetherTheServerHasFuzzyWatch()
    {
        // The ability table arrives with the handshake, so a client that has never connected has
        // nothing to check against. The watch has to establish the connection first, otherwise a
        // server that states fuzzyWatch=false would still receive a watch request.
        await using WatchHarness harness = new(abilities: new Dictionary<string, bool>
        {
            ["fuzzyWatch"] = false,
        });

        await using IAsyncEnumerator<NacosConfigWatchEvent> events = harness.Watch("app*", Group);

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            async () => await events.MoveNextAsync().AsTask()
                .WaitAsync(EventTimeout, TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(501);
        harness.RequestsOfType("ConfigFuzzyWatchRequest").ShouldBeEmpty();
    }

    [Fact]
    public async Task Watch_IgnoresAMalformedGroupKeyWithoutLosingTheRestOfTheBatch()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events = harness.Watch("app*", Group);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "ConfigFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        harness.Push(new ConfigFuzzyWatchSyncRequest
        {
            GroupKeyPattern = Pattern,
            SyncType = FuzzyWatchProtocol.InitNotify,
            TotalBatch = 1,
            CurrentBatch = 1,
            Contexts =
            [
                // Four segments cannot have been produced by the codec.
                Add("app.one+DEFAULT_GROUP+public+extra"),
                Add("app.two+DEFAULT_GROUP+public"),
            ],
        });

        await NextAsync(pending);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);
        events.Current.Key!.DataId.ShouldBe("app.two");

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosConfigWatchKind.InitialSyncCompleted);
    }

    [Fact]
    public async Task Watch_ResynchronisesOnTheConfiguredInterval()
    {
        await using WatchHarness harness = new(resyncInterval: TimeSpan.FromMilliseconds(50));
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events =
            await harness.StartInitializedWatchAsync("app.one+DEFAULT_GROUP+public");

        await harness.WaitForRequestsAsync(
            "ConfigFuzzyWatchRequest", 2, TestContext.Current.CancellationToken);

        using JsonDocument body = harness.RequestBody("ConfigFuzzyWatchRequest", 1);
        body.RootElement.GetProperty("initializing").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Watch_GroupOnlyOverloadWatchesEveryDataIdInTheGroup()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosConfigWatchEvent> events =
            harness.Client.WatchAsync(Group, TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "ConfigFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        using (JsonDocument body = harness.RequestBody("ConfigFuzzyWatchRequest", 0))
        {
            body.RootElement.GetProperty("groupKeyPattern").GetString()
                .ShouldBe("public>>DEFAULT_GROUP>>*");
        }

        // Finish the enumeration so the pending read completes before disposal.
        harness.Push(Finish("public>>DEFAULT_GROUP>>*"));
        await NextAsync(pending);
    }

    /// <summary>Builds an add entry for an initial diff.</summary>
    /// <param name="groupKey">The wire group key.</param>
    /// <returns>The context.</returns>
    private static ConfigFuzzyWatchContext Add(string groupKey) => new()
    {
        GroupKey = groupKey,
        ChangedType = ConfigFuzzyWatchChangeType.AddConfig,
    };

    /// <summary>Builds one initial-diff batch.</summary>
    /// <param name="pattern">The pattern the batch belongs to.</param>
    /// <param name="total">The number of batches in the sync.</param>
    /// <param name="current">The one-based index of this batch.</param>
    /// <param name="contexts">The entries in the batch.</param>
    /// <returns>The push.</returns>
    private static ConfigFuzzyWatchSyncRequest Batch(
        string pattern,
        int total,
        int current,
        params ConfigFuzzyWatchContext[] contexts) => new()
        {
            GroupKeyPattern = pattern,
            SyncType = FuzzyWatchProtocol.InitNotify,
            TotalBatch = total,
            CurrentBatch = current,
            Contexts = [.. contexts],
        };

    /// <summary>Builds the finish marker that ends an empty initial sync.</summary>
    /// <param name="pattern">The pattern the marker belongs to.</param>
    /// <returns>The push.</returns>
    private static ConfigFuzzyWatchSyncRequest Finish(string pattern) => new()
    {
        GroupKeyPattern = pattern,
        SyncType = FuzzyWatchProtocol.FinishInitNotify,
    };

    /// <summary>Builds a single-configuration change push.</summary>
    /// <param name="groupKey">The wire group key.</param>
    /// <param name="changeType">The change type.</param>
    /// <returns>The push.</returns>
    private static ConfigFuzzyWatchChangeNotifyRequest Change(string groupKey, string changeType) => new()
    {
        GroupKey = groupKey,
        ChangeType = changeType,
    };

    /// <summary>
    /// Advances an enumeration, failing the test instead of hanging when no event arrives.
    /// </summary>
    /// <param name="events">The enumerator to advance.</param>
    /// <returns>A task that completes once the enumerator moved.</returns>
    private static Task NextAsync(IAsyncEnumerator<NacosConfigWatchEvent> events) =>
        NextAsync(events.MoveNextAsync().AsTask());

    /// <summary>
    /// Waits for a pending read, failing the test instead of hanging when no event arrives.
    /// </summary>
    /// <param name="pending">The pending read.</param>
    /// <returns>A task that completes once the read finished.</returns>
    private static async Task NextAsync(Task<bool> pending) =>
        (await pending.WaitAsync(EventTimeout, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

    /// <summary>
    /// Wires a config client to a fake transport and drives fuzzy watch pushes.
    /// </summary>
    private sealed class WatchHarness : IAsyncDisposable
    {
        private readonly List<FakeNacosChannel> _channels = [];
        private readonly Queue<FakeNacosDuplexStream> _streams;
        private readonly Dictionary<string, bool> _abilities;
        private readonly string _snapshotRoot;

        private FakeNacosDuplexStream _currentStream = null!;

        public WatchHarness(
            Dictionary<string, bool>? abilities = null,
            TimeSpan? resyncInterval = null)
        {
            _snapshotRoot = TestPaths.CreateScratchDirectory("config-watch");
            _abilities = abilities ?? [];

            // A supply of streams so a test can drop one and let the reconnect replace it.
            _streams = new Queue<FakeNacosDuplexStream>(
                Enumerable.Range(0, 4).Select(_ => new FakeNacosDuplexStream
                {
                    SetupAckAbilityTable = _abilities,
                }));

            var options = new NacosOptions
            {
                ServerAddresses = ["127.0.0.1:8848"],
                Namespace = "public",
                Config = new NacosConfigOptions
                {
                    FuzzyWatchResyncInterval = resyncInterval ?? TimeSpan.FromMinutes(3),
                },
                Reconnect = new NacosReconnectOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    MaxDelay = TimeSpan.FromMilliseconds(2),
                    JitterFactor = 0,
                },
            };

            var factory = new FakeNacosChannelFactory(address =>
            {
                var channel = new FakeNacosChannel(
                    address,
                    request => PayloadCodec.ReadTypeName(request) switch
                    {
                        "ServerCheckRequest" => FakeServerCheck.Responding()(request),
                        "ConfigFuzzyWatchRequest" => PayloadCodec.Encode(new ConfigFuzzyWatchResponse()),
                        _ => throw new InvalidOperationException(
                            $"Unexpected request '{PayloadCodec.ReadTypeName(request)}'."),
                    },
                    NextStream());

                _channels.Add(channel);
                return channel;
            });

            Connection = new NacosGrpcConnection(
                options,
                NacosConnectionModule.Config,
                [NacosServerAddress.Parse("127.0.0.1:8848")],
                factory,
                TimeProvider.System,
                NullLogger.Instance);

            Client = new NacosConfigClient(
                options,
                Connection,
                TimeProvider.System,
                NullLogger<NacosConfigClient>.Instance,
                snapshots: null);

            // In production the factory owns this routing. Wiring it here keeps the push handling
            // in the client under test rather than standing in for it with a fake client.
            Connection.ServerRequestReceived += OnServerRequestAsync;
        }

        public NacosConfigClient Client { get; }

        public NacosGrpcConnection Connection { get; }

        public Task ConnectAsync(CancellationToken ct) => Connection.ConnectAsync(ct);

        /// <summary>Starts a watch and leaves the first read pending.</summary>
        /// <param name="dataIdPattern">The data id pattern.</param>
        /// <param name="groupPattern">The group pattern.</param>
        /// <returns>The enumerator, positioned before its first event.</returns>
        public IAsyncEnumerator<NacosConfigWatchEvent> Watch(string dataIdPattern, string groupPattern) =>
            Client.WatchAsync(dataIdPattern, groupPattern, TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        /// <summary>
        /// Starts a watch, completes its initial sync with one matching key, and returns it ready
        /// for change pushes.
        /// </summary>
        /// <param name="groupKey">The group key the server reports as matching.</param>
        /// <returns>The enumerator, positioned after the completion marker.</returns>
        public async Task<IAsyncEnumerator<NacosConfigWatchEvent>> StartInitializedWatchAsync(
            string groupKey)
        {
            IAsyncEnumerator<NacosConfigWatchEvent> events = Watch("app*", Group);

            Task<bool> pending = events.MoveNextAsync().AsTask();
            await WaitForRequestsAsync(
                "ConfigFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

            Push(Batch(Pattern, total: 1, current: 1, Add(groupKey)));

            await NextAsync(pending);
            events.Current.Kind.ShouldBe(NacosConfigWatchKind.Added);

            await NextAsync(events);
            events.Current.Kind.ShouldBe(NacosConfigWatchKind.InitialSyncCompleted);

            return events;
        }

        /// <summary>Pushes a request to the transport, as a server would.</summary>
        /// <param name="request">The request to push.</param>
        public void Push(NacosRequest request) => _currentStream.Push(TestPayload.ForRequest(request));

        /// <summary>Gets every unary request of a given type, in order.</summary>
        /// <param name="typeName">The payload type name.</param>
        /// <returns>The matching requests.</returns>
        public List<Payload> RequestsOfType(string typeName) =>
            [.. _channels
                .SelectMany(channel => channel.UnaryRequests)
                .Where(payload => PayloadCodec.ReadTypeName(payload) == typeName)];

        /// <summary>Gets the body of one unary request of a given type.</summary>
        /// <param name="typeName">The payload type name.</param>
        /// <param name="index">The zero-based index within the matching requests.</param>
        /// <returns>The parsed JSON body.</returns>
        public JsonDocument RequestBody(string typeName, int index) =>
            JsonDocument.Parse(PayloadCodec.ReadBodyBytes(RequestsOfType(typeName)[index]));

        /// <summary>Waits until a given number of requests of a type have been sent.</summary>
        /// <param name="typeName">The payload type name.</param>
        /// <param name="count">The number expected.</param>
        /// <param name="ct">A token that cancels the wait.</param>
        /// <returns>A task that completes once the count was reached.</returns>
        public async Task WaitForRequestsAsync(string typeName, int count, CancellationToken ct)
        {
            for (int i = 0; i < 1000; i++)
            {
                if (RequestsOfType(typeName).Count >= count)
                {
                    return;
                }

                await Task.Delay(10, ct);
            }

            throw new TimeoutException(
                $"Expected {count} '{typeName}' request(s) but the client sent "
                + $"{RequestsOfType(typeName).Count}.");
        }

        /// <summary>Drops the current connection and waits for the reconnect to replace it.</summary>
        /// <param name="ct">A token that cancels the wait.</param>
        /// <returns>A task that completes once a fresh channel is in use.</returns>
        public async Task DropAndReconnectAsync(CancellationToken ct)
        {
            int channelsBefore = _channels.Count;
            _currentStream.EndStream();

            await WaitForAsync(() => _channels.Count > channelsBefore, ct);
            await WaitForAsync(() => _channels[^1].UnaryRequests.Count >= 1, ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            await Connection.DisposeAsync().ConfigureAwait(false);

            TestPaths.TryDelete(_snapshotRoot);
        }

        private FakeNacosDuplexStream NextStream()
        {
            _currentStream = _streams.Count > 0
                ? _streams.Dequeue()
                : new FakeNacosDuplexStream { SetupAckAbilityTable = _abilities };

            return _currentStream;
        }

        /// <summary>
        /// Routes a pushed request to the client, as the factory does for a real connection.
        /// </summary>
        /// <param name="request">The pushed request.</param>
        /// <param name="ct">A token that cancels the work.</param>
        /// <returns>The acknowledgement the server expects.</returns>
        private Task<NacosResponse?> OnServerRequestAsync(NacosRequest request, CancellationToken ct)
        {
            switch (request)
            {
                case ConfigFuzzyWatchSyncRequest sync:
                    Client.OnFuzzyWatchSync(sync);
                    return Task.FromResult<NacosResponse?>(
                        new ConfigFuzzyWatchSyncResponse { RequestId = sync.RequestId });

                case ConfigFuzzyWatchChangeNotifyRequest change:
                    Client.OnFuzzyWatchChange(change);
                    return Task.FromResult<NacosResponse?>(
                        new ConfigFuzzyWatchChangeNotifyResponse { RequestId = change.RequestId });

                default:
                    return Task.FromResult<NacosResponse?>(null);
            }
        }

        private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
        {
            for (int i = 0; i < 1000; i++)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10, ct);
            }

            throw new TimeoutException("The expected condition was not met within the timeout.");
        }
    }
}
