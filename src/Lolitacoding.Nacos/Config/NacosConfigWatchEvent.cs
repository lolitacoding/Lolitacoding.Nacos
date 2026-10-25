namespace Lolitacoding.Nacos.Config;

/// <summary>
/// The kinds of change a configuration fuzzy watch reports.
/// </summary>
/// <remarks>
/// <para>
/// A fuzzy watch follows a pattern rather than one configuration, so its events describe the
/// matched set: a configuration entering it or leaving it. A Nacos server tracks that membership and
/// does not push a content change for a configuration that already matches; use
/// <see cref="INacosConfigClient.ListenAsync"/> when the value itself matters.
/// </para>
/// <para>
/// The event carries coordinates only. The push that produced it carries no content either, so a
/// consumer that needs the new value reads it with <see cref="INacosConfigClient.GetConfigAsync"/>.
/// </para>
/// </remarks>
public enum NacosConfigWatchKind
{
    /// <summary>The configuration now matches the watched pattern.</summary>
    Added,

    /// <summary>
    /// An already matched configuration changed.
    /// </summary>
    /// <remarks>
    /// A server reports a configuration that starts or stops matching, and says nothing about a
    /// content change to one that already matches, so this kind is the mapping for a change push
    /// about a key the watch already holds — something the server does not currently send. The
    /// official client drops such a push; it is reported here instead so that a push the server did
    /// send is never silently discarded.
    /// </remarks>
    Modified,

    /// <summary>The configuration no longer matches the watched pattern.</summary>
    Removed,

    /// <summary>
    /// The initial catch-up is complete, so every configuration that matched when the watch began
    /// has been reported.
    /// </summary>
    /// <remarks>
    /// Exactly one of these is delivered per subscription. It carries no configuration, which is
    /// why <see cref="NacosConfigWatchEvent.Key"/> is <see langword="null"/> for it. A consumer that
    /// builds a set from the stream can treat the marker as "the set is now complete".
    /// </remarks>
    InitialSyncCompleted,
}

/// <summary>
/// One event from a configuration fuzzy watch.
/// </summary>
/// <remarks>
/// <see cref="Key"/> is the configuration the event is about, or <see langword="null"/> for
/// <see cref="NacosConfigWatchKind.InitialSyncCompleted"/>. <see cref="GroupKeyPattern"/> is always
/// the pattern the subscription was opened with, so a consumer that multiplexes several watches can
/// tell them apart.
/// </remarks>
public sealed record NacosConfigWatchEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosConfigWatchEvent"/> record.
    /// </summary>
    /// <param name="kind">The kind of change.</param>
    /// <param name="groupKeyPattern">The pattern the subscription watches.</param>
    /// <param name="key">The configuration, or <see langword="null"/> for the completion marker.</param>
    internal NacosConfigWatchEvent(
        NacosConfigWatchKind kind,
        string groupKeyPattern,
        NacosConfigKey? key)
    {
        Kind = kind;
        GroupKeyPattern = groupKeyPattern;
        Key = key;
    }

    /// <summary>Gets the kind of change.</summary>
    public NacosConfigWatchKind Kind { get; }

    /// <summary>
    /// Gets the pattern this subscription watches, as
    /// <c>namespace&gt;&gt;groupPattern&gt;&gt;dataIdPattern</c>.
    /// </summary>
    public string GroupKeyPattern { get; }

    /// <summary>
    /// Gets the configuration the event is about, or <see langword="null"/> when
    /// <see cref="Kind"/> is <see cref="NacosConfigWatchKind.InitialSyncCompleted"/>.
    /// </summary>
    public NacosConfigKey? Key { get; }
}
