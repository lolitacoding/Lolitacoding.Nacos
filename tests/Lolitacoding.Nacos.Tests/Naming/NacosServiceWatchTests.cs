using System.Text.Json;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Naming;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Lolitacoding.Nacos.Tests.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Naming;

/// <summary>
/// Verifies the naming fuzzy watch against a scripted in-memory server.
/// </summary>
/// <remarks>
/// The watch is a state machine over three inputs: the client's own registration request, the
/// initial diff the server pushes, and the per-service change pushes that follow. All three are
/// exercised here; the real server's decision to push is covered by the integration tests.
/// </remarks>
public sealed class NacosServiceWatchTests
{
    private const string Group = "DEFAULT_GROUP";
    private const string Pattern = "public>>DEFAULT_GROUP>>app*";
    private const string One = "public@@DEFAULT_GROUP@@app.one";
    private const string Two = "public@@DEFAULT_GROUP@@app.two";

    /// <summary>The longest a test waits for one pushed event.</summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Watch_RegistersThePatternAndReportsTheInitialDiff()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events = harness.Watch("app*", Group);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "NamingFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        using (JsonDocument body = harness.RequestBody("NamingFuzzyWatchRequest", 0))
        {
            JsonElement root = body.RootElement;

            root.GetProperty("namespace").GetString().ShouldBe("public");
            root.GetProperty("groupKeyPattern").GetString().ShouldBe(Pattern);
            root.GetProperty("watchType").GetString().ShouldBe(FuzzyWatchProtocol.Watch);
            // The client has nothing yet, so the server must answer with the full set.
            root.GetProperty("initializing").GetBoolean().ShouldBeTrue();
            root.GetProperty("receivedGroupKeys").GetArrayLength().ShouldBe(0);
        }

        harness.Push(new NamingFuzzyWatchSyncRequest
        {
            GroupKeyPattern = Pattern,
            SyncType = FuzzyWatchProtocol.InitNotify,
            TotalBatch = 1,
            CurrentBatch = 1,
            Contexts = [Add(One), Add(Two)],
        });

        await NextAsync(pending);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.one");
        events.Current.Service.Namespace.ShouldBe("public");
        events.Current.GroupKeyPattern.ShouldBe(Pattern);

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.two");

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.InitialSyncCompleted);
        events.Current.Service.ShouldBeNull();
    }

    [Fact]
    public async Task Watch_CompletesTheInitialSyncWhenTheServerHasNothingToDiff()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events = harness.Watch("app*", Group);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "NamingFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        // An empty initial diff is ended by the finish marker alone.
        harness.Push(Finish(Pattern));

        await NextAsync(pending);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.InitialSyncCompleted);
    }

    [Fact]
    public async Task Watch_CompletesOnTheLastInitialBatchWhenNoFinishArrives()
    {
        // The last batch ends the initial phase on its own, so a consumer does not have to wait for
        // the finish marker the server sends once the batch has been acknowledged.
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events = harness.Watch("app*", Group);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "NamingFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        harness.Push(Batch(Pattern, total: 2, current: 1, Add(One)));

        await NextAsync(pending);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);

        // A batch that is not the last one must not end the phase: the rest of the diff is still on
        // its way, and a caller told its set was complete would use an incomplete one.
        harness.Push(Batch(Pattern, total: 2, current: 2, Add(Two)));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.two");

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.InitialSyncCompleted);
    }

    [Fact]
    public async Task Watch_ReportsMembershipChangesAfterTheInitialSync()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events =
            await harness.StartInitializedWatchAsync(One);

        harness.Push(Change(Two, NamingFuzzyWatchChangeType.AddService));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.two");

        // A repeated add for a service the subscription already knows must not be delivered again.
        harness.Push(Change(Two, NamingFuzzyWatchChangeType.AddService));
        harness.Push(Change(One, NamingFuzzyWatchChangeType.DeleteService));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Removed);
        events.Current.Service!.ServiceName.ShouldBe("app.one");

        // A repeated delete for a service already removed must not be delivered either.
        harness.Push(Change(One, NamingFuzzyWatchChangeType.DeleteService));
        harness.Push(Change("public@@DEFAULT_GROUP@@app.three", NamingFuzzyWatchChangeType.AddService));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.three");
    }

    [Fact]
    public async Task Watch_ReportsAnInstanceChangeWithoutChangingTheMatchedSet()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events =
            await harness.StartInitializedWatchAsync(One);

        // The server only pushes a change to the matched membership, so this is defensive: a known
        // service is reported as an instance change, and the later delete is still a removal.
        harness.Push(Change(One, NamingFuzzyWatchChangeType.InstanceChanged));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.InstancesChanged);
        events.Current.Service!.ServiceName.ShouldBe("app.one");

        harness.Push(Change(One, NamingFuzzyWatchChangeType.DeleteService));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Removed);
    }

    [Fact]
    public async Task Watch_TreatsAnInstanceChangeForAnUnknownServiceAsAnAdd()
    {
        // An instance change for a service the watch does not hold cannot be an update, so it is
        // reported as an add. Dropping it would leave the service out of the set, which in turn
        // would suppress its later removal.
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events =
            await harness.StartInitializedWatchAsync(One);

        harness.Push(Change(Two, NamingFuzzyWatchChangeType.InstanceChanged));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.two");

        harness.Push(Change(Two, NamingFuzzyWatchChangeType.DeleteService));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Removed);
    }

    [Fact]
    public async Task Watch_IgnoresAHeartBeat()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events =
            await harness.StartInitializedWatchAsync(One);

        harness.Push(Change(One, NamingFuzzyWatchChangeType.HeartBeat));
        harness.Push(Change(Two, NamingFuzzyWatchChangeType.AddService));

        // A heartbeat changes no membership, so the next event is the add rather than anything for
        // the service that beat.
        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.two");
    }

    [Fact]
    public void Options_RejectANonPositiveResyncInterval()
    {
        // The interval is the bound on how long a lost push can leave the set wrong, so a zero or
        // negative value would either spin the timer or never repair anything.
        var options = new NacosNamingOptions { FuzzyWatchResyncInterval = TimeSpan.Zero };

        Should.Throw<NacosConfigurationException>(options.Validate);
    }

    [Fact]
    public async Task Watch_SeedsALateSubscriberFromTheSetAlreadyKnown()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> first =
            await harness.StartInitializedWatchAsync(One);

        await using IAsyncEnumerator<NacosServiceWatchEvent> second = harness.Watch("app*", Group);

        // A subscriber that joins an initialised watch is handed the current set, so it does not
        // have to wait for a change to learn what already matches.
        await NextAsync(second);
        second.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        second.Current.Service!.ServiceName.ShouldBe("app.one");

        await NextAsync(second);
        second.Current.Kind.ShouldBe(NacosServiceWatchKind.InitialSyncCompleted);

        // The second subscriber shares the existing registration instead of adding another one.
        harness.RequestsOfType("NamingFuzzyWatchRequest").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Watch_ReRegistersTheDiffAfterAReconnect()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events =
            await harness.StartInitializedWatchAsync(One);

        await harness.DropAndReconnectAsync(TestContext.Current.CancellationToken);
        await harness.WaitForRequestsAsync(
            "NamingFuzzyWatchRequest", 2, TestContext.Current.CancellationToken);

        using JsonDocument body = harness.RequestBody("NamingFuzzyWatchRequest", 1);
        JsonElement root = body.RootElement;

        root.GetProperty("watchType").GetString().ShouldBe(FuzzyWatchProtocol.Watch);
        // The registration is already initialised, so the replay asks for a diff against the set
        // the client still holds rather than for a second initialisation.
        root.GetProperty("initializing").GetBoolean().ShouldBeFalse();

        string[] known = [.. root.GetProperty("receivedGroupKeys").EnumerateArray()
            .Select(item => item.GetString()!)];
        known.ShouldBe([One]);

        // The subscription is not interrupted by the reconnect and still receives pushes.
        harness.Push(Change(Two, NamingFuzzyWatchChangeType.AddService));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.two");
    }

    [Fact]
    public async Task Watch_IgnoresTheFinishMarkerThatFollowsTheLastBatch()
    {
        // The server sends the finish marker after the last initial batch has been acknowledged, so
        // a non-empty catch-up sees both. The marker must not become a second InitialSyncCompleted:
        // that would tell a caller its set was complete twice.
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events =
            await harness.StartInitializedWatchAsync(One);

        harness.Push(Finish(Pattern));
        harness.Push(Change(Two, NamingFuzzyWatchChangeType.AddService));

        // The next event is the change, not a repeated marker.
        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.two");
    }

    [Fact]
    public async Task Watch_CancelsTheServerSideWatchWhenTheLastSubscriberLeaves()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        IAsyncEnumerator<NacosServiceWatchEvent> events =
            await harness.StartInitializedWatchAsync(One);

        await events.DisposeAsync();
        await harness.WaitForRequestsAsync(
            "NamingFuzzyWatchRequest", 2, TestContext.Current.CancellationToken);

        using JsonDocument body = harness.RequestBody("NamingFuzzyWatchRequest", 1);
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

        await using IAsyncEnumerator<NacosServiceWatchEvent> events = harness.Watch("app*", Group);

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            async () => await events.MoveNextAsync().AsTask()
                .WaitAsync(EventTimeout, TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(501);
        harness.RequestsOfType("NamingFuzzyWatchRequest").ShouldBeEmpty();
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

        await using IAsyncEnumerator<NacosServiceWatchEvent> events = harness.Watch("app*", Group);

        NacosServerException exception = await Should.ThrowAsync<NacosServerException>(
            async () => await events.MoveNextAsync().AsTask()
                .WaitAsync(EventTimeout, TestContext.Current.CancellationToken));

        exception.ErrorCode.ShouldBe(501);
        harness.RequestsOfType("NamingFuzzyWatchRequest").ShouldBeEmpty();
    }

    [Fact]
    public async Task Watch_IgnoresAMalformedServiceKeyWithoutLosingTheRestOfTheBatch()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events = harness.Watch("app*", Group);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "NamingFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        harness.Push(new NamingFuzzyWatchSyncRequest
        {
            GroupKeyPattern = Pattern,
            SyncType = FuzzyWatchProtocol.InitNotify,
            TotalBatch = 1,
            CurrentBatch = 1,
            Contexts =
            [
                // Four segments cannot have been produced by the codec.
                Add("public@@DEFAULT_GROUP@@app.one@@extra"),
                Add(Two),
            ],
        });

        await NextAsync(pending);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.two");

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.InitialSyncCompleted);
    }

    [Fact]
    public async Task Watch_DoesNotDeliverAChangeForAServiceOutsideThePattern()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events =
            await harness.StartInitializedWatchAsync(One);

        // The server pushes only to the patterns it matched, so this cannot arrive from a real
        // server; routing by the local pattern keeps a misrouted push out of the consumer's set.
        harness.Push(Change("public@@DEFAULT_GROUP@@other", NamingFuzzyWatchChangeType.AddService));
        harness.Push(Change(Two, NamingFuzzyWatchChangeType.AddService));

        await NextAsync(events);
        events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);
        events.Current.Service!.ServiceName.ShouldBe("app.two");
    }

    [Fact]
    public async Task Watch_ResynchronisesOnTheConfiguredInterval()
    {
        await using WatchHarness harness = new(resyncInterval: TimeSpan.FromMilliseconds(50));
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events =
            await harness.StartInitializedWatchAsync(One);

        await harness.WaitForRequestsAsync(
            "NamingFuzzyWatchRequest", 2, TestContext.Current.CancellationToken);

        using JsonDocument body = harness.RequestBody("NamingFuzzyWatchRequest", 1);
        body.RootElement.GetProperty("initializing").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Watch_GroupOnlyOverloadWatchesEveryServiceInTheGroup()
    {
        await using WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await using IAsyncEnumerator<NacosServiceWatchEvent> events =
            harness.Client.WatchAsync(Group, TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Task<bool> pending = events.MoveNextAsync().AsTask();
        await harness.WaitForRequestsAsync(
            "NamingFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

        using (JsonDocument body = harness.RequestBody("NamingFuzzyWatchRequest", 0))
        {
            body.RootElement.GetProperty("groupKeyPattern").GetString()
                .ShouldBe("public>>DEFAULT_GROUP>>*");
        }

        // Finish the enumeration so the pending read completes before disposal.
        harness.Push(Finish("public>>DEFAULT_GROUP>>*"));
        await NextAsync(pending);
    }

    [Fact]
    public async Task Dispose_CompletesEveryWatchStream()
    {
        // A consumer parked on the next event must be released by disposal; leaving it parked would
        // hang shutdown for as long as the application waits for its pumps.
        WatchHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        IAsyncEnumerator<NacosServiceWatchEvent> events =
            await harness.StartInitializedWatchAsync(One);

        Task<bool> pending = events.MoveNextAsync().AsTask();

        await harness.DisposeAsync();
        (await pending.WaitAsync(EventTimeout, TestContext.Current.CancellationToken)).ShouldBeFalse();

        await events.DisposeAsync();
    }

    /// <summary>Builds an add entry for an initial diff.</summary>
    /// <param name="serviceKey">The wire service key.</param>
    /// <returns>The context.</returns>
    private static NamingFuzzyWatchContext Add(string serviceKey) => new()
    {
        ServiceKey = serviceKey,
        ChangedType = NamingFuzzyWatchChangeType.AddService,
    };

    /// <summary>Builds one initial-diff batch.</summary>
    /// <param name="pattern">The pattern the batch belongs to.</param>
    /// <param name="total">The number of batches in the sync.</param>
    /// <param name="current">The one-based index of this batch.</param>
    /// <param name="contexts">The entries in the batch.</param>
    /// <returns>The push.</returns>
    private static NamingFuzzyWatchSyncRequest Batch(
        string pattern,
        int total,
        int current,
        params NamingFuzzyWatchContext[] contexts) => new()
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
    private static NamingFuzzyWatchSyncRequest Finish(string pattern) => new()
    {
        GroupKeyPattern = pattern,
        SyncType = FuzzyWatchProtocol.FinishInitNotify,
    };

    /// <summary>Builds a single-service change push.</summary>
    /// <param name="serviceKey">The wire service key.</param>
    /// <param name="changeType">The change type.</param>
    /// <returns>The push.</returns>
    private static NamingFuzzyWatchChangeNotifyRequest Change(string serviceKey, string changeType) =>
        new()
        {
            ServiceKey = serviceKey,
            ChangedType = changeType,
            SyncType = FuzzyWatchProtocol.ResourceChanged,
        };

    /// <summary>
    /// Advances an enumeration, failing the test instead of hanging when no event arrives.
    /// </summary>
    /// <param name="events">The enumerator to advance.</param>
    /// <returns>A task that completes once the enumerator moved.</returns>
    private static Task NextAsync(IAsyncEnumerator<NacosServiceWatchEvent> events) =>
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
    /// Wires a naming client to a fake transport and drives fuzzy watch pushes.
    /// </summary>
    private sealed class WatchHarness : IAsyncDisposable
    {
        private readonly List<FakeNacosChannel> _channels = [];
        private readonly Queue<FakeNacosDuplexStream> _streams;
        private readonly Dictionary<string, bool> _abilities;

        private FakeNacosDuplexStream _currentStream = null!;

        public WatchHarness(
            Dictionary<string, bool>? abilities = null,
            TimeSpan? resyncInterval = null)
        {
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
                Naming = new NacosNamingOptions
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
                        "NamingFuzzyWatchRequest" =>
                            PayloadCodec.Encode(new NamingFuzzyWatchResponse()),
                        _ => throw new InvalidOperationException(
                            $"Unexpected request '{PayloadCodec.ReadTypeName(request)}'."),
                    },
                    NextStream());

                _channels.Add(channel);
                return channel;
            });

            Connection = new NacosGrpcConnection(
                options,
                NacosConnectionModule.Naming,
                [NacosServerAddress.Parse("127.0.0.1:8848")],
                factory,
                TimeProvider.System,
                NullLogger.Instance);

            Client = new NacosNamingClient(
                options,
                Connection,
                TimeProvider.System,
                NullLogger<NacosNamingClient>.Instance,
                snapshots: null);

            // In production the factory owns this routing. Wiring it here keeps the push handling
            // in the client under test rather than standing in for it with a fake client.
            Connection.ServerRequestReceived += OnServerRequestAsync;
        }

        public NacosNamingClient Client { get; }

        public NacosGrpcConnection Connection { get; }

        public Task ConnectAsync(CancellationToken ct) => Connection.ConnectAsync(ct);

        /// <summary>Starts a watch and leaves the first read pending.</summary>
        /// <param name="serviceNamePattern">The service name pattern.</param>
        /// <param name="groupPattern">The group pattern.</param>
        /// <returns>The enumerator, positioned before its first event.</returns>
        public IAsyncEnumerator<NacosServiceWatchEvent> Watch(
            string serviceNamePattern,
            string groupPattern) =>
            Client.WatchAsync(serviceNamePattern, groupPattern, TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        /// <summary>
        /// Starts a watch, completes its initial sync with one matching service, and returns it
        /// ready for change pushes.
        /// </summary>
        /// <param name="serviceKey">The service key the server reports as matching.</param>
        /// <returns>The enumerator, positioned after the completion marker.</returns>
        public async Task<IAsyncEnumerator<NacosServiceWatchEvent>> StartInitializedWatchAsync(
            string serviceKey)
        {
            IAsyncEnumerator<NacosServiceWatchEvent> events = Watch("app*", Group);

            Task<bool> pending = events.MoveNextAsync().AsTask();
            await WaitForRequestsAsync(
                "NamingFuzzyWatchRequest", 1, TestContext.Current.CancellationToken);

            Push(Batch(Pattern, total: 1, current: 1, Add(serviceKey)));

            await NextAsync(pending);
            events.Current.Kind.ShouldBe(NacosServiceWatchKind.Added);

            await NextAsync(events);
            events.Current.Kind.ShouldBe(NacosServiceWatchKind.InitialSyncCompleted);

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
                case NamingFuzzyWatchSyncRequest sync:
                    Client.OnFuzzyWatchSync(sync);
                    return Task.FromResult<NacosResponse?>(
                        new NamingFuzzyWatchChangeNotifyResponse { RequestId = sync.RequestId });

                case NamingFuzzyWatchChangeNotifyRequest change:
                    Client.OnFuzzyWatchChange(change);
                    return Task.FromResult<NacosResponse?>(
                        new NamingFuzzyWatchChangeNotifyResponse { RequestId = change.RequestId });

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
