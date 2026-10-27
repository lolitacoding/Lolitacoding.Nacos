using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Lolitacoding.Nacos.Naming;

namespace Lolitacoding.Nacos.Tests.ServiceDiscovery;

/// <summary>
/// An <see cref="INacosNamingClient"/> whose instance list and change stream a test drives.
/// </summary>
/// <remarks>
/// The service discovery provider only reads the registry and observes pushes, so the fake exposes
/// exactly those two behaviours and records what the provider asked for.
/// </remarks>
internal sealed class FakeNamingClient : INacosNamingClient
{
    private readonly Channel<NacosServiceChange> _changes = Channel.CreateUnbounded<NacosServiceChange>();
    private readonly TaskCompletionSource _subscribed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeNamingClient"/> class.
    /// </summary>
    /// <param name="info">The instance list to return.</param>
    /// <param name="failure">The exception every query should throw, or <see langword="null"/>.</param>
    public FakeNamingClient(NacosServiceInfo info, Exception? failure = null)
    {
        Info = info;
        Failure = failure;
    }

    /// <summary>Gets or sets the instance list returned by the next query.</summary>
    public NacosServiceInfo Info { get; set; }

    /// <summary>Gets or sets the exception a query throws instead of returning a list.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Gets the number of queries the provider issued.</summary>
    public int QueryCount { get; private set; }

    /// <summary>Gets the service coordinates of the most recent query.</summary>
    public NacosServiceKey? LastService { get; private set; }

    /// <summary>Gets the cluster filter of the most recent query.</summary>
    public string? LastCluster { get; private set; }

    /// <summary>Gets the health filter of the most recent query.</summary>
    public bool? LastHealthyOnly { get; private set; }

    /// <summary>Gets a task that completes once the change stream has been subscribed to.</summary>
    public Task Subscribed => _subscribed.Task;

    /// <inheritdoc />
    public NacosServerStatus ServerStatus => NacosServerStatus.Up;

    /// <inheritdoc />
    public Task RegisterInstanceAsync(
        NacosServiceKey service, NacosInstance instance, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task RegisterInstancesAsync(
        NacosServiceKey service, IReadOnlyList<NacosInstance> instances, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task DeregisterInstanceAsync(
        NacosServiceKey service, NacosInstance instance, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task DeregisterInstancesAsync(
        NacosServiceKey service, IReadOnlyList<NacosInstance> instances, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <inheritdoc />
    public Task<NacosServiceInfo> GetInstancesAsync(
        NacosServiceKey service,
        string? cluster = null,
        bool healthyOnly = true,
        CancellationToken ct = default)
    {
        QueryCount++;
        LastService = service;
        LastCluster = cluster;
        LastHealthyOnly = healthyOnly;

        return Failure is null
            ? Task.FromResult(Info)
            : Task.FromException<NacosServiceInfo>(Failure);
    }

    /// <inheritdoc />
    public Task<NacosServiceListPage> GetServicesAsync(
        int pageNo = 1,
        int pageSize = 10,
        string? groupName = null,
        CancellationToken ct = default) =>
        // The provider under test resolves individual services; listing a group is outside its
        // contract, so the fake reports an empty page instead of standing a namespace up.
        Task.FromResult(new NacosServiceListPage(0, []));

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosServiceChange> SubscribeAsync(
        NacosServiceKey service,
        string? clusters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _subscribed.TrySetResult();

        yield return new NacosServiceChange(Info, null);

        await foreach (NacosServiceChange change in _changes.Reader.ReadAllAsync(ct))
        {
            yield return change;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<NacosServiceChange> SubscribeAsync(
        NacosServiceKey service,
        INacosInstanceSelector selector) =>
        // The provider under test never narrows a subscription, so the fake delivers the same stream
        // it would without a selector rather than duplicating the filter it does not exercise.
        SubscribeAsync(service, clusters: null, ct: CancellationToken.None);

    /// <inheritdoc />
    public async IAsyncEnumerable<NacosServiceWatchEvent> WatchAsync(
        string serviceNamePattern,
        string groupPattern,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The provider under test only subscribes to individual services; a pattern watch is
        // outside its contract, so this fake reports an empty stream rather than standing one up.
        await Task.CompletedTask;
        yield break;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<NacosServiceWatchEvent> WatchAsync(
        string groupPattern,
        CancellationToken ct) =>
        WatchAsync("*", groupPattern, ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _changes.Writer.TryComplete();
        return default;
    }

    /// <summary>Delivers a change to an active subscription.</summary>
    /// <param name="info">The new instance list.</param>
    public void Push(NacosServiceInfo info) =>
        _changes.Writer.TryWrite(new NacosServiceChange(info, Info.Checksum));
}
