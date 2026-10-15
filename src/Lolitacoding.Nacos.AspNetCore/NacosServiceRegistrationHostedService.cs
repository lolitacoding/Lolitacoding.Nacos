using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Naming;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.AspNetCore;

/// <summary>
/// Registers the application as a Nacos instance for the lifetime of the host.
/// </summary>
/// <remarks>
/// <para>
/// Registration is bound to the host's lifetime rather than to the first request so the instance
/// exists before traffic arrives, and so an application that never receives a request is still
/// discoverable.
/// </para>
/// <para>
/// The actual registration runs on <see cref="IHostApplicationLifetime.ApplicationStarted"/> rather
/// than in <see cref="IHostedService.StartAsync"/>. The server's bound addresses are only known
/// once it has started listening, and the server starts after every other hosted service — so
/// reading the addresses during <c>StartAsync</c> would always observe an empty list and force the
/// operator to repeat an address the application already knows.
/// </para>
/// </remarks>
internal sealed class NacosServiceRegistrationHostedService : IHostedService, IDisposable
{
    private readonly NacosClient _client;
    private readonly NacosServiceRegistrationOptions _options;
    private readonly IServer _server;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<NacosServiceRegistrationHostedService> _logger;
    private readonly bool _optional;

    /// <summary>
    /// Serializes registration and deregistration so shutdown cannot race a still-running startup
    /// registration and deregister an instance that was not registered yet.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private NacosServiceKey? _service;
    private NacosInstance? _instance;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosServiceRegistrationHostedService"/> class.
    /// </summary>
    /// <param name="client">The shared Nacos client.</param>
    /// <param name="options">The registration options.</param>
    /// <param name="clientOptions">
    /// The shared client options, consulted for <see cref="NacosOptions.Optional"/>.
    /// </param>
    /// <param name="server">The server, whose bound addresses the instance address is inferred from.</param>
    /// <param name="lifetime">The host lifetime the registration is scheduled on.</param>
    /// <param name="logger">The logger.</param>
    public NacosServiceRegistrationHostedService(
        NacosClient client,
        IOptions<NacosServiceRegistrationOptions> options,
        IOptions<NacosOptions> clientOptions,
        IServer server,
        IHostApplicationLifetime lifetime,
        ILogger<NacosServiceRegistrationHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clientOptions);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        // Reading Value here means a misconfigured registration fails while the container is
        // being built, which is earlier and clearer than failing at startup.
        _options = options.Value;
        _optional = clientOptions.Value.Optional;
        _server = server;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The callback must not throw: it runs synchronously inside the host's start sequence, so
        // an exception here would abort startup. RegisterAsync captures its own failures instead.
        _lifetime.ApplicationStarted.Register(
            static state => ((NacosServiceRegistrationHostedService)state!).OnApplicationStarted(),
            this);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (_instance is null || _service is null || !_options.DeregisterOnStop)
            {
                return;
            }

            await _client
                .Naming
                .DeregisterInstanceAsync(_service, _instance, cancellationToken)
                .ConfigureAwait(false);

            NacosAspNetCoreLog.Deregistered(_logger, _service.ServiceName, _instance.Endpoint);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Shutdown must not fail because the control plane is unreachable; the server reaps an
            // ephemeral instance on its own once the connection closes.
            NacosAspNetCoreLog.DeregistrationFailed(_logger, _service?.ServiceName ?? "<unknown>", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Schedules the registration, observing any failure rather than letting it escape.
    /// </summary>
    private void OnApplicationStarted() => _ = RegisterAsync();

    /// <summary>
    /// Resolves the address and registers the instance.
    /// </summary>
    /// <returns>A task that completes once registration was attempted.</returns>
    private async Task RegisterAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (_instance is not null)
            {
                return;
            }

            string serviceName = _options.ResolveServiceName();
            _service = new NacosServiceKey(serviceName, _options.GroupName, @namespace: null);

            IEnumerable<string> addresses =
                _server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];

            (string ip, int port) = NacosEndpointResolver.Resolve(_options, addresses);

            var instance = new NacosInstance(
                ip,
                port,
                _options.Weight,
                _options.ClusterName,
                healthy: true,
                enabled: true,
                ephemeral: _options.Ephemeral,
                metadata: BuildMetadata());

            await _client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);

            await _client
                .Naming
                .RegisterInstanceAsync(_service, instance, CancellationToken.None)
                .ConfigureAwait(false);

            _instance = instance;
            NacosAspNetCoreLog.Registered(_logger, serviceName, instance.Endpoint);
        }
        catch (Exception ex)
        {
            // Registration runs after the host has started, so it cannot abort startup. In
            // optional mode an unavailable server is expected; otherwise it is an error an
            // operator must see.
            string service = _service?.ServiceName ?? _options.ServiceName ?? "<unknown>";

            if (_optional)
            {
                NacosAspNetCoreLog.RegistrationSkipped(_logger, service, ex.Message);
            }
            else
            {
                NacosAspNetCoreLog.RegistrationFailed(_logger, service, ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Builds the metadata published with the instance.
    /// </summary>
    /// <returns>The metadata.</returns>
    private Dictionary<string, string> BuildMetadata()
    {
        var metadata = new Dictionary<string, string>(
            _options.Metadata,
            StringComparer.Ordinal)
        {
            // Records how the instance was registered, which the Nacos console surfaces and which
            // is otherwise impossible to tell after the fact.
            [NacosServiceRegistrationOptions.RegisterSourceKey] =
                NacosServiceRegistrationOptions.RegisterSourceValue,
        };

        if (!string.IsNullOrWhiteSpace(_options.HealthCheckPath))
        {
            metadata[NacosServiceRegistrationOptions.HealthCheckPathKey] = _options.HealthCheckPath;
        }

        return metadata;
    }
}
