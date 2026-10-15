using System.Net;
using System.Text.Json;
using Lolitacoding.Nacos.Internal;

namespace Lolitacoding.Nacos.Auth;

/// <summary>
/// Logs in over the Nacos HTTP OpenAPI to exchange credentials for an access token.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint choice follows the official client exactly: the current
/// <c>/v3/auth/user/login</c> endpoint is tried first, and only when the server answers
/// <c>404 Not Found</c> or <c>501 Not Implemented</c> does the client fall back to the legacy
/// <c>/v1/auth/users/login</c>. Those two status codes are the contract: a Nacos 2.x server does
/// not expose the v3 endpoint at all, while a 3.x server exposes both.
/// </para>
/// <para>
/// Credentials are sent in the request body rather than the query string. The official client
/// splits them — user name in the query, password in the body — but the server reads both
/// through <c>HttpServletRequest.getParameter</c>, which covers query and form parameters alike,
/// so a body-only request authenticates identically while keeping the user name out of access
/// logs and URL histories.
/// </para>
/// </remarks>
internal sealed class NacosHttpLoginClient : INacosLoginClient
{
    /// <summary>The current login endpoint.</summary>
    private const string LoginV3Path = "/v3/auth/user/login";

    /// <summary>The login endpoint exposed by Nacos 2.x.</summary>
    private const string LoginV1Path = "/v1/auth/users/login";

    /// <summary>The prefix the server applies to the token in the <c>Authorization</c> header.</summary>
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// The lifetime assumed when the server returns a token without stating one.
    /// </summary>
    /// <remarks>
    /// Both Nacos login endpoints normally report <c>tokenTtl</c>, so this is a fallback for
    /// other auth plugins. It is deliberately conservative: the token is simply refreshed
    /// earlier than strictly necessary, whereas assuming a long lifetime would risk serving an
    /// expired token.
    /// </remarks>
    private static readonly TimeSpan AssumedTokenTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosHttpLoginClient"/> class.
    /// </summary>
    /// <param name="httpClient">The client used to issue login requests.</param>
    public NacosHttpLoginClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async ValueTask<NacosLoginResponse?> LoginAsync(
        NacosServerAddress server,
        string userName,
        string password,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);

        using HttpResponseMessage response = await PostLoginAsync(
            server.BuildHttpUri(LoginV3Path), userName, password, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            // The server predates the v3 API, so retry against the legacy endpoint.
            using HttpResponseMessage legacy = await PostLoginAsync(
                server.BuildHttpUri(LoginV1Path), userName, password, ct).ConfigureAwait(false);

            return await ReadLoginResponseAsync(server, legacy, ct).ConfigureAwait(false);
        }

        return await ReadLoginResponseAsync(server, response, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostLoginAsync(
        Uri uri,
        string userName,
        string password,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("username", userName),
            new KeyValuePair<string, string>("password", password),
        ]);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };

        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new NacosConnectionException(
                $"Could not reach the Nacos server at {uri.GetLeftPart(UriPartial.Authority)} to authenticate.",
                ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new NacosConnectionException(
                $"The login request to {uri.GetLeftPart(UriPartial.Authority)} timed out.", ex);
        }
    }

    private static async Task<NacosLoginResponse?> ReadLoginResponseAsync(
        NacosServerAddress server,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        // 403 is what the Nacos auth plugin returns for bad credentials; 401 is what the
        // Spring Security path behind it returns. Both mean "these credentials are wrong",
        // which is not the same as "the server is broken".
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new NacosConnectionException(
                $"The Nacos server at {server.Original} answered the login request with " +
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        // The body is buffered before parsing so it can be inspected twice: once as the Nacos
        // token object, and once as the token carried in the response header.
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        NacosLoginResponse? login = TryParseTokenObject(body);
        if (login is not null)
        {
            return login;
        }

        // Every Nacos login implementation also returns the token as an Authorization header.
        // Reading it covers auth plugins whose body this client does not model, and it is the
        // only way to obtain a token from the legacy Spring Security path.
        return TryReadBearerHeader(response, server);
    }

    /// <summary>
    /// Parses the flat token object both Nacos login endpoints return.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <returns>The parsed token, or <see langword="null"/> when the body is not that shape.</returns>
    private static NacosLoginResponse? TryParseTokenObject(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            NacosLoginResponse? login = JsonSerializer.Deserialize(
                body, NacosAuthJsonContext.Default.NacosLoginResponse);

            return string.IsNullOrEmpty(login?.AccessToken) ? null : login;
        }
        catch (JsonException)
        {
            // A body that is not JSON at all means this is not the endpoint shape expected;
            // the header fallback below still has a chance of producing a usable token.
            return null;
        }
    }

    /// <summary>
    /// Reads the token from the <c>Authorization</c> response header.
    /// </summary>
    /// <param name="response">The login response.</param>
    /// <param name="server">The server, for the error message.</param>
    /// <returns>The token, or <see langword="null"/> when the server reported no token.</returns>
    private static NacosLoginResponse? TryReadBearerHeader(
        HttpResponseMessage response,
        NacosServerAddress server)
    {
        if (!response.Headers.TryGetValues("Authorization", out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string value in values)
        {
            if (!value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string token = value[BearerPrefix.Length..].Trim();
            if (token.Length == 0)
            {
                continue;
            }

            return new NacosLoginResponse
            {
                AccessToken = token,
                TokenTtl = (long)AssumedTokenTtl.TotalSeconds,
                UserName = server.Host,
            };
        }

        return null;
    }
}
