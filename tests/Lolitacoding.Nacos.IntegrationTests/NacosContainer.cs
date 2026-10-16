using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Starts a Nacos container and exposes its endpoints to the tests.
/// </summary>
/// <remarks>
/// <para>
/// The container is built directly rather than through a compose file so each test class can
/// choose its own image and authentication setting.
/// </para>
/// <para>
/// Host ports are chosen as a consecutive pair rather than assigned independently. That is not
/// cosmetic: a Nacos client derives the gRPC endpoint as the main port plus 1000, so a container
/// whose two mapped ports were not exactly 1000 apart would send gRPC traffic to the wrong place,
/// and every test would fail for a reason unrelated to what it checks.
/// </para>
/// <para>
/// All published ports matter: the main HTTP API and the gRPC transport. Subscribing to
/// configuration or service changes happens over gRPC, so a container that published only the
/// HTTP port could not run the change-notification tests at all.
/// </para>
/// </remarks>
internal sealed class NacosContainer : IAsyncDisposable
{
    /// <summary>
    /// The number of times to retry a start Docker refused because the host port was taken.
    /// </summary>
    private const int MaxStartAttempts = 5;

    /// <summary>The lowest host port a container's ports are taken from.</summary>
    private const int MinPort = 20000;

    /// <summary>The bound the host ports are taken below.</summary>
    private const int MaxPort = 60000;

    /// <summary>How many port pairs to consider before giving up on finding a free one.</summary>
    private const int PortPairAttempts = 32;

    /// <summary>
    /// The pair of ports the next container will try.
    /// </summary>
    /// <remarks>
    /// Advanced for every start so that two containers in this process never aim at the same pair.
    /// That is the part that matters, because a port Docker has allocated to another container cannot
    /// be seen as taken from here: the start fails with "port is already allocated" even though the
    /// port can be bound. Only the starts read and write the cursor, and they are serialized.
    /// </remarks>
    private static int _nextPort = MinPort;

    /// <summary>
    /// Serializes the starts, so that only one server is brought up at a time.
    /// </summary>
    /// <remarks>
    /// The tests run in parallel, and each of them starts a server. Bringing them up together is what
    /// breaks: the port pair one test looks for is free at the moment another test looks at it, so
    /// both try to bind the same ports and one start fails. Several JVMs coming up at once on a
    /// development machine also make readiness take minutes. Starting one at a time costs far less
    /// than the failures it avoids, and the test bodies still run in parallel afterwards.
    /// </remarks>
    private static readonly SemaphoreSlim StartGate = new(1, 1);

    /// <summary>How long to wait for another start in this process to finish.</summary>
    private static readonly TimeSpan StartGateTimeout = TimeSpan.FromMinutes(15);

    private readonly IContainer _container;

    private NacosContainer(IContainer container, ushort httpPort)
    {
        _container = container;
        HttpPort = httpPort;
    }

    /// <summary>Gets the mapped host port for the Nacos HTTP API.</summary>
    public ushort HttpPort { get; }

    /// <summary>Gets the mapped host port for the Nacos gRPC transport.</summary>
    public ushort GrpcPort => (ushort)(HttpPort + NacosIntegrationImages.GrpcPortOffset);

    /// <summary>Gets the HTTP base address of the running server.</summary>
    public string HttpAddress => $"http://{_container.Hostname}:{HttpPort}";

    /// <summary>
    /// Starts a Nacos container.
    /// </summary>
    /// <param name="image">The image to run.</param>
    /// <param name="authEnabled">Whether to enable the built-in authentication plugin.</param>
    /// <returns>The started container.</returns>
    /// <exception cref="TimeoutException">Another start in this process did not finish in time.</exception>
    public static async Task<NacosContainer> StartAsync(string image, bool authEnabled = false)
    {
        if (!await StartGate.WaitAsync(StartGateTimeout).ConfigureAwait(false))
        {
            throw new TimeoutException(
                "Timed out waiting for another Nacos container in this process to finish starting.");
        }

        try
        {
            return await StartOnceAsync(image, authEnabled).ConfigureAwait(false);
        }
        finally
        {
            StartGate.Release();
        }
    }

    /// <summary>
    /// Builds and starts a container, retrying when Docker refuses the host ports.
    /// </summary>
    /// <param name="image">The image to run.</param>
    /// <param name="authEnabled">Whether to enable the built-in authentication plugin.</param>
    /// <returns>The started container.</returns>
    /// <exception cref="DockerApiException">Docker could not start the container.</exception>
    private static async Task<NacosContainer> StartOnceAsync(string image, bool authEnabled)
    {
        for (int attempt = 1; ; attempt++)
        {
            // Both host ports must be reserved before the container starts, and the gRPC one must be
            // exactly 1000 above the HTTP one, so the pair is found first and then bound explicitly.
            ushort httpPort = FindConsecutivePortPair();
            IContainer container = Build(image, authEnabled, httpPort).Build();

            try
            {
                await container.StartAsync().ConfigureAwait(false);

                var started = new NacosContainer(container, httpPort);

                if (authEnabled)
                {
                    await started.EnsureInitialAdminAsync().ConfigureAwait(false);
                }

                return started;
            }
            catch (DockerApiException exception)
                when (attempt < MaxStartAttempts && IsPortConflict(exception))
            {
                // The pair was taken by something the probe could not see. The next attempt probes
                // from beyond it, because the cursor moved on when the pair was handed out, and the
                // container has to go before another pair is tried.
                await container.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Describes the container to run.
    /// </summary>
    /// <param name="image">The image to run.</param>
    /// <param name="authEnabled">Whether to enable the built-in authentication plugin.</param>
    /// <param name="httpPort">The host port to publish the HTTP API on.</param>
    /// <returns>The builder for the container.</returns>
    private static ContainerBuilder Build(string image, bool authEnabled, ushort httpPort) =>
        new ContainerBuilder(image)
            .WithEnvironment("MODE", "standalone")
            .WithEnvironment("NACOS_AUTH_ENABLE", authEnabled ? "true" : "false")
            // Nacos 3.2 refuses to start without a signing key and a server identity, whether or not
            // the authentication plugin is enabled, so all three are always supplied. This is also
            // why an auth-disabled server still needs <see cref="NacosIntegrationImages.AuthToken"/>.
            .WithEnvironment("NACOS_AUTH_TOKEN", NacosIntegrationImages.AuthToken)
            .WithEnvironment("NACOS_AUTH_IDENTITY_KEY", NacosIntegrationImages.AuthIdentityKey)
            .WithEnvironment("NACOS_AUTH_IDENTITY_VALUE", NacosIntegrationImages.AuthIdentityValue)
            .WithEnvironment("JVM_XMS", "512m")
            .WithEnvironment("JVM_XMX", "512m")
            .WithEnvironment("JVM_XMN", "256m")
            .WithPortBinding(httpPort, NacosIntegrationImages.HttpPort)
            .WithPortBinding(
                httpPort + NacosIntegrationImages.GrpcPortOffset,
                NacosIntegrationImages.GrpcPort)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(
                    request => request
                        .ForPort(NacosIntegrationImages.HttpPort)
                        // Readiness is the only endpoint that reports "the server can serve traffic";
                        // the liveness probe returns 200 while the server is still loading data.
                        .ForPath(NacosReadiness.Path)
                        // A success status code is not enough: the endpoint answers 200 while it is
                        // still reporting that a module is not ready, so the body decides.
                        .ForResponseMessageMatching(NacosReadiness.IsReadyAsync),
                    wait => wait.WithTimeout(TimeSpan.FromMinutes(3))));

    /// <summary>
    /// Reports whether Docker refused a start because a host port was already taken.
    /// </summary>
    /// <param name="exception">The failure Docker reported.</param>
    /// <returns><see langword="true"/> when another pair of ports is worth trying.</returns>
    private static bool IsPortConflict(DockerApiException exception) =>
        exception.Message.Contains("ports are not available", StringComparison.Ordinal)
        || exception.Message.Contains("failed to set up container networking", StringComparison.Ordinal);

    /// <summary>
    /// Creates the administrator an auth-enabled server is tested with.
    /// </summary>
    /// <returns>A task that completes once the account exists.</returns>
    /// <exception cref="InvalidOperationException">The server refused to create the account.</exception>
    /// <remarks>
    /// A Nacos 3.x server ships no accounts, so every login is rejected until the first administrator
    /// is created through the setup endpoint the console uses. The endpoint refuses once a global
    /// administrator exists, which is what a server that was restarted answers, so that result is
    /// accepted as well.
    /// </remarks>
    private async Task EnsureInitialAdminAsync()
    {
        using var http = new HttpClient();

        string url = string.Concat(
            HttpAddress,
            NacosIntegrationImages.InitialAdminPath,
            "?password=",
            Uri.EscapeDataString(NacosIntegrationImages.DefaultPassword));

        using HttpResponseMessage response = await http.PostAsync(
            url, content: null).ConfigureAwait(false);

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
        {
            throw new InvalidOperationException(string.Concat(
                "Could not create the initial Nacos administrator: the server answered ",
                (int)response.StatusCode,
                " ",
                await response.Content.ReadAsStringAsync().ConfigureAwait(false)));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Stops the container, which drops every connection to it.
    /// </summary>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes once the container has stopped.</returns>
    /// <remarks>
    /// The container is only stopped, never removed, so that <see cref="StartAsync"/> can bring the
    /// same instance back and the reconnect tests keep the server identity they are asserting on.
    /// </remarks>
    public Task StopAsync(CancellationToken ct = default) => _container.StopAsync(ct);

    /// <summary>
    /// Restarts a container that <see cref="StopAsync"/> previously stopped.
    /// </summary>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>A task that completes once the server is serving traffic again.</returns>
    /// <remarks>
    /// The wait strategy only runs on the first start, so the restarted server is waited for here.
    /// The same readiness body is what the first start waits on.
    /// </remarks>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _container.StartAsync(ct).ConfigureAwait(false);
        await NacosReadiness.WaitAsync(HttpAddress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds a pair of free TCP ports that are exactly 1000 apart.
    /// </summary>
    /// <returns>The lower port of the pair.</returns>
    /// <remarks>
    /// The pair is probed by binding a listener, which cannot be relied on: a port Docker has
    /// allocated to another container can still be bound, and the start then fails with "port is
    /// already allocated". The cursor is what keeps two containers in this process apart, and a
    /// collision that reaches Docker is retried on the next pair by <see cref="StartOnceAsync"/>.
    /// </remarks>
    private static ushort FindConsecutivePortPair()
    {
        const int Offset = NacosIntegrationImages.GrpcPortOffset;

        for (int attempt = 0; attempt < PortPairAttempts; attempt++)
        {
            int candidate = _nextPort;
            _nextPort = candidate + Offset + 1 > MaxPort - Offset ? MinPort : candidate + Offset + 1;

            if (IsFree(candidate) && IsFree(candidate + Offset))
            {
                return (ushort)candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not find a free pair of TCP ports 1000 apart to run the Nacos container on.");
    }

    /// <summary>
    /// Reports whether a TCP port can be bound.
    /// </summary>
    /// <param name="port">The port to probe.</param>
    /// <returns><see langword="true"/> when the port is free.</returns>
    /// <remarks>
    /// The wildcard address is probed rather than the loopback one, because that is how Docker
    /// publishes a port. A probe on the loopback address alone can report a port as free while a
    /// container is publishing it on every interface, and the start then fails on the port.
    /// </remarks>
    private static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
