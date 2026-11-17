using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Obtains a Nacos access token from an OpenID Connect identity provider.
/// </summary>
/// <remarks>
/// <para>
/// This is the official client's <c>OidcClientAuthServiceImpl</c>: the token is minted by the
/// identity provider through the OAuth2 client-credentials grant and presented to Nacos as the
/// access token. The Nacos server's OIDC plugin validates it as a bearer token, so nothing about
/// the transport changes.
/// </para>
/// <para>
/// The refresh policy matches the user-name/password provider: a token is used for
/// <c>ttl - refreshWindow</c> seconds, where the window is drawn at random from
/// <c>[ttl/15, ttl/10]</c>, and a forced refresh after a rejection is suppressed for 60 seconds so
/// that a provider rejecting its own token cannot put the client into a login loop.
/// </para>
/// <para>
/// Discovery runs on the first token request rather than at construction, so creating a client
/// never blocks on the identity provider. The resolved endpoint is remembered for the lifetime of
/// the provider.
/// </para>
/// </remarks>
internal sealed class NacosOidcAuthProvider : INacosAuthProvider
{
    /// <summary>
    /// The shortest interval between two token requests that follow a rejection, in seconds.
    /// </summary>
    private const long ReFetchWindowSeconds = 60;

    /// <summary>
    /// The lifetime assumed when the identity provider omits <c>expires_in</c>.
    /// </summary>
    private static readonly TimeSpan AssumedTokenTtl = TimeSpan.FromSeconds(300);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Random _random;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scope;
    private readonly string? _issuerUri;
    private readonly string? _configuredTokenEndpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _accessToken;
    private long _expiresAtTicks;
    private long _lastFetchTicks;
    private string? _tokenEndpoint;
    private bool _refreshRequested;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosOidcAuthProvider"/> class.
    /// </summary>
    /// <param name="options">The OpenID Connect settings.</param>
    /// <param name="httpClient">The client used for discovery and token requests.</param>
    /// <param name="timeProvider">The clock used for expiry decisions.</param>
    /// <param name="logger">The logger for authentication diagnostics.</param>
    /// <param name="random">The random source used to pick the refresh window.</param>
    public NacosOidcAuthProvider(
        NacosOidcOptions options,
        HttpClient httpClient,
        TimeProvider timeProvider,
        ILogger logger,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _timeProvider = timeProvider;
        _logger = logger;
        _random = random ?? new Random();
        _clientId = options.ClientId ?? string.Empty;
        _clientSecret = options.ClientSecret ?? string.Empty;
        _scope = options.Scope ?? string.Empty;
        _issuerUri = string.IsNullOrWhiteSpace(options.IssuerUri) ? null : options.IssuerUri;
        _configuredTokenEndpoint =
            string.IsNullOrWhiteSpace(options.TokenEndpoint) ? null : options.TokenEndpoint;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        string? cached = Volatile.Read(ref _accessToken);

        if (cached is not null && _timeProvider.GetUtcNow().UtcTicks < Volatile.Read(ref _expiresAtTicks))
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have fetched a token while this one waited for the gate.
            cached = _accessToken;
            if (cached is not null && _timeProvider.GetUtcNow().UtcTicks < _expiresAtTicks)
            {
                return cached;
            }

            bool force = _refreshRequested;
            _refreshRequested = false;

            string? refreshed = await FetchTokenAsync(force, ct).ConfigureAwait(false);

            // A suppressed refresh returns null. The token that was just rejected is still the
            // best available credential; sending nothing would guarantee an anonymous request.
            return refreshed ?? Volatile.Read(ref _accessToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        // The token is kept for the same reason as in NacosTokenProvider: a refresh may be
        // suppressed, and the rejected token is still better than none.
        Volatile.Write(ref _expiresAtTicks, 0);
        _refreshRequested = true;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Requests a token, rate-limiting the request that follows a rejection.
    /// </summary>
    /// <param name="force">
    /// Whether this request follows a rejected token rather than a cache miss.
    /// </param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>The token, or <see langword="null"/> when a forced refresh was suppressed.</returns>
    /// <exception cref="NacosAuthException">The identity provider could not issue a token.</exception>
    private async Task<string?> FetchTokenAsync(bool force, CancellationToken ct)
    {
        long now = _timeProvider.GetUtcNow().UtcTicks;

        if (force && _lastFetchTicks != 0 &&
            now - _lastFetchTicks < TimeSpan.FromSeconds(ReFetchWindowSeconds).Ticks)
        {
            NacosAuthLog.OidcRefreshThrottled(_logger, ReFetchWindowSeconds);
            return null;
        }

        string endpoint = _tokenEndpoint ?? await DiscoverAsync(ct).ConfigureAwait(false);

        OidcTokenResponse? token = await RequestTokenAsync(endpoint, ct).ConfigureAwait(false);

        if (token is null || string.IsNullOrEmpty(token.AccessToken))
        {
            throw new NacosAuthException(
                $"The OpenID Connect provider at {endpoint} answered the token request without an access_token.");
        }

        TimeSpan ttl = ReadTokenTtl(token);

        if (ttl <= TimeSpan.Zero)
        {
            throw new NacosAuthException(
                $"The OpenID Connect provider at {endpoint} returned an access token without a " +
                "usable expires_in, so its expiry cannot be determined.");
        }

        TimeSpan refreshWindow = GenerateRefreshWindow(ttl);
        TimeSpan usable = ttl - refreshWindow;

        _lastFetchTicks = now;
        _expiresAtTicks = now + usable.Ticks;
        Volatile.Write(ref _accessToken, token.AccessToken);

        NacosAuthLog.OidcTokenObtained(
            _logger, endpoint, (long)ttl.TotalSeconds, refreshWindow.TotalSeconds);

        return token.AccessToken;
    }

    /// <summary>
    /// Resolves the token endpoint, performing OIDC discovery if no endpoint was configured.
    /// </summary>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>The token endpoint.</returns>
    /// <exception cref="NacosAuthException">Discovery failed or the document named no endpoint.</exception>
    private async Task<string> DiscoverAsync(CancellationToken ct)
    {
        if (_configuredTokenEndpoint is not null)
        {
            _tokenEndpoint = _configuredTokenEndpoint;
            return _configuredTokenEndpoint;
        }

        string issuer = _issuerUri!;
        string discoveryUrl = issuer.EndsWith('/')
            ? issuer + ".well-known/openid-configuration"
            : issuer + "/.well-known/openid-configuration";

        using var request = new HttpRequestMessage(HttpMethod.Get, discoveryUrl);
        request.Headers.Accept.ParseAdd("application/json");

        using HttpResponseMessage response =
            await SendAsync(request, discoveryUrl, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new NacosAuthException(
                $"OpenID Connect discovery at {discoveryUrl} answered HTTP " +
                $"{(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        OidcDiscoveryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(
                body, NacosAuthJsonContext.Default.OidcDiscoveryDocument);
        }
        catch (JsonException ex)
        {
            throw new NacosAuthException(
                $"OpenID Connect discovery at {discoveryUrl} did not return valid JSON.", ex);
        }

        if (string.IsNullOrWhiteSpace(document?.TokenEndpoint))
        {
            throw new NacosAuthException(
                $"OpenID Connect discovery at {discoveryUrl} did not name a token_endpoint.");
        }

        string endpoint = document.TokenEndpoint;
        _tokenEndpoint = endpoint;

        NacosAuthLog.OidcDiscovered(_logger, discoveryUrl, endpoint);

        return endpoint;
    }

    /// <summary>
    /// Performs the client-credentials grant.
    /// </summary>
    /// <param name="endpoint">The token endpoint.</param>
    /// <param name="ct">A token that cancels the work.</param>
    /// <returns>The parsed response.</returns>
    /// <exception cref="NacosAuthException">The endpoint rejected the request or the body was not JSON.</exception>
    private async Task<OidcTokenResponse?> RequestTokenAsync(string endpoint, CancellationToken ct)
    {
        List<KeyValuePair<string, string>> form =
        [
            new("grant_type", "client_credentials"),
            new("client_id", _clientId),
            new("client_secret", _clientSecret),
        ];

        if (!string.IsNullOrEmpty(_scope))
        {
            form.Add(new KeyValuePair<string, string>("scope", _scope));
        }

        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");

        using HttpResponseMessage response =
            await SendAsync(request, endpoint, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new NacosAuthException(
                $"The OpenID Connect provider at {endpoint} rejected the client credentials " +
                $"(HTTP {(int)response.StatusCode}). Check ClientId and ClientSecret.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new NacosAuthException(
                $"The OpenID Connect provider at {endpoint} answered the token request with " +
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            return JsonSerializer.Deserialize(body, NacosAuthJsonContext.Default.OidcTokenResponse);
        }
        catch (JsonException ex)
        {
            throw new NacosAuthException(
                $"The OpenID Connect provider at {endpoint} returned a token response that is not JSON.",
                ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        string url,
        CancellationToken ct)
    {
        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new NacosAuthException(
                $"Could not reach the OpenID Connect endpoint at {url}.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new NacosAuthException(
                $"The request to the OpenID Connect endpoint at {url} timed out.", ex);
        }
    }

    /// <summary>
    /// Reads the lifetime the identity provider granted.
    /// </summary>
    /// <param name="token">The token response.</param>
    /// <returns>The lifetime, or the assumed default when none was reported.</returns>
    private static TimeSpan ReadTokenTtl(OidcTokenResponse token)
    {
        JsonElement? value = token.ExpiresIn;

        if (value is null)
        {
            return AssumedTokenTtl;
        }

        long seconds = value.Value.ValueKind switch
        {
            JsonValueKind.Number when value.Value.TryGetInt64(out long number) => number,
            JsonValueKind.String when long.TryParse(
                value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long parsed) => parsed,
            _ => 0,
        };

        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }

    /// <summary>
    /// Draws the refresh window as a random value in <c>[ttl/15, ttl/10]</c>.
    /// </summary>
    /// <param name="ttl">The lifetime the identity provider granted.</param>
    /// <returns>The window subtracted from the lifetime before the token is considered stale.</returns>
    private TimeSpan GenerateRefreshWindow(TimeSpan ttl)
    {
        double ttlSeconds = ttl.TotalSeconds;
        long lower = (long)(ttlSeconds / 15);
        long upper = (long)(ttlSeconds / 10);

        if (upper <= lower)
        {
            TimeSpan fallback = TimeSpan.FromSeconds(Math.Max(1, lower));
            return fallback < ttl ? fallback : TimeSpan.FromSeconds(ttlSeconds / 2);
        }

        return TimeSpan.FromSeconds(_random.NextInt64(lower, upper));
    }

    /// <summary>
    /// Exposes <see cref="GenerateRefreshWindow"/> to the test assembly.
    /// </summary>
    /// <param name="ttl">The lifetime to compute a window for.</param>
    /// <returns>The refresh window.</returns>
    internal TimeSpan GenerateRefreshWindowForTest(TimeSpan ttl) => GenerateRefreshWindow(ttl);
}
