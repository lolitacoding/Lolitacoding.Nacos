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

    /// <summary>Logs that the address server could not be reached or timed out.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="url">The address server URL.</param>
    /// <param name="reason">The failure reason.</param>
    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "The Nacos address server {Url} could not be read: {Reason}")]
    public static partial void AddressServerUnreachable(ILogger logger, Uri url, string reason);

    /// <summary>Logs that the address server answered with an error status.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="url">The address server URL.</param>
    /// <param name="statusCode">The status code it answered with.</param>
    [LoggerMessage(
        EventId = 1004,
        Level = LogLevel.Warning,
        Message = "The Nacos address server {Url} answered with HTTP {StatusCode}")]
    public static partial void AddressServerRejected(ILogger logger, Uri url, int statusCode);

    /// <summary>Logs that the address server published an empty list.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="url">The address server URL.</param>
    [LoggerMessage(
        EventId = 1005,
        Level = LogLevel.Warning,
        Message = "The Nacos address server {Url} published no servers; the previous list stays in use")]
    public static partial void AddressServerReturnedNoServers(ILogger logger, Uri url);

    /// <summary>Logs that the published server list changed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="url">The address server URL.</param>
    /// <param name="count">How many servers are now listed.</param>
    [LoggerMessage(
        EventId = 1006,
        Level = LogLevel.Information,
        Message = "The Nacos address server {Url} published {Count} server(s); the client list was replaced")]
    public static partial void AddressServerListChanged(ILogger logger, Uri url, int count);

    /// <summary>Logs that the initial seeding attempts all failed.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="url">The address server URL.</param>
    /// <param name="attempts">How many attempts were made.</param>
    [LoggerMessage(
        EventId = 1007,
        Level = LogLevel.Error,
        Message = "The Nacos address server {Url} did not return a server list after {Attempts} attempts; the client will keep polling but has no server to dial yet")]
    public static partial void AddressServerSeedGaveUp(ILogger logger, Uri url, int attempts);

    /// <summary>Logs that an address server overrides the configured server addresses.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="endpoint">The configured address server.</param>
    /// <param name="count">How many configured addresses are being ignored.</param>
    [LoggerMessage(
        EventId = 1008,
        Level = LogLevel.Warning,
        Message = "NacosOptions.Endpoint '{Endpoint}' is set, so the {Count} address(es) on NacosOptions.ServerAddresses are ignored")]
    public static partial void EndpointOverridesServers(ILogger logger, string endpoint, int count);

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

    /// <summary>Logs that a request stopped waiting for a reconnect and failed instead.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="waitedMs">How long the request waited for the connection.</param>
    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Information,
        Message = "A Nacos request waited {WaitedMs}ms for a reconnect that did not arrive and is being "
            + "failed instead, so the caller can fall back to its local copy")]
    public static partial void ReconnectWaitAbandoned(ILogger logger, double waitedMs);
}
