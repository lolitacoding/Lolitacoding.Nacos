using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Locking;
using Lolitacoding.Nacos.Protocol;
using Lolitacoding.Nacos.Protocol.Generated;
using Lolitacoding.Nacos.Protocol.Messages;
using Lolitacoding.Nacos.Tests.Transport;
using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Client;

/// <summary>
/// Verifies how <see cref="NacosClientFactory"/> wires the clients to their connections.
/// </summary>
/// <remarks>
/// The property that matters is that the domains do not share a connection: Nacos routes each
/// connection by the module label it declares, and one connection cannot declare two modules.
/// </remarks>
public sealed class NacosClientFactoryTests
{
    [Fact]
    public async Task Create_OpensOneConnectionPerModuleCarryingItsLabel()
    {
        var streams = new List<FakeNacosDuplexStream>();

        var channels = new FakeNacosChannelFactory(address =>
        {
            var stream = new FakeNacosDuplexStream();
            streams.Add(stream);
            return new FakeNacosChannel(address, FakeServerCheck.Responding(), stream);
        });

        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "factory-tests",
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };

        await using NacosClientFactory client = NacosClientFactory.Create(
            options,
            httpClient: null,
            channelFactory: channels,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance,
            random: new Random(1234));

        await client.ConnectAsync(TestContext.Current.CancellationToken);

        // A channel per module, and every connection declares the module it serves.
        channels.RequestedAddresses.Count.ShouldBe(2);
        streams.Count.ShouldBe(2);

        string[] modules = streams
            .Select(stream => PayloadCodec.Decode<ConnectionSetupRequest>(stream.Written[0])!)
            .Select(setup => setup.Labels![ProtocolHeaders.Module])
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToArray();

        modules.ShouldBe(["config", "naming"]);
    }

    [Fact]
    public async Task Create_WithAnAddressServer_DialsThePublishedServerInsteadOfTheConfiguredOne()
    {
        // The endpoint is the authority on which nodes exist, so a client that has one does not
        // also dial the addresses in configuration.
        var channels = new FakeNacosChannelFactory(address =>
            new FakeNacosChannel(address, FakeServerCheck.Responding(), new FakeNacosDuplexStream()));

        var options = new NacosOptions
        {
            Endpoint = "address-server",
            ServerAddresses = ["127.0.0.1:9999"],
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };

        using var http = new HttpClient(new AddressServerHandler("127.0.0.1:8848"));

        await using NacosClientFactory client = NacosClientFactory.Create(
            options,
            httpClient: http,
            channelFactory: channels,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance,
            random: new Random(1234));

        await client.ConnectAsync(TestContext.Current.CancellationToken);

        channels.RequestedAddresses.ShouldNotBeEmpty();
        channels.RequestedAddresses.ShouldAllBe(address => address.Port == 8848);
    }

    [Fact]
    public async Task DisposeAsync_DisposesEveryConnection()
    {
        var streams = new List<FakeNacosDuplexStream>();

        var channels = new FakeNacosChannelFactory(address =>
        {
            var stream = new FakeNacosDuplexStream();
            streams.Add(stream);
            return new FakeNacosChannel(address, FakeServerCheck.Responding(), stream);
        });

        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };

        NacosClientFactory client = NacosClientFactory.Create(
            options,
            httpClient: null,
            channelFactory: channels,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance,
            random: new Random(1234));

        await client.ConnectAsync(TestContext.Current.CancellationToken);
        await client.DisposeAsync();

        // Every stream the module connections opened must be released, or a disposed client would
        // keep its server-side registrations alive.
        streams.ShouldAllBe(stream => stream.Disposed);
    }

    [Fact]
    public async Task Lock_OpensItsConnectionOnFirstUseRatherThanAtStartup()
    {
        // The lock is a module most applications never use, so connecting it eagerly would buy a
        // handshake, a heartbeat and a socket per cluster node for nothing.
        var streams = new List<FakeNacosDuplexStream>();

        var channels = new FakeNacosChannelFactory(address =>
        {
            var stream = new FakeNacosDuplexStream();
            streams.Add(stream);
            return new FakeNacosChannel(address, RespondingWithLockSupport(), stream);
        });

        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AppName = "factory-tests",
            ConnectionTimeout = TimeSpan.FromSeconds(5),
        };

        await using NacosClientFactory client = NacosClientFactory.Create(
            options,
            httpClient: null,
            channelFactory: channels,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance,
            random: new Random(1234));

        await client.ConnectAsync(TestContext.Current.CancellationToken);

        channels.RequestedAddresses.Count.ShouldBe(2);

        NacosLockResult result = await client.Lock.AcquireAsync(
            new NacosLockKey("probe"), ct: TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        channels.RequestedAddresses.Count.ShouldBe(3);

        ConnectionSetupRequest setup =
            PayloadCodec.Decode<ConnectionSetupRequest>(streams[2].Written[0])!;
        setup.Labels![ProtocolHeaders.Module].ShouldBe("lock");

        await client.DisposeAsync();

        streams.ShouldAllBe(stream => stream.Disposed);
    }

    /// <summary>
    /// Answers the per-connection probe and grants any lock, so a connection can be established and
    /// then used without scripting each operation.
    /// </summary>
    /// <returns>The handler for a fake channel.</returns>
    private static Func<Payload, Payload> RespondingWithLockSupport() => request =>
        PayloadCodec.ReadTypeName(request) == "ServerCheckRequest"
            ? FakeServerCheck.Responding()(request)
            : PayloadCodec.Encode(new LockOperationResponse
            {
                Result = true,
                LockResult = new LockResult
                {
                    Success = true,
                    ReentrantCount = 1,
                    WaitPosition = -1,
                },
            });

    /// <summary>
    /// Answers the address server with a fixed list of Nacos servers.
    /// </summary>
    private sealed class AddressServerHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
    }
}
