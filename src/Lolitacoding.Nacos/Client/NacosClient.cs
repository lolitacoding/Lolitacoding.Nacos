using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Naming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Client;

/// <summary>
/// The entry point for programmatic access to Nacos configuration and service discovery.
/// </summary>
/// <remarks>
/// <para>
/// One client shares a single gRPC connection between the configuration centre and the naming
/// service. That is not merely an optimisation: Nacos multiplexes both domains over one stream, so
/// a second connection would duplicate the handshake and heartbeat work against the server for no
/// benefit.
/// </para>
/// <para>
/// A client is safe for concurrent use and is intended to live for the lifetime of the process.
/// Create it once, connect it, and dispose it during shutdown.
/// </para>
/// </remarks>
public sealed class NacosClient : IAsyncDisposable, IDisposable
{
    private readonly NacosClientFactory _factory;

    private NacosClient(NacosClientFactory factory) => _factory = factory;

    /// <summary>Gets the configuration centre client.</summary>
    public INacosConfigClient Config => _factory.Config;

    /// <summary>Gets the service registration and discovery client.</summary>
    public INacosNamingClient Naming => _factory.Naming;

    /// <summary>
    /// Creates a client from the supplied options.
    /// </summary>
    /// <param name="options">The options to use.</param>
    /// <param name="httpClient">
    /// The HTTP client used for authentication, or <see langword="null"/> to create one. Supply an
    /// <c>IHttpClientFactory</c>-managed client in a hosted application so its handler lifetime and
    /// resilience policies apply.
    /// </param>
    /// <param name="loggerFactory">
    /// The logger factory, or <see langword="null"/> to discard logs.
    /// </param>
    /// <param name="timeProvider">
    /// The clock and timer source, or <see langword="null"/> for the system clock.
    /// </param>
    /// <returns>The created client, which has not yet connected.</returns>
    /// <exception cref="NacosConfigurationException">The options are not usable.</exception>
    /// <remarks>
    /// Construction does not connect. Call <see cref="ConnectAsync"/> before issuing requests, or
    /// let the first request attempt the connection implicitly. Keeping the two separate lets a
    /// host decide whether an unreachable server should fail startup.
    /// </remarks>
    public static NacosClient Create(
        NacosOptions options,
        HttpClient? httpClient = null,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new NacosClient(NacosClientFactory.Create(
            options,
            httpClient,
            channelFactory: null,
            timeProvider,
            loggerFactory ?? NullLoggerFactory.Instance));
    }

    /// <summary>
    /// Establishes the shared connection.
    /// </summary>
    /// <param name="ct">A token that cancels the attempt.</param>
    /// <returns>A task that completes once the connection is usable.</returns>
    /// <exception cref="NacosConnectionException">
    /// No configured server could be reached and
    /// <see cref="NacosOptions.Optional"/> was not set.
    /// </exception>
    /// <remarks>
    /// When <see cref="NacosOptions.Optional"/> is set, an unreachable server is tolerated and the
    /// client keeps retrying with backoff in the background, so the call returns without throwing.
    /// </remarks>
    public Task ConnectAsync(CancellationToken ct = default) => _factory.ConnectAsync(ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    /// <summary>
    /// Releases the client synchronously.
    /// </summary>
    /// <remarks>
    /// Provided because a dependency injection container disposes its singletons synchronously by
    /// default, and a service that implements only <see cref="IAsyncDisposable"/> makes the
    /// container throw when it is disposed that way. The disposal itself is not I/O bound — it
    /// closes in-process resources — so blocking briefly here is safe.
    /// </remarks>
    void IDisposable.Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
