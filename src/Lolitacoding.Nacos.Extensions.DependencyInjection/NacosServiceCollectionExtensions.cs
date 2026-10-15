using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.Extensions.DependencyInjection;

/// <summary>
/// Registers the Nacos clients with a dependency injection container.
/// </summary>
/// <remarks>
/// The registration is deliberately minimal: one options object, one shared connection and two
/// feature clients. Everything else in the library is an implementation detail that a host should
/// not have to know about.
/// </remarks>
public static class NacosServiceCollectionExtensions
{
    /// <summary>
    /// The name of the named <see cref="HttpClient"/> the client uses for authentication.
    /// </summary>
    /// <remarks>
    /// Exposed so an application can attach its own resilience or telemetry handlers to the same
    /// named client without this library knowing about them.
    /// </remarks>
    public const string HttpClientName = "Lolitacoding.Nacos";

    /// <summary>
    /// Adds the Nacos clients, configured through a delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">The delegate that configures the options.</param>
    /// <returns>The service collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IServiceCollection AddNacos(
        this IServiceCollection services,
        Action<NacosOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<NacosOptions>().Configure(configure);

        return AddNacosCore(services);
    }

    /// <summary>
    /// Adds the Nacos clients, configured by binding an <see cref="IConfiguration"/> section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="section">The configuration section to bind, for example <c>Nacos</c>.</param>
    /// <returns>The service collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// Binding happens when the options are resolved rather than when this method is called, so it
    /// observes configuration that is added to the builder later — including configuration that
    /// came from Nacos itself.
    /// </remarks>
    public static IServiceCollection AddNacos(
        this IServiceCollection services,
        IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(section);

        services.AddOptions<NacosOptions>().Configure(options => section.Bind(options));

        return AddNacosCore(services);
    }

    /// <summary>
    /// Adds the Nacos clients with no eager configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <remarks>
    /// Intended for a host that configures the options through <see cref="IConfiguration"/> or
    /// another options source rather than a delegate. The options are still validated when they are
    /// resolved, so a missing server address fails at startup with a clear message.
    /// </remarks>
    public static IServiceCollection AddNacos(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return AddNacosCore(services);
    }

    /// <summary>
    /// Registers the shared graph that every <c>AddNacos</c> overload needs.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    private static IServiceCollection AddNacosCore(IServiceCollection services)
    {
        // The validation rules live in the core library so every host integration shares them.
        services.TryAddSingleton<IValidateOptions<NacosOptions>, NacosOptionsValidator>();

        // The clock is registered so tests and hosts can substitute one; the default is the system
        // clock, which is what a production host wants.
        services.TryAddSingleton(TimeProvider.System);

        // Authentication uses an IHttpClientFactory-managed client, which gives the application a
        // single place to attach resilience or telemetry handlers. The primary handler applies the
        // configured TLS behaviour so login over HTTPS honours custom certificate authorities and
        // mutual TLS exactly like the gRPC channel does.
        services
            .AddHttpClient(HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                NacosTlsOptions tls = sp.GetRequiredService<IOptions<NacosOptions>>().Value.Tls;
                return NacosTlsHandler.CreateHandler(tls) ?? new SocketsHttpHandler();
            });

        services.TryAddSingleton(sp => NacosClient.Create(
            sp.GetRequiredService<IOptions<NacosOptions>>().Value,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            sp.GetService<ILoggerFactory>(),
            sp.GetService<TimeProvider>()));

        services.TryAddSingleton(sp => sp.GetRequiredService<NacosClient>().Config);
        services.TryAddSingleton(sp => sp.GetRequiredService<NacosClient>().Naming);

        // The hosted service owns the startup connection so that a host's own start-up ordering
        // decides when Nacos is contacted; disposal of the singleton client closes the connection.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, NacosHostedService>());

        return services;
    }
}
