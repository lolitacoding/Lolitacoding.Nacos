using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Extensions.Configuration;

/// <summary>
/// Hands the host's logger factory to a deferred factory once the container is built.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that makes a configuration source's startup diagnostics visible without asking
/// the application to pass a factory: the source is created before the container exists, so it
/// starts with <see cref="NacosDeferredLoggerFactory"/> and this service connects that factory to
/// the real one at the first moment it can be resolved.
/// </para>
/// <para>
/// The service does nothing on stop. It owns neither factory, and the buffered entries were
/// already replayed at start.
/// </para>
/// </remarks>
internal sealed class NacosConfigurationLogBinder(
    NacosDeferredLoggerFactory deferred,
    ILoggerFactory inner) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        deferred.Bind(inner);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
