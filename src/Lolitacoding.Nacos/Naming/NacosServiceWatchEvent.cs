namespace Lolitacoding.Nacos.Naming;

/// <summary>
/// The kinds of change a naming fuzzy watch reports.
/// </summary>
/// <remarks>
/// <para>
/// A fuzzy watch follows a pattern rather than one service, so its events describe the matched
/// <em>set</em>: a service entering it, leaving it, or changing while it stays in it. The push
/// carries no instance list; a consumer that needs the instances subscribes to the service it
/// learned about with <see cref="INacosNamingClient.SubscribeAsync"/> or reads them with
/// <see cref="INacosNamingClient.GetInstancesAsync"/>.
/// </para>
/// <para>
/// The server pushes a change only when the matched membership changed. <see cref="InstancesChanged"/>
/// is therefore defensive: the naming protocol defines <c>INSTANCE_CHANGED</c>, and this client
/// reports it rather than dropping it, but no current server sends one for a service the watch
/// already holds.
/// </para>
/// </remarks>
public enum NacosServiceWatchKind
{
    /// <summary>The service now matches the watched pattern.</summary>
    Added,

    /// <summary>
    /// An already matched service's instance list changed.
    /// </summary>
    /// <remarks>
    /// The event carries the service identity only. Re-read the instances or open a subscription to
    /// see what changed.
    /// </remarks>
    InstancesChanged,

    /// <summary>The service no longer matches the watched pattern.</summary>
    Removed,

    /// <summary>
    /// The initial catch-up is complete, so every service that matched when the watch began has
    /// been reported.
    /// </summary>
    /// <remarks>
    /// Exactly one of these is delivered per subscription. It carries no service, which is why
    /// <see cref="NacosServiceWatchEvent.Service"/> is <see langword="null"/> for it. A consumer
    /// that builds a set from the stream can treat the marker as "the set is now complete".
    /// </remarks>
    InitialSyncCompleted,
}

/// <summary>
/// One event from a naming fuzzy watch.
/// </summary>
/// <remarks>
/// <see cref="Service"/> is the service the event is about, or <see langword="null"/> for
/// <see cref="NacosServiceWatchKind.InitialSyncCompleted"/>. <see cref="GroupKeyPattern"/> is
/// always the pattern the subscription was opened with, so a consumer that multiplexes several
/// watches can tell them apart.
/// </remarks>
public sealed record NacosServiceWatchEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServiceWatchEvent"/> record.
    /// </summary>
    /// <param name="kind">The kind of change.</param>
    /// <param name="groupKeyPattern">The pattern the subscription watches.</param>
    /// <param name="service">The service, or <see langword="null"/> for the completion marker.</param>
    internal NacosServiceWatchEvent(
        NacosServiceWatchKind kind,
        string groupKeyPattern,
        NacosServiceKey? service)
    {
        Kind = kind;
        GroupKeyPattern = groupKeyPattern;
        Service = service;
    }

    /// <summary>Gets the kind of change.</summary>
    public NacosServiceWatchKind Kind { get; }

    /// <summary>
    /// Gets the pattern this subscription watches, as
    /// <c>namespace&gt;&gt;groupPattern&gt;&gt;serviceNamePattern</c>.
    /// </summary>
    public string GroupKeyPattern { get; }

    /// <summary>
    /// Gets the service the event is about, or <see langword="null"/> when <see cref="Kind"/> is
    /// <see cref="NacosServiceWatchKind.InitialSyncCompleted"/>.
    /// </summary>
    public NacosServiceKey? Service { get; }
}
