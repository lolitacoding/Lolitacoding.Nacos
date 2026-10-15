using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Extensions.Configuration;

/// <summary>
/// Source-generated log messages for the configuration provider.
/// </summary>
internal static partial class NacosConfigurationLog
{
    /// <summary>Logs that the server could not be reached during the initial load.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "Could not reach the Nacos server while loading configuration; continuing without it: {Reason}")]
    public static partial void ConnectFailed(ILogger logger, string reason);

    /// <summary>Logs that a data id could not be subscribed to.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="dataId">The data id.</param>
    /// <param name="reason">The failure.</param>
    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Warning,
        Message = "Could not subscribe to Nacos configuration {DataId}; changes to it will not be observed: {Reason}")]
    public static partial void SubscribeFailed(ILogger logger, string dataId, string reason);

    /// <summary>Logs that the initial load finished.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="dataIdCount">The number of data ids requested.</param>
    /// <param name="keyCount">The number of keys produced.</param>
    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Debug,
        Message = "Loaded {KeyCount} configuration key(s) from {DataIdCount} Nacos data id(s).")]
    public static partial void Loaded(ILogger logger, int dataIdCount, int keyCount);

    /// <summary>Logs that a subscribed configuration changed and the provider is reloading.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="dataId">The data id that changed.</param>
    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Information,
        Message = "Nacos configuration {DataId} changed; signalling a configuration reload.")]
    public static partial void Reloading(ILogger logger, string dataId);
}
