using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Lolitacoding.Nacos.Tests.Transport;

/// <summary>
/// Verifies the handshake sequence, push handling and state reporting of
/// <see cref="NacosGrpcConnection"/> against a scripted in-memory server.
/// </summary>
public sealed class NacosGrpcConnectionTests
{
    private static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:8848");

    [Fact]
    public async Task Connect_SendsServerCheckThenSetupAsFirstStreamFrame()
    {
        // The handshake order is fixed by the protocol: a unary ServerCheckRequest, then the
        // stream is opened, then ConnectionSetupRequest is the first frame written to it.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        await using var connection = CreateConnection(factory);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        factory.RequestedAddresses.Count.ShouldBe(1);
        stream.Written.Count.ShouldBe(1);
        PayloadCodec.ReadTypeName(stream.Written[0]).ShouldBe("ConnectionSetupRequest");
        connection.State.ShouldBe(NacosTransportState.Connected);
        connection.ConnectionId.ShouldBe("conn-1");
    }

    [Fact]
    public async Task Connect_AdvertisesNonNullAbilityTableAndLabels()
    {
        // A null ability table makes the server skip SetupAckRequest entirely, leaving the client
        // unable to tell when the connection became usable.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        ConnectionSetupRequest setup =
            PayloadCodec.Decode<ConnectionSetupRequest>(stream.Written[0])!;

        setup.AbilityTable.ShouldNotBeNull();
        setup.AbilityTable.ShouldNotBeEmpty();
        setup.Labels.ShouldNotBeNull();
        setup.Labels.ShouldContainKey(ProtocolHeaders.AppName);
        setup.ClientVersion.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Connect_SendsTheModuleAndSourceLabels()
    {
        // Nacos routes a connection by the "module" label, and it reads that label only from the
        // connection setup request. A connection that omits it, or sends another module's value,
        // is treated as one the module does not own and its registrations are silently dropped.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        await using NacosGrpcConnection connection =
            CreateConnection(factory, module: NacosConnectionModule.Lock);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        ConnectionSetupRequest setup =
            PayloadCodec.Decode<ConnectionSetupRequest>(stream.Written[0])!;

        setup.Labels![ProtocolHeaders.Module].ShouldBe("lock");
        setup.Labels[ProtocolHeaders.Source].ShouldBe(ProtocolHeaders.SourceSdk);
    }

    [Fact]
    public async Task Connect_MergesModuleExtraLabels()
    {
        // A module contributes labels the transport knows nothing about — the AI client's UUID, for
        // instance — and those take precedence over the standard labels.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        var extraLabels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aiClientUuid"] = "uuid-1",
            [ProtocolHeaders.AppName] = "override",
        };

        await using NacosGrpcConnection connection =
            CreateConnection(factory, module: NacosConnectionModule.AI, extraLabels: extraLabels);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        ConnectionSetupRequest setup =
            PayloadCodec.Decode<ConnectionSetupRequest>(stream.Written[0])!;

        setup.Labels!["aiClientUuid"].ShouldBe("uuid-1");
        setup.Labels[ProtocolHeaders.AppName].ShouldBe("override");
        setup.Labels[ProtocolHeaders.Module].ShouldBe("ai");
    }

    [Fact]
    public async Task Connect_RecordsServerAbilitiesFromSetupAck()
    {
        var stream = new FakeNacosDuplexStream
        {
            AutoAckSetup = false,
        };

        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        await using var connection = CreateConnection(factory);

        Task connect = connection.ConnectAsync(TestContext.Current.CancellationToken);

        stream.Push(TestPayload.ForRequest(new SetupAckRequest
        {
            AbilityTable = new Dictionary<string, bool> { ["fuzzyWatch"] = true },
        }));

        await connect;

        connection.ServerAbilities.ShouldContainKey("fuzzyWatch");
        connection.ServerAbilities["fuzzyWatch"].ShouldBeTrue();
    }

    [Fact]
    public async Task Connect_DoesNotAckTheSetupAcknowledgement()
    {
        // SetupAckRequest is request-typed, but the client must not answer it.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        // Only the setup request was written; the acknowledgement produced no reply.
        stream.Written.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Connect_ReportsConnectingThenConnected()
    {
        var stream = new FakeNacosDuplexStream { AutoAckSetup = false };
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        var states = new List<NacosTransportState>();
        await using var connection = CreateConnection(factory);
        connection.StateChanged += (_, e) => states.Add(e.Current);

        Task connect = connection.ConnectAsync(TestContext.Current.CancellationToken);
        stream.Push(TestPayload.ForRequest(new SetupAckRequest { AbilityTable = [] }));
        await connect;

        states.ShouldBe([NacosTransportState.Connecting, NacosTransportState.Connected]);
    }

    [Fact]
    public async Task ClientDetection_IsAnsweredWithDetectionResponse()
    {
        // Failing to answer this probe makes the server unregister the connection.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        stream.Push(TestPayload.ForRequest(new ClientDetectionRequest { RequestId = "probe-7" }));

        await WaitForWriteCountAsync(stream, 2, TestContext.Current.CancellationToken);

        PayloadCodec.ReadTypeName(stream.Written[1]).ShouldBe("ClientDetectionResponse");
        PayloadCodec.Decode<ClientDetectionResponse>(stream.Written[1])!.RequestId.ShouldBe("probe-7");
    }

    [Fact]
    public async Task ConnectReset_IsAnsweredAndMovesToTheNextServer()
    {
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        var servers = new List<NacosServerAddress>
        {
            NacosServerAddress.Parse("127.0.0.1:8848"),
            NacosServerAddress.Parse("127.0.0.1:8849"),
        };

        await using var connection = CreateConnection(factory, servers);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        stream.Push(TestPayload.ForRequest(new ConnectResetRequest
        {
            ServerIp = "127.0.0.1",
            ServerPort = "8849",
            RequestId = "reset-1",
        }));

        await WaitForWriteCountAsync(stream, 2, TestContext.Current.CancellationToken);

        PayloadCodec.ReadTypeName(stream.Written[1]).ShouldBe("ConnectResetResponse");
        PayloadCodec.Decode<ConnectResetResponse>(stream.Written[1])!.RequestId.ShouldBe("reset-1");
    }

    [Fact]
    public async Task UnknownPushType_IsIgnoredWithoutBreakingTheConnection()
    {
        // A newer server may push payload types this client predates; that must not tear down an
        // otherwise healthy connection.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        stream.Push(new Payload
        {
            Metadata = new Metadata { Type = "SomeFuturePayload" },
        });

        await Task.Delay(50, TestContext.Current.CancellationToken);

        connection.State.ShouldBe(NacosTransportState.Connected);
    }

    [Fact]
    public async Task ServerPush_IsAnsweredWithTheResponseItsHandlerProduced()
    {
        // The server matches an acknowledgement by the request id of the push it answers, and it
        // keeps retrying a lock notification until one arrives. A handler response that the
        // transport dropped would therefore silence every push-driven feature that needs a reply.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        await using NacosGrpcConnection connection =
            CreateConnection(factory, module: NacosConnectionModule.Lock);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        connection.ServerRequestReceived += (request, _) =>
            Task.FromResult<NacosResponse?>(
                request is LockNotificationRequest ? new LockNotificationResponse() : null);

        stream.Push(TestPayload.ForRequest(new LockNotificationRequest
        {
            LockKey = "lock-1",
            Owner = "owner-1",
            NotificationType = "AVAILABLE",
            RequestId = "notify-9",
        }));

        await WaitForWriteCountAsync(stream, 2, TestContext.Current.CancellationToken);

        PayloadCodec.ReadTypeName(stream.Written[1]).ShouldBe("LockNotificationResponse");
        PayloadCodec.Decode<LockNotificationResponse>(stream.Written[1])!.RequestId.ShouldBe("notify-9");
    }

    [Fact]
    public async Task ServerPush_WithNoHandlerResponseWritesNothingBack()
    {
        // A handler that only observes a push — the configuration and naming change listeners do —
        // must not produce a reply the server never asked for.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        await using NacosGrpcConnection connection =
            CreateConnection(factory, module: NacosConnectionModule.Lock);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        connection.ServerRequestReceived += (_, _) => Task.FromResult<NacosResponse?>(null);

        stream.Push(TestPayload.ForRequest(new LockNotificationRequest
        {
            LockKey = "lock-1",
            RequestId = "notify-10",
        }));

        await Task.Delay(100, TestContext.Current.CancellationToken);

        stream.Written.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RequestAsync_SendsTheRequestAndReturnsTheDecodedResponse()
    {
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(address => new FakeNacosChannel(
            address,
            request => PayloadCodec.ReadTypeName(request) == "ServerCheckRequest"
                ? FakeServerCheck.Responding()(request)
                : PayloadCodec.Encode(new ConfigQueryResponse
                {
                    Content = "hello",
                    Md5 = "abc",
                }),
            stream));

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        ConfigQueryResponse response = await connection.RequestAsync<ConfigQueryResponse>(
            new ConfigQueryRequest { DataId = "a", Group = "g", Tenant = "public" },
            TestContext.Current.CancellationToken);

        response.Content.ShouldBe("hello");
        response.Md5.ShouldBe("abc");

        // The transport must have assigned a correlation identifier so a late response can be
        // matched to its request.
        response.RequestId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task RequestAsync_ThrowsServerExceptionOnErrorResponse()
    {
        // The server reports transport-level failures as ErrorResponse whatever the request type.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(address => new FakeNacosChannel(
            address,
            request => PayloadCodec.ReadTypeName(request) == "ServerCheckRequest"
                ? FakeServerCheck.Responding()(request)
                : PayloadCodec.Encode(new ErrorResponse
                {
                    ResultCode = NacosResponse.FailureCode,
                    ErrorCode = 500,
                    Message = "handler exploded",
                }),
            stream));

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        NacosServerException ex = await Should.ThrowAsync<NacosServerException>(
            () => connection.RequestAsync<ConfigQueryResponse>(
                new ConfigQueryRequest { DataId = "a", Group = "g" },
                TestContext.Current.CancellationToken));

        ex.ErrorCode.ShouldBe(500);
        ex.Message.ShouldBe("handler exploded");
    }

    [Fact]
    public async Task RequestAsync_ThrowsServerExceptionWhenTheResponseTypeIsUnexpected()
    {
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(address => new FakeNacosChannel(
            address,
            request => PayloadCodec.ReadTypeName(request) == "ServerCheckRequest"
                ? FakeServerCheck.Responding()(request)
                : PayloadCodec.Encode(new ConfigPublishResponse()),
            stream));

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        NacosServerException ex = await Should.ThrowAsync<NacosServerException>(
            () => connection.RequestAsync<ConfigQueryResponse>(
                new ConfigQueryRequest { DataId = "a", Group = "g" },
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("ConfigPublishResponse");
    }

    [Fact]
    public async Task Connect_FailsWhenNoServerIsReachable()
    {
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(
                address,
                _ => throw new NacosConnectionException("refused"),
                stream));

        await using var connection = CreateConnection(factory);

        await Should.ThrowAsync<NacosConnectionException>(
            () => connection.ConnectAsync(TestContext.Current.CancellationToken));

        connection.State.ShouldBe(NacosTransportState.Disconnected);
    }

    [Fact]
    public async Task Connect_FailsOverToTheNextServer()
    {
        var stream = new FakeNacosDuplexStream();
        int calls = 0;
        var factory = new FakeNacosChannelFactory(address => new FakeNacosChannel(
            address,
            request =>
            {
                // Fail the first server, answer from the second.
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new NacosConnectionException("refused");
                }

                return FakeServerCheck.Responding()(request);
            },
            stream));

        var servers = new List<NacosServerAddress>
        {
            NacosServerAddress.Parse("127.0.0.1:8848"),
            NacosServerAddress.Parse("127.0.0.1:8849"),
        };

        await using var connection = CreateConnection(factory, servers);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        connection.CurrentServer!.Original.ShouldBe("127.0.0.1:8849");
    }

    [Fact]
    public async Task ServerList_WhenTheCurrentServerIsWithdrawn_ReconnectsToTheRemainingOne()
    {
        // An address server retiring a node must move the client off it, or the client keeps
        // talking to a server the deployment no longer publishes.
        var streams = new Queue<FakeNacosDuplexStream>([new(), new()]);
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), streams.Dequeue()));

        var list = new NacosServerList(
            [NacosServerAddress.Parse("127.0.0.1:8848")], isDynamic: true);

        await using var connection = CreateConnection(factory, list);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        connection.CurrentServer!.Original.ShouldBe("127.0.0.1:8848");

        list.Replace([NacosServerAddress.Parse("127.0.0.1:8849")]);

        await WaitForConditionAsync(
            () => connection.CurrentServer?.Original == "127.0.0.1:8849",
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        connection.State.ShouldBe(NacosTransportState.Connected);
    }

    [Fact]
    public async Task ServerList_WhenAnUnrelatedServerIsAdded_KeepsTheConnection()
    {
        // Adding a node must not cost a healthy connection: the next natural reconnect picks the
        // new node up, and the rotation spreads requests over it from then on.
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(
                address, FakeServerCheck.Responding(), new FakeNacosDuplexStream()));

        var list = new NacosServerList(
            [NacosServerAddress.Parse("127.0.0.1:8848")], isDynamic: true);

        await using var connection = CreateConnection(factory, list);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        list.Replace(
        [
            NacosServerAddress.Parse("127.0.0.1:8848"),
            NacosServerAddress.Parse("127.0.0.1:8850"),
        ]);

        await Task.Delay(200, TestContext.Current.CancellationToken);

        factory.RequestedAddresses.Count.ShouldBe(1);
        connection.CurrentServer!.Original.ShouldBe("127.0.0.1:8848");
        connection.State.ShouldBe(NacosTransportState.Connected);
    }

    [Fact]
    public async Task ServerList_WhenEmpty_WaitsForTheFirstPublishedAddress()
    {
        // Client creation must not block on the address server, so the connection waits for the
        // list rather than failing as soon as it is asked to connect.
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(
                address, FakeServerCheck.Responding(), new FakeNacosDuplexStream()));

        var list = new NacosServerList([], isDynamic: true);

        await using var connection = CreateConnection(factory, list);

        Task connecting = connection.ConnectAsync(TestContext.Current.CancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        connecting.IsCompleted.ShouldBeFalse();

        list.Replace([NacosServerAddress.Parse("127.0.0.1:8848")]);

        await connecting;

        connection.State.ShouldBe(NacosTransportState.Connected);
        factory.RequestedAddresses.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ServerList_WhenNothingIsPublished_FailsAfterTheConnectionTimeout()
    {
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(
                address, FakeServerCheck.Responding(), new FakeNacosDuplexStream()));

        var list = new NacosServerList([], isDynamic: true);

        await using var connection = CreateConnection(
            factory, list, connectionTimeout: TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<NacosConnectionException>(
            () => connection.ConnectAsync(TestContext.Current.CancellationToken));

        connection.State.ShouldBe(NacosTransportState.Disconnected);
        factory.RequestedAddresses.ShouldBeEmpty();
    }

    [Fact]
    public async Task StreamEnd_TriggersReconnectAndRaisesReconnected()
    {
        // A dropped stream must recover, and subscribers must be told so they can restore the
        // connection-scoped state the server discarded.
        var first = new FakeNacosDuplexStream();
        var second = new FakeNacosDuplexStream();
        var streams = new Queue<FakeNacosDuplexStream>([first, second]);

        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), streams.Dequeue()));

        await using var connection = CreateConnection(factory);

        int reconnected = 0;
        connection.Reconnected += (_, _) =>
        {
            Interlocked.Increment(ref reconnected);
            return Task.CompletedTask;
        };

        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        // The server drops the connection.
        first.EndStream();

        await WaitForConditionAsync(
            () => Volatile.Read(ref reconnected) == 1,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        connection.State.ShouldBe(NacosTransportState.Connected);
        connection.ConnectionId.ShouldBe("conn-1");
    }

    [Fact]
    public async Task StreamEnd_StopsReconnectingAfterMaxAttempts()
    {
        var stream = new FakeNacosDuplexStream();
        int created = 0;
        var factory = new FakeNacosChannelFactory(address => new FakeNacosChannel(
            address,
            request =>
            {
                // Only the very first server check succeeds; every reconnect attempt fails.
                if (Interlocked.Increment(ref created) == 1)
                {
                    return FakeServerCheck.Responding()(request);
                }

                throw new NacosConnectionException("refused");
            },
            stream));

        var options = new NacosOptions
        {
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            Reconnect = new NacosReconnectOptions
            {
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
                MaxAttempts = 3,
                JitterFactor = 0,
            },
        };

        await using var connection = new NacosGrpcConnection(
            options,
            NacosConnectionModule.Config,
            [Server],
            factory,
            TimeProvider.System,
            NullLogger.Instance,
            new Random(1234));

        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        stream.EndStream();

        await WaitForConditionAsync(
            () => connection.State is NacosTransportState.Disconnected,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        // 1 initial connect + at most MaxAttempts reconnect attempts.
        Volatile.Read(ref created).ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task HealthCheck_ReconnectsWhenTheServerStopsAnswering()
    {
        var first = new FakeNacosDuplexStream();
        var second = new FakeNacosDuplexStream();
        var streams = new Queue<FakeNacosDuplexStream>([first, second]);
        bool failHealthCheck = false;

        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(
                address,
                request =>
                {
                    string type = PayloadCodec.ReadTypeName(request);

                    if (type == "HealthCheckRequest")
                    {
                        if (failHealthCheck)
                        {
                            throw new NacosConnectionException("no answer");
                        }

                        return PayloadCodec.Encode(new HealthCheckResponse());
                    }

                    return FakeServerCheck.Responding()(request);
                },
                streams.Dequeue()));

        var timeProvider = new FakeTimeProvider();
        var options = new NacosOptions
        {
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            HealthCheckInterval = TimeSpan.FromSeconds(5),
            Reconnect = new NacosReconnectOptions
            {
                // A zero delay keeps the retry deterministic: FakeTimeProvider would otherwise
                // never elapse the backoff timer, and this test is about the stale-connection
                // decision rather than the delay.
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                JitterFactor = 0,
            },
        };

        await using var connection = new NacosGrpcConnection(
            options,
            NacosConnectionModule.Config,
            [Server],
            factory,
            timeProvider,
            NullLogger.Instance,
            new Random(1234));

        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        failHealthCheck = true;

        // Move the fake clock past the health check interval, then run one round.
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await connection.HealthCheckAsync(TestContext.Current.CancellationToken);

        // The stale connection was replaced with a fresh one.
        second.Written.Count.ShouldBe(1);
        connection.State.ShouldBe(NacosTransportState.Connected);
    }

    [Fact]
    public async Task HealthCheck_IsSkippedWhileTheConnectionIsBusy()
    {
        // A connection that is demonstrably serving traffic must not be probed.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        var timeProvider = new FakeTimeProvider();
        var options = new NacosOptions
        {
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            HealthCheckInterval = TimeSpan.FromSeconds(5),
        };

        await using var connection = new NacosGrpcConnection(
            options,
            NacosConnectionModule.Config,
            [Server],
            factory,
            timeProvider,
            NullLogger.Instance,
            new Random(1234));

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        await connection.HealthCheckAsync(TestContext.Current.CancellationToken);

        // No health check request was issued, so no extra stream frame appeared.
        stream.Written.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DisposeAsync_TransitionsToShutdownAndDisposesTheStream()
    {
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        await connection.DisposeAsync();

        connection.State.ShouldBe(NacosTransportState.Shutdown);
        stream.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task StreamEnd_DuringShutdownDoesNotReconnect()
    {
        var stream = new FakeNacosDuplexStream();
        int created = 0;
        var factory = new FakeNacosChannelFactory(address => new FakeNacosChannel(
            address,
            request =>
            {
                Interlocked.Increment(ref created);
                return FakeServerCheck.Responding()(request);
            },
            stream));

        var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        await connection.DisposeAsync();

        // A deliberate shutdown must not be followed by a reconnect attempt.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Volatile.Read(ref created).ShouldBe(1);
    }

    [Fact]
    public async Task Request_WaitsForAReconnectInsteadOfFailingOnTheMissingConnection()
    {
        // The connection is gone but a reconnect is under way, so the request has something to wait
        // for. Failing here would hand the caller a failure for an outage it is about to survive.
        var first = new FakeNacosDuplexStream();
        var second = new FakeNacosDuplexStream();
        using var handshakeGate = new ManualResetEventSlim(false);
        int created = 0;

        var factory = new FakeNacosChannelFactory(address =>
        {
            int index = Interlocked.Increment(ref created);

            return index == 1
                ? new FakeNacosChannel(address, FakeServerCheck.Responding(), first)
                : new FakeNacosChannel(address, Reconnect(handshakeGate), second);
        });

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        // The server drops the connection, which starts the reconnect.
        first.EndStream();

        // The reconnect is holding its handshake open, so the connection is provably absent and
        // provably coming back.
        await WaitForConditionAsync(
            () => Volatile.Read(ref created) >= 2,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Task<ConfigQueryResponse> pending = connection.RequestAsync<ConfigQueryResponse>(
            new ConfigQueryRequest { DataId = "a", Group = "g" },
            TestContext.Current.CancellationToken);

        await Task.Delay(100, TestContext.Current.CancellationToken);

        // The only two possible outcomes for the request are "still waiting" and "failed". Waiting
        // is the one this test is about: the connection cannot serve it yet, and the request must
        // not have given up on the reconnect.
        pending.IsCompleted.ShouldBeFalse();

        handshakeGate.Set();

        ConfigQueryResponse response = await pending;

        response.Content.ShouldBe("reconnected");
        connection.State.ShouldBe(NacosTransportState.Connected);
    }

    [Fact]
    public async Task Request_StopsWaitingWhenTheFirstReconnectAttemptIsRefused()
    {
        // Waiting for a reconnect is only worth anything while a connection is coming. Once the
        // server has refused an attempt, the caller is handed back to its own failover rather than
        // being held until the whole request budget runs out.
        var stream = new FakeNacosDuplexStream();
        int requests = 0;

        var factory = new FakeNacosChannelFactory(address => new FakeNacosChannel(
            address,
            request =>
            {
                // The initial handshake succeeds; every later attempt is refused.
                if (Interlocked.Increment(ref requests) == 1)
                {
                    return FakeServerCheck.Responding()(request);
                }

                throw new NacosConnectionException("refused");
            },
            stream));

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        stream.EndStream();

        await WaitForConditionAsync(
            () => Volatile.Read(ref requests) >= 2,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        DateTimeOffset start = DateTimeOffset.UtcNow;

        await Should.ThrowAsync<NacosConnectionException>(
            () => connection.RequestAsync<ConfigQueryResponse>(
                new ConfigQueryRequest { DataId = "a", Group = "g" },
                TestContext.Current.CancellationToken));

        // The request budget is five seconds, so a request that waited for the reconnect would sit
        // here for that long before failing.
        (DateTimeOffset.UtcNow - start).ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Request_FailsAtOnceWhenTheClientNeverConnected()
    {
        // A client that never connected has no reconnect to wait for: its background probe is
        // trying to establish a first connection. Waiting for it would turn a failed startup into
        // a slow one, and the caller is owed the failure so it can serve its own local copy.
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(
                address,
                _ => throw new NacosConnectionException("refused"),
                stream));

        await using var connection = CreateConnection(
            factory,
            optional: true,
            reconnectDelay: TimeSpan.FromSeconds(30));

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        connection.State.ShouldBe(NacosTransportState.Disconnected);

        DateTimeOffset start = DateTimeOffset.UtcNow;

        await Should.ThrowAsync<NacosConnectionException>(
            () => connection.RequestAsync<ConfigQueryResponse>(
                new ConfigQueryRequest { DataId = "a", Group = "g" },
                TestContext.Current.CancellationToken));

        // The probe is still in its first half-minute backoff, so a request that waited for it
        // would block for far longer than this.
        (DateTimeOffset.UtcNow - start).ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Request_GivesUpOnTheReconnectOnceTheRequestBudgetIsSpent()
    {
        // A connection attempt that never answers must not outlive the request that is waiting for
        // it, or a black-holed server would stall every caller indefinitely.
        var first = new FakeNacosDuplexStream();
        var second = new FakeNacosDuplexStream();
        using var handshakeGate = new ManualResetEventSlim(false);
        int created = 0;

        var factory = new FakeNacosChannelFactory(address =>
        {
            int index = Interlocked.Increment(ref created);

            return index == 1
                ? new FakeNacosChannel(address, FakeServerCheck.Responding(), first)
                : new FakeNacosChannel(address, Reconnect(handshakeGate), second);
        });

        await using var connection = CreateConnection(
            factory,
            requestTimeout: TimeSpan.FromMilliseconds(300));

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        first.EndStream();

        await WaitForConditionAsync(
            () => Volatile.Read(ref created) >= 2,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        DateTimeOffset start = DateTimeOffset.UtcNow;

        await Should.ThrowAsync<NacosConnectionException>(
            () => connection.RequestAsync<ConfigQueryResponse>(
                new ConfigQueryRequest { DataId = "a", Group = "g" },
                TestContext.Current.CancellationToken));

        // The handshake is still held open, so only the request's own budget can end the wait.
        (DateTimeOffset.UtcNow - start).ShouldBeLessThan(TimeSpan.FromSeconds(3));

        handshakeGate.Set();

        await WaitForConditionAsync(
            () => connection.State == NacosTransportState.Connected,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Builds a handler for the channel a reconnect is carried on.
    /// </summary>
    /// <param name="gate">The gate that holds the handshake open.</param>
    /// <returns>The handler.</returns>
    /// <remarks>
    /// Holding the handshake open puts the client in the state this behaviour is about: a
    /// connection that is neither usable nor abandoned, so a test can observe a request that is
    /// waiting for it rather than racing the reconnect.
    /// </remarks>
    private static Func<Payload, Payload> Reconnect(ManualResetEventSlim gate) => request =>
    {
        if (PayloadCodec.ReadTypeName(request) == "ServerCheckRequest")
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return FakeServerCheck.Responding()(request);
        }

        return PayloadCodec.Encode(new ConfigQueryResponse
        {
            Content = "reconnected",
            Md5 = "after",
        });
    };

    private static NacosGrpcConnection CreateConnection(
        FakeNacosChannelFactory factory,
        IReadOnlyList<NacosServerAddress>? servers = null,
        TimeProvider? timeProvider = null,
        NacosConnectionModule module = NacosConnectionModule.Config,
        IReadOnlyDictionary<string, string>? extraLabels = null,
        bool optional = false,
        TimeSpan? requestTimeout = null,
        TimeSpan? reconnectDelay = null,
        TimeSpan? connectionTimeout = null)
    {
        var options = new NacosOptions
        {
            AppName = "test-app",
            ConnectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(5),
            Optional = optional,
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5),
            Reconnect = new NacosReconnectOptions
            {
                // Small real delays keep reconnect tests fast without hanging: a fake clock would
                // never elapse the backoff timer, so the retry loop would block forever.
                InitialDelay = reconnectDelay ?? TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
                JitterFactor = 0,
            },
        };

        return new NacosGrpcConnection(
            options,
            module,
            servers ?? [Server],
            factory,
            timeProvider ?? TimeProvider.System,
            NullLogger.Instance,
            new Random(1234),
            null,
            extraLabels);
    }

    private static async Task WaitForWriteCountAsync(
        FakeNacosDuplexStream stream,
        int expected,
        CancellationToken ct)
    {
        for (int i = 0; i < 500; i++)
        {
            lock (stream.Written)
            {
                if (stream.Written.Count >= expected)
                {
                    return;
                }
            }

            await Task.Delay(10, ct);
        }

        throw new TimeoutException($"The stream never received {expected} frame(s).");
    }

    private static async Task WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20, ct);
        }

        throw new TimeoutException("The expected condition was not met within the timeout.");
    }
}

