using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// Keeps the leases of the held locks alive, and gives a lock up when it no longer can.
/// </summary>
/// <remarks>
/// <para>
/// A Nacos lock is a lease, so a holder that intends to keep one has to renew it before it expires.
/// This is the client half of that: one renewal loop per held lock, started when the lock is taken
/// and stopped when it is released. A renewal that the server refuses, or that never reaches it,
/// is indistinguishable from a holder that died, so the lock is given up locally and
/// <see cref="NacosLockHolding.Ended"/> completes — the caller is told rather than left believing it
/// still holds a lock the server has since given away.
/// </para>
/// <para>
/// The renewal interval is taken from the lease rather than fixed, so a caller that asks for a long
/// lease is not renewed at the same rate as one that asks for a short one. It is capped at ten
/// seconds because a renewal is a request through the cluster's consensus log: renewing a holder
/// that is sleeping on a long lease more often than that would cost the server more than the lease
/// is worth. This mirrors the intervals Nacos' own client watchdog uses.
/// </para>
/// </remarks>
internal sealed class NacosLockWatchdog : IDisposable
{
    /// <summary>The most frequent renewal allowed, however short the lease is.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    /// <summary>The least frequent renewal allowed, however long the lease is.</summary>
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(10);

    /// <summary>The lease assumed when the holder left it to the server.</summary>
    /// <remarks>
    /// The server's default lease is thirty seconds. Renewing one of those every ten seconds is the
    /// same cadence a holder that named a thirty-second lease gets.
    /// </remarks>
    private static readonly TimeSpan AssumedLease = TimeSpan.FromSeconds(30);

    private readonly Func<NacosLockHolding, CancellationToken, Task<bool>> _renew;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<NacosLockHolding, CancellationTokenSource> _renewals = new();

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosLockWatchdog"/> class.
    /// </summary>
    /// <param name="renew">Renews one holding and reports whether the server accepted it.</param>
    /// <param name="timeProvider">The clock and timer source used for the renewal intervals.</param>
    /// <param name="logger">The logger.</param>
    public NacosLockWatchdog(
        Func<NacosLockHolding, CancellationToken, Task<bool>> renew,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(renew);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _renew = renew;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Starts renewing a holding.
    /// </summary>
    /// <param name="holding">The holding to keep alive.</param>
    /// <remarks>
    /// A holding registered while the client is shutting down is ended instead: the connection that
    /// would carry the renewals is already going away, so the lease is lost no matter what this does.
    /// </remarks>
    public void Register(NacosLockHolding holding)
    {
        ArgumentNullException.ThrowIfNull(holding);

        if (_disposed)
        {
            holding.End();
            return;
        }

        var renewal = new CancellationTokenSource();

        if (!_renewals.TryAdd(holding, renewal))
        {
            renewal.Dispose();
            return;
        }

        _ = RenewAsync(holding, renewal);
    }

    /// <summary>
    /// Stops renewing a holding.
    /// </summary>
    /// <param name="holding">The holding.</param>
    public void Unregister(NacosLockHolding holding)
    {
        ArgumentNullException.ThrowIfNull(holding);

        if (_renewals.TryRemove(holding, out CancellationTokenSource? renewal))
        {
            renewal.Cancel();
            renewal.Dispose();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every holder is ended rather than merely stopped: a disposed client has closed the connection
    /// its locks were held over, so the server is about to count every one of them as released.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (KeyValuePair<NacosLockHolding, CancellationTokenSource> renewal in _renewals)
        {
            // The loops notice the cancellation and unregister themselves, which is also what
            // disposes the source; cancelling here rather than disposing keeps that single owner.
            renewal.Key.End();
            renewal.Value.Cancel();
        }
    }

    /// <summary>Computes how often a lease has to be renewed to survive.</summary>
    /// <param name="lease">The lease, or <see langword="null"/> for the server's default.</param>
    /// <returns>The renewal interval.</returns>
    private static TimeSpan Interval(TimeSpan? lease)
    {
        TimeSpan effective = lease ?? AssumedLease;
        double third = effective.TotalMilliseconds / 3;

        double milliseconds = Math.Clamp(
            third, MinimumInterval.TotalMilliseconds, MaximumInterval.TotalMilliseconds);

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>Renews one holding repeatedly until it is released or lost.</summary>
    /// <param name="holding">The holding.</param>
    /// <param name="renewal">The source that stops the loop.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    private async Task RenewAsync(NacosLockHolding holding, CancellationTokenSource renewal)
    {
        try
        {
            TimeSpan interval = Interval(holding.LeaseDuration);

            while (true)
            {
                await Task.Delay(interval, _timeProvider, renewal.Token).ConfigureAwait(false);

                if (!await _renew(holding, CancellationToken.None).ConfigureAwait(false))
                {
                    Lost(holding, "the server refused the renewal");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The lock was released, or the client is shutting down. Both are ordinary.
        }
        catch (Exception ex)
        {
            Lost(holding, ex.Message);
        }
        finally
        {
            Unregister(holding);
        }
    }

    /// <summary>Reports a lease that could not be kept alive.</summary>
    /// <param name="holding">The holding that was lost.</param>
    /// <param name="reason">Why the renewal failed.</param>
    private void Lost(NacosLockHolding holding, string reason)
    {
        NacosLockLog.LeaseRenewFailed(_logger, holding.Key.Name, holding.Owner, reason);
        holding.End();
    }
}
