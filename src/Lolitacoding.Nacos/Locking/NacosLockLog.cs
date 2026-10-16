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
}
