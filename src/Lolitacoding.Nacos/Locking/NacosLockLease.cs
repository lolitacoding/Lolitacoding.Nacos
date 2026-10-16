using System.Globalization;

namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// One level of a lock held by one holder, released when it is disposed.
/// </summary>
/// <remarks>
/// <para>
/// Disposing the last lease of a holder releases the lock on the server. A lease disposed more than
/// once is released once, so an explicit release and the <c>await using</c> form cannot release
/// another holder's acquisition by accident.
/// </para>
/// <para>
/// Releasing is best effort: a lease the server has already forgotten, or a server that cannot be
/// reached, is reported through logging rather than by throwing out of a dispose — there is nothing
/// useful the caller could do about it, and an exception from a dispose would mask whatever ended
/// the scope. A caller that needs to know whether the lock was still held when its work finished
/// asks <see cref="IsHeld"/> before giving the lease up.
/// </para>
/// <para>
/// The lock behind the lease is renewed as long as it is held. A renewal that fails means the lock
/// is no longer this holder's, which <see cref="Ended"/> reports and <see cref="IsHeld"/> answers:
/// the server keeps no fencing token, so work that must not outlive its lock has to check.
/// </para>
/// </remarks>
public sealed class NacosLockLease : IAsyncDisposable
{
    private readonly NacosLockCoordinator _coordinator;
    private readonly NacosLockHolding _holding;

    private int _released;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosLockLease"/> class.
    /// </summary>
    /// <param name="coordinator">The coordinator the lease is released through.</param>
    /// <param name="holding">The holder this lease is one level of.</param>
    internal NacosLockLease(NacosLockCoordinator coordinator, NacosLockHolding holding)
    {
        _coordinator = coordinator;
        _holding = holding;
    }

    /// <summary>Gets the lock that is held.</summary>
    public NacosLockKey Key => _holding.Key;

    /// <summary>
    /// Gets the owner the lock was granted to.
    /// </summary>
    /// <remarks>
    /// The name is this client's identifier followed by a serial number of its own, so it is stable
    /// for the lifetime of the lease and can be matched against the lock as the server reports it.
    /// </remarks>
    public string Owner => _holding.Owner;

    /// <summary>Gets how many levels deep the holder holds the lock, counting this lease.</summary>
    public int ReentrancyCount => _holding.Depth;

    /// <summary>Gets a value indicating whether the holder still holds the lock.</summary>
    /// <remarks>
    /// It becomes <see langword="false"/> when this lease is disposed, when the last lease of the
    /// holder is disposed, when a renewal failed, and when the client is shut down.
    /// </remarks>
    public bool IsHeld => Volatile.Read(ref _released) == 0 && _holding.IsHeld;

    /// <summary>Gets a task that completes when the holder stops holding the lock.</summary>
    public Task Ended => _holding.Ended;

    /// <summary>
    /// Gives up this lease, releasing the lock when it was the holder's last one.
    /// </summary>
    /// <returns>A task that completes once the release finished.</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return _coordinator.ReleaseAsync(_holding);
    }

    /// <inheritdoc />
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{(IsHeld ? "Held" : "Not held")} {Key} (count {ReentrancyCount}) by {Owner}");
}
