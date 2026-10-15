using System.Diagnostics.CodeAnalysis;
using Lolitacoding.Nacos.Naming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.ServiceDiscovery;

namespace Lolitacoding.Nacos.Extensions.ServiceDiscovery;

/// <summary>
/// Creates Nacos endpoint providers for the service names this provider claims.
/// </summary>
/// <remarks>
/// Claiming a name is a promise that a provider can resolve it, and the promise is made without a
/// network call, so the decision is driven entirely by the URI scheme and the options. A name this
/// factory claims but the registry does not know simply resolves to no endpoints, leaving the
/// pass-through provider to handle it, which is why claiming a bare name is safe.
/// </remarks>
internal sealed class NacosServiceEndpointProviderFactory : IServiceEndpointProviderFactory
{
    private readonly INacosNamingClient _naming;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOptions<NacosServiceDiscoveryOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServiceEndpointProviderFactory"/> class.
    /// </summary>
    /// <param name="naming">The naming client used by the providers.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public NacosServiceEndpointProviderFactory(
        INacosNamingClient naming,
        IOptions<NacosServiceDiscoveryOptions> options,
        ILoggerFactory loggerFactory)
    {
        _naming = naming;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public bool TryCreateProvider(
        ServiceEndpointQuery query,
        [NotNullWhen(true)] out IServiceEndpointProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(query);

        NacosServiceDiscoveryOptions options = _options.Value;

        if (!Matches(query, options))
        {
            provider = null;
            return false;
        }

        provider = new NacosServiceEndpointProvider(
            _naming,
            new NacosServiceKey(query.ServiceName, options.GroupName),
            query.ServiceName,
            options.Cluster,
            options.HealthyOnly,
            _loggerFactory.CreateLogger<NacosServiceEndpointProvider>());

        return true;
    }

    /// <summary>
    /// Determines whether this factory claims a query.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="options">The options.</param>
    /// <returns><see langword="true"/> when the Nacos provider should resolve the query.</returns>
    private static bool Matches(ServiceEndpointQuery query, NacosServiceDiscoveryOptions options)
    {
        if (query.IncludedSchemes.Count != 0)
        {
            // An explicit scheme always wins, which is how a caller opts a name into Nacos
            // resolution regardless of ResolveAllServices.
            foreach (string scheme in query.IncludedSchemes)
            {
                if (options.Schemes.Contains(scheme, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // A bare name has no scheme to match on. It is claimed only when the host opted in, so
        // that ordinary host names are left to the discovery provider that already handles them.
        return options.ResolveAllServices;
    }
}
