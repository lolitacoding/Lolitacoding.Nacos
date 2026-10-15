using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the gRPC transport against real Nacos 2.x and 3.x servers.
/// </summary>
/// <remarks>
/// The protocol implementation is only as good as its agreement with a real server, so these
/// tests exercise the handshake, a request/response round trip and reconnection after the server
/// is restarted. Both server generations are covered because the client claims support for both.
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class TransportIntegrationTests
{
    /// <summary>Each server generation the client claims to support.</summary>
    public static TheoryData<string> Images => new()
    {
        NacosIntegrationImages.V2Image,
        NacosIntegrationImages.V3Image,
    };

    [Theory]
    [MemberData(nameof(Images))]
    public async Task Connect_CompletesTheHandshakeAgainstARealServer(string image)
    {
        await using NacosContainer container = await NacosContainer.StartAsync(image);

        await using NacosGrpcConnection connection = CreateConnection(container);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        connection.State.ShouldBe(NacosTransportState.Connected);
        connection.ConnectionId.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [MemberData(nameof(Images))]
    public async Task Connect_ReceivesServerAbilities(string image)
    {
        // Both 2.x and 3.x support ability negotiation, so the acknowledgement must arrive and
        // carry the server's capability table. An empty table here would mean the client failed
        // to advertise its own abilities and the server stayed silent.
        await using NacosContainer container = await NacosContainer.StartAsync(image);

        await using NacosGrpcConnection connection = CreateConnection(container);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        connection.ServerAbilities.ShouldNotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Images))]
    public async Task RequestAsync_RoundTripsAConfigQuery(string image)
    {
        await using NacosContainer container = await NacosContainer.StartAsync(image);

        string dataId = NacosHttp.UniqueId("transport-it");
        string content = NacosHttp.ConfigContent("round-trip");

        using (var http = new HttpClient())
        {
            await NacosHttp.PublishConfigAsync(
                http, container.HttpAddress, dataId, "DEFAULT_GROUP", content,
                accessToken: null, TestContext.Current.CancellationToken);
        }

        await using NacosGrpcConnection connection = CreateConnection(container);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        ConfigQueryResponse response = await connection.RequestAsync<ConfigQueryResponse>(
            new ConfigQueryRequest
            {
                DataId = dataId,
                Group = "DEFAULT_GROUP",
                Tenant = string.Empty,
            },
            TestContext.Current.CancellationToken);

        // The server reports the content either inline or as a locatable reference depending on
        // size; both are a successful query, so only the success of the round trip is asserted.
        response.Success.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Images))]
    public async Task ServerRestart_IsRecoveredAutomatically(string image)
    {
        // PLAN.md's acceptance criterion for T1.3 is that killing and restarting the container
        // restores the connection without any action from the caller.
        await using NacosContainer container = await NacosContainer.StartAsync(image);

        await using NacosGrpcConnection connection = CreateConnection(container);

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Reconnected += (_, _) =>
        {
            reconnected.TrySetResult();
            return Task.CompletedTask;
        };

        await connection.ConnectAsync(TestContext.Current.CancellationToken);
        connection.State.ShouldBe(NacosTransportState.Connected);

        // Stopping the container drops the stream, which is what the client must notice.
        await container.StopAsync(TestContext.Current.CancellationToken);

        // The handler fires only after a successful re-handshake, so waiting on it proves the
        // connection came back rather than merely that a retry was attempted.
        Task recovery = reconnected.Task.WaitAsync(
            TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        await container.StartAsync(TestContext.Current.CancellationToken);

        await recovery;

        connection.State.ShouldBe(NacosTransportState.Connected);
        connection.ConnectionId.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [MemberData(nameof(Images))]
    public async Task DisposeAsync_ShutsDownCleanly(string image)
    {
        await using NacosContainer container = await NacosContainer.StartAsync(image);

        NacosGrpcConnection connection = CreateConnection(container);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        await connection.DisposeAsync();

        connection.State.ShouldBe(NacosTransportState.Shutdown);
    }

    /// <summary>
    /// An envelope for a payload type this client does not know about.
    /// </summary>
    /// <remarks>
    /// Used to prove that an unrecognized push is ignored rather than fatal.
    /// </remarks>
    internal static Payload UnknownPayload() => new()
    {
        Metadata = new Metadata { Type = "SomeFutureServerPush" },
    };

    private static NacosGrpcConnection CreateConnection(NacosContainer container)
    {
        var options = new NacosOptions
        {
            AppName = "integration-tests",
            ConnectionTimeout = TimeSpan.FromSeconds(15),
            Reconnect = new NacosReconnectOptions
            {
                InitialDelay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
                JitterFactor = 0.1,
            },
        };

        IReadOnlyList<NacosServerAddress> servers =
            NacosServerAddress.ParseAll([container.HttpAddress]);

        return new NacosGrpcConnection(
            options,
            servers,
            new GrpcNacosChannelFactory(options.RequestTimeout),
            TimeProvider.System,
            NullLogger.Instance);
    }
}
