using System.Net;
using Lolitacoding.Nacos.Naming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.ServiceDiscovery;

namespace Lolitacoding.Nacos.Extensions.ServiceDiscovery;

/// <summary>
/// Resolves a service's endpoints from the Nacos naming service.
/// </summary>
/// <remarks>
/// <para>
/// The instance list is fetched on every resolution rather than cached here: the naming client
/// already owns the caching, including the on-disk failover snapshot, and duplicating that would
/// mean two caches with two staleness policies.
/// </para>
/// <para>
/// A subscription runs alongside the resolution so a push from the server invalidates the
/// resolved endpoints immediately. The change token is installed before the fetch, which closes
/// the race where a change arrives mid-fetch and would otherwise be lost.
/// </para>
/// </remarks>
internal sealed class NacosServiceEndpointProvider : IServiceEndpointProvider, IHostNameFeature
{
    private readonly INacosNamingClient _naming;
    private readonly NacosServiceKey _key;
    private readonly string _serviceName;
    private readonly string? _cluster;
    private readonly bool _healthyOnly;
    private readonly ILogger _logger;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private CancellationTokenSource? _change;
    private Task? _subscription;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServiceEndpointProvider"/> class.
    /// </summary>
    /// <param name="naming">The naming client used to fetch instances and observe changes.</param>
    /// <param name="key">The service coordinates.</param>
    /// <param name="serviceName">The service name exactly as the caller wrote it.</param>
    /// <param name="cluster">The cluster filter, or <see langword="null"/>.</param>
    /// <param name="healthyOnly">Whether only healthy, enabled instances become endpoints.</param>
    /// <param name="logger">The logger.</param>
    public NacosServiceEndpointProvider(
        INacosNamingClient naming,
        NacosServiceKey key,
        string serviceName,
        string? cluster,
        bool healthyOnly,
        ILogger logger)
    {
        _naming = naming;
        _key = key;
        _serviceName = serviceName;
        _cluster = cluster;
        _healthyOnly = healthyOnly;
        _logger = logger;
    }

    /// <inheritdoc />
    string IHostNameFeature.HostName => _serviceName;

    /// <inheritdoc />
    public override string ToString() => NacosServiceDiscoveryDefaults.ProviderName;

    /// <inheritdoc />
    public async ValueTask PopulateAsync(
        IServiceEndpointBuilder endpoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        if (endpoints.Endpoints.Count != 0)
        {
            // Another provider resolved this service first; adding more endpoints would silently
            // merge two independent views of the same service.
            NacosServiceDiscoveryLog.SkippedResolution(_logger, _serviceName);
            return;
        }

        EnsureSubscription();

        // Installed before the fetch so a change delivered while the fetch is in flight cancels
        // the token. The watcher then resolves again instead of caching a list that a notification
        // has already superseded.
        IChangeToken changeToken = ResetChangeToken();

        NacosServiceInfo info = await _naming
            .GetInstancesAsync(_key, _cluster, _healthyOnly, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<NacosInstance> instances = _healthyOnly ? info.HealthyInstances : info.Instances;

        foreach (NacosInstance instance in instances)
        {
            ServiceEndpoint endpoint = ServiceEndpoint.Create(CreateEndPoint(instance));
            endpoint.Features.Set<IServiceEndpointProvider>(this);
            endpoint.Features.Set<IHostNameFeature>(this);
            endpoints.Endpoints.Add(endpoint);
        }

        if (endpoints.Endpoints.Count == 0)
        {
            NacosServiceDiscoveryLog.NoInstances(_logger, _serviceName);
        }
        else
        {
            NacosServiceDiscoveryLog.EndpointsResolved(
                _logger, _serviceName, endpoints.Endpoints.Count);
        }

        // Added even when nothing resolved: a service that gains its first instance must be able
        // to invalidate an empty result.
        endpoints.AddChangeToken(changeToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource lifetime;
        CancellationTokenSource? change;
        Task? subscription;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lifetime = _lifetime;
            change = _change;
            _change = null;
            subscription = _subscription;
            _subscription = null;
        }

        // Cancelled outside the gate: the cancellation callbacks schedule work on other threads
        // and must not run while this provider holds the lock.
        TryCancel(change);
        TryCancel(lifetime);

        if (subscription is not null)
        {
            try
            {
                await subscription.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the subscription was cancelled by the disposal above.
            }
        }

        lifetime.Dispose();
    }

    /// <summary>
    /// Starts the change subscription unless one is already running.
    /// </summary>
    /// <remarks>
    /// A subscription is started per provider, and the provider itself is per service, so the
    /// number of subscriptions tracks the number of services the host actually resolves.
    /// </remarks>
    private void EnsureSubscription()
    {
        lock (_gate)
        {
            if (_disposed || _subscription is { IsCompleted: false })
            {
                return;
            }

            _subscription = RunSubscriptionAsync();
        }
    }

    /// <summary>
    /// Pumps the naming client's change stream into the resolution change token.
    /// </summary>
    /// <returns>A task that completes when the subscription ends.</returns>
    private async Task RunSubscriptionAsync()
    {
        try
        {
            bool initial = true;

            await foreach (NacosServiceChange change in
                _naming.SubscribeAsync(_key, _cluster, _lifetime.Token).ConfigureAwait(false))
            {
                // The first element reports the state at subscription time, which the resolve that
                // started this subscription already read; only later changes invalidate a result.
                if (initial)
                {
                    initial = false;
                    continue;
                }

                SignalChange();
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
        catch (Exception ex)
        {
            // A subscription that quietly ended would leave the endpoint list frozen with no way
            // to notice a change. Signalling forces one more resolve, which starts a fresh
            // subscription and therefore repairs the notification path.
            NacosServiceDiscoveryLog.SubscriptionFailed(_logger, _serviceName, ex.Message);
            SignalChange();
        }
    }

    /// <summary>
    /// Installs a fresh change token and returns it.
    /// </summary>
    /// <returns>The token that represents the next change.</returns>
    private CancellationChangeToken ResetChangeToken()
    {
        var source = new CancellationTokenSource();

        lock (_gate)
        {
            _change = source;
        }

        return new CancellationChangeToken(source.Token);
    }

    /// <summary>
    /// Signals that the resolved endpoints may be stale.
    /// </summary>
    private void SignalChange()
    {
        CancellationTokenSource? source;

        lock (_gate)
        {
            source = _change;
        }

        TryCancel(source);
    }

    /// <summary>
    /// Creates the endpoint for a Nacos instance.
    /// </summary>
    /// <param name="instance">The instance.</param>
    /// <returns>The endpoint.</returns>
    /// <remarks>
    /// Nacos normally returns an IP address, but a hostname is accepted so a deployment that
    /// registers one is not silently dropped.
    /// </remarks>
    private static EndPoint CreateEndPoint(NacosInstance instance) =>
        IPAddress.TryParse(instance.IpAddress, out IPAddress? address)
            ? new IPEndPoint(address, instance.Port)
            : new DnsEndPoint(instance.IpAddress, instance.Port);

    private static void TryCancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A source disposed by a racing disposal has nothing left to signal.
        }
    }
}
