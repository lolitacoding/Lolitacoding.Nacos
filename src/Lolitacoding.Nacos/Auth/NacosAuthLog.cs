using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Source-generated log messages for the authentication layer.
/// </summary>
/// <remarks>
/// Logging is source-generated rather than string-interpolated so that the arguments stay
/// structured and the message templates are not evaluated when the level is disabled.
/// </remarks>
internal static partial class NacosAuthLog
{
    /// <summary>Logs a successful login.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="server">The server that issued the token.</param>
    /// <param name="ttlSeconds">The granted lifetime, in seconds.</param>
    /// <param name="refreshWindowSeconds">The refresh window drawn for this token.</param>
    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Debug,
        Message = "Authenticated against {Server}; token ttl {TtlSeconds}s, refreshing after {RefreshWindowSeconds}s.")]
    public static partial void LoginSucceeded(
        ILogger logger,
        string server,
        long ttlSeconds,
        double refreshWindowSeconds);

    /// <summary>Logs that a server could not be reached for authentication.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="server">The unreachable server.</param>
    /// <param name="reason">Why the attempt failed.</param>
    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "Could not reach {Server} to authenticate: {Reason}")]
    public static partial void ServerUnreachable(ILogger logger, string server, string reason);

    /// <summary>Logs that a forced re-login was suppressed by the re-login window.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="windowSeconds">The width of the suppression window, in seconds.</param>
    [LoggerMessage(
        EventId = 1103,
        Level = LogLevel.Warning,
        Message = "A token was rejected but a login happened less than {WindowSeconds}s ago; not logging in again.")]
    public static partial void ReLoginThrottled(ILogger logger, long windowSeconds);

    /// <summary>Logs that the server rejected the access token presented on a request.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="server">The server that rejected the token.</param>
    /// <param name="message">The message the server returned.</param>
    [LoggerMessage(
        EventId = 1104,
        Level = LogLevel.Warning,
        Message = "The Nacos server {Server} rejected the access token: {Message}")]
    public static partial void TokenRejected(ILogger logger, string server, string message);
}
