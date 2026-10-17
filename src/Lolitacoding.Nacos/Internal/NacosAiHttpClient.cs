using System.Net;
using System.Text.Json;
using Lolitacoding.Nacos.Auth;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Internal;

/// <summary>
/// Issues the AI module's HTTP requests.
/// </summary>
/// <remarks>
/// <para>
/// Most of the AI registry is served over the module's gRPC connection, but skills and agent
/// specifications are not: the official client implements them only over HTTP and answers the gRPC
/// requests with "not implemented", and the server side is an ordinary OpenAPI endpoint that streams
/// a ZIP or a JSON body rather than a message. This type is that path, kept apart from the gRPC
/// transport because it has a different failure model — a status code and a body instead of a result
/// code inside a response message.
/// </para>
/// <para>
/// The token is presented in the header Nacos reads on every path, <c>accessToken</c>, rather than as
/// an <c>Authorization</c> bearer header: the client OpenAPI is authenticated by the plugin filter,
/// which looks the token up by that name.
/// </para>
/// <para>
/// A server that cannot be reached is retried on the next configured server, but an HTTP error is
/// not: a resource that does not exist is a fact about the cluster rather than about the node that
/// answered, and retrying it would only add latency to a definite answer.
/// </para>
/// </remarks>
internal sealed class NacosAiHttpClient
{
    /// <summary>The header Nacos reads the access token from.</summary>
    private const string AccessTokenHeader = "accessToken";

    /// <summary>The status Nacos answers a conditional read with when the hash still matches.</summary>
    private const int NotModifiedStatusCode = 304;

    private readonly IReadOnlyList<NacosServerAddress> _servers;
    private readonly HttpClient _httpClient;
    private readonly INacosAuthProvider? _authProvider;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    private int _nextServer;

    /// <summary>
    /// Initializes a new instance of the <see cref="NacosAiHttpClient"/> class.
    /// </summary>
    /// <param name="servers">The servers to address, in configured order.</param>
    /// <param name="httpClient">The client the requests are issued on.</param>
    /// <param name="timeout">How long one request may take.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="authProvider">
    /// Supplies the access token, or <see langword="null"/> for anonymous access.
    /// </param>
    public NacosAiHttpClient(
        IReadOnlyList<NacosServerAddress> servers,
        HttpClient httpClient,
        TimeSpan timeout,
        ILogger logger,
        INacosAuthProvider? authProvider = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _servers = servers;
        _httpClient = httpClient;
        _timeout = timeout;
        _logger = logger;
        _authProvider = authProvider;
    }

    /// <summary>
    /// Issues a GET and returns the response, whatever its status.
    /// </summary>
    /// <param name="path">The absolute path of the endpoint, such as <c>/v3/client/ai/skills</c>.</param>
    /// <param name="query">The query parameters, in order. A <see langword="null"/> value is omitted.</param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The response, with the body already read.</returns>
    /// <exception cref="NacosConnectionException">No configured server could be reached.</exception>
    /// <remarks>
    /// A 304 is a successful answer and is returned like any other status; the decision what an
    /// unchanged body means belongs to the resource, not to the transport.
    /// </remarks>
    public async Task<NacosAiHttpResponse> GetAsync(
        string path,
        IReadOnlyList<KeyValuePair<string, string?>> query,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(query);

        NacosException? failure = null;
        int start = NextServer();

        for (int attempt = 0; attempt < _servers.Count; attempt++)
        {
            NacosServerAddress server = _servers[(start + attempt) % _servers.Count];

            try
            {
                return await SendAsync(server, path, query, retryOnAuthRejection: true, ct)
                    .ConfigureAwait(false);
            }
            catch (NacosConnectionException ex)
            {
                failure = ex;

                _logger.LogDebug(
                    "The AI HTTP request to {Server} failed and the next server will be tried: {Reason}",
                    server.Original,
                    ex.Message);
            }
        }

        throw failure ?? new NacosConnectionException(
            "No Nacos server is configured to serve the AI registry.");
    }

    /// <summary>
    /// Chooses the server a request starts from.
    /// </summary>
    /// <returns>The index of the first server to try.</returns>
    /// <remarks>
    /// The starting point rotates so a client with several servers spreads its requests over them
    /// instead of loading the first one and using the rest only as failover.
    /// </remarks>
    private int NextServer()
    {
        if (_servers.Count <= 1)
        {
            return 0;
        }

        int next = Interlocked.Increment(ref _nextServer);

        return (int)((uint)next % _servers.Count);
    }

    /// <summary>
    /// Issues one request against one server.
    /// </summary>
    /// <param name="server">The server to address.</param>
    /// <param name="path">The path of the endpoint.</param>
    /// <param name="query">The query parameters.</param>
    /// <param name="retryOnAuthRejection">
    /// Whether a 403 may still be retried once with a fresh token.
    /// </param>
    /// <param name="ct">A token that cancels the request.</param>
    /// <returns>The response, with the body already read.</returns>
    /// <exception cref="NacosConnectionException">The server could not be reached.</exception>
    private async Task<NacosAiHttpResponse> SendAsync(
        NacosServerAddress server,
        string path,
        IReadOnlyList<KeyValuePair<string, string?>> query,
        bool retryOnAuthRejection,
        CancellationToken ct)
    {
        Uri uri = server.BuildHttpUri(BuildQueryString(path, query));

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        if (_authProvider is not null)
        {
            string? token = await _authProvider.GetAccessTokenAsync(ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.TryAddWithoutValidation(AccessTokenHeader, token);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        HttpResponseMessage response;

        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new NacosConnectionException(
                string.Concat(
                    "Could not reach the Nacos server at ",
                    server.Original,
                    " to read the AI registry."),
                ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new NacosConnectionException(
                string.Concat(
                    "The AI registry request to ",
                    server.Original,
                    " timed out after ",
                    _timeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    " seconds."),
                ex);
        }

        using (response)
        {
            // A rejected token is fixed by obtaining a fresh one, and only one retry is attempted so
            // a genuinely unauthorized identity fails fast instead of looping. The invalidation is
            // what makes the retry authenticate rather than repeat the same header.
            if (response.StatusCode == HttpStatusCode.Forbidden && retryOnAuthRejection && _authProvider is not null)
            {
                _authProvider.Invalidate();

                return await SendAsync(server, path, query, retryOnAuthRejection: false, ct)
                    .ConfigureAwait(false);
            }

            byte[] body = response.StatusCode == (HttpStatusCode)NotModifiedStatusCode
                ? []
                : await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            return new NacosAiHttpResponse
            {
                StatusCode = response.StatusCode,
                Body = body,
                Headers = CopyHeaders(response),
                ErrorMessage = response.IsSuccessStatusCode || response.StatusCode == (HttpStatusCode)NotModifiedStatusCode
                    ? null
                    : ReadErrorMessage(body, response.ReasonPhrase),
            };
        }
    }

    /// <summary>
    /// Copies the response headers into a lookup.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <returns>The headers, keyed case-insensitively.</returns>
    /// <remarks>
    /// The headers are copied rather than read from the response because the response is disposed
    /// with the request while the caller still needs the resolved version and the content hash.
    /// </remarks>
    private static Dictionary<string, string> CopyHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        return headers;
    }

    /// <summary>
    /// Extracts the message from an error response body.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <param name="reasonPhrase">The status reason phrase, used when the body is not a v3 result.</param>
    /// <returns>The message to report.</returns>
    /// <remarks>
    /// A v3 endpoint answers an error with a result object, and the two string members say different
    /// things: <c>message</c> names the code (<c>resource not found</c>) while <c>data</c> carries the
    /// detail that is actually worth reporting (<c>Skill not found: hello</c>). The detail is
    /// preferred for that reason, with the code name as the fallback for a result that carries none.
    /// Anything else — a gateway's HTML page, an empty body — is not a message at all, so the reason
    /// phrase stands in rather than the body being handed to the caller as if it were one.
    /// </remarks>
    private static string? ReadErrorMessage(byte[] body, string? reasonPhrase)
    {
        if (body.Length > 0)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);

                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return ReadString(document.RootElement, "data")
                        ?? ReadString(document.RootElement, "message")
                        ?? reasonPhrase;
                }
            }
            catch (JsonException)
            {
                // Not a v3 result; the reason phrase says more than an arbitrary body would.
            }
        }

        return reasonPhrase;
    }

    /// <summary>
    /// Reads a string member of a JSON object.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The member name.</param>
    /// <returns>The value, or <see langword="null"/> when absent or not a non-empty string.</returns>
    private static string? ReadString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    /// <summary>
    /// Appends the query parameters to a path.
    /// </summary>
    /// <param name="path">The path of the endpoint.</param>
    /// <param name="query">The parameters, in order.</param>
    /// <returns>The path with its query string.</returns>
    private static string BuildQueryString(string path, IReadOnlyList<KeyValuePair<string, string?>> query)
    {
        if (query.Count == 0)
        {
            return path;
        }

        var builder = new System.Text.StringBuilder(path);
        char separator = '?';

        foreach (KeyValuePair<string, string?> parameter in query)
        {
            if (parameter.Value is null)
            {
                continue;
            }

            builder.Append(separator);
            builder.Append(Uri.EscapeDataString(parameter.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(parameter.Value));
            separator = '&';
        }

        return builder.ToString();
    }
}

/// <summary>
/// One AI registry HTTP response.
/// </summary>
internal sealed class NacosAiHttpResponse
{
    /// <summary>Gets the status code the server answered with.</summary>
    public required HttpStatusCode StatusCode { get; init; }

    /// <summary>Gets the response body.</summary>
    public required byte[] Body { get; init; }

    /// <summary>Gets the response headers, keyed case-insensitively.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>Gets the message of an error response.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Reads one response header.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <returns>The header value, or <see langword="null"/> when the server did not send it.</returns>
    public string? Header(string name)
        => Headers.TryGetValue(name, out string? value) ? value : null;
}
