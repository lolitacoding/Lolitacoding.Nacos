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

    /// <summary>Logs that a naming fuzzy watch finished its initial catch-up.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern.</param>
    /// <param name="count">The number of services that matched.</param>
    [LoggerMessage(
        EventId = 5013,
        Level = LogLevel.Information,
        Message = "Naming fuzzy watch {Pattern} completed its initial synchronisation with {Count} matching service(s).")]
    public static partial void FuzzyWatchInitialized(ILogger logger, string pattern, int count);

    /// <summary>Logs that re-registering or resynchronising a naming fuzzy watch failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 5014,
        Level = LogLevel.Warning,
        Message = "Naming fuzzy watch {Pattern} could not be synchronised with the server: {Reason}")]
    public static partial void FuzzyWatchSyncFailed(ILogger logger, string pattern, string reason);

    /// <summary>Logs that a service key pushed by the server could not be decoded.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern.</param>
    /// <param name="serviceKey">The malformed key.</param>
    [LoggerMessage(
        EventId = 5015,
        Level = LogLevel.Warning,
        Message = "Ignoring a naming fuzzy watch event for {Pattern}: the service key {ServiceKey} is malformed.")]
    public static partial void FuzzyWatchMalformedServiceKey(
        ILogger logger,
        string pattern,
        string? serviceKey);

    /// <summary>Logs that naming fuzzy watches were re-registered after a reconnect.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of watches re-registered.</param>
    [LoggerMessage(
        EventId = 5016,
        Level = LogLevel.Information,
        Message = "Re-registered {Count} naming fuzzy watch(es) after reconnecting.")]
    public static partial void FuzzyWatchesRestored(ILogger logger, int count);

    /// <summary>Logs that a naming fuzzy watch delivered an event.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern.</param>
    /// <param name="kind">The kind of event.</param>
    /// <param name="service">The service the event is about.</param>
    [LoggerMessage(
        EventId = 5017,
        Level = LogLevel.Debug,
        Message = "Naming fuzzy watch {Pattern} reported {Kind} for {Service}.")]
    public static partial void FuzzyWatchEvent(
        ILogger logger,
        string pattern,
        NacosServiceWatchKind kind,
        string service);

    /// <summary>Logs that the server suppressed a naming fuzzy watch because a limit was reached.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern.</param>
    /// <param name="errorCode">The code the server reported.</param>
    [LoggerMessage(
        EventId = 5018,
        Level = LogLevel.Warning,
        Message = "Naming fuzzy watch {Pattern} was refused by the server with code {ErrorCode}; "
            + "the server suppresses a pattern once its pattern or match limit is reached.")]
    public static partial void FuzzyWatchRefused(ILogger logger, string pattern, int errorCode);

    /// <summary>Logs that a pushed naming event carried a change type this client does not know.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern, or the push when it is not tied to one.</param>
    /// <param name="changeType">The unrecognised change type.</param>
    [LoggerMessage(
        EventId = 5019,
        Level = LogLevel.Debug,
        Message = "Ignoring a naming fuzzy watch event for {Pattern}: the change type {ChangeType} is not "
            + "part of the protocol this client implements.")]
    public static partial void FuzzyWatchUnknownChangeType(
        ILogger logger,
        string pattern,
        string? changeType);

    /// <summary>Logs that a client-side selector failed while a push was being applied.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service the subscription observes.</param>
    /// <param name="reason">The failure.</param>
    /// <remarks>
    /// The subscription keeps the view it last delivered. The failure is not rethrown because the
    /// selector runs on the shared push path: throwing there would abandon the other consumers of
    /// the same service and would surface as an unobserved task failure rather than at a call site.
    /// </remarks>
    [LoggerMessage(
        EventId = 5020,
        Level = LogLevel.Error,
        Message = "A naming instance selector for {Service} failed and was skipped; the subscription "
            + "keeps the view it last delivered: {Reason}")]
    public static partial void SelectorFailed(ILogger logger, string service, string reason);

    /// <summary>Logs that several instances of one service were published as one batch.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service the instances belong to.</param>
    /// <param name="count">The number of instances in the published set.</param>
    [LoggerMessage(
        EventId = 5021,
        Level = LogLevel.Debug,
        Message = "Published {Count} instances of {Service} as one batch.")]
    public static partial void InstancesRegistered(ILogger logger, string service, int count);
}
