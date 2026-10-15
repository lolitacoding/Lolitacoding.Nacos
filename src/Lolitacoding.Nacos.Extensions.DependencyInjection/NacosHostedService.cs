using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.Hosting;

namespace Lolitacoding.Nacos.Extensions.DependencyInjection;

/// <summary>
/// Connects the shared Nacos client when the host starts.
/// </summary>
/// <remarks>
/// <para>
/// Connecting here rather than lazily on the first request means a failure to reach the server
/// surfaces during start-up, where an operator is watching, instead of on the first user request
/// after the application has reported itself healthy.
/// </para>
/// <para>
/// Shutdown needs no work: the client is a singleton that the container disposes, which closes the
/// connection and, for an ephemeral instance registered by the ASP.NET Core integration, lets the
/// server remove it.
/// </para>
/// </remarks>
internal sealed class NacosHostedService : IHostedService
{
    private readonly NacosClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosHostedService"/> class.
    /// </summary>
    /// <param name="client">The client to connect.</param>
    public NacosHostedService(NacosClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) =>
        _client.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
