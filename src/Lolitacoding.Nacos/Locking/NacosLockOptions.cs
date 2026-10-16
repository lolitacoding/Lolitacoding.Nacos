namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// Describes the locks taken through one <see cref="NacosLock"/>.
/// </summary>
/// <remarks>
/// <para>
/// These are the properties of the lock itself rather than of one attempt at it: how long it stays
/// held between renewals, and what it is for. How long to wait for a lock is a property of the call
/// that asks for it, so it is expressed by the method that is called —
/// <see cref="NacosLock.AcquireAsync"/> waits, <see cref="NacosLock.TryAcquireAsync(TimeSpan, CancellationToken)"/>
/// gives up.
/// </para>
/// <para>
/// There is deliberately no owner here. A lock taken through <see cref="NacosLock"/> names its own
/// holder per acquisition, which is what makes two acquisitions from one process contend like two
/// processes instead of both being granted the same lock.
/// </para>
/// </remarks>
public sealed class NacosLockOptions
{
    /// <summary>
    /// Gets or sets how long the lock stays held without being renewed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to <see langword="null"/>, which asks the server for its own lease — 30 seconds.
    /// The lease is what frees the lock if the holder dies, so it trades recovery time against the
    /// cost of renewals: a long lease recovers slowly after a crash, and a short one needs frequent
    /// renewals to survive a slow network.
    /// </para>
    /// <para>
    /// The lease is renewed for as long as the lock is held, three times per lease or every ten
    /// seconds, whichever is less often. A locked holder therefore keeps its lock indefinitely while
    /// it is alive and gives it up within one lease of dying — and because the connection goes with
    /// it, the server usually considers the lock free at once rather than waiting the lease out.
    /// </para>
    /// </remarks>
    public TimeSpan? LeaseDuration { get; set; }

    /// <summary>
    /// Gets or sets metadata the server stores alongside the lock.
    /// </summary>
    /// <remarks>
    /// The server records only how many entries there are; nothing on the server interprets them.
    /// They exist so a holder can leave a note about what the lock protects, which is visible to an
    /// operator inspecting the lock rather than to the client that was refused it.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Parameters { get; set; }
}
