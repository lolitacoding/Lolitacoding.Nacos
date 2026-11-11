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
/// <para>
/// <see cref="NacosConfigWatchEvent"/> is a union, so a consumer normally switches on the case
/// instead of comparing this enum; the enum remains the vocabulary of <see cref="NacosConfigWatchEvent.Kind"/>.
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
/// <para>
/// The union has four cases, one per shape the protocol can produce: a configuration entering the
/// matched set, one that already matched changing, one leaving it, and the marker that ends the
/// initial catch-up. Matching on the case is exhaustive, so a consumer cannot forget that the
/// completion marker carries no configuration.
/// </para>
/// <para>
/// <see cref="GroupKeyPattern"/> is always the pattern the subscription was opened with, so a
/// consumer that multiplexes several watches can tell them apart. <see cref="Key"/> is the
/// configuration the event is about, or <see langword="null"/> for
/// <see cref="NacosConfigWatchKind.InitialSyncCompleted"/>.
/// </para>
/// </remarks>
public readonly union NacosConfigWatchEvent(
    NacosConfigWatchAdded,
    NacosConfigWatchModified,
    NacosConfigWatchRemoved,
    NacosConfigWatchCompleted)
{
    /// <summary>Gets the kind of change.</summary>
    public NacosConfigWatchKind Kind => Value switch
    {
        NacosConfigWatchAdded => NacosConfigWatchKind.Added,
        NacosConfigWatchModified => NacosConfigWatchKind.Modified,
        NacosConfigWatchRemoved => NacosConfigWatchKind.Removed,
        NacosConfigWatchCompleted => NacosConfigWatchKind.InitialSyncCompleted,
        _ => throw new InvalidOperationException(
            "A default NacosConfigWatchEvent carries no case; it is not a value the client produces."),
    };

    /// <summary>
    /// Gets the pattern this subscription watches, as
    /// <c>namespace&gt;&gt;groupPattern&gt;&gt;dataIdPattern</c>.
    /// </summary>
    public string GroupKeyPattern => Value switch
    {
        NacosConfigWatchAdded added => added.GroupKeyPattern,
        NacosConfigWatchModified modified => modified.GroupKeyPattern,
        NacosConfigWatchRemoved removed => removed.GroupKeyPattern,
        NacosConfigWatchCompleted completed => completed.GroupKeyPattern,
        _ => throw new InvalidOperationException(
            "A default NacosConfigWatchEvent carries no case; it is not a value the client produces."),
    };

    /// <summary>
    /// Gets the configuration the event is about, or <see langword="null"/> when
    /// <see cref="Kind"/> is <see cref="NacosConfigWatchKind.InitialSyncCompleted"/>.
    /// </summary>
    public NacosConfigKey? Key => Value switch
    {
        NacosConfigWatchAdded added => added.Key,
        NacosConfigWatchModified modified => modified.Key,
        NacosConfigWatchRemoved removed => removed.Key,
        NacosConfigWatchCompleted => null,
        _ => throw new InvalidOperationException(
            "A default NacosConfigWatchEvent carries no case; it is not a value the client produces."),
    };
}

/// <summary>
/// A configuration that started matching the watched pattern.
/// </summary>
/// <param name="GroupKeyPattern">The pattern the subscription watches.</param>
/// <param name="Key">The configuration that now matches.</param>
public sealed record NacosConfigWatchAdded(string GroupKeyPattern, NacosConfigKey Key);

/// <summary>
/// An already matched configuration changed.
/// </summary>
/// <param name="GroupKeyPattern">The pattern the subscription watches.</param>
/// <param name="Key">The configuration that changed.</param>
public sealed record NacosConfigWatchModified(string GroupKeyPattern, NacosConfigKey Key);

/// <summary>
/// A configuration that stopped matching the watched pattern.
/// </summary>
/// <param name="GroupKeyPattern">The pattern the subscription watches.</param>
/// <param name="Key">The configuration that no longer matches.</param>
public sealed record NacosConfigWatchRemoved(string GroupKeyPattern, NacosConfigKey Key);

/// <summary>
/// The initial catch-up is complete: every configuration that matched when the watch began has
/// been reported.
/// </summary>
/// <param name="GroupKeyPattern">The pattern the subscription watches.</param>
public sealed record NacosConfigWatchCompleted(string GroupKeyPattern);
