using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Extensions.ServiceDiscovery;

/// <summary>
/// Source-generated log messages for the service discovery integration.
/// </summary>
internal static partial class NacosServiceDiscoveryLog
{
    /// <summary>Logs that a service's endpoints were resolved.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="count">The number of endpoints resolved.</param>
    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Debug,
        Message = "Resolved {Count} endpoint(s) for the Nacos service {Service}.")]
    public static partial void EndpointsResolved(ILogger logger, string service, int count);

    /// <summary>Logs that a service was found but had no usable instances.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    [LoggerMessage(
        EventId = 6002,
        Level = LogLevel.Debug,
        Message = "The Nacos service {Service} currently has no healthy, enabled instance.")]
    public static partial void NoInstances(ILogger logger, string service);

    /// <summary>Logs that the change subscription for a service ended unexpectedly.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 6003,
        Level = LogLevel.Warning,
        Message = "The change subscription for the Nacos service {Service} ended: {Reason}")]
    public static partial void SubscriptionFailed(ILogger logger, string service, string reason);

    /// <summary>Logs that a provider declined a service that another provider had already resolved.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="service">The service name.</param>
    [LoggerMessage(
        EventId = 6004,
        Level = LogLevel.Debug,
        Message = "Skipping Nacos resolution for {Service} because another provider already resolved it.")]
    public static partial void SkippedResolution(ILogger logger, string service);
}
