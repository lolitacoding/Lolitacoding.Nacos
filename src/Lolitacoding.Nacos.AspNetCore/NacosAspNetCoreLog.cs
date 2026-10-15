using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.AspNetCore;

/// <summary>
/// Source-generated log messages for the ASP.NET Core registration integration.
/// </summary>
internal static partial class NacosAspNetCoreLog
{
    /// <summary>Logs that the instance was registered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="endpoint">The registered endpoint.</param>
    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Information,
        Message = "Registered Nacos instance {Endpoint} for service {Service}.")]
    public static partial void Registered(ILogger logger, string service, string endpoint);

    /// <summary>Logs that the instance could not be registered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4202,
        Level = LogLevel.Error,
        Message = "Failed to register a Nacos instance for service {Service}: {Reason}")]
    public static partial void RegistrationFailed(ILogger logger, string service, string reason);

    /// <summary>Logs that registration was skipped because the server was unavailable.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="reason">The failure.</param>
    /// <remarks>
    /// Used when the application opted into optional mode, where an unavailable server is an
    /// accepted condition rather than a fault.
    /// </remarks>
    [LoggerMessage(
        EventId = 4205,
        Level = LogLevel.Warning,
        Message = "Skipped registering service {Service} because the Nacos server is unavailable: {Reason}")]
    public static partial void RegistrationSkipped(ILogger logger, string service, string reason);

    /// <summary>Logs that the instance was deregistered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="endpoint">The deregistered endpoint.</param>
    [LoggerMessage(
        EventId = 4203,
        Level = LogLevel.Information,
        Message = "Deregistered Nacos instance {Endpoint} for service {Service}.")]
    public static partial void Deregistered(ILogger logger, string service, string endpoint);

    /// <summary>Logs that the instance could not be deregistered.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="reason">The failure.</param>
    /// <remarks>
    /// A failure to deregister is not fatal: the server removes an ephemeral instance on its own
    /// once the connection drops, so this is reported rather than raised.
    /// </remarks>
    [LoggerMessage(
        EventId = 4204,
        Level = LogLevel.Warning,
        Message = "Failed to deregister the Nacos instance for service {Service}; the server will remove an ephemeral instance when the connection closes: {Reason}")]
    public static partial void DeregistrationFailed(ILogger logger, string service, string reason);
}
