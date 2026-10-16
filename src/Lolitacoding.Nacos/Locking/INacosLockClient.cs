namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// Acquires, renews and releases locks held by the Nacos distributed lock service.
/// </summary>
/// <remarks>
/// <para>
/// Nacos' lock is a server-side lease: a lock is granted to an owner for a bounded time and then
/// expires on its own. A process that dies therefore loses its locks within one lease rather than
/// holding them until an operator intervenes, which is what makes the lock usable for work that is
/// not supervised.
/// </para>
/// <para>
/// Every operation addresses a lock key and an owner. When no owner is supplied the client uses a
/// stable identifier for itself, so an acquisition, its renewals and its release all name the same
/// holder; two clients cannot share a lock by accident because neither knows the other's default.
/// </para>
/// <para>
/// The lock service offers no fencing token. A holder whose lease expired can still act on whatever
/// the lock guarded, because nothing prevents it — the server has already given the lock away. The
/// lock is therefore only safe around work whose effects are idempotent, or that checks for its own
/// lease before committing.
/// </para>
/// <para>
/// The lock service addressed here is the one Nacos 3.3 introduced. The module a 3.2 server ships
/// only acquires and releases: it renews and queues nothing, so a renewal or a wait against such a
/// server fails the request rather than being served late.
/// </para>
/// <para>
/// Implementations are expected to be safe for concurrent use, since a hosted application will
/// serve many requests through one client.
/// </para>
/// </remarks>
public interface INacosLockClient : IAsyncDisposable
{
    /// <summary>
    /// Attempts to acquire a lock.
    /// </summary>
    /// <param name="key">The lock to acquire.</param>
    /// <param name="options">
    /// The lease, the owner and how long to wait, or <see langword="null"/> for the defaults: the
    /// server's lease, the client's own owner, and no waiting.
    /// </param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>
    /// The outcome. When <see cref="NacosLockResult.Success"/> is <see langword="false"/> the lock
    /// was not acquired and the result explains why.
    /// </returns>
    /// <exception cref="NacosServerException">
    /// The connected server does not implement the lock service, or rejected the request.
    /// </exception>
    /// <exception cref="NacosConnectionException">The server could not be reached.</exception>
    /// <remarks>
    /// <para>
    /// When <see cref="NacosLockAcquireOptions.WaitTime"/> is set the call joins the server's wait
    /// queue and returns only once the lock was acquired, the wait elapsed, or the server dropped
    /// the waiter. The queue is first-in-first-out, so a caller that waited longest is offered the
    /// lock first, and the wait ends early if the token is cancelled.
    /// </para>
    /// <para>
    /// A successful acquisition is not durable: it must be renewed with
    /// <see cref="RenewAsync(NacosLockKey, string?, TimeSpan?, CancellationToken)"/> before the
    /// lease expires, or released with
    /// <see cref="ReleaseAsync(NacosLockKey, string?, CancellationToken)"/>.
    /// </para>
    /// </remarks>
    Task<NacosLockResult> AcquireAsync(
        NacosLockKey key,
        NacosLockAcquireOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Releases a lock.
    /// </summary>
    /// <param name="key">The lock to release.</param>
    /// <param name="owner">
    /// The owner that holds it, or <see langword="null"/> for this client's own owner.
    /// </param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>The outcome, whose reentrant count is zero once the lock is fully released.</returns>
    /// <exception cref="NacosServerException">
    /// The connected server does not implement the lock service, or rejected the request.
    /// </exception>
    /// <remarks>
    /// The owner is what the release is checked against, so releasing a lock held by another owner
    /// fails rather than stealing it. Releasing a lock that has already expired fails for the same
    /// reason: the server no longer knows the owner. Neither failure leaves the caller stuck, which
    /// is why both are reported as an unsuccessful result.
    /// </remarks>
    Task<NacosLockResult> ReleaseAsync(
        NacosLockKey key,
        string? owner = null,
        CancellationToken ct = default);

    /// <summary>
    /// Extends a lock's lease.
    /// </summary>
    /// <param name="key">The lock to renew.</param>
    /// <param name="owner">
    /// The owner that holds it, or <see langword="null"/> for this client's own owner.
    /// </param>
    /// <param name="leaseDuration">
    /// The new lease, or <see langword="null"/> for the server's default.
    /// </param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="NacosServerException">
    /// The connected server does not implement the lock service, or rejected the request.
    /// </exception>
    /// <remarks>
    /// Renewal is the client half of the lease: a holder that intends to keep a lock longer than one
    /// lease must renew well before the lease expires, or another caller will be granted the lock.
    /// A renewal that fails to reach the server is indistinguishable from a holder that died, so the
    /// caller must treat an unsuccessful renewal as having lost the lock.
    /// </remarks>
    Task<NacosLockResult> RenewAsync(
        NacosLockKey key,
        string? owner = null,
        TimeSpan? leaseDuration = null,
        CancellationToken ct = default);

    /// <summary>
    /// Withdraws a waiter that this client queued for a lock.
    /// </summary>
    /// <param name="key">The lock that was queued for.</param>
    /// <param name="owner">
    /// The owner that queued, or <see langword="null"/> for this client's own owner.
    /// </param>
    /// <param name="ct">A token that cancels the operation.</param>
    /// <returns>The outcome, which is unsuccessful when the waiter was already gone.</returns>
    /// <remarks>
    /// <see cref="AcquireAsync(NacosLockKey, NacosLockAcquireOptions?, CancellationToken)"/>
    /// withdraws its own waiter when it gives up, so this is only needed to withdraw a waiter whose
    /// acquisition is being abandoned from somewhere else — for example when a host is shutting down
    /// and the cancellation token the acquisition was given is not the one being cancelled.
    /// </remarks>
    Task<NacosLockResult> CancelWaitAsync(
        NacosLockKey key,
        string? owner = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the lock a caller holds, renews and releases without writing any of that itself.
    /// </summary>
    /// <param name="key">The lock.</param>
    /// <param name="options">
    /// The lease and the metadata of every acquisition of the lock, or <see langword="null"/> for
    /// the defaults: the server's lease and no metadata.
    /// </param>
    /// <returns>The lock.</returns>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    /// <remarks>
    /// <para>
    /// The returned object is a thin handle on this client, so it may be created per use or kept for
    /// as long as the client lives. It names its own holder for every acquisition, so two
    /// acquisitions made through it contend with each other rather than both being granted the lock
    /// — which is what the low-level methods cannot offer, because they only send what they are told
    /// to send.
    /// </para>
    /// <para>
    /// Use the low-level methods instead when the shape of a single request matters: an owner shared
    /// with another process, a renewal or release of a lock this client did not take, or an
    /// acquisition that is to be abandoned from somewhere other than the call that made it.
    /// </para>
    /// </remarks>
    NacosLock GetLock(NacosLockKey key, NacosLockOptions? options = null);
}
