using Microsoft.Extensions.Configuration;

namespace Lolitacoding.Nacos.Extensions.Configuration;

/// <summary>
/// Adds Nacos as a source of <see cref="IConfiguration"/> values.
/// </summary>
/// <remarks>
/// The source is appended, so it overrides the sources added before it — the usual arrangement
/// where <c>appsettings.json</c> supplies the defaults and Nacos supplies the deployed values.
/// Add it after the file sources if that precedence is wanted.
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
}
