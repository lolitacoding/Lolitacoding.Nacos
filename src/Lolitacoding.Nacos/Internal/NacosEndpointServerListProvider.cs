using Lolitacoding.Nacos.Transport;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// Keeps a <see cref="NacosServerList"/> in step with the list an address server publishes.
/// </summary>
/// <remarks>
/// <para>
/// An address server is how a deployment adds or retires Nacos nodes without redeploying its
/// clients. This provider seeds the list with a handful of quick attempts, then polls the endpoint
/// on <see cref="NacosOptions.EndpointRefreshInterval"/> and replaces the list whenever the
/// published set differs.
/// </para>
/// <para>
/// The rules follow the official Java client's <c>EndpointServerListProvider</c>: five seeding
/// attempts 100 ms apart, the same URL shape, a missing port defaulting to 8848, and at most one
/// applied change every 30 seconds. Seeding runs in the background so that creating a client never
/// blocks on the address server; a connection that has no address yet waits for the list for as
/// long as <see cref="NacosOptions.ConnectionTimeout"/> allows, which is where a failure surfaces.
/// </para>
/// </remarks>
internal sealed class NacosEndpointServerListProvider : IAsyncDisposable
{
    /// <summary>How many times the list is requested before the first successful answer.</summary>
    private const int SeedAttempts = 5;

    /// <summary>
    /// The shortest interval between two applied changes, regardless of the refresh interval.
    /// </summary>
    /// <remarks>
    /// A flapping address server would otherwise make the client reconnect in a tight loop; the
    /// Java client imposes the same floor.
    /// </remarks>
    private static readonly TimeSpan MinimumChangeInterval = TimeSpan.FromSeconds(30);

    private readonly NacosOptions _options;
    private readonly NacosServerList _servers;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _poll;
    private long _lastChangeTicks;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosEndpointServerListProvider"/> class.
    /// </summary>
    /// <param name="options">The options describing the address server.</param>
    /// <param name="servers">The list the provider keeps current.</param>
    /// <param name="httpClient">The client used to reach the address server.</param>
    /// <param name="timeProvider">The clock that measures the refresh interval.</param>
    /// <param name="logger">The logger.</param>
    public NacosEndpointServerListProvider(
        NacosOptions options,
        NacosServerList servers,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _servers = servers;
        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _logger = logger;
        AddressServerUri = BuildAddressServerUri(options);
    }

    /// <summary>Gets the URL the published list is read from.</summary>
    public Uri AddressServerUri { get; }

    /// <summary>
    /// Builds the URL the published list is read from.
    /// </summary>
    /// <param name="options">The options describing the address server.</param>
    /// <returns>The absolute address server URL.</returns>
    /// <remarks>
    /// The shape matches the Java client: the namespace is only added when the caller chose one,
    /// and <see cref="NacosOptions.EndpointQueryParams"/> is appended verbatim afterwards.
    /// </remarks>
    public static Uri BuildAddressServerUri(NacosOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string contextPath = NacosServerAddress.NormalizeContextPath(
            options.EndpointContextPath ?? options.ContextPath);

        var builder = new UriBuilder(
            Uri.UriSchemeHttp,
            options.Endpoint!,
            options.EndpointPort,
            string.Concat(contextPath, "/", options.EndpointServerListName));

        var query = new List<string>(2);

        if (options.NamespaceWasConfigured)
        {
            // The namespace is a resource coordinate, so it is escaped rather than pasted in; the
            // Java client interpolates it raw, which breaks for ids containing '&' or '='.
            query.Add(string.Concat("namespace=", Uri.EscapeDataString(options.Namespace)));
        }

        if (!string.IsNullOrWhiteSpace(options.EndpointQueryParams))
        {
            query.Add(options.EndpointQueryParams);
        }

        if (query.Count > 0)
        {
            builder.Query = string.Join('&', query);
        }

        return builder.Uri;
    }

    /// <summary>
    /// Starts seeding and polling the list in the background.
    /// </summary>
    /// <remarks>
    /// Idempotent: a second call does nothing, which keeps a client that is rebuilt from starting
    /// a second poll loop against the same list.
    /// </remarks>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _poll ??= Task.Run(() => RunAsync(_shutdown.Token), CancellationToken.None);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_poll is { } poll)
        {
            try
            {
                await poll.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The loop ends by cancellation; that is the expected shutdown path.
            }
        }

        _shutdown.Dispose();
    }

    /// <summary>
    /// Seeds the list and then polls it until the client shuts down.
    /// </summary>
    /// <param name="ct">A token that ends the loop.</param>
    /// <returns>A task that completes when the loop ends.</returns>
    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await SeedAsync(ct).ConfigureAwait(false);

            using var timer = new PeriodicTimer(_options.EndpointRefreshInterval, _timeProvider);

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await RefreshIfDueAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Makes the first few attempts to obtain a list.
    /// </summary>
    /// <param name="ct">A token that cancels the seeding.</param>
    /// <returns>A task that completes once seeded or out of attempts.</returns>
    /// <remarks>
    /// A client is not failed here: the address server may simply be slower than the client's
    /// startup, and the poll loop keeps trying. The connection that needs an address reports the
    /// failure, which is where a caller can act on it. Internal rather than private so a test can
    /// drive it against a fake clock without starting the poll loop.
    /// </remarks>
    internal async Task SeedAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < SeedAttempts; attempt++)
        {
            if (await FetchAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds((attempt + 1) * 100), _timeProvider, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }

        NacosTransportLog.AddressServerSeedGaveUp(_logger, AddressServerUri, SeedAttempts);
    }

    /// <summary>
    /// Polls the endpoint when the change gate allows it.
    /// </summary>
    /// <param name="ct">A token that cancels the poll.</param>
    /// <returns>A task that completes once the poll finished.</returns>
    /// <remarks>
    /// Internal rather than private so a test can drive a poll without waiting for a real timer.
    /// </remarks>
    internal async Task RefreshIfDueAsync(CancellationToken ct)
    {
        long now = _timeProvider.GetUtcNow().UtcTicks;

        if (now - _lastChangeTicks < MinimumChangeInterval.Ticks)
        {
            return;
        }

        await FetchAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the published list and replaces the shared one when it differs.
    /// </summary>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns><see langword="true"/> when a non-empty list was read.</returns>
    private async Task<bool> FetchAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, AddressServerUri);

        // The Java client identifies the module it fetches for; the address server does not
        // require it, but sending it keeps the request recognizable in server-side logs.
        request.Headers.TryAddWithoutValidation("Request-Module", "default");

        using var budget = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        try
        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, linked.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                NacosTransportLog.AddressServerRejected(
                    _logger, AddressServerUri, (int)response.StatusCode);
                return false;
            }

            string body = await response.Content
                .ReadAsStringAsync(linked.Token)
                .ConfigureAwait(false);

            IReadOnlyList<NacosServerAddress> parsed = Parse(body);

            if (parsed.Count == 0)
            {
                NacosTransportLog.AddressServerReturnedNoServers(_logger, AddressServerUri);
                return false;
            }

            if (_servers.Replace(parsed))
            {
                _lastChangeTicks = _timeProvider.GetUtcNow().UtcTicks;

                NacosTransportLog.AddressServerListChanged(_logger, AddressServerUri, parsed.Count);
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException)
        {
            // A timeout, a refused connection or an unusable answer all mean the same thing: this
            // poll produced no usable list, so the previous one stays in force.
            NacosTransportLog.AddressServerUnreachable(_logger, AddressServerUri, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Turns the response body into server addresses.
    /// </summary>
    /// <param name="body">The body, one address per line.</param>
    /// <returns>The parsed addresses.</returns>
    /// <exception cref="FormatException">One of the lines is not a usable address.</exception>
    /// <remarks>
    /// A line without a port is a bare host, which defaults to 8848 exactly as it does in the Java
    /// client. A malformed line fails the whole answer rather than producing a partial list: half a
    /// cluster is worse than the list already in use.
    /// </remarks>
    private IReadOnlyList<NacosServerAddress> Parse(string body)
    {
        var lines = new List<string>();

        foreach (string raw in body.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return NacosServerAddress.ParseAll(
            lines,
            _options.EndpointContextPath ?? _options.ContextPath);
    }
}
