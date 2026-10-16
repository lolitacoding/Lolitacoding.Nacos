namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// Describes one attempt to acquire a lock.
/// </summary>
/// <remarks>
/// Every property is optional. The defaults ask for the server's standard lease, do not queue, and
/// identify the caller by the client's own identifier.
/// </remarks>
public sealed class NacosLockAcquireOptions
{
    /// <summary>
    /// Gets or sets the owner the lock is granted to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner is what reentrancy, release and renewal are all keyed by, so two acquisitions that
    /// name the same owner are treated as the same holder. Leaving this unset uses the client's own
    /// identifier, which is the right choice for a single process that acquires and releases its
    /// own locks.
    /// </para>
    /// <para>
    /// A caller that needs one logical holder spread across several processes — or across several
    /// threads that must share the lock — must pass the same value to each of them. A value a
    /// client chose for itself is not visible to the other clients, so a per-client default is
    /// wrong for that case.
    /// </para>
    /// </remarks>
    public string? Owner { get; set; }

    /// <summary>
    /// Gets or sets how long the lock stays held without being renewed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see langword="null"/>, which asks the server for its own lease — 30 seconds.
    /// The server caps the value it accepts, so a much larger request is silently reduced rather
    /// than refused.
    /// </para>
    /// <para>
    /// The lease is the only thing that releases a lock whose holder died, so it trades recovery
    /// time against the cost of renewals: a long lease recovers slowly after a crash, and a short
    /// one needs frequent renewals to survive a slow network.
    /// </para>
    /// </remarks>
    public TimeSpan? LeaseDuration { get; set; }

    /// <summary>
    /// Gets or sets how long the caller is willing to wait for a lock that is already held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> and <see cref="TimeSpan.Zero"/> both mean "do not wait": the server
    /// is asked once and a held lock comes straight back as a failure. A positive value asks the
    /// server to queue the caller and to push a notification when the lock becomes available, and
    /// the caller keeps retrying until it acquires the lock or the time runs out.
    /// </para>
    /// <para>
    /// Nacos' own client maps a zero wait time onto its fifteen-second default. This client does
    /// not reproduce that: "wait zero" reading as "wait fifteen seconds" is a trap, and a caller
    /// that wants the queue says so with a duration.
    /// </para>
    /// </remarks>
    public TimeSpan? WaitTime { get; set; }

    /// <summary>
    /// Gets or sets metadata the server stores alongside the lock.
    /// </summary>
    /// <remarks>
    /// The server records only how many entries there are; nothing on the server interprets them.
    /// They exist so a holder can leave a note about what the lock protects, which is visible to
    /// an operator inspecting the lock rather than to the client that was refused it.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Parameters { get; set; }
}
