using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// A scripted stand-in for the AI module's HTTP endpoints.
/// </summary>
/// <remarks>
/// Skills and agent specifications are served over HTTP rather than over the AI connection, so the
/// transport they use has a different shape from the gRPC one: a path with query parameters, an
/// <c>accessToken</c> header and a status code. This handler records both and answers from a script,
/// so a test asserts on the request the client built as well as on what it made of the answer.
/// </remarks>
internal sealed class AiHttpTestServer : HttpMessageHandler
{
    private readonly List<Uri> _requests = [];
    private readonly List<string?> _tokens = [];
    private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _respond;

    /// <summary>
    /// Initializes a new instance of the <see cref="AiHttpTestServer"/> class.
    /// </summary>
    /// <param name="respond">
    /// Produces the answer, given the zero-based index of the request and the request itself.
    /// </param>
    public AiHttpTestServer(Func<int, HttpRequestMessage, HttpResponseMessage> respond) =>
        _respond = respond;

    /// <summary>Gets the URIs that were requested, in order.</summary>
    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Gets the access token each request presented, in order.</summary>
    public IReadOnlyList<string?> Tokens
    {
        get
        {
            lock (_tokens)
            {
                return [.. _tokens];
            }
        }
    }

    /// <summary>Gets how many requests were made.</summary>
    public int RequestCount
    {
        get
        {
            lock (_requests)
            {
                return _requests.Count;
            }
        }
    }

    /// <summary>
    /// Reads one query parameter of a recorded request.
    /// </summary>
    /// <param name="index">The index of the request.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The decoded value, or <see langword="null"/> when the request did not carry it.</returns>
    public string? Query(int index, string name) => ReadQuery(Requests[index], name);

    /// <summary>Builds an answer that carries a skill archive.</summary>
    /// <param name="archive">The archive bytes.</param>
    /// <param name="md5">The published hash, or <see langword="null"/> to omit the header.</param>
    /// <param name="version">The resolved version, or <see langword="null"/> to omit the header.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage Zip(byte[] archive, string? md5 = null, string? version = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive),
        };

        response.Content.Headers.ContentType = new("application/zip");

        if (md5 is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Nacos-Skill-Md5", md5);
            response.Headers.TryAddWithoutValidation("ETag", md5);
        }

        if (version is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Nacos-Skill-Resolved-Version", version);
        }

        return response;
    }

    /// <summary>Builds the answer a conditional read gets when the hash still matches.</summary>
    /// <param name="md5">The published hash the server echoes.</param>
    /// <param name="version">The resolved version.</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage NotModified(string? md5 = null, string? version = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotModified);

        if (md5 is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Nacos-Skill-Md5", md5);
        }

        if (version is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Nacos-Skill-Resolved-Version", version);
        }

        return response;
    }

    /// <summary>Builds the answer a missing skill or version gets.</summary>
    /// <param name="detail">The detail to report.</param>
    /// <param name="code">The business code in the body, or <c>20004</c> for "resource not found".</param>
    /// <returns>The response.</returns>
    public static HttpResponseMessage NotFound(string detail, int code = 20004)
        => Result(HttpStatusCode.NotFound, code, "resource not found", detail);

    /// <summary>Builds a v3 result body with the given status.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="code">The business code.</param>
    /// <param name="message">The message, which names the code.</param>
    /// <param name="data">The detail, which is what the client reports.</param>
    /// <returns>The response.</returns>
    /// <remarks>
    /// The two string members are separate on purpose: a Nacos error names the code in
    /// <c>message</c> and puts the reason in <c>data</c>, so a test that put the reason in
    /// <c>message</c> would not be exercising the shape a real server answers with.
    /// </remarks>
    public static HttpResponseMessage Result(
        HttpStatusCode status,
        int code,
        string message,
        string? data = null)
    {
        string body = JsonSerializer.Serialize(new { code, message, data });

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// Builds a ZIP archive with the given entries.
    /// </summary>
    /// <param name="entries">The entry names and their text content.</param>
    /// <returns>The archive bytes.</returns>
    /// <remarks>
    /// The entries are written under the skill directory, the way the server serves them, so a test
    /// that needs an unsafe entry asks for one by name rather than by patching the bytes.
    /// </remarks>
    public static byte[] CreateArchive(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);

                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        int index;

        lock (_requests)
        {
            index = _requests.Count;
            _requests.Add(request.RequestUri!);
            _tokens.Add(
                request.Headers.TryGetValues("accessToken", out IEnumerable<string>? values)
                    ? string.Join(",", values)
                    : null);
        }

        return Task.FromResult(_respond(index, request));
    }

    /// <summary>
    /// Reads one query parameter out of a URI.
    /// </summary>
    /// <param name="uri">The request URI.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The decoded value, or <see langword="null"/>.</returns>
    private static string? ReadQuery(Uri uri, string name)
    {
        string query = uri.Query;

        if (query.Length <= 1)
        {
            return null;
        }

        foreach (string pair in query[1..].Split('&'))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator < 0)
            {
                continue;
            }

            if (string.Equals(
                Uri.UnescapeDataString(pair[..separator]),
                name,
                StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        return null;
    }
}
