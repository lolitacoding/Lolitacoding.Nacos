using Lolitacoding.Nacos.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.ServiceDiscovery;

namespace Lolitacoding.Nacos.Extensions.ServiceDiscovery;

/// <summary>
/// Registers Nacos-backed service discovery with a dependency injection container.
/// </summary>
/// <remarks>
/// The registration adds the Nacos provider alongside the providers
/// <c>AddServiceDiscovery</c> installs, rather than replacing them. Keeping the configuration and
/// pass-through providers means a name the Nacos registry does not know still resolves the way it
/// would without this package.
/// </remarks>
public static class NacosServiceDiscoveryServiceCollectionExtensions
{
    /// <summary>
    /// Adds Nacos service discovery using the configured options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <remarks>
    /// Use this overload when the options are supplied by configuration binding or another source;
    /// otherwise use the overload that takes a configuration delegate.
    /// </remarks>
    public static IServiceCollection AddNacosServiceDiscovery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return AddNacosServiceDiscoveryCore(services, configure: null);
    }

    /// <summary>
    /// Adds Nacos service discovery configured through a delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The delegate that configures service discovery.</param>
    /// <returns>The service collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// This overload also ensures the Nacos clients are registered, so an application that only
    /// resolves services does not have to call <c>AddNacos</c> as well. If it does, that call keeps
    /// its own options and this one adds nothing.
    /// </remarks>
    public static IServiceCollection AddNacosServiceDiscovery(
        this IServiceCollection services,
        Action<NacosServiceDiscoveryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return AddNacosServiceDiscoveryCore(services, configure);
    }

    /// <summary>
    /// Performs the shared registration work.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The optional configuration delegate.</param>
    /// <returns>The service collection.</returns>
    private static IServiceCollection AddNacosServiceDiscoveryCore(
        IServiceCollection services,
        Action<NacosServiceDiscoveryOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);

        OptionsBuilder<NacosServiceDiscoveryOptions> options =
            services.AddOptions<NacosServiceDiscoveryOptions>();

        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<NacosServiceDiscoveryOptions>,
                NacosServiceDiscoveryOptionsValidator>());

        // The provider resolves through the naming client, so registering the core graph here
        // makes the two calls order-independent and safe to make together.
        services.AddNacos();

        // Installs the resolution pipeline (the watcher factory, selector and HTTP handler) and
        // the configuration and pass-through providers the Nacos provider sits alongside.
        services.AddServiceDiscovery();

        // TryAddEnumerable keeps a repeated call from registering the provider twice, which would
        // otherwise make every service resolve through two identical providers.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IServiceEndpointProviderFactory,
                NacosServiceEndpointProviderFactory>());

        return services;
    }
}
