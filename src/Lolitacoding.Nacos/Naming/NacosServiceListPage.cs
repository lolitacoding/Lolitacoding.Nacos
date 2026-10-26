namespace Lolitacoding.Nacos.Naming;

/// <summary>
/// One page of the service names in a group.
/// </summary>
/// <remarks>
/// <para>
/// The server reports the total before it pages, so <see cref="Count"/> is the number of services
/// in the group, not the number of names in <see cref="Services"/>. A caller combines it with the
/// page size it asked for to know how many pages there are.
/// </para>
/// <para>
/// Each entry is the bare service name. The server strips the group prefix from the keys it pages
/// over, which is also what the Java client's <c>ListView&lt;String&gt;</c> returns, so the group
/// passed to the query is the group every name belongs to.
/// </para>
/// </remarks>
public sealed record NacosServiceListPage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServiceListPage"/> record.
    /// </summary>
    /// <param name="count">The total number of services in the group.</param>
    /// <param name="services">The service names on this page.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> is <see langword="null"/>.
    /// </exception>
    internal NacosServiceListPage(int count, IReadOnlyList<string> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Count = count;
        Services = services;
    }

    /// <summary>Gets the total number of services in the group, across every page.</summary>
    public int Count { get; }

    /// <summary>Gets the service names on this page.</summary>
    public IReadOnlyList<string> Services { get; }
}
