namespace Lolitacoding.Nacos.AI;

/// <summary>
/// Decides when gRPC is worth trying again after a connection failure.
/// </summary>
/// <remarks>
/// <para>
/// In <see cref="NacosAITransportMode.Auto"/> a read tries gRPC first. When the attempt fails because
/// the connection cannot be reached, the same read is retried over HTTP — but a failed connect costs
/// the connection timeout, so paying it on every read would make <c>Auto</c> slower than pinning
/// HTTP. After one such failure the client stays on HTTP for a short window before trying gRPC again,
/// which is short enough that a recovered connection is picked up without a restart.
/// </para>
/// <para>
/// One instance belongs to one AI resource rather than to the AI connection. The official client
/// tracks transport health per resource for the same reason: the MCP registry being unreachable says
/// nothing about whether prompts are, and a window shared by both would move a resource that is
/// working onto HTTP because a different one is not.
/// </para>
/// </remarks>
internal sealed class NacosAITransportFallback
{
    /// <summary>How long the client stays on HTTP before trying gRPC again.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;

    /// <summary>The time at which gRPC becomes worth trying again, as UTC ticks; zero for now.</summary>
    private long _retryAtTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAITransportFallback"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock the window is measured with.</param>
    public NacosAITransportFallback(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Decides whether gRPC may be tried.
    /// </summary>
    /// <returns><see langword="true"/> when gRPC may be tried.</returns>
    public bool GrpcIsWorthTrying()
    {
        long retryAt = Volatile.Read(ref _retryAtTicks);

        return retryAt == 0 || _timeProvider.GetUtcNow().UtcTicks >= retryAt;
    }

    /// <summary>
    /// Records that gRPC could not be reached, which starts the window.
    /// </summary>
    public void RecordGrpcFailure() =>
        Volatile.Write(ref _retryAtTicks, _timeProvider.GetUtcNow().Add(RetryInterval).UtcTicks);
}
