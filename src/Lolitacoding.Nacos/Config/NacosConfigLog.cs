using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Config;

/// <summary>
/// Source-generated log messages for the configuration client.
/// </summary>
internal static partial class NacosConfigLog
{
    /// <summary>Logs that a configuration was served from a local snapshot.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="reason">Why the server could not be used.</param>
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "Serving configuration {Key} from the local snapshot: {Reason}")]
    public static partial void ServedFromSnapshot(ILogger logger, string key, string reason);

    /// <summary>Logs that a configuration was not found and had no snapshot.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Debug,
        Message = "Configuration {Key} does not exist on the server and has no local snapshot.")]
    public static partial void NotFound(ILogger logger, string key);

    /// <summary>Logs a configuration content hash mismatch.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="expected">The hash the server reported.</param>
    /// <param name="actual">The hash computed locally.</param>
    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Error,
        Message = "Configuration {Key} failed MD5 verification: the server reported {Expected} but the content hashes to {Actual}.")]
    public static partial void Md5Mismatch(ILogger logger, string key, string expected, string actual);

    /// <summary>Logs that a change listener threw.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Error,
        Message = "A change listener for configuration {Key} threw and its notification was dropped: {Reason}")]
    public static partial void ListenerFailed(ILogger logger, string key, string reason);

    /// <summary>Logs that a listener subscription was re-registered after a reconnect.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of configurations re-registered.</param>
    [LoggerMessage(
        EventId = 4005,
        Level = LogLevel.Information,
        Message = "Re-registered {Count} configuration listener(s) after reconnecting.")]
    public static partial void ListenersRestored(ILogger logger, int count);

    /// <summary>Logs that re-registering listeners after a reconnect failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4006,
        Level = LogLevel.Error,
        Message = "Failed to re-register configuration listeners after reconnecting: {Reason}")]
    public static partial void ListenerRestoreFailed(ILogger logger, string reason);

    /// <summary>Logs that a configuration change was observed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="previousMd5">The previous hash.</param>
    /// <param name="currentMd5">The new hash.</param>
    [LoggerMessage(
        EventId = 4007,
        Level = LogLevel.Information,
        Message = "Configuration {Key} changed from {PreviousMd5} to {CurrentMd5}.")]
    public static partial void ConfigChanged(
        ILogger logger,
        string key,
        string? previousMd5,
        string? currentMd5);

    /// <summary>Logs that an unchanged notification was ignored.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="md5">The hash that did not change.</param>
    [LoggerMessage(
        EventId = 4008,
        Level = LogLevel.Debug,
        Message = "Ignoring a notification for configuration {Key}: the content hash {Md5} is unchanged.")]
    public static partial void ChangeIgnored(ILogger logger, string key, string? md5);

    /// <summary>Logs that the config client could not read a snapshot it was asked for.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4009,
        Level = LogLevel.Debug,
        Message = "Could not read the local snapshot for configuration {Key}: {Reason}")]
    public static partial void SnapshotReadFailed(ILogger logger, string key, string reason);

    /// <summary>Logs that writing a snapshot failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4010,
        Level = LogLevel.Warning,
        Message = "Could not write a local snapshot for configuration {Key}: {Reason}")]
    public static partial void SnapshotWriteFailed(ILogger logger, string key, string reason);

    /// <summary>Logs that configurations changed while the client was disconnected.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of configurations that changed.</param>
    [LoggerMessage(
        EventId = 4011,
        Level = LogLevel.Information,
        Message = "{Count} listened configuration(s) changed while the Nacos connection was down; "
            + "the changes are being delivered now")]
    public static partial void ChangesMissedWhileAway(ILogger logger, int count);

    /// <summary>Logs that a fuzzy watch finished its initial catch-up.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern.</param>
    /// <param name="count">The number of configurations that matched.</param>
    [LoggerMessage(
        EventId = 4012,
        Level = LogLevel.Information,
        Message = "Fuzzy watch {Pattern} completed its initial synchronisation with {Count} matching configuration(s).")]
    public static partial void FuzzyWatchInitialized(ILogger logger, string pattern, int count);

    /// <summary>Logs that re-registering or resynchronising a fuzzy watch failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4013,
        Level = LogLevel.Warning,
        Message = "Fuzzy watch {Pattern} could not be synchronised with the server: {Reason}")]
    public static partial void FuzzyWatchSyncFailed(ILogger logger, string pattern, string reason);

    /// <summary>Logs that a group key pushed by the server could not be decoded.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern.</param>
    /// <param name="groupKey">The malformed key.</param>
    [LoggerMessage(
        EventId = 4014,
        Level = LogLevel.Warning,
        Message = "Ignoring a fuzzy watch event for {Pattern}: the group key {GroupKey} is malformed.")]
    public static partial void FuzzyWatchMalformedGroupKey(
        ILogger logger,
        string pattern,
        string? groupKey);

    /// <summary>Logs that fuzzy watches were re-registered after a reconnect.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of watches re-registered.</param>
    [LoggerMessage(
        EventId = 4015,
        Level = LogLevel.Information,
        Message = "Re-registered {Count} fuzzy watch(es) after reconnecting.")]
    public static partial void FuzzyWatchesRestored(ILogger logger, int count);

    /// <summary>Logs that a fuzzy watch delivered an event.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="kind">The kind of event.</param>
    /// <param name="pattern">The watched pattern.</param>
    /// <param name="key">The configuration the event is about.</param>
    [LoggerMessage(
        EventId = 4016,
        Level = LogLevel.Debug,
        Message = "Fuzzy watch {Pattern} reported {Kind} for {Key}.")]
    public static partial void FuzzyWatchEvent(
        ILogger logger,
        string pattern,
        NacosConfigWatchKind kind,
        string key);

    /// <summary>Logs that the server suppressed a fuzzy watch because a limit was reached.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern.</param>
    /// <param name="errorCode">The code the server reported.</param>
    [LoggerMessage(
        EventId = 4017,
        Level = LogLevel.Warning,
        Message = "Fuzzy watch {Pattern} was refused by the server with code {ErrorCode}; "
            + "the server suppresses a pattern once its pattern or match limit is reached.")]
    public static partial void FuzzyWatchRefused(ILogger logger, string pattern, int errorCode);

    /// <summary>Logs that a pushed fuzzy watch event carried a change type this client does not know.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="pattern">The watched pattern, or the push when it is not tied to one.</param>
    /// <param name="changeType">The unrecognised change type.</param>
    [LoggerMessage(
        EventId = 4018,
        Level = LogLevel.Debug,
        Message = "Ignoring a fuzzy watch event for {Pattern}: the change type {ChangeType} is not "
            + "part of the protocol this client implements.")]
    public static partial void FuzzyWatchUnknownChangeType(
        ILogger logger,
        string pattern,
        string? changeType);

    /// <summary>Logs that a cipher data id has no provider for its algorithm.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="algorithm">The algorithm the data id names, when one could be read.</param>
    [LoggerMessage(
        EventId = 4019,
        Level = LogLevel.Warning,
        Message = "No encryption provider named {Algorithm} is configured, so configuration {Key} "
            + "is passed through unchanged.")]
    public static partial void EncryptionProviderMissing(
        ILogger logger,
        string key,
        string? algorithm);

    /// <summary>Logs that a filter failed while a pushed change was being prepared.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4020,
        Level = LogLevel.Error,
        Message = "A configuration filter failed for {Key} while a pushed change was being "
            + "prepared; the change was not delivered: {Reason}")]
    public static partial void ConfigFilterFailedOnPush(
        ILogger logger,
        string key,
        string reason);

    /// <summary>Logs that a filter name was already taken and the new filter was ignored.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The duplicate filter name.</param>
    [LoggerMessage(
        EventId = 4021,
        Level = LogLevel.Debug,
        Message = "A configuration filter named {Name} is already registered; the additional one "
            + "is ignored.")]
    public static partial void ConfigFilterDuplicateIgnored(ILogger logger, string name);

    /// <summary>Logs that a filter removed the content of a configuration being published.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    [LoggerMessage(
        EventId = 4022,
        Level = LogLevel.Error,
        Message = "A configuration filter removed the content of {Key} while publishing; the "
            + "publish was aborted.")]
    public static partial void FilterRemovedPublishedContent(ILogger logger, string key);

    /// <summary>Logs that a listener was added for a configuration.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="count">The number of listeners now registered for it.</param>
    [LoggerMessage(
        EventId = 4023,
        Level = LogLevel.Debug,
        Message = "Added a listener for configuration {Key}; it now has {Count} listener(s).")]
    public static partial void ListenerAdded(ILogger logger, string key, int count);

    /// <summary>Logs that a listener was removed from a configuration.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="count">The number of listeners still registered for it.</param>
    [LoggerMessage(
        EventId = 4024,
        Level = LogLevel.Debug,
        Message = "Removed a listener for configuration {Key}; {Count} listener(s) remain.")]
    public static partial void ListenerRemoved(ILogger logger, string key, int count);

    /// <summary>Logs that the server announced a change for a listened configuration.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    [LoggerMessage(
        EventId = 4025,
        Level = LogLevel.Debug,
        Message = "Received a change notification for configuration {Key}.")]
    public static partial void ChangeNotificationReceived(ILogger logger, string key);

    /// <summary>Logs that a change notification did not name the configuration it is about.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="dataId">The data id the notification carried, if any.</param>
    /// <param name="group">The group the notification carried, if any.</param>
    [LoggerMessage(
        EventId = 4026,
        Level = LogLevel.Warning,
        Message = "Ignoring a change notification with missing coordinates (dataId='{DataId}', "
            + "group='{Group}').")]
    public static partial void ChangeNotificationMalformed(
        ILogger logger,
        string? dataId,
        string? group);

    /// <summary>Logs that a change notification arrived for a configuration nobody listens to.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <remarks>
    /// A notification can outlive the listener that asked for it: the last listener may be removed
    /// while a push is already in flight. Distinguishing that from a lost notification is what
    /// makes "why was my listener not called" answerable.
    /// </remarks>
    [LoggerMessage(
        EventId = 4027,
        Level = LogLevel.Debug,
        Message = "Ignoring a change notification for {Key}: no listener is registered for it.")]
    public static partial void ChangeNotificationUnmatched(ILogger logger, string key);

    /// <summary>Logs that a fetched change was handed to the listeners.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="count">The number of callbacks invoked.</param>
    [LoggerMessage(
        EventId = 4028,
        Level = LogLevel.Debug,
        Message = "Delivered the change of {Key} to {Count} listener(s).")]
    public static partial void ChangeDelivered(ILogger logger, string key, int count);

    /// <summary>Logs that the content of a changed configuration could not be fetched.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4029,
        Level = LogLevel.Warning,
        Message = "Could not fetch the new content of {Key} after a change notification: {Reason}")]
    public static partial void ContentFetchFailed(ILogger logger, string key, string reason);

    /// <summary>Logs that processing a change notification failed unexpectedly.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="key">The configuration coordinates.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4030,
        Level = LogLevel.Error,
        Message = "Processing a change notification for {Key} failed: {Reason}")]
    public static partial void ChangeProcessingFailed(ILogger logger, string key, string reason);
}
