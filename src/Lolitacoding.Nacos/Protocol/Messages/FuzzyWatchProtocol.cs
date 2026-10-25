namespace Lolitacoding.Nacos.Protocol.Messages;

/// <summary>
/// The string constants the fuzzy watch protocol uses on the wire.
/// </summary>
/// <remarks>
/// Values come from <c>com.alibaba.nacos.api.common.Constants</c> and travel as plain strings, so
/// they are compared ordinally. The sync types describe why a diff was sent: initialisation, a
/// later change to the matched set, or the end of initialisation.
/// </remarks>
internal static class FuzzyWatchProtocol
{
    /// <summary>Separates the three segments of a watch pattern.</summary>
    public const string PatternSplitter = ">>";

    /// <summary>Registers a watch.</summary>
    public const string Watch = "WATCH";

    /// <summary>Removes a watch.</summary>
    public const string CancelWatch = "CANCEL_WATCH";

    /// <summary>A batch of the initial diff between client and server.</summary>
    public const string InitNotify = "FUZZY_WATCH_INIT_NOTIFY";

    /// <summary>The initial diff is complete.</summary>
    public const string FinishInitNotify = "FINISH_FUZZY_WATCH_INIT_NOTIFY";

    /// <summary>A diff produced after initialisation.</summary>
    public const string DiffSyncNotify = "FUZZY_WATCH_DIFF_SYNC_NOTIFY";

    /// <summary>A change notify that came from the resource itself rather than a diff.</summary>
    public const string ResourceChanged = "FUZZY_WATCH_RESOURCE_CHANGED";
}

/// <summary>
/// The change types a configuration fuzzy watch reports.
/// </summary>
internal static class ConfigFuzzyWatchChangeType
{
    /// <summary>The configuration now matches the pattern.</summary>
    public const string AddConfig = "ADD_CONFIG";

    /// <summary>The configuration no longer matches the pattern.</summary>
    public const string DeleteConfig = "DELETE_CONFIG";

    /// <summary>An already matched configuration's content changed.</summary>
    public const string ConfigChanged = "CONFIG_CHANGED";
}

/// <summary>
/// The change types a naming fuzzy watch reports.
/// </summary>
internal static class NamingFuzzyWatchChangeType
{
    /// <summary>The service now matches the pattern.</summary>
    public const string AddService = "ADD_SERVICE";

    /// <summary>The service no longer matches the pattern.</summary>
    public const string DeleteService = "DELETE_SERVICE";

    /// <summary>An already matched service's instances changed.</summary>
    public const string InstanceChanged = "INSTANCE_CHANGED";

    /// <summary>An instance sent a heartbeat; it does not change the matched set.</summary>
    public const string HeartBeat = "HEART_BEAT";
}
