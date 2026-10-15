using System.Threading.Channels;
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
/// Verifies the naming client against a scripted in-memory server.
/// </summary>
/// <remarks>
/// The client's contract is almost entirely about what it puts on the wire and how it interprets
/// what comes back, so every test here asserts on the decoded request or on the model the client
/// produced rather than on incidental log output.
/// </remarks>
public sealed class NacosNamingClientTests
{
    private static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:8848");
    private static readonly NacosServiceKey Service = new("order-service", "DEFAULT_GROUP");

    [Fact]
    public async Task RegisterInstance_SendsTheRegistrationWithItsCoordinates()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        InstanceRequest request = harness.LastRequest<InstanceRequest>();
        request.Type.ShouldBe(InstanceRequest.RegisterType);
        request.Namespace.ShouldBe("public");
        request.ServiceName.ShouldBe("order-service");
        request.GroupName.ShouldBe("DEFAULT_GROUP");
        request.Instance.ShouldNotBeNull();
        request.Instance.Ip.ShouldBe("10.0.0.5");
        request.Instance.Port.ShouldBe(8080);
        request.Instance.ServiceName.ShouldBe("order-service");
    }

    [Fact]
    public async Task RegisterInstance_AppliesTheConfiguredNamespaceAndCluster()
    {
        // A registration that omitted either would land in the public namespace's default cluster
        // rather than where the caller configured it to go.
        using NamingHarness harness = new(@namespace: "tenant-a", cluster: "zone-b");
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        InstanceRequest request = harness.LastRequest<InstanceRequest>();
        request.Namespace.ShouldBe("tenant-a");
        request.Instance!.ClusterName.ShouldBe("zone-b");
    }

    [Fact]
    public async Task RegisterInstance_UsesAPersistentRequestForAPersistentInstance()
    {
        // The server routes a persistent instance to a different handler, which is what makes it
        // survive the connection; sending the wrong request type silently stores it as ephemeral.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.RegisterInstanceAsync(
            Service,
            new NacosInstance("10.0.0.5", 8080, ephemeral: false),
            TestContext.Current.CancellationToken);

        harness.LastRequestTypeName().ShouldBe(nameof(PersistentInstanceRequest));
    }

    [Fact]
    public async Task RegisterInstance_HonoursReservedMetadataOnTheWireInstance()
    {
        // The official client expresses the heartbeat settings as reserved metadata keys; a caller
        // that sets them that way must still have them reach the computed wire fields.
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Instance.InstanceHeartBeatIntervalKey] = "7000",
        };

        await harness.Client.RegisterInstanceAsync(
            Service,
            new NacosInstance("10.0.0.5", 8080, metadata: metadata),
            TestContext.Current.CancellationToken);

        harness.LastRequest<InstanceRequest>().Instance!.InstanceHeartBeatInterval.ShouldBe(7000);
    }

    [Fact]
    public async Task RegisterInstance_WhenTheServerRejects_ForgetsTheRegistration()
    {
        // A registration that never reached the server must not be replayed later as though it had
        // been accepted, or a reconnect would resurrect an instance the caller was told failed.
        using NamingHarness harness = new(handler: request =>
            PayloadCodec.ReadTypeName(request) is "InstanceRequest" or "PersistentInstanceRequest"
                ? PayloadCodec.Encode(new InstanceResponse
                {
                    ResultCode = NacosResponse.FailureCode,
                    ErrorCode = 20005,
                    Message = "rejected",
                })
                : NamingHarness.Fallback(request));

        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NacosServerException>(
            () => harness.Client.RegisterInstanceAsync(
                Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken));

        await harness.DropAndReconnectAsync();

        harness.CountRequests("InstanceRequest").ShouldBe(1);
        harness.CountRequests("PersistentInstanceRequest").ShouldBe(0);
    }

    [Fact]
    public async Task DeregisterInstance_SendsTheDeregistration()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.DeregisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        InstanceRequest request = harness.LastRequest<InstanceRequest>();
        request.Type.ShouldBe(InstanceRequest.DeregisterType);
        request.Instance!.Ip.ShouldBe("10.0.0.5");
    }

    [Fact]
    public async Task GetInstances_SendsTheQueryCoordinatesAndFilter()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await harness.Client.GetInstancesAsync(
            Service, cluster: "zone-a", healthyOnly: true, TestContext.Current.CancellationToken);

        ServiceQueryRequest request = harness.LastRequest<ServiceQueryRequest>();
        request.Namespace.ShouldBe("public");
        request.ServiceName.ShouldBe("order-service");
        request.GroupName.ShouldBe("DEFAULT_GROUP");
        request.Cluster.ShouldBe("zone-a");
        request.HealthyOnly.ShouldBeTrue();
    }

    [Fact]
    public async Task GetInstances_MapsTheWireInstancesToThePublicModel()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        NacosServiceInfo info = await harness.Client.GetInstancesAsync(
            Service, ct: TestContext.Current.CancellationToken);

        info.Instances.Count.ShouldBe(2);
        info.Checksum.ShouldBe("sum-1");
        info.FromLocalCache.ShouldBeFalse();

        NacosInstance first = info.Instances[0];
        first.IpAddress.ShouldBe("10.0.0.1");
        first.Port.ShouldBe(8080);
        first.InstanceId.ShouldBe("inst-8080");
    }

    [Fact]
    public async Task GetInstances_TreatsANonPositiveWeightAsUnweighted()
    {
        // A malformed entry must not fail the whole lookup, and a zero weight cannot be balanced.
        using NamingHarness harness = new(handler: request =>
            PayloadCodec.ReadTypeName(request) == "ServiceQueryRequest"
                ? QueryResponse([Host(port: 8080, weight: 0)])
                : NamingHarness.Fallback(request));
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        NacosServiceInfo info = await harness.Client.GetInstancesAsync(
            Service, ct: TestContext.Current.CancellationToken);

        info.Instances[0].Weight.ShouldBe(1.0);
    }

    [Fact]
    public async Task GetInstances_ServesTheLastKnownListWhenTheServerIsUnreachable()
    {
        using NamingHarness harness = new();
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        // Populate the cache through a successful query first.
        await harness.Client.GetInstancesAsync(Service, ct: TestContext.Current.CancellationToken);

        harness.Handler = _ => throw new NacosConnectionException("server is down");

        NacosServiceInfo cached = await harness.Client.GetInstancesAsync(
            Service, ct: TestContext.Current.CancellationToken);

        cached.FromLocalCache.ShouldBeTrue();
        cached.Instances.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetInstances_ThrowsWhenThereIsNoCachedList()
    {
        using NamingHarness harness = new(
            handler: _ => throw new NacosConnectionException("server is down"));
        await harness.ConnectAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NacosConnectionException>(
            () => harness.Client.GetInstancesAsync(Service, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetInstances_FailsOverToTheOnDiskSnapshotAfterARestart()
    {
        // The in-memory cache dies with the process, which is exactly when failover matters most:
        // a rolling restart during a control-plane outage must not become an application outage.
        string root = TestPaths.CreateScratchDirectory("naming-failover");
        try
        {
            var snapshots = new NacosServiceSnapshotStore(root);

            // A first process populates the snapshot through a successful query, then exits.
            using (NamingHarness first = new(snapshots: snapshots))
            {
                await first.ConnectAsync(TestContext.Current.CancellationToken);
                await first.Client.GetInstancesAsync(
                    Service, ct: TestContext.Current.CancellationToken);
            }

            // A second process starts while the server is unreachable and has no in-process cache.
            using NamingHarness restarted = new(
                handler: _ => throw new NacosConnectionException("server is down"),
                snapshots: snapshots);
            await restarted.ConnectAsync(TestContext.Current.CancellationToken);

            NacosServiceInfo info = await restarted.Client.GetInstancesAsync(
                Service, ct: TestContext.Current.CancellationToken);

            info.FromLocalCache.ShouldBeTrue();
            info.Instances.Count.ShouldBe(2);
            info.Instances[0].IpAddress.ShouldBe("10.0.0.1");
        }
        finally
        {
            TestPaths.TryDelete(root);
        }
    }

    [Fact]
    public async Task Subscribe_DeliversTheInitialStateThenLaterPushes()
    {
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(Service);

        NacosServiceChange initial = await subscription.NextAsync();
        initial.PreviousChecksum.ShouldBeNull();
        initial.Service.Checksum.ShouldBe("sum-1");

        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-2", ports: [8080, 8081]),
            TestContext.Current.CancellationToken);

        NacosServiceChange change = await subscription.NextAsync();
        change.PreviousChecksum.ShouldBe("sum-1");
        change.Service.Checksum.ShouldBe("sum-2");
        change.Instances.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Subscribe_IgnoresAPushWhoseChecksumDidNotChange()
    {
        // The server notifies on changes it may already have delivered, so an unchanged checksum
        // must not reach user code as a spurious change.
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(Service);
        await subscription.NextAsync();

        await harness.Client.OnServiceChangedAsync(
            Push(checksum: "sum-1", ports: [8080, 8081]),
            TestContext.Current.CancellationToken);

        (await subscription.TryNextAsync(TimeSpan.FromMilliseconds(200))).ShouldBeNull();
    }

    [Fact]
    public async Task Reconnect_ReplaysRegistrationsAndSubscriptions()
    {
        // PLAN.md requires the client to restore everything the server discarded with the
        // connection, or the instance silently stops being discoverable after an outage.
        using NamingHarness harness = new();

        await harness.ConnectAsync(TestContext.Current.CancellationToken);
        await harness.Client.RegisterInstanceAsync(
            Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken);

        await using Subscription subscription = await harness.SubscribeAsync(Service);
        await subscription.NextAsync();

        int registrationsBefore = harness.CountRequests("InstanceRequest");
        int subscriptionsBefore = harness.CountRequests("SubscribeServiceRequest");

        await harness.DropAndReconnectAsync();

        await NamingHarness.WaitForAsync(
            () => harness.CountRequests("InstanceRequest") > registrationsBefore);

        harness.CountRequests("InstanceRequest").ShouldBe(registrationsBefore + 1);
        harness.CountRequests("SubscribeServiceRequest").ShouldBeGreaterThanOrEqualTo(subscriptionsBefore + 1);
    }

    [Fact]
    public async Task Dispose_EndsEverySubscription()
    {
        using NamingHarness harness = new();

        await using Subscription subscription = await harness.SubscribeAsync(Service);
        await subscription.NextAsync();

        await harness.Client.DisposeAsync();

        // The stream completed rather than hanging, so the enumeration ends cleanly.
        (await subscription.TryNextAsync(TimeSpan.FromSeconds(2))).ShouldBeNull();
    }

    [Fact]
    public async Task RegisterInstance_ThrowsAfterDispose()
    {
        using NamingHarness harness = new();
        await harness.Client.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(
            () => harness.Client.RegisterInstanceAsync(
                Service, new NacosInstance("10.0.0.5", 8080), TestContext.Current.CancellationToken));
    }

    /// <summary>Builds a successful query response carrying the given hosts.</summary>
    private static Payload QueryResponse(IReadOnlyList<Instance> hosts) =>
        PayloadCodec.Encode(new QueryServiceResponse
        {
            ServiceInfo = new ServiceInfo
            {
                Name = "order-service",
                GroupName = "DEFAULT_GROUP",
                Checksum = "sum-1",
                Hosts = [.. hosts],
            },
        });

    /// <summary>Builds a successful subscription response for the default service.</summary>
    private static Payload SubscribeResponse() =>
        PayloadCodec.Encode(new SubscribeServiceResponse
        {
            ServiceInfo = new ServiceInfo
            {
                Name = "order-service",
                GroupName = "DEFAULT_GROUP",
                Checksum = "sum-1",
                Hosts = [Host(8080), Host(8081)],
            },
        });

    /// <summary>Builds a full subscriber push for the default service.</summary>
    private static NotifySubscriberRequest Push(string checksum, IReadOnlyList<int> ports) => new()
    {
        Namespace = "public",
        ServiceName = "order-service",
        GroupName = "DEFAULT_GROUP",
        ServiceInfo = new ServiceInfo
        {
            Name = "order-service",
            GroupName = "DEFAULT_GROUP",
            Checksum = checksum,
            Hosts = [.. ports.Select(port => Host(port))],
        },
    };

    /// <summary>Builds a single healthy wire host.</summary>
    private static Instance Host(int port, double weight = 1.0) => new()
    {
        Ip = "10.0.0.1",
        Port = port,
        Weight = weight,
        ClusterName = "DEFAULT",
        Healthy = true,
        Enabled = true,
        InstanceId = "inst-" + port.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Wires a naming client to a fake transport and exposes the requests it sent.
    /// </summary>
    private sealed class NamingHarness : IDisposable
    {
        private readonly Queue<FakeNacosDuplexStream> _streams;
        private readonly List<FakeNacosChannel> _channels = [];
        private readonly NacosGrpcConnection _connection;

        private FakeNacosDuplexStream _currentStream = null!;

        public NamingHarness(
            string @namespace = "public",
            string cluster = "DEFAULT",
            Func<Payload, Payload>? handler = null,
            NacosServiceSnapshotStore? snapshots = null)
        {
            Handler = handler ?? Fallback;
            Snapshots = snapshots;

            // A generous supply of streams so tests can drop one and let a fresh one replace it.
            _streams = new Queue<FakeNacosDuplexStream>(
                Enumerable.Range(0, 4).Select(_ => new FakeNacosDuplexStream()));

            var factory = new FakeNacosChannelFactory(address =>
            {
                var channel = new FakeNacosChannel(
                    address,
                    request => PayloadCodec.ReadTypeName(request) == "ServerCheckRequest"
                        ? FakeServerCheck.Responding()(request)
                        : Handler(request),
                    NextStream());

                _channels.Add(channel);
                return channel;
            });

            var options = new NacosOptions
            {
                ServerAddresses = ["127.0.0.1:8848"],
                Namespace = @namespace,
                Naming = new NacosNamingOptions { DefaultCluster = cluster },
                Reconnect = new NacosReconnectOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    MaxDelay = TimeSpan.FromMilliseconds(2),
                    JitterFactor = 0,
                },
            };

            _connection = new NacosGrpcConnection(
                options,
                NacosConnectionModule.Naming,
                [Server],
                factory,
                TimeProvider.System,
                NullLogger.Instance,
                new Random(1234));

            Client = new NacosNamingClient(
                options, _connection, TimeProvider.System, NullLogger<NacosNamingClient>.Instance, Snapshots);
        }

        /// <summary>Gets or sets the request handler, so a test can change behaviour midway.</summary>
        public Func<Payload, Payload> Handler { get; set; }

        public NacosNamingClient Client { get; }

        /// <summary>Gets the snapshot store the client writes to, when the test supplied one.</summary>
        public NacosServiceSnapshotStore? Snapshots { get; }

        /// <summary>
        /// Answers request types the test did not script with a successful response of the
        /// matching kind.
        /// </summary>
        /// <param name="request">The request to answer.</param>
        /// <returns>The encoded response.</returns>
        public static Payload Fallback(Payload request) => PayloadCodec.ReadTypeName(request) switch
        {
            "ServiceQueryRequest" => QueryResponse([Host(8080), Host(8081)]),
            "SubscribeServiceRequest" => SubscribeResponse(),
            "InstanceRequest" or "PersistentInstanceRequest" => PayloadCodec.Encode(new InstanceResponse()),
            _ => throw new InvalidOperationException(
                $"The harness has no response for '{PayloadCodec.ReadTypeName(request)}'."),
        };

        /// <summary>Establishes the transport.</summary>
        /// <param name="ct">A token that cancels the connection.</param>
        /// <returns>A task that completes once connected.</returns>
        public Task ConnectAsync(CancellationToken ct) => _connection.ConnectAsync(ct);

        /// <summary>Counts the unary requests of a payload type name across every channel.</summary>
        /// <param name="typeName">The payload type name.</param>
        /// <returns>The number of matching requests.</returns>
        public int CountRequests(string typeName) =>
            _channels.Sum(channel => channel.UnaryRequests.Count(
                payload => PayloadCodec.ReadTypeName(payload) == typeName));

        /// <summary>Decodes the most recent request of the given type.</summary>
        /// <typeparam name="T">The expected request type.</typeparam>
        /// <returns>The decoded request.</returns>
        public T LastRequest<T>()
            where T : NacosRequest
        {
            for (int i = _channels.Count - 1; i >= 0; i--)
            {
                for (int j = _channels[i].UnaryRequests.Count - 1; j >= 0; j--)
                {
                    Payload payload = _channels[i].UnaryRequests[j];

                    if (PayloadCodec.ReadTypeName(payload) == typeof(T).Name)
                    {
                        return PayloadCodec.Decode<T>(payload)
                            ?? throw new InvalidOperationException(
                                $"The last '{typeof(T).Name}' request could not be decoded.");
                    }
                }
            }

            throw new InvalidOperationException($"No '{typeof(T).Name}' request was sent.");
        }

        /// <summary>Gets the payload type name of the most recent unary request.</summary>
        /// <returns>The type name.</returns>
        public string LastRequestTypeName() =>
            PayloadCodec.ReadTypeName(_channels[^1].UnaryRequests[^1]);

        /// <summary>Starts a subscription and returns a handle that reads its changes.</summary>
        /// <param name="service">The service to observe.</param>
        /// <returns>The subscription handle.</returns>
        public async Task<Subscription> SubscribeAsync(NacosServiceKey service)
        {
            await _connection.ConnectAsync(TestContext.Current.CancellationToken);

            var changes = Channel.CreateUnbounded<NacosServiceChange>();
            var cts = new CancellationTokenSource();

            Task pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (NacosServiceChange change in
                        Client.SubscribeAsync(service, ct: cts.Token))
                    {
                        changes.Writer.TryWrite(change);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when the handle is disposed.
                }
                finally
                {
                    changes.Writer.TryComplete();
                }
            });

            // The server-side subscription exists only after the request was sent, so wait for it
            // before allowing a test to push a change.
            await WaitForAsync(() => CountRequests("SubscribeServiceRequest") >= 1);

            return new Subscription(changes.Reader, cts, pump);
        }

        /// <summary>Drops the current stream and waits for the automatic reconnect to connect.</summary>
        /// <returns>A task that completes once a fresh channel is in use.</returns>
        public async Task DropAndReconnectAsync()
        {
            int channelsBefore = _channels.Count;
            _currentStream.EndStream();

            await WaitForAsync(() => _channels.Count > channelsBefore);

            // Wait for the fresh channel to have completed a handshake before returning.
            await WaitForAsync(() => _channels[^1].UnaryRequests.Count >= 1);
        }

        /// <summary>Waits until a condition holds, or throws when it never does.</summary>
        /// <param name="condition">The condition to wait for.</param>
        /// <returns>A task that completes once the condition holds.</returns>
        public static async Task WaitForAsync(Func<bool> condition)
        {
            for (int i = 0; i < 1000; i++)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("The expected condition was not met within the timeout.");
        }

        public void Dispose()
        {
            Client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private FakeNacosDuplexStream NextStream()
        {
            _currentStream = _streams.Count > 0 ? _streams.Dequeue() : new FakeNacosDuplexStream();
            return _currentStream;
        }
    }

    /// <summary>Reads changes from a running subscription.</summary>
    private sealed class Subscription : IAsyncDisposable
    {
        private readonly ChannelReader<NacosServiceChange> _reader;
        private readonly CancellationTokenSource _cts;
        private readonly Task _pump;

        public Subscription(
            ChannelReader<NacosServiceChange> reader, CancellationTokenSource cts, Task pump)
        {
            _reader = reader;
            _cts = cts;
            _pump = pump;
        }

        /// <summary>Waits for the next change.</summary>
        /// <returns>The change.</returns>
        public Task<NacosServiceChange> NextAsync() =>
            _reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();

        /// <summary>Waits a bounded time for a change, returning null when none arrives.</summary>
        /// <param name="timeout">How long to wait.</param>
        /// <returns>The change, or <see langword="null"/>.</returns>
        public async Task<NacosServiceChange?> TryNextAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return await _reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ChannelClosedException)
            {
                // The subscription ended, which is what a disposal test is asserting.
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _pump;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            _cts.Dispose();
        }
    }
}
