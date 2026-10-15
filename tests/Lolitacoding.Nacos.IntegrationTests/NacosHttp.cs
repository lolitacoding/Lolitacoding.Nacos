using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Shared helpers for talking to a running Nacos server over HTTP.
/// </summary>
/// <remarks>
/// These helpers deliberately use raw <see cref="HttpClient"/> calls rather than the client under
/// test. An integration test that used the client to set up its own preconditions could not tell
/// a client bug from a server one, so setup and verification go through the OpenAPI directly
/// while only the behaviour under test uses the client.
/// </remarks>
internal static class NacosHttp
{
    /// <summary>
    /// Publishes or replaces a configuration.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="dataId">The configuration identifier.</param>
    /// <param name="group">The configuration group.</param>
    /// <param name="content">The configuration content.</param>
    /// <param name="accessToken">The access token, when the server requires authentication.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The task.</returns>
    public static async Task PublishConfigAsync(
        HttpClient http,
        string baseAddress,
        string dataId,
        string group,
        string content,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        string url = string.Concat(
            baseAddress, "/nacos/v1/cs/configs?dataId=", Uri.EscapeDataString(dataId),
            "&group=", Uri.EscapeDataString(group),
            "&content=", Uri.EscapeDataString(content),
            "&type=text");

        using HttpResponseMessage response = await SendAsync(http, HttpMethod.Post, url, accessToken, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Reads a configuration.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="dataId">The configuration identifier.</param>
    /// <param name="group">The configuration group.</param>
    /// <param name="accessToken">The access token, when the server requires authentication.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The configuration content, or <see langword="null"/> when absent.</returns>
    public static async Task<string?> GetConfigAsync(
        HttpClient http,
        string baseAddress,
        string dataId,
        string group,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        string url = string.Concat(
            baseAddress, "/nacos/v1/cs/configs?dataId=", Uri.EscapeDataString(dataId),
            "&group=", Uri.EscapeDataString(group));

        using HttpResponseMessage response = await SendAsync(http, HttpMethod.Get, url, accessToken, ct);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(ct);
        return body.Length == 0 ? null : body;
    }

    /// <summary>
    /// Attempts a login and reports whether the server accepted the credentials.
    /// </summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="baseAddress">The server base address.</param>
    /// <param name="userName">The user name to present.</param>
    /// <param name="password">The password to present.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The access token, or <see langword="null"/> when the server rejected the login.</returns>
    public static async Task<string?> TryLoginAsync(
        HttpClient http,
        string baseAddress,
        string userName,
        string password,
        CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("username", userName),
            new KeyValuePair<string, string>("password", password),
        ]);

        var response = await http.PostAsync(
            string.Concat(baseAddress, "/nacos/v1/auth/users/login"), form, ct);

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(ct);

            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("accessToken", out JsonElement token)
                ? token.GetString()
                : null;
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        HttpMethod method,
        string url,
        string? accessToken,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrEmpty(accessToken))
        {
            // The server accepts a bearer Authorization header and an accessToken parameter;
            // the header is the form a real client uses when it has a token rather than a login.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await http.SendAsync(request, ct);
    }

    /// <summary>
    /// Builds a configuration body large enough to be unambiguous in an assertion.
    /// </summary>
    /// <param name="marker">A unique marker to embed.</param>
    /// <returns>The configuration content.</returns>
    public static string ConfigContent(string marker) => string.Concat(
        "marker=", marker, "\n", new string('x', 64));

    /// <summary>Creates a deterministic, unique configuration identifier.</summary>
    /// <param name="prefix">A prefix identifying the test.</param>
    /// <returns>The identifier.</returns>
    public static string UniqueId(string prefix) => string.Concat(
        prefix, "-", Guid.NewGuid().ToString("N")[..12]);

    /// <summary>Encodes a string as UTF-8 bytes for a raw HTTP body.</summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>The encoded bytes.</returns>
    public static ByteArrayContent Utf8(string value) => new(Encoding.UTF8.GetBytes(value));
}
