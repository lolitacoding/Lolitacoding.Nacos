using Lolitacoding.Nacos.Naming;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.ServiceDiscovery;

namespace Lolitacoding.Nacos.Tests.ServiceDiscovery;

/// <summary>
/// Builds the inputs the service discovery tests share.
/// </summary>
internal static class ServiceDiscoveryTestData
{
    /// <summary>The service every test resolves unless it says otherwise.</summary>
    public static readonly NacosServiceKey Key = new("order-service", "DEFAULT_GROUP");

    /// <summary>Builds a Nacos instance.</summary>
    /// <param name="ipAddress">The instance address.</param>
    /// <param name="port">The instance port.</param>
    /// <param name="healthy">Whether the instance is healthy.</param>
    /// <param name="enabled">Whether the instance is enabled.</param>
    /// <returns>The instance.</returns>
    public static NacosInstance Instance(
        string ipAddress, int port, bool healthy = true, bool enabled = true) =>
        new(ipAddress, port, healthy: healthy, enabled: enabled);

    /// <summary>Builds a service state carrying the given instances.</summary>
    /// <param name="instances">The instances.</param>
    /// <returns>The service state.</returns>
    public static NacosServiceInfo Info(params NacosInstance[] instances) =>
        new(Key, [.. instances], clusters: null, checksum: "sum", fromLocalCache: false);

    /// <summary>Waits until a condition holds, or fails the test when it never does.</summary>
    /// <param name="condition">The condition to wait for.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    public static async Task WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 1000; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The expected condition was not met within the timeout.");
    }

    /// <summary>
    /// An <see cref="IServiceEndpointBuilder"/> that keeps what a provider added to it.
    /// </summary>
    internal sealed class TestEndpointBuilder : IServiceEndpointBuilder
    {
        /// <inheritdoc />
        public IList<ServiceEndpoint> Endpoints { get; } = [];

        /// <inheritdoc />
        public IFeatureCollection Features { get; } = new FeatureCollection();

        /// <summary>Gets the change tokens the provider added.</summary>
        public List<IChangeToken> ChangeTokens { get; } = [];

        /// <inheritdoc />
        public void AddChangeToken(IChangeToken changeToken) => ChangeTokens.Add(changeToken);
    }
}
