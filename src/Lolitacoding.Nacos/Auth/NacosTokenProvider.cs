using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Obtains and caches a Nacos access token, re-authenticating before it expires.
/// </summary>
/// <remarks>
/// <para>
/// The refresh policy reproduces the official client's, because the constants involved are
/// deliberate rather than arbitrary:
/// </para>
/// <list type="bullet">
/// <item>
/// A token is considered usable for <c>ttl - refreshWindow</c> seconds, where
/// <c>refreshWindow</c> is drawn at random from <c>[ttl/15, ttl/10]</c>. Randomising the window
/// spreads re-authentication across a fleet so that a large deployment does not stampede the
/// authentication endpoint at a shared token expiry.
/// </item>
/// <item>
/// A forced refresh is suppressed for 60 seconds after the last successful login. Without that
/// guard, a server that rejects a token it just issued would put the client into a tight login
/// loop; with it, the rejection surfaces to the caller instead.
/// </item>
/// </list>
/// <para>
/// This type is safe for concurrent use: callers race to fill a single cache entry, and only one
/// of them performs the login.
/// </para>
/// </remarks>
internal sealed class NacosTokenProvider : INacosAuthProvider
{
    /// <summary>
    /// The shortest interval between two logins, in seconds.
    /// </summary>
    /// <remarks>
    /// Matches the official client's <c>reLoginWindow</c>.
    /// </remarks>
    private const long ReLoginWindowSeconds = 60;

    private readonly NacosServerList _servers;
    private readonly INacosLoginClient _loginClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Random _random;
    private readonly string _userName;
    private readonly string _password;
    private readonly SemaphoreSlim _loginGate = new(1, 1);

    private string? _accessToken;
    private long _expiresAtTicks;
    private long _lastLoginTicks;
    private int _serverIndex;

    /// <summary>
    /// Set when <see cref="Invalidate"/> asked for a fresh token because the server rejected one.
    /// </summary>
    /// <remarks>
    /// This distinction matters: an ordinary cache miss may log in immediately, but a re-login
    /// after a rejection is rate limited, because the server rejecting a token it issued moments
    /// ago means repeating the identical request is unlikely to help.
    /// </remarks>
    private bool _reLoginRequested;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosTokenProvider"/> class.
    /// </summary>
    /// <param name="options">The client options holding the credentials.</param>
    /// <param name="servers">The servers to try authenticating against, in order.</param>
    /// <param name="loginClient">Performs the login request.</param>
    /// <param name="timeProvider">The clock used for expiry decisions.</param>
    /// <param name="logger">The logger for authentication diagnostics.</param>
    /// <param name="random">The random source used to pick the refresh window.</param>
    public NacosTokenProvider(
        NacosOptions options,
        IReadOnlyList<NacosServerAddress> servers,
        INacosLoginClient loginClient,
        TimeProvider timeProvider,
        ILogger logger,
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(loginClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _servers = servers as NacosServerList ?? new NacosServerList(servers);
        _loginClient = loginClient;
        _timeProvider = timeProvider;
        _logger = logger;
        _random = random ?? new Random();
        _userName = options.UserName ?? string.Empty;
        _password = options.Password ?? string.Empty;
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        string? cached = Volatile.Read(ref _accessToken);

        if (cached is not null && _timeProvider.GetUtcNow().UtcTicks < Volatile.Read(ref _expiresAtTicks))
        {
            return cached;
        }

        await _loginGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed the token while this one waited for the gate.
            cached = _accessToken;
            if (cached is not null && _timeProvider.GetUtcNow().UtcTicks < _expiresAtTicks)
            {
                return cached;
            }

            bool force = _reLoginRequested;
            _reLoginRequested = false;

            string? refreshed = await LoginAsync(force, ct).ConfigureAwait(false);

            // A suppressed re-login returns null. Presenting the previously issued token is the
            // best available option: it may still be accepted, whereas sending no credential
            // would guarantee an anonymous request.
            return refreshed ?? Volatile.Read(ref _accessToken);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        // Only the expiry is cleared, not the token. Dropping the token outright would leave the
        // retry with no credential at all, which is strictly worse than presenting the token that
        // was just rejected; the official client behaves the same way, leaving its identity
        // context untouched when a re-login is suppressed.
        Volatile.Write(ref _expiresAtTicks, 0);

        // Flagged so the next login is treated as a re-login and therefore rate limited.
        _reLoginRequested = true;
    }

    /// <summary>
    /// Performs a login, trying each configured server in turn.
    /// </summary>
    /// <param name="force">
    /// Whether this login follows a rejected token rather than a cache miss. A forced login is
    /// suppressed when one happened within the last 60 seconds.
    /// </param>
    /// <param name="ct">A token that cancels the login.</param>
    /// <returns>The token issued by the server.</returns>
    /// <exception cref="NacosAuthException">
    /// The server rejected the credentials, or the response was missing the token or its
    /// lifetime.
    /// </exception>
    private async Task<string?> LoginAsync(bool force, CancellationToken ct)
    {
        long now = _timeProvider.GetUtcNow().UtcTicks;

        if (force && _lastLoginTicks != 0 &&
            now - _lastLoginTicks < TimeSpan.FromSeconds(ReLoginWindowSeconds).Ticks)
        {
            // Logging in again this soon would spin: the server rejected a token it issued
            // moments ago, so repeating the same request is unlikely to help.
            NacosAuthLog.ReLoginThrottled(_logger, ReLoginWindowSeconds);
            return null;
        }

        IReadOnlyList<NacosServerAddress> servers = _servers.Items;

        if (servers.Count == 0)
        {
            throw new NacosAuthException("No Nacos server address is configured to authenticate against.");
        }

        NacosAuthException? lastFailure = null;

        for (int attempt = 0; attempt < servers.Count; attempt++)
        {
            int index = (_serverIndex + attempt) % servers.Count;
            NacosServerAddress server = servers[index];

            NacosLoginResponse? response;
            try
            {
                response = await _loginClient
                    .LoginAsync(server, _userName, _password, ct)
                    .ConfigureAwait(false);
            }
            catch (NacosConnectionException ex)
            {
                // An unreachable server is not a credential failure, so keep trying the others.
                lastFailure = new NacosAuthException(
                    $"Could not reach the Nacos server at {server.Original} to authenticate.", ex);
                NacosAuthLog.ServerUnreachable(_logger, server.Original, ex.Message);
                continue;
            }

            if (response is null)
            {
                // The server answered but rejected the credentials; another server would give
                // the same verdict, so fail fast rather than retrying the whole list.
                throw new NacosAuthException(
                    $"The Nacos server at {server.Original} rejected the configured credentials. " +
                    "Check the user name and password, and confirm the account is enabled.");
            }

            if (string.IsNullOrEmpty(response.AccessToken))
            {
                throw new NacosAuthException(
                    $"The Nacos server at {server.Original} accepted the login but returned no access token.");
            }

            if (response.TokenTtl <= 0)
            {
                // Assuming a default here would risk caching an already-expired token, so this
                // is treated as a protocol violation rather than defaulted.
                throw new NacosAuthException(
                    $"The Nacos server at {server.Original} returned an access token without a " +
                    "usable tokenTtl, so its expiry cannot be determined.");
            }

            TimeSpan ttl = TimeSpan.FromSeconds(response.TokenTtl);
            TimeSpan refreshWindow = GenerateRefreshWindow(ttl);
            TimeSpan usable = ttl - refreshWindow;

            _serverIndex = index;
            _lastLoginTicks = now;
            _expiresAtTicks = now + usable.Ticks;
            Volatile.Write(ref _accessToken, response.AccessToken);

            NacosAuthLog.LoginSucceeded(
                _logger, server.Original, response.TokenTtl, refreshWindow.TotalSeconds);

            return response.AccessToken;
        }

        throw lastFailure ?? new NacosAuthException("Authentication failed against every configured Nacos server.");
    }

    /// <summary>
    /// Draws the refresh window as a random value in <c>[ttl/15, ttl/10]</c>.
    /// </summary>
    /// <param name="ttl">The lifetime the server granted.</param>
    /// <returns>The window subtracted from the lifetime before the token is considered stale.</returns>
    /// <remarks>
    /// Randomising this window is deliberate: if every client refreshed at exactly
    /// <c>ttl - ttl/10</c>, a fleet that started together would re-authenticate together.
    /// </remarks>
    private TimeSpan GenerateRefreshWindow(TimeSpan ttl)
    {
        double ttlSeconds = ttl.TotalSeconds;
        long lower = (long)(ttlSeconds / 15);
        long upper = (long)(ttlSeconds / 10);

        if (upper <= lower)
        {
            // A very short TTL leaves no room to spread the refresh, so fall back to the
            // midpoint rather than passing an inverted range to the random source.
            return TimeSpan.FromSeconds(Math.Max(1, lower));
        }

        return TimeSpan.FromSeconds(_random.NextInt64(lower, upper));
    }

    /// <inheritdoc />
    public void Dispose() => _loginGate.Dispose();

    /// <summary>
    /// Exposes <see cref="GenerateRefreshWindow"/> to the test assembly.
    /// </summary>
    /// <param name="ttl">The lifetime to compute a window for.</param>
    /// <returns>The refresh window.</returns>
    /// <remarks>
    /// The window is randomized, so it can only be asserted through a seam like this one rather
    /// than by observing a login timestamp.
    /// </remarks>
    internal TimeSpan GenerateRefreshWindowForTest(TimeSpan ttl) => GenerateRefreshWindow(ttl);
}
