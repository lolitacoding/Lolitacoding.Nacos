using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Locking;

/// <summary>
/// Source-generated log messages for the distributed lock client.
/// </summary>
internal static partial class NacosLockLog
{
    /// <summary>Logs that a waiter could not be withdrawn from the server's queue.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The lock that was queued for.</param>
    /// <param name="reason">Why the withdrawal failed.</param>
    /// <remarks>
    /// The local wait is over either way, so this is a warning rather than an error: the server
    /// drops the entry when it expires, and the client will never be offered a lock it is no longer
    /// waiting for.
    /// </remarks>
    [LoggerMessage(
        EventId = 7001,
        Level = LogLevel.Warning,
        Message = "Withdrawing the waiter for lock {Key} failed, so the server may hold its queue entry until it expires: {Reason}")]
    public static partial void WithdrawWaiterFailed(ILogger logger, string key, string reason);

    /// <summary>Logs that a lock notification carried a type this client does not recognise.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="type">The type the server sent.</param>
    /// <param name="key">The lock the notification was about.</param>
    [LoggerMessage(
        EventId = 7002,
        Level = LogLevel.Debug,
        Message = "Lock notification for {Key} carried the unknown type {Type} and was treated as available.")]
    public static partial void UnknownNotificationType(ILogger logger, string key, string type);

    /// <summary>Logs that a lease could not be kept alive, so the lock was given up.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The lock whose lease was lost.</param>
    /// <param name="owner">The owner that held it.</param>
    /// <param name="reason">Why the renewal failed.</param>
    /// <remarks>
    /// A renewal the server refused and one that never reached it look the same from here, and both
    /// mean the lock is no longer this holder's — the server hands it to somebody else when the lease
    /// runs out. The holder is told through the lease as well; this is the record of why.
    /// </remarks>
    [LoggerMessage(
        EventId = 7003,
        Level = LogLevel.Warning,
        Message = "The lease on lock {Key} for owner {Owner} could not be renewed, so the lock was given up: {Reason}")]
    public static partial void LeaseRenewFailed(ILogger logger, string key, string owner, string reason);

    /// <summary>Logs that releasing a lock was refused or could not be sent.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The lock that was released.</param>
    /// <param name="owner">The owner that held it.</param>
    /// <param name="reason">Why the release failed.</param>
    /// <remarks>
    /// The caller is done with the lock either way, and a server that no longer knows the owner has
    /// already freed it. A failed release therefore only delays another holder by the rest of the
    /// lease, which is a warning rather than an error.
    /// </remarks>
    [LoggerMessage(
        EventId = 7004,
        Level = LogLevel.Warning,
        Message = "Releasing lock {Key} for owner {Owner} failed, so the server may hold it until the lease expires: {Reason}")]
    public static partial void LeaseReleaseFailed(ILogger logger, string key, string owner, string reason);
}
