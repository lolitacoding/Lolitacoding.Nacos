namespace Lolitacoding.Nacos.Transport;

/// <summary>
/// Owns the connection of one module and creates it the first time the module is used.
/// </summary>
/// <remarks>
/// <para>
/// Configuration and naming are connected eagerly because nearly every application uses them. The
/// lock and AI modules are not: an application that only reads configuration would pay for a
/// handshake, a heartbeat every few seconds and one more socket per cluster node, for a service it
/// never calls. Creating the connection on first use costs one round trip on a path that is already
/// about to make a request.
/// </para>
/// <para>
/// Creation is serialised, so concurrent first calls share one connection instead of each opening
/// their own, and the connection is published only once it is usable. A caller that raced a
/// shutdown has its connection disposed rather than published, so nothing is leaked.
/// </para>
/// </remarks>
internal sealed class NacosLazyModuleConnection : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<NacosGrpcConnection>> _create;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NacosGrpcConnection? _connection;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosLazyModuleConnection"/> class.
    /// </summary>
    /// <param name="create">
    /// Creates and connects the module's connection. It is called at most once.
    /// </param>
    public NacosLazyModuleConnection(Func<CancellationToken, Task<NacosGrpcConnection>> create)
    {
        ArgumentNullException.ThrowIfNull(create);

        _create = create;
    }

    /// <summary>
    /// Gets the module's connection, creating it if this is the first call.
    /// </summary>
    /// <param name="ct">A token that cancels the creation.</param>
    /// <returns>The connected connection.</returns>
    /// <exception cref="ObjectDisposedException">The owner was disposed.</exception>
    public async Task<NacosGrpcConnection> GetAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        NacosGrpcConnection? existing = Volatile.Read(ref _connection);
        if (existing is not null)
        {
            return existing;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connection is not null)
            {
                return _connection;
            }

            NacosGrpcConnection connection = await _create(ct).ConfigureAwait(false);

            if (_disposed)
            {
                // Disposal overtook the creation. The connection was never published, so it is
                // disposed here instead of being left to a finalizer that does not exist.
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new ObjectDisposedException(nameof(NacosLazyModuleConnection));
            }

            Volatile.Write(ref _connection, connection);
            return connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Waiting for the gate means a creation already in flight is finished and disposed rather than
    /// abandoned half-built. The gate itself is deliberately not disposed: disposing a
    /// <see cref="SemaphoreSlim"/> that another thread is waiting on would raise from that thread's
    /// release, turning a shutdown race into a spurious failure.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _gate.WaitAsync().ConfigureAwait(false);

        NacosGrpcConnection? connection;

        try
        {
            connection = _connection;
            _connection = null;
        }
        finally
        {
            _gate.Release();
        }

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
