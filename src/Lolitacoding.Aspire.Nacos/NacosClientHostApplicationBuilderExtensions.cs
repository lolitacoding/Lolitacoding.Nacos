using Lolitacoding.Nacos;
using Lolitacoding.Nacos.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Lolitacoding.Aspire.Nacos;

/// <summary>
/// Registers the Nacos clients with a host using the connection string that an Aspire hosting
/// resource injects.
/// </summary>
/// <remarks>
/// This is the client half of the Aspire integration. When an application references a Nacos
/// resource added by <c>Lolitacoding.Aspire.Hosting.Nacos</c>, Aspire injects a connection string
/// named after the resource; this method turns that connection string into a configured
/// <see cref="NacosOptions"/> instance.
/// </remarks>
public static class NacosClientHostApplicationBuilderExtensions
{
    /// <summary>
    /// The connection string name used when no explicit name is supplied, matching the default
    /// resource name of the hosting integration.
    /// </summary>
    public const string DefaultConnectionName = "nacos";

    /// <summary>
    /// Adds the Nacos clients, reading the server address from an injected connection string.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <param name="connectionName">
    /// The connection string name, or <see langword="null"/> to use
    /// <see cref="DefaultConnectionName"/>.
    /// </param>
    /// <param name="configure">
    /// An optional delegate that further configures the options. It runs after the connection
    /// string has been applied, so it may override any value.
    /// </param>
    /// <returns>The builder, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// No connection string with the resolved name is configured.
    /// </exception>
    public static IHostApplicationBuilder AddNacosClient(
        this IHostApplicationBuilder builder,
        string? connectionName = null,
        Action<NacosOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        string name = connectionName ?? DefaultConnectionName;
        string? connectionString = builder.Configuration.GetConnectionString(name);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"A Nacos connection string named '{name}' was not found. " +
                $"Reference a Nacos resource with 'WithReference', or configure 'ConnectionStrings:{name}'.");
        }

        builder.Services.AddNacos(options =>
        {
            options.ServerAddresses = [connectionString];
            configure?.Invoke(options);
        });

        return builder;
    }
}
