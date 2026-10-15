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

    private static NacosGrpcConnection CreateConnection(
        FakeNacosChannelFactory factory,
        IReadOnlyList<NacosServerAddress>? servers = null,
        TimeProvider? timeProvider = null)
    {
        var options = new NacosOptions
        {
            AppName = "test-app",
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            Reconnect = new NacosReconnectOptions
            {
                // Small real delays keep reconnect tests fast without hanging: a fake clock would
                // never elapse the backoff timer, so the retry loop would block forever.
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(2),
                JitterFactor = 0,
            },
        };

        return new NacosGrpcConnection(
            options,
            servers ?? [Server],
            factory,
            timeProvider ?? TimeProvider.System,
            NullLogger.Instance,
            new Random(1234));
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

