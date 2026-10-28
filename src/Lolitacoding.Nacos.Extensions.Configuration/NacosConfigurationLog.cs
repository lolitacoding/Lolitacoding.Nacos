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
    /// <remarks>
    /// Information rather than Debug: this line is the only evidence that the source is attached
    /// and serving values, so it must be visible at the level a production host logs by default.
    /// </remarks>
    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Information,
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

    /// <summary>Logs that the provider started watching its data ids.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of data ids being watched.</param>
    /// <param name="dataIds">The data ids, comma separated.</param>
    [LoggerMessage(
        EventId = 4105,
        Level = LogLevel.Debug,
        Message = "Watching {Count} Nacos data id(s) for changes: {DataIds}")]
    public static partial void StartedWatching(ILogger logger, int count, string dataIds);

    /// <summary>Logs that the provider stopped watching its data ids.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="count">The number of data ids that were watched.</param>
    [LoggerMessage(
        EventId = 4106,
        Level = LogLevel.Debug,
        Message = "Stopped watching {Count} Nacos data id(s).")]
    public static partial void StoppedWatching(ILogger logger, int count);

    /// <summary>Logs that a data id's content changed without changing any key.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="dataId">The data id whose content changed.</param>
    /// <remarks>
    /// Worth a line of its own: the push arrived and was fetched, yet no reload follows. Without it
    /// the change would look like a notification that was lost.
    /// </remarks>
    [LoggerMessage(
        EventId = 4107,
        Level = LogLevel.Debug,
        Message = "The content of Nacos configuration {DataId} changed but produced the same keys; "
            + "no reload is needed.")]
    public static partial void ContentUnchanged(ILogger logger, string dataId);

    /// <summary>Logs that a document could not be read as the format it claims to be.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="dataId">The data id of the document.</param>
    /// <param name="format">The format that was attempted.</param>
    /// <param name="reason">The failure reported by the parser.</param>
    /// <remarks>
    /// Warning, not Debug: the keys the document would have produced do not exist, so every lookup
    /// for them falls back to a default. Without this line the application looks like it is using
    /// the configured value while it is actually using the fallback.
    /// </remarks>
    [LoggerMessage(
        EventId = 4108,
        Level = LogLevel.Warning,
        Message = "Could not read Nacos configuration {DataId} as {Format}; only its raw text is "
            + "available: {Reason}")]
    public static partial void MalformedDocument(
        ILogger logger,
        string dataId,
        NacosConfigurationFormat format,
        string reason);
}
