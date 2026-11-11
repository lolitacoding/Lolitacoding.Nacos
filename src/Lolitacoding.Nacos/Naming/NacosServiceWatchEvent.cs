namespace Lolitacoding.Nacos.Naming;

/// <summary>
/// The kinds of change a naming fuzzy watch reports.
/// </summary>
/// <remarks>
/// <para>
/// A fuzzy watch follows a pattern rather than one service, so its events describe the matched
/// <em>set</em>: a service entering it, leaving it, or changing while it stays in it. The push
/// carries no instance list; a consumer that needs the instances subscribes to the service it
/// learned about with
/// <see cref="INacosNamingClient.SubscribeAsync(NacosServiceKey, string?, CancellationToken)"/> or
/// reads them with
/// <see cref="INacosNamingClient.GetInstancesAsync"/>.
/// </para>
/// <para>
/// The server pushes a change only when the matched membership changed. <see cref="InstancesChanged"/>
/// is therefore defensive: the naming protocol defines <c>INSTANCE_CHANGED</c>, and this client
/// reports it rather than dropping it, but no current server sends one for a service the watch
/// already holds.
/// </para>
/// <para>
/// <see cref="NacosServiceWatchEvent"/> is a union, so a consumer normally switches on the case
/// instead of comparing this enum; the enum remains the vocabulary of
/// <see cref="NacosServiceWatchEvent.Kind"/>.
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
/// <para>
/// The union has four cases, one per shape the protocol can produce: a service entering the matched
/// set, one already in it changing, one leaving it, and the marker that ends the initial catch-up.
/// Matching on the case is exhaustive, so a consumer cannot forget that the completion marker
/// carries no service.
/// </para>
/// <para>
/// <see cref="GroupKeyPattern"/> is always the pattern the subscription was opened with, so a
/// consumer that multiplexes several watches can tell them apart. <see cref="Service"/> is the
/// service the event is about, or <see langword="null"/> for
/// <see cref="NacosServiceWatchKind.InitialSyncCompleted"/>.
/// </para>
/// </remarks>
public readonly union NacosServiceWatchEvent(
    NacosServiceWatchAdded,
    NacosServiceWatchInstancesChanged,
    NacosServiceWatchRemoved,
    NacosServiceWatchCompleted)
{
    /// <summary>Gets the kind of change.</summary>
    public NacosServiceWatchKind Kind => Value switch
    {
        NacosServiceWatchAdded => NacosServiceWatchKind.Added,
        NacosServiceWatchInstancesChanged => NacosServiceWatchKind.InstancesChanged,
        NacosServiceWatchRemoved => NacosServiceWatchKind.Removed,
        NacosServiceWatchCompleted => NacosServiceWatchKind.InitialSyncCompleted,
        _ => throw new InvalidOperationException(
            "A default NacosServiceWatchEvent carries no case; it is not a value the client produces."),
    };

    /// <summary>
    /// Gets the pattern this subscription watches, as
    /// <c>namespace&gt;&gt;groupPattern&gt;&gt;serviceNamePattern</c>.
    /// </summary>
    public string GroupKeyPattern => Value switch
    {
        NacosServiceWatchAdded added => added.GroupKeyPattern,
        NacosServiceWatchInstancesChanged changed => changed.GroupKeyPattern,
        NacosServiceWatchRemoved removed => removed.GroupKeyPattern,
        NacosServiceWatchCompleted completed => completed.GroupKeyPattern,
        _ => throw new InvalidOperationException(
            "A default NacosServiceWatchEvent carries no case; it is not a value the client produces."),
    };

    /// <summary>
    /// Gets the service the event is about, or <see langword="null"/> when <see cref="Kind"/> is
    /// <see cref="NacosServiceWatchKind.InitialSyncCompleted"/>.
    /// </summary>
    public NacosServiceKey? Service => Value switch
    {
        NacosServiceWatchAdded added => added.Service,
        NacosServiceWatchInstancesChanged changed => changed.Service,
        NacosServiceWatchRemoved removed => removed.Service,
        NacosServiceWatchCompleted => null,
        _ => throw new InvalidOperationException(
            "A default NacosServiceWatchEvent carries no case; it is not a value the client produces."),
    };
}

/// <summary>
/// A service that started matching the watched pattern.
/// </summary>
/// <param name="GroupKeyPattern">The pattern the subscription watches.</param>
/// <param name="Service">The service that now matches.</param>
public sealed record NacosServiceWatchAdded(string GroupKeyPattern, NacosServiceKey Service);

/// <summary>
/// An already matched service's instance list changed.
/// </summary>
/// <param name="GroupKeyPattern">The pattern the subscription watches.</param>
/// <param name="Service">The service whose instances changed.</param>
public sealed record NacosServiceWatchInstancesChanged(string GroupKeyPattern, NacosServiceKey Service);

/// <summary>
/// A service that stopped matching the watched pattern.
/// </summary>
/// <param name="GroupKeyPattern">The pattern the subscription watches.</param>
/// <param name="Service">The service that no longer matches.</param>
public sealed record NacosServiceWatchRemoved(string GroupKeyPattern, NacosServiceKey Service);

/// <summary>
/// The initial catch-up is complete: every service that matched when the watch began has been
/// reported.
/// </summary>
/// <param name="GroupKeyPattern">The pattern the subscription watches.</param>
public sealed record NacosServiceWatchCompleted(string GroupKeyPattern);
