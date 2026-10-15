using Lolitacoding.Nacos.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.AspNetCore;

/// <summary>
/// Registers automatic Nacos service registration with a dependency injection container.
/// </summary>
/// <remarks>
/// Registration is driven by a hosted service rather than by middleware, because the instance must
/// exist before the server accepts traffic and must be removed during graceful shutdown — two
/// moments that middleware, which only runs when a request is in flight, cannot observe.
/// </remarks>
public static class NacosServiceRegistrationExtensions
{
    /// <summary>
    /// Adds automatic service registration using the configured options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <remarks>
    /// Use this overload when the options are supplied by configuration binding or by another
    /// source; otherwise use the overload that takes a configuration delegate.
    /// </remarks>
    public static IServiceCollection AddNacosServiceRegistration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return AddNacosServiceRegistrationCore(services, configure: null);
    }

    /// <summary>
    /// Adds automatic service registration configured through a delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The delegate that configures the registration.</param>
    /// <returns>The service collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// This overload also ensures the Nacos clients are registered, so an application that only
    /// registers instances does not have to call <c>AddNacos</c> as well. If it does, that call
    /// keeps its own options and this one adds nothing.
    /// </remarks>
    public static IServiceCollection AddNacosServiceRegistration(
        this IServiceCollection services,
        Action<NacosServiceRegistrationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return AddNacosServiceRegistrationCore(services, configure);
    }

    /// <summary>
    /// Performs the shared registration work.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The optional configuration delegate.</param>
    /// <returns>The service collection.</returns>
    private static IServiceCollection AddNacosServiceRegistrationCore(
        IServiceCollection services,
        Action<NacosServiceRegistrationOptions>? configure)
    {
        OptionsBuilder<NacosServiceRegistrationOptions> registration =
            services.AddOptions<NacosServiceRegistrationOptions>();

        if (configure is not null)
        {
            registration.Configure(configure);
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<NacosServiceRegistrationOptions>,
                NacosServiceRegistrationOptionsValidator>());

        // The hosted service needs the shared client; registering the core graph here means the
        // two calls are order-independent and safe to make together.
        services.AddNacos();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, NacosServiceRegistrationHostedService>());

        return services;
    }
}
