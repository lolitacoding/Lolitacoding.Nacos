using System.Diagnostics;
using System.Diagnostics.Metrics;
using Lolitacoding.Nacos.Diagnostics;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Tests.Transport;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Diagnostics;

/// <summary>
/// Groups every diagnostics test into a collection that never runs in parallel with the rest of
/// the suite.
/// </summary>
/// <remarks>
/// The metric instruments are process-wide singletons, so a listener attached here also observes
/// the traffic produced by every other test class. Running this collection on its own removes
/// that cross-talk and lets the assertions be exact.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NacosDiagnosticsTestGroup
{
    /// <summary>The collection name shared by the diagnostics tests.</summary>
    public const string Name = "Nacos diagnostics";
}

/// <summary>
/// Verifies the telemetry the client publishes through <see cref="NacosDiagnostics"/>.
/// </summary>
[Collection(NacosDiagnosticsTestGroup.Name)]
public sealed class NacosDiagnosticsTests
{
    /// <summary>
    /// A server address used only by these tests, so activities can be picked out of the shared
    /// source even if a listener is attached while other telemetry is flowing.
    /// </summary>
    private static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:18848");

    [Fact]
    public void Diagnostics_ExposeStableSourceAndMeterNames()
    {
        NacosDiagnostics.ActivitySourceName.ShouldBe("Lolitacoding.Nacos");
        NacosDiagnostics.MeterName.ShouldBe("Lolitacoding.Nacos");

        NacosDiagnostics.ActivitySource.Name.ShouldBe(NacosDiagnostics.ActivitySourceName);
        NacosDiagnostics.Meter.Name.ShouldBe(NacosDiagnostics.MeterName);
    }

    [Fact]
    public void Meter_PublishesTheDocumentedInstruments()
    {
        var published = new List<string>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == NacosDiagnostics.MeterName)
                {
                    published.Add(instrument.Name);
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.Start();

        // Touching the instrument holder forces the instruments to be created, which also fires
        // InstrumentPublished for a listener that started before they existed.
        _ = NacosMetrics.Requests;

        published.ShouldContain("nacos.client.requests");
        published.ShouldContain("nacos.client.request.duration");
        published.ShouldContain("nacos.client.reconnects");
        published.ShouldContain("nacos.client.pushes");
        published.ShouldContain("nacos.client.connection.state.changes");
    }

    [Fact]
    public async Task RequestAsync_RecordsAnActivityAndTheRequestMetrics()
    {
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(address => new FakeNacosChannel(
            address,
            request => PayloadCodec.ReadTypeName(request) == "ServerCheckRequest"
                ? FakeServerCheck.Responding()(request)
                : PayloadCodec.Encode(new ConfigQueryResponse { Content = "hello" }),
            stream));

        var activities = new List<Activity>();
        using var activityListener = ListenToActivities(activities);

        long requests = 0;
        int durations = 0;

        using var meterListener = new MeterListener
        {
            InstrumentPublished = PublishEveryInstrument,
        };

        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "nacos.client.requests"
                && HasTag(tags, NacosDiagnostics.RequestTypeTag, "ConfigQueryRequest")
                && HasTag(tags, NacosDiagnostics.OutcomeTag, NacosDiagnostics.OutcomeSuccess))
            {
                Interlocked.Add(ref requests, measurement);
            }
        });

        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (instrument.Name == "nacos.client.request.duration"
                && HasTag(tags, NacosDiagnostics.RequestTypeTag, "ConfigQueryRequest"))
            {
                Interlocked.Increment(ref durations);
            }
        });

        meterListener.Start();

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        // The connect activity shares the server address tag, so only the request span is kept.
        activities.Clear();

        await connection.RequestAsync<ConfigQueryResponse>(
            new ConfigQueryRequest { DataId = "a", Group = "g" },
            TestContext.Current.CancellationToken);

        Activity requestActivity = activities
            .Where(activity => activity.OperationName == NacosDiagnostics.RequestActivityName)
            .Single(activity => Equals(activity.GetTagItem(NacosDiagnostics.ServerAddressTag), Server.Original));

        requestActivity.Kind.ShouldBe(ActivityKind.Client);
        requestActivity.GetTagItem(NacosDiagnostics.RequestTypeTag).ShouldBe("ConfigQueryRequest");
        requestActivity.Status.ShouldBe(ActivityStatusCode.Ok);

        requests.ShouldBe(1);
        durations.ShouldBe(1);
    }

    [Fact]
    public async Task RequestAsync_RecordsAnErrorActivityAndOutcomeWhenTheServerFails()
    {
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(address => new FakeNacosChannel(
            address,
            request => PayloadCodec.ReadTypeName(request) == "ServerCheckRequest"
                ? FakeServerCheck.Responding()(request)
                : PayloadCodec.Encode(new ErrorResponse
                {
                    ResultCode = NacosResponse.FailureCode,
                    ErrorCode = 500,
                    Message = "boom",
                }),
            stream));

        var activities = new List<Activity>();
        using var activityListener = ListenToActivities(activities);

        long errors = 0;
        using var meterListener = new MeterListener { InstrumentPublished = PublishEveryInstrument };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "nacos.client.requests"
                && HasTag(tags, NacosDiagnostics.OutcomeTag, NacosDiagnostics.OutcomeError))
            {
                Interlocked.Add(ref errors, measurement);
            }
        });
        meterListener.Start();

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        activities.Clear();

        await Should.ThrowAsync<NacosServerException>(
            () => connection.RequestAsync<ConfigQueryResponse>(
                new ConfigQueryRequest { DataId = "a", Group = "g" },
                TestContext.Current.CancellationToken));

        Activity requestActivity = activities
            .Single(activity => activity.OperationName == NacosDiagnostics.RequestActivityName
                && Equals(activity.GetTagItem(NacosDiagnostics.ServerAddressTag), Server.Original));

        requestActivity.Status.ShouldBe(ActivityStatusCode.Error);
        requestActivity.StatusDescription.ShouldBe("boom");
        errors.ShouldBe(1);
    }

    [Fact]
    public async Task ServerPush_RecordsThePushMetric()
    {
        var stream = new FakeNacosDuplexStream();
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        long pushes = 0;
        using var meterListener = new MeterListener { InstrumentPublished = PublishEveryInstrument };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "nacos.client.pushes"
                && HasTag(tags, NacosDiagnostics.PushTypeTag, "ConfigChangeNotifyRequest"))
            {
                Interlocked.Add(ref pushes, measurement);
            }
        });
        meterListener.Start();

        await using var connection = CreateConnection(factory);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        int handled = 0;
        connection.ServerRequestReceived += (_, _) =>
        {
            Interlocked.Increment(ref handled);
            return Task.FromResult<NacosResponse?>(null);
        };

        stream.Push(TestPayload.ForRequest(new ConfigChangeNotifyRequest
        {
            DataId = "appsettings.json",
            Group = "DEFAULT_GROUP",
        }));

        await WaitForConditionAsync(
            () => Volatile.Read(ref handled) == 1,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        pushes.ShouldBe(1);
    }

    [Fact]
    public async Task Connect_RecordsTheStateTransitionMetric()
    {
        var stream = new FakeNacosDuplexStream { AutoAckSetup = false };
        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), stream));

        long connecting = 0;
        long connected = 0;

        using var meterListener = new MeterListener { InstrumentPublished = PublishEveryInstrument };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name != "nacos.client.connection.state.changes")
            {
                return;
            }

            if (HasTag(tags, NacosDiagnostics.ConnectionStateTag, "Connecting"))
            {
                Interlocked.Add(ref connecting, measurement);
            }

            if (HasTag(tags, NacosDiagnostics.ConnectionStateTag, "Connected"))
            {
                Interlocked.Add(ref connected, measurement);
            }
        });
        meterListener.Start();

        await using var connection = CreateConnection(factory);

        Task connect = connection.ConnectAsync(TestContext.Current.CancellationToken);
        stream.Push(TestPayload.ForRequest(new SetupAckRequest { AbilityTable = [] }));
        await connect;

        connecting.ShouldBe(1);
        connected.ShouldBe(1);
    }

    [Fact]
    public async Task Reconnect_RecordsTheReconnectMetric()
    {
        var first = new FakeNacosDuplexStream();
        var second = new FakeNacosDuplexStream();
        var streams = new Queue<FakeNacosDuplexStream>([first, second]);

        var factory = new FakeNacosChannelFactory(
            address => new FakeNacosChannel(address, FakeServerCheck.Responding(), streams.Dequeue()));

        long reconnects = 0;
        using var meterListener = new MeterListener { InstrumentPublished = PublishEveryInstrument };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "nacos.client.reconnects")
            {
                Interlocked.Add(ref reconnects, measurement);
            }
        });
        meterListener.Start();

        await using var connection = CreateConnection(factory);

        int reconnected = 0;
        connection.Reconnected += (_, _) =>
        {
            Interlocked.Increment(ref reconnected);
            return Task.CompletedTask;
        };

        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        first.EndStream();

        await WaitForConditionAsync(
            () => Volatile.Read(ref reconnected) == 1,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        reconnects.ShouldBe(1);
    }

    private static ActivityListener ListenToActivities(List<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NacosDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void PublishEveryInstrument(Instrument instrument, MeterListener listener)
    {
        if (instrument.Meter.Name == NacosDiagnostics.MeterName)
        {
            listener.EnableMeasurementEvents(instrument);
        }
    }

    private static bool HasTag(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        string key,
        object? value)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == key)
            {
                return Equals(tag.Value, value);
            }
        }

        return false;
    }

    private static NacosGrpcConnection CreateConnection(FakeNacosChannelFactory factory) =>
        new(
            new NacosOptions
            {
                AppName = "diagnostics-tests",
                ConnectionTimeout = TimeSpan.FromSeconds(5),
                Reconnect = new NacosReconnectOptions
                {
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    MaxDelay = TimeSpan.FromMilliseconds(2),
                    JitterFactor = 0,
                },
            },
            NacosConnectionModule.Config,
            [Server],
            factory,
            TimeProvider.System,
            NullLogger.Instance,
            new Random(1234));

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
