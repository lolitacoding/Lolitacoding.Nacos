using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Locking;
using Lolitacoding.Nacos.Naming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Client;

/// <summary>
/// The entry point for programmatic access to Nacos configuration, service discovery and the
/// distributed lock.
/// </summary>
/// <remarks>
/// <para>
/// The client owns one gRPC connection per module. Nacos routes each connection by the
/// <c>module</c> label it declares, and the naming, lock and AI servers discard connections that
/// did not declare them, so configuration and naming do not share a stream. The lock connection is
/// opened the first time a lock is used rather than at startup.
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

    /// <summary>Gets the distributed lock client.</summary>
    /// <remarks>
    /// Its connection is not opened until the first lock is used, so reading this property costs
    /// nothing to an application that never takes one.
    /// </remarks>
    public INacosLockClient Lock => _factory.Lock;

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
    /// Construction does not connect. The configuration and naming connections are opened by
    /// <see cref="ConnectAsync"/>, which the hosting integrations call during startup, while the lock
    /// connection is opened when the first lock is taken. Keeping construction and connection apart
    /// lets a host decide whether an unreachable server should fail startup.
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
    /// Establishes the connections.
    /// </summary>
    /// <param name="ct">A token that cancels the attempt.</param>
    /// <returns>A task that completes once every connection is usable.</returns>
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
