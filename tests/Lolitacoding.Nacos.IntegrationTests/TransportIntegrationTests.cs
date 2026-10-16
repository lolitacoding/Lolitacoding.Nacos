using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies the gRPC transport against a real Nacos 3.x server.
/// </summary>
/// <remarks>
/// The protocol implementation is only as good as its agreement with a real server, so these
/// tests exercise the handshake, a request/response round trip and reconnection after the server
/// is restarted.
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class TransportIntegrationTests
{
    [Fact]
    public async Task Connect_CompletesTheHandshakeAgainstARealServer()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);

        await using NacosGrpcConnection connection = CreateConnection(container);

        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        connection.State.ShouldBe(NacosTransportState.Connected);
        connection.ConnectionId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Connect_ReceivesServerAbilities()
    {
        // A Nacos 3.x server supports ability negotiation, so the acknowledgement must arrive and
        // carry the server's capability table. An empty table here would mean the client failed
        // to advertise its own abilities and the server stayed silent.
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);

        await using NacosGrpcConnection connection = CreateConnection(container);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        connection.ServerAbilities.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task RequestAsync_RoundTripsAConfigQuery()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);

        string dataId = NacosHttp.UniqueId("transport-it");
        string content = NacosHttp.ConfigContent("round-trip");

        await using NacosGrpcConnection connection = CreateConnection(container);
        await connection.ConnectAsync(TestContext.Current.CancellationToken);

        // The configuration is published over the transport that reads it back rather than over the
        // administration API: as well as keeping this a transport test, that write API demands a token
        // even on a server whose authentication plugin is disabled, which is what this server is.
        ConfigPublishResponse published = await connection.RequestAsync<ConfigPublishResponse>(
            new ConfigPublishRequest
            {
                DataId = dataId,
                Group = "DEFAULT_GROUP",
                Tenant = string.Empty,
                Content = content,
            },
            TestContext.Current.CancellationToken);

        published.Success.ShouldBeTrue();

        // The server can report the configuration as missing for a moment after the publish, so the
        // query is repeated while it does. The server reports the content either inline or as a
        // locatable reference depending on size, so only a successful round trip is asserted.
        ConfigQueryResponse response = await NacosEventually.BecomesAsync(
            () => connection.RequestAsync<ConfigQueryResponse>(
                new ConfigQueryRequest
                {
                    DataId = dataId,
                    Group = "DEFAULT_GROUP",
                    Tenant = string.Empty,
                },
                TestContext.Current.CancellationToken),
            result => result.Success,
            TestContext.Current.CancellationToken,
            tolerateFailures: true);

        response.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task ServerRestart_IsRecoveredAutomatically()
    {
        // PLAN.md's acceptance criterion for T1.3 is that killing and restarting the container
        // restores the connection without any action from the caller.
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);

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

    [Fact]
    public async Task DisposeAsync_ShutsDownCleanly()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);

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
            NacosConnectionModule.Config,
            servers,
            new GrpcNacosChannelFactory(options.RequestTimeout),
            TimeProvider.System,
            NullLogger.Instance);
    }
}
