namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// A lock that waits for itself, keeps its own lease alive and knows when it was acquired twice.
/// </summary>
/// <remarks>
/// <para>
/// This is the lock to reach for unless the per-request shape of
/// <see cref="INacosLockClient"/> is needed. Acquiring returns a <see cref="NacosLockLease"/> to
/// dispose, which releases the lock — the <c>await using</c> form is the whole of a critical
/// section:
/// </para>
/// <code>
/// await using NacosLockLease lease = await client.Lock.GetLock(key).AcquireAsync(ct);
/// // ... work that must not run twice ...
/// </code>
/// <para>
/// While the lease is held the client renews it, so a critical section that takes minutes no longer
/// than the caller keeps the lease: the renewals are what stop the lock from expiring under it. A
/// holder that dies loses the lock within one lease, and usually at once, because the connection
/// carrying the renewals goes with it.
/// </para>
/// <para>
/// Ownership follows the asynchronous flow a call runs in. An acquisition by a flow that already
/// holds the lock re-enters it instead of waiting for itself, which is what makes a nested call —
/// or a helper that takes the same lock — work without the caller passing anything along. A lock of
/// a type that does not allow re-entrancy is refused by
/// <see cref="LockRecursionException"/> rather than deadlocking against itself. Work that must
/// *exclude* the holder rather than re-enter has to run in a flow that holds nothing: start it with
/// its own execution context, or take the lock through <see cref="INacosLockClient"/> with an owner
/// of its own.
/// </para>
/// <para>
/// The lock service offers no fencing token, so a holder whose lease expired can still act on what
/// the lock guarded. A critical section whose effect must not outlive its lock checks
/// <see cref="NacosLockLease.IsHeld"/> before committing, or makes its effect idempotent.
/// </para>
/// </remarks>
public sealed class NacosLock
{
    private readonly NacosLockCoordinator _coordinator;
    private readonly NacosLockKey _key;
    private readonly NacosLockOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosLock"/> class.
    /// </summary>
    /// <param name="coordinator">The coordinator acquisitions are made through.</param>
    /// <param name="key">The lock.</param>
    /// <param name="options">The lease and the metadata of every acquisition of this lock.</param>
    internal NacosLock(NacosLockCoordinator coordinator, NacosLockKey key, NacosLockOptions options)
    {
        _coordinator = coordinator;
        _key = key;
        _options = options;
    }

    /// <summary>Gets the lock that is acquired.</summary>
    public NacosLockKey Key => _key;

    /// <summary>
    /// Acquires the lock, waiting for as long as it takes.
    /// </summary>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>The lease, which is disposed to release the lock.</returns>
    /// <exception cref="LockRecursionException">
    /// The flow already holds the lock and its type does not allow the same holder to acquire it
    /// twice.
    /// </exception>
    /// <exception cref="NacosServerException">
    /// The connected server does not implement the lock service, or rejected the request.
    /// </exception>
    /// <exception cref="NacosConnectionException">The server could not be reached.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public Task<NacosLockLease> AcquireAsync(CancellationToken ct = default) =>
        _coordinator.AcquireAsync(_coordinator.Held(), _key, _options, ct);

    /// <summary>
    /// Acquires the lock if it is free, waiting up to <paramref name="wait"/> for it.
    /// </summary>
    /// <param name="wait">
    /// How long to wait for a lock that is held. <see cref="TimeSpan.Zero"/> asks the server once
    /// and does not queue the caller at all.
    /// </param>
    /// <param name="ct">A token that cancels the wait.</param>
    /// <returns>The lease, or <see langword="null"/> when the lock was not free in time.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="wait"/> is negative.</exception>
    /// <exception cref="LockRecursionException">
    /// The flow already holds the lock and its type does not allow the same holder to acquire it
    /// twice.
    /// </exception>
    /// <exception cref="NacosServerException">
    /// The connected server does not implement the lock service, or rejected the request.
    /// </exception>
    /// <exception cref="NacosConnectionException">The server could not be reached.</exception>
    /// <remarks>
    /// <para>
    /// A wait longer than zero joins the server's wait queue, so the lock is offered to whoever
    /// waited longest rather than to whoever asks at the right moment; the wait ends as soon as the
    /// lock is free, or when it elapses and <see langword="null"/> is returned.
    /// </para>
    /// <para>
    /// Zero is the "try once" form: the server is told not to queue the caller, so a lock somebody
    /// else holds comes straight back as <see langword="null"/>. It is a value of this one parameter
    /// rather than a second method because waiting is the only thing that distinguishes them.
    /// </para>
    /// </remarks>
    public Task<NacosLockLease?> TryAcquireAsync(TimeSpan wait, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wait, TimeSpan.Zero);

        return _coordinator.TryAcquireAsync(_coordinator.Held(), _key, _options, wait, ct);
    }
}
