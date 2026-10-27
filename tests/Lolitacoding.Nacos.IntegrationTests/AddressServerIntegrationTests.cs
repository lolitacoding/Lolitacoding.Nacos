using System.Net;
using System.Net.Sockets;
using System.Text;
using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies that a client can be pointed at an address server instead of at fixed addresses.
/// </summary>
/// <remarks>
/// The address server is a plain HTTP resource that returns one Nacos server per line. A stub is
/// used rather than a second Nacos container because the resource is not part of Nacos itself: any
/// deployment can publish it, so what matters is that this client requests the right URL and then
/// talks to the server that URL named.
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AddressServerIntegrationTests
{
    [Fact]
    public async Task Client_ConnectsToTheServerTheAddressServerPublishes()
    {
        await using NacosContainer container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image);
        await using var addressServer = new StubAddressServer(container.HttpAddress);

        var options = new NacosOptions
        {
            Endpoint = "127.0.0.1",
            EndpointPort = addressServer.Port,
            ConnectionTimeout = TimeSpan.FromSeconds(15),
            Reconnect = new NacosReconnectOptions
            {
                InitialDelay = TimeSpan.FromMilliseconds(500),
                MaxDelay = TimeSpan.FromSeconds(5),
                JitterFactor = 0.1,
            },
        };

        await using NacosClientFactory client = NacosClientFactory.Create(
            options,
            httpClient: null,
            channelFactory: null,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance);

        await client.ConnectAsync(TestContext.Current.CancellationToken);

        // The request reached the resource the Java client asks for, under the default context path.
        addressServer.Requests.ShouldContain(request =>
            request.StartsWith("GET /nacos/serverlist HTTP/1.1", StringComparison.Ordinal));

        client.Config.ServerStatus.ShouldBe(NacosServerStatus.Up);
        client.Naming.ServerStatus.ShouldBe(NacosServerStatus.Up);

        // A round trip proves the address in the published list is the one actually dialled.
        var key = new NacosConfigKey(
            $"address-server-{Guid.NewGuid():N}", "DEFAULT_GROUP");

        await client.Config.PublishConfigAsync(
            key, "from-the-endpoint", "text", ct: TestContext.Current.CancellationToken);

        NacosConfigResult result = await NacosEventually.BecomesAsync(
            () => client.Config.GetConfigAsync(key, TestContext.Current.CancellationToken),
            read => read.Content == "from-the-endpoint",
            TestContext.Current.CancellationToken,
            tolerateFailures: true);

        result.Content.ShouldBe("from-the-endpoint");
    }

    /// <summary>
    /// A minimal address server: it answers every request with the same body and closes.
    /// </summary>
    private sealed class StubAddressServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _body;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _loop;

        public StubAddressServer(string body)
        {
            _body = body;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => ServeAsync(_shutdown.Token), CancellationToken.None);
        }

        public int Port { get; }

        public List<string> Requests { get; } = [];

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            _listener.Stop();

            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The loop ends by cancellation.
            }
            catch (SocketException)
            {
                // Stopping the listener is how the loop is unblocked.
            }

            _shutdown.Dispose();
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient connection;

                try
                {
                    connection = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }

                using (connection)
                {
                    NetworkStream stream = connection.GetStream();
                    await using (stream.ConfigureAwait(false))
                    {
                        string? requestLine = await ReadRequestLineAsync(stream, ct).ConfigureAwait(false);

                        if (requestLine is not null)
                        {
                            lock (Requests)
                            {
                                Requests.Add(requestLine);
                            }
                        }

                        byte[] body = Encoding.UTF8.GetBytes(_body);
                        string head = string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");

                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
                        await stream.WriteAsync(body, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>
        /// Reads the request line and discards the rest of the headers.
        /// </summary>
        /// <param name="stream">The connection.</param>
        /// <param name="ct">A token that cancels the read.</param>
        /// <returns>The request line, or <see langword="null"/> when the peer said nothing.</returns>
        private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[1024];
            var received = new StringBuilder();

            while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);

                if (read == 0)
                {
                    return null;
                }

                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            string text = received.ToString();
            int end = text.IndexOf("\r\n", StringComparison.Ordinal);

            return end < 0 ? text : text[..end];
        }
    }
}
