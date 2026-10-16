using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Naming;

/// <summary>
/// Source-generated log messages for the naming client.
/// </summary>
internal static partial class NacosNamingLog
{
    /// <summary>Logs that an instance was registered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="instance">The instance endpoint.</param>
    /// <param name="ephemeral">Whether the instance is ephemeral.</param>
    [LoggerMessage(
        EventId = 5001,
        Level = LogLevel.Information,
        Message = "Registered {Instance} with the Nacos service {Service} (ephemeral={Ephemeral})")]
    public static partial void InstanceRegistered(
        ILogger logger, string service, string instance, bool ephemeral);

    /// <summary>Logs that an instance was deregistered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="instance">The instance endpoint.</param>
    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Information,
        Message = "Deregistered {Instance} from the Nacos service {Service}")]
    public static partial void InstanceDeregistered(ILogger logger, string service, string instance);

    /// <summary>Logs that the instance list of a service changed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="count">The number of instances now known.</param>
    /// <param name="previousCount">The number of instances before the change.</param>
    [LoggerMessage(
        EventId = 5003,
        Level = LogLevel.Information,
        Message = "The instance list of {Service} changed to {Count} instance(s), was {PreviousCount}.")]
    public static partial void ServiceChanged(
        ILogger logger, string service, int count, int previousCount);

    /// <summary>Logs that a change notification was ignored because nothing changed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    [LoggerMessage(
        EventId = 5004,
        Level = LogLevel.Debug,
        Message = "Ignoring a notification for {Service}: the instance list is unchanged.")]
    public static partial void ChangeIgnored(ILogger logger, string service);

    /// <summary>Logs that instances were re-registered after a reconnect.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of instances re-registered.</param>
    [LoggerMessage(
        EventId = 5005,
        Level = LogLevel.Information,
        Message = "Re-registered {Count} Nacos instance(s) and subscriptions after reconnecting.")]
    public static partial void RestoredAfterReconnect(ILogger logger, int count);

    /// <summary>Logs that restoring state after a reconnect failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 5006,
        Level = LogLevel.Error,
        Message = "Failed to restore Nacos naming state after reconnecting: {Reason}")]
    public static partial void RestoreFailed(ILogger logger, string reason);

    /// <summary>Logs that the instance list was served from the local cache.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="reason">Why the server could not be used.</param>
    [LoggerMessage(
        EventId = 5007,
        Level = LogLevel.Warning,
        Message = "Serving the instance list of {Service} from the local cache: {Reason}")]
    public static partial void ServedFromCache(ILogger logger, string service, string reason);

    /// <summary>Logs that a heartbeat was rejected because the instance is no longer registered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="instance">The instance endpoint.</param>
    [LoggerMessage(
        EventId = 5008,
        Level = LogLevel.Debug,
        Message = "The Nacos server no longer knows {Instance} in {Service}; it will be re-registered on the next reconnect.")]
    public static partial void InstanceUnknown(ILogger logger, string service, string instance);

    /// <summary>Logs that the instance list was served from the on-disk snapshot.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="reason">Why the server could not be used.</param>
    [LoggerMessage(
        EventId = 5009,
        Level = LogLevel.Warning,
        Message = "Serving the instance list of {Service} from the local snapshot: {Reason}")]
    public static partial void ServedFromSnapshot(ILogger logger, string service, string reason);

    /// <summary>Logs that writing a service snapshot failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 5010,
        Level = LogLevel.Warning,
        Message = "Failed to write the local snapshot for {Service}: {Reason}")]
    public static partial void SnapshotWriteFailed(ILogger logger, string service, string reason);

    /// <summary>Logs that reading a service snapshot failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 5011,
        Level = LogLevel.Warning,
        Message = "Failed to read the local snapshot for {Service}: {Reason}")]
    public static partial void SnapshotReadFailed(ILogger logger, string service, string reason);

    /// <summary>Logs that a push describing an older state than the one already applied was dropped.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="pushTime">The time the pushed state was built at.</param>
    /// <param name="appliedTime">The time of the state that is already applied.</param>
    [LoggerMessage(
        EventId = 5012,
        Level = LogLevel.Warning,
        Message = "Ignoring a notification for {Service}: it was built at {PushTime}, before the applied state at {AppliedTime}.")]
    public static partial void StaleChangeIgnored(
        ILogger logger, string service, long pushTime, long appliedTime);
}
