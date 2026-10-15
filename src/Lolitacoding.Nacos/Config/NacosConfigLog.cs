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
        Level = LogLevel.Debug,
        Message = "Could not write a local snapshot for configuration {Key}: {Reason}")]
    public static partial void SnapshotWriteFailed(ILogger logger, string key, string reason);
}
