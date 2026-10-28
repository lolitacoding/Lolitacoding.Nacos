using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Extensions.Configuration;

/// <summary>
/// Adds Nacos as a source of <see cref="IConfiguration"/> values.
/// </summary>
/// <remarks>
/// The source is appended, so it overrides the sources added before it — the usual arrangement
/// where <c>appsettings.json</c> supplies the defaults and Nacos supplies the deployed values.
/// Add it after the file sources if that precedence is wanted.
/// <para>
/// From a host, prefer the <see cref="IHostApplicationBuilder"/> overloads: they connect the
/// source's diagnostics to the host's logging, which the
/// <see cref="IConfigurationBuilder"/> overloads cannot do because no container exists yet.
/// </para>
/// </remarks>
public static class NacosConfigurationExtensions
{
    /// <summary>
    /// Adds a Nacos configuration source configured through a delegate.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="configure">The delegate that configures the source.</param>
    /// <returns>The builder, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="NacosConfigurationException">
    /// The configuration is not usable, for example because no data id was requested.
    /// </exception>
    /// <remarks>
    /// The validation runs when this method is called rather than when the provider first reads,
    /// so a mistake in the options aborts host building at the point where the mistake was made.
    /// </remarks>
    public static IConfigurationBuilder AddNacosConfiguration(
        this IConfigurationBuilder builder,
        Action<NacosConfigurationSourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NacosConfigurationSourceOptions();
        configure(options);
        options.Validate();

        builder.Add(new NacosConfigurationSource(options));

        return builder;
    }

    /// <summary>
    /// Adds a Nacos configuration source bound to an <see cref="IConfiguration"/> section.
    /// </summary>
    /// <param name="builder">The configuration builder.</param>
    /// <param name="section">The section to bind, for example <c>Nacos</c>.</param>
    /// <returns>The builder, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="NacosConfigurationException">The bound configuration is not usable.</exception>
    /// <remarks>
    /// Binding happens immediately, so the section must already be populated — unlike the
    /// dependency-injection overload, a configuration provider cannot defer its read, because the
    /// very next source may depend on the values this one produces.
    /// </remarks>
    public static IConfigurationBuilder AddNacosConfiguration(
        this IConfigurationBuilder builder,
        IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(section);

        var options = new NacosConfigurationSourceOptions();
        section.Bind(options);
        options.Validate();

        builder.Add(new NacosConfigurationSource(options));

        return builder;
    }

    /// <summary>
    /// Adds a Nacos configuration source to a host and connects its diagnostics to the host's
    /// logging.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="configure">The delegate that configures the source.</param>
    /// <returns>The builder, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="NacosConfigurationException">
    /// The configuration is not usable, for example because no data id was requested.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This is the overload to use from a host. The source is created before the container exists,
    /// so it cannot resolve an <see cref="ILoggerFactory"/> on its own; without one, a server that
    /// cannot be reached during startup would be reported to nobody. The source therefore starts
    /// with a deferred factory, and a hosted service connects it to the host's factory as soon as
    /// the container is built, replaying the warnings and information logged in the meantime.
    /// </para>
    /// <para>
    /// A factory assigned to <see cref="NacosConfigurationSourceOptions.LoggerFactory"/> by the
    /// delegate wins: it is used as-is and nothing is deferred.
    /// </para>
    /// </remarks>
    public static IHostApplicationBuilder AddNacosConfiguration(
        this IHostApplicationBuilder builder,
        Action<NacosConfigurationSourceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NacosConfigurationSourceOptions();
        configure(options);
        options.Validate();

        ConnectLogging(builder, options);

        builder.Configuration.Add(new NacosConfigurationSource(options));

        return builder;
    }

    /// <summary>
    /// Adds a Nacos configuration source bound to an <see cref="IConfiguration"/> section and
    /// connects its diagnostics to the host's logging.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="section">The section to bind, for example <c>Nacos</c>.</param>
    /// <returns>The builder, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="NacosConfigurationException">The bound configuration is not usable.</exception>
    /// <remarks>
    /// Binding happens immediately, so the section must already be populated. Everything else
    /// behaves like the delegate overload, including the deferred logging.
    /// </remarks>
    public static IHostApplicationBuilder AddNacosConfiguration(
        this IHostApplicationBuilder builder,
        IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(section);

        var options = new NacosConfigurationSourceOptions();
        section.Bind(options);
        options.Validate();

        ConnectLogging(builder, options);

        builder.Configuration.Add(new NacosConfigurationSource(options));

        return builder;
    }

    /// <summary>
    /// Ensures the source has a logger factory, deferring the bind to the host's when none was
    /// supplied.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="options">The source options.</param>
    private static void ConnectLogging(
        IHostApplicationBuilder builder,
        NacosConfigurationSourceOptions options)
    {
        if (options.LoggerFactory is not null)
        {
            // An explicit factory is a deliberate choice, so the deferred path only fills the gap
            // when nobody supplied one.
            return;
        }

        var deferred = new NacosDeferredLoggerFactory();
        options.LoggerFactory = deferred;

        builder.Services.AddHostedService(provider => new NacosConfigurationLogBinder(
            deferred,
            provider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));
    }
}
