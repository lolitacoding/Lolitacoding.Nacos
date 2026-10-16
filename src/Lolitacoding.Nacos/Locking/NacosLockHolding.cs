namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// One lock held by one owner, together with how many times its holder acquired it.
/// </summary>
/// <remarks>
/// <para>
/// A holding is created by the acquisition that reached the server. A re-entrant acquisition by the
/// same holder reuses it and only changes the depth, which is why the server never hears about it: a
/// second acquisition from the same owner can only be granted as a re-entrant one, and the server
/// would otherwise queue a holder behind itself — or refuse it outright for a lock type that forbids
/// re-entrancy — instead of answering the caller that already holds the lock.
/// </para>
/// <para>
/// The depth is the number of leases handed out for this holding that have not been released. The
/// server is told about the lock exactly twice, once when it is taken and once when the last lease
/// is released, so the depth is what keeps the two ends of that conversation in step.
/// </para>
/// <para>
/// A holding ends when its last lease is released, or when the lease is lost — a renewal that the
/// server refused or that never reached it. A lost holding is what <see cref="Ended"/> reports, and
/// it is the only notice a holder gets that the lock may no longer be its own.
/// </para>
/// </remarks>
internal sealed class NacosLockHolding
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _depth;
    private bool _held = true;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosLockHolding"/> class.
    /// </summary>
    /// <param name="key">The lock that was acquired.</param>
    /// <param name="owner">The owner the server granted it to.</param>
    /// <param name="leaseDuration">The lease the holder asked for, or <see langword="null"/> for the server's own.</param>
    public NacosLockHolding(NacosLockKey key, string owner, TimeSpan? leaseDuration)
    {
        Key = key;
        Owner = owner;
        LeaseDuration = leaseDuration;
    }

    /// <summary>Gets the lock that was acquired.</summary>
    public NacosLockKey Key { get; }

    /// <summary>Gets the owner the server granted the lock to.</summary>
    public string Owner { get; }

    /// <summary>
    /// Gets the lease the holder asked for, or <see langword="null"/> for the server's own.
    /// </summary>
    /// <remarks>
    /// Renewal re-requests the same lease rather than a fixed extension, so the server's cap and its
    /// default are both left where the server put them.
    /// </remarks>
    public TimeSpan? LeaseDuration { get; }

    /// <summary>
    /// Gets the flow that looks this holding up, or <see langword="null"/> once it ended.
    /// </summary>
    /// <remarks>
    /// The holding knows where it is filed so that ending it can also unfile it. Without that, a
    /// long-lived flow would accumulate one dead entry per lock it ever took.
    /// </remarks>
    public NacosLockFlow? Flow { get; internal set; }

    /// <summary>Gets a task that completes when the holder stops holding the lock.</summary>
    /// <remarks>
    /// It completes for both endings: a lease that was released and a lease that was lost. The two
    /// are told apart by asking <see cref="IsHeld"/> before the lock is given up, which is the
    /// question worth asking anyway — did this caller still hold the lock when its work finished?
    /// </remarks>
    public Task Ended => _ended.Task;

    /// <summary>Gets how many levels deep the holder currently holds the lock.</summary>
    public int Depth
    {
        get
        {
            lock (_gate)
            {
                return _depth;
            }
        }
    }

    /// <summary>Gets a value indicating whether the holder still holds the lock.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }
    }

    /// <summary>
    /// Records another acquisition by the holder.
    /// </summary>
    /// <exception cref="InvalidOperationException">The holder no longer holds the lock.</exception>
    public void Enter()
    {
        lock (_gate)
        {
            if (!_held)
            {
                throw new InvalidOperationException(
                    string.Concat("The lock '", Key.Name, "' is no longer held and cannot be re-entered."));
            }

            _depth++;
        }
    }

    /// <summary>
    /// Records that one level of the holder's acquisition was released.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when that was the last level, so the lock itself has to be released.
    /// </returns>
    public bool Exit()
    {
        lock (_gate)
        {
            if (!_held)
            {
                return false;
            }

            if (--_depth > 0)
            {
                return false;
            }

            _held = false;
        }

        _ended.TrySetResult();
        return true;
    }

    /// <summary>
    /// Ends the holding without releasing the lock, because the lease is gone.
    /// </summary>
    /// <remarks>
    /// Used when a renewal failed, and when the client is shutting down: in both cases the server
    /// stops counting this owner as a holder on its own, so there is nothing left to tell it.
    /// </remarks>
    public void End()
    {
        lock (_gate)
        {
            if (!_held)
            {
                return;
            }

            _held = false;
            _depth = 0;
        }

        _ended.TrySetResult();
    }
}
