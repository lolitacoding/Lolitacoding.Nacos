using System.Globalization;
using System.Text;
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
/// <para>
/// This is the client half of the Aspire integration. When an application references a Nacos
/// resource added by <c>Lolitacoding.Aspire.Hosting.Nacos</c>, Aspire injects a connection string
/// named after the resource; this method turns that connection string into a configured
/// <see cref="NacosOptions"/> instance.
/// </para>
/// <para>
/// Aspire publishes each Nacos container port on its own host port, so the gRPC port is not
/// necessarily the main port plus <c>1000</c>. The hosting resource therefore also injects
/// <c>NACOS_PORT</c> and <c>NACOS_GRPCPORT</c> (the prefix follows the connection name), and this
/// method sets <see cref="NacosOptions.GrpcPortOffset"/> to the difference between them. When
/// those variables are absent — a plain connection string outside an Aspire orchestration, for
/// example — the default offset of <c>1000</c> is kept.
/// </para>
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
    /// string and the published gRPC port have been applied, so it may override any value.
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

        int? grpcPortOffset = ReadGrpcPortOffset(builder.Configuration, name);

        builder.Services.AddNacos(options =>
        {
            options.ServerAddresses = [connectionString];

            if (grpcPortOffset is { } offset)
            {
                options.GrpcPortOffset = offset;
            }

            configure?.Invoke(options);
        });

        return builder;
    }

    /// <summary>
    /// Derives the gRPC port offset from the connection properties Aspire injects next to the
    /// connection string.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="connectionName">The logical connection name, which is the variable prefix.</param>
    /// <returns>
    /// The difference between the published gRPC port and the published main port, or
    /// <see langword="null"/> when either variable is missing or is not a port number.
    /// </returns>
    /// <remarks>
    /// The prefix follows the connection name and is encoded the way Aspire encodes environment
    /// variable names: upper-cased, with any character that is not an ASCII letter or digit
    /// replaced by an underscore.
    /// </remarks>
    private static int? ReadGrpcPortOffset(IConfiguration configuration, string connectionName)
    {
        string prefix = EncodeName(connectionName).ToUpperInvariant();

        if (TryReadPort(configuration, string.Concat(prefix, "_PORT"), out int httpPort)
            && TryReadPort(configuration, string.Concat(prefix, "_GRPCPORT"), out int grpcPort))
        {
            return grpcPort - httpPort;
        }

        return null;
    }

    /// <summary>Reads a port from configuration.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="key">The configuration key.</param>
    /// <param name="port">The parsed port.</param>
    /// <returns><see langword="true"/> when the value is an integer.</returns>
    private static bool TryReadPort(IConfiguration configuration, string key, out int port) =>
        int.TryParse(
            configuration[key],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out port);

    /// <summary>
    /// Encodes a resource name the way Aspire encodes environment variable names.
    /// </summary>
    /// <param name="name">The connection or resource name.</param>
    /// <returns>The encoded name, before upper-casing.</returns>
    private static string EncodeName(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 1);

        if (char.IsAsciiDigit(name[0]))
        {
            builder.Append('_');
        }

        foreach (char c in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return builder.ToString();
    }
}
