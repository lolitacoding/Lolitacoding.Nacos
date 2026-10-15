using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Transport;

/// <summary>
/// Source-generated log messages for the transport layer.
/// </summary>
/// <remarks>
/// Logging goes through <see cref="LoggerMessageAttribute"/> rather than interpolated strings so
/// the message templates are compiled and the arguments are only formatted when a listener is
/// actually attached. Event identifiers are grouped by area: 1xxx for connection lifecycle,
/// 2xxx for pushes and 3xxx for reconnect recovery.
/// </remarks>
internal static partial class NacosTransportLog
{
    /// <summary>Logs that a server could not be reached during connect or failover.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="address">The server address.</param>
    /// <param name="reason">The failure reason.</param>
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Nacos server {Address} could not be reached: {Reason}")]
    public static partial void ConnectFailed(ILogger logger, string address, string reason);

    /// <summary>Logs a transition between transport states.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="previous">The state being left.</param>
    /// <param name="current">The state being entered.</param>
    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Debug,
        Message = "Nacos transport state changed from {Previous} to {Current}")]
    public static partial void StateChanged(ILogger logger, string previous, string current);

    /// <summary>Logs that an incoming payload had no registered type.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="typeName">The unrecognized payload type name.</param>
    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Debug,
        Message = "Ignoring a Nacos push of unrecognized payload type '{TypeName}'")]
    public static partial void UnknownPush(ILogger logger, string typeName);

    /// <summary>Logs a push this client does not act on.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="typeName">The payload type name.</param>
    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Debug,
        Message = "No handler is registered for the Nacos push '{TypeName}'; the push was dropped")]
    public static partial void UnhandledPush(ILogger logger, string typeName);

    /// <summary>Logs the start of a reconnect attempt.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="attempt">The attempt number.</param>
    /// <param name="delayMs">The delay waited before the attempt.</param>
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Reconnecting to Nacos (attempt {Attempt}) after waiting {DelayMs}ms")]
    public static partial void Reconnecting(ILogger logger, int attempt, double delayMs);

    /// <summary>Logs a successful reconnect.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="attempt">The attempt that succeeded.</param>
    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Information,
        Message = "Reconnected to Nacos on attempt {Attempt}")]
    public static partial void Reconnected(ILogger logger, int attempt);

    /// <summary>Logs that the reconnect attempt budget was exhausted.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="attempts">The number of attempts made.</param>
    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Error,
        Message = "Giving up reconnecting to Nacos after {Attempts} attempt(s)")]
    public static partial void ReconnectGaveUp(ILogger logger, int attempts);

    /// <summary>Logs that a reconnected subscriber failed to restore its state.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="reason">The failure reason.</param>
    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Error,
        Message = "A Nacos reconnect subscriber failed to restore its state: {Reason}")]
    public static partial void RestoreFailed(ILogger logger, string reason);

    /// <summary>Logs that a state observer threw.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="reason">The failure reason.</param>
    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Error,
        Message = "A Nacos transport state observer threw: {Reason}")]
    public static partial void StateObserverFailed(ILogger logger, string reason);

    /// <summary>Logs that a registered push handler threw.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="typeName">The payload type name.</param>
    /// <param name="reason">The failure reason.</param>
    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Error,
        Message = "A handler for the Nacos push '{TypeName}' threw and the push was dropped: {Reason}")]
    public static partial void PushHandlerFailed(ILogger logger, string typeName, string reason);
}
