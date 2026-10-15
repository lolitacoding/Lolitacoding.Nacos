using System.Net;
using System.Net.Sockets;
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
/// All three published ports matter: the main HTTP API, the gRPC transport, and the gRPC TLS
/// port. Subscribing to configuration or service changes happens over gRPC, so a container that
/// published only the HTTP port could not run the change-notification tests at all.
/// </para>
/// </remarks>
internal sealed class NacosContainer : IAsyncDisposable
{
    /// <summary>The gRPC TLS port inside the container.</summary>
    private const int GrpcTlsPort = 9849;

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
    public static async Task<NacosContainer> StartAsync(string image, bool authEnabled = false)
    {
        // Both host ports must be reserved before the container starts, and the gRPC one must be
        // exactly 1000 above the HTTP one, so the pair is found first and then bound explicitly.
        ushort httpPort = FindConsecutivePortPair();

        var builder = new ContainerBuilder(image)
            .WithEnvironment("MODE", "standalone")
            .WithEnvironment("NACOS_AUTH_ENABLE", authEnabled ? "true" : "false")
            .WithEnvironment("JVM_XMS", "512m")
            .WithEnvironment("JVM_XMX", "512m")
            .WithEnvironment("JVM_XMN", "256m")
            .WithPortBinding(httpPort, NacosIntegrationImages.HttpPort)
            .WithPortBinding(
                httpPort + NacosIntegrationImages.GrpcPortOffset,
                NacosIntegrationImages.GrpcPort)
            .WithPortBinding(
                httpPort + NacosIntegrationImages.GrpcTlsPortOffset,
                GrpcTlsPort)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request
                    .ForPort(NacosIntegrationImages.HttpPort)
                    // Readiness is the only endpoint that reports "the server can serve traffic";
                    // the liveness probe returns 200 while the server is still loading data.
                    .ForPath("/nacos/v1/console/health/readiness"),
                    wait => wait.WithTimeout(TimeSpan.FromMinutes(3))));

        if (authEnabled)
        {
            // Nacos refuses to start with authentication enabled unless the signing key is set.
            builder = builder
                .WithEnvironment("NACOS_AUTH_TOKEN", NacosIntegrationImages.AuthToken)
                .WithEnvironment("NACOS_AUTH_IDENTITY_KEY", "serverIdentity")
                .WithEnvironment("NACOS_AUTH_IDENTITY_VALUE", "security");
        }

        IContainer container = builder.Build();
        await container.StartAsync().ConfigureAwait(false);

        return new NacosContainer(container, httpPort);
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
    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    /// <summary>
    /// Finds a pair of free TCP ports that are exactly 1000 apart.
    /// </summary>
    /// <returns>The lower port of the pair.</returns>
    /// <remarks>
    /// The pair is probed by binding a listener, which is inherently racy: another process can
    /// take the port between the probe and the container binding it. Retrying the whole start
    /// would be the robust fix, but a collision here fails loudly on a port binding error rather
    /// than producing a misleading test failure, so the simple probe is adequate.
    /// </remarks>
    private static ushort FindConsecutivePortPair()
    {
        const int Offset = NacosIntegrationImages.GrpcPortOffset;
        const int MinPort = 20000;
        const int MaxPort = 60000;

        for (int candidate = MinPort; candidate + Offset < MaxPort; candidate += Offset + 1)
        {
            if (IsFree(candidate) && IsFree(candidate + Offset))
            {
                return (ushort)candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not find a free pair of TCP ports 1000 apart to run the Nacos container on.");
    }

    private static bool IsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
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
