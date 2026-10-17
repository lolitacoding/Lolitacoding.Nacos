using System.Net;
using Lolitacoding.Nacos.Internal;
using Lolitacoding.Nacos.Tests.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Ai;

/// <summary>
/// Verifies the HTTP behaviour an AI resource is read with.
/// </summary>
/// <remarks>
/// Skills and agent specifications are the only AI resources served over HTTP, and their transport
/// differs from the gRPC one in the ways covered here: the token travels in a different header, an
/// error is a status code rather than a result code, a failed server is worth retrying but a failed
/// request is not, and a 304 is an answer rather than a failure.
/// </remarks>
public sealed class NacosAiHttpClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task GetAsync_PresentsTheTokenInTheHeaderNacosReads()
    {
        var http = new AiHttpTestServer((_, _) => Ok("ok"));
        var auth = new FakeAuthProvider("token-1");

        NacosAiHttpResponse response = await CreateClient(http, auth)
            .GetAsync("/v3/client/ai/skills", [], TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        http.Tokens[0].ShouldBe("token-1");
    }

    [Fact]
    public async Task GetAsync_SendsNoTokenWhenTheClientIsAnonymous()
    {
        var http = new AiHttpTestServer((_, _) => Ok("ok"));

        await CreateClient(http, authProvider: null)
            .GetAsync("/v3/client/ai/skills", [], TestContext.Current.CancellationToken);

        http.Tokens[0].ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_RetriesOnceWithAFreshTokenAfterARejection()
    {
        var http = new AiHttpTestServer((index, _) => index == 0
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Ok("ok"));

        var auth = new FakeAuthProvider("token-1", "token-2");

        NacosAiHttpResponse response = await CreateClient(http, auth)
            .GetAsync("/v3/client/ai/skills", [], TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        auth.InvalidateCount.ShouldBe(1);
        http.RequestCount.ShouldBe(2);
        http.Tokens[1].ShouldBe("token-2");
    }

    [Fact]
    public async Task GetAsync_DoesNotRetryARejectionForever()
    {
        var http = new AiHttpTestServer((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var auth = new FakeAuthProvider("token-1", "token-2");

        NacosAiHttpResponse response = await CreateClient(http, auth)
            .GetAsync("/v3/client/ai/skills", [], TestContext.Current.CancellationToken);

        // The status is returned rather than thrown: what a rejection means is the resource's
        // decision, and a second rejection is a fact the caller has to see.
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        http.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_TriesTheNextServerWhenOneCannotBeReached()
    {
        var http = new AiHttpTestServer((_, request) => request.RequestUri!.Port == 8848
            ? throw new HttpRequestException("connection refused")
            : Ok("served"));

        NacosAiHttpResponse response = await CreateClient(
                http,
                authProvider: null,
                servers: ["127.0.0.1:8848", "127.0.0.1:8849"])
            .GetAsync("/v3/client/ai/skills", [], TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Body.ShouldBe("served"u8.ToArray());
    }

    [Fact]
    public async Task GetAsync_ReportsAConnectionFailureWhenNoServerAnswers()
    {
        var http = new AiHttpTestServer((_, _) => throw new HttpRequestException("refused"));

        await Should.ThrowAsync<NacosConnectionException>(() => CreateClient(
                http,
                authProvider: null,
                servers: ["127.0.0.1:8848", "127.0.0.1:8849"])
            .GetAsync("/v3/client/ai/skills", [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_ReportsATimeoutAsAConnectionFailure()
    {
        await Should.ThrowAsync<NacosConnectionException>(() => CreateClient(
                new HangingHandler(),
                authProvider: null,
                timeout: TimeSpan.FromMilliseconds(50))
            .GetAsync("/v3/client/ai/skills", [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_BuildsTheQueryInOrderAndOmitsEmptyValues()
    {
        var http = new AiHttpTestServer((_, _) => Ok("ok"));

        await CreateClient(http, authProvider: null).GetAsync(
            "/v3/client/ai/skills",
            [
                new KeyValuePair<string, string?>("namespaceId", "public"),
                new KeyValuePair<string, string?>("name", "hello world"),
                new KeyValuePair<string, string?>("version", null),
                new KeyValuePair<string, string?>("label", "stable"),
            ],
            TestContext.Current.CancellationToken);

        string query = http.Requests[0].Query;

        query.ShouldBe("?namespaceId=public&name=hello%20world&label=stable");
    }

    [Fact]
    public async Task GetAsync_ReturnsA304WithoutABody()
    {
        var http = new AiHttpTestServer((_, _) => AiHttpTestServer.NotModified("hash-1", "1.0.0"));

        NacosAiHttpResponse response = await CreateClient(http, authProvider: null)
            .GetAsync("/v3/client/ai/skills", [], TestContext.Current.CancellationToken);

        // Not-modified is a successful answer, not a failure, and it carries the headers the client
        // needs to keep its cache bookkeeping current.
        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        response.Body.ShouldBeEmpty();
        response.Header("X-Nacos-Skill-Md5").ShouldBe("hash-1");
        response.Header("X-Nacos-Skill-Resolved-Version").ShouldBe("1.0.0");
    }

    [Fact]
    public async Task GetAsync_ReadsTheMessageOutOfAV3Error()
    {
        var http = new AiHttpTestServer((_, _) => AiHttpTestServer.NotFound("Skill not found: hello"));

        NacosAiHttpResponse response = await CreateClient(http, authProvider: null)
            .GetAsync("/v3/client/ai/skills", [], TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.ErrorMessage.ShouldBe("Skill not found: hello");
    }

    [Fact]
    public async Task GetAsync_FallsBackToTheReasonPhraseWhenTheBodyIsNotAResult()
    {
        var http = new AiHttpTestServer((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>gateway</html>"),
        });

        NacosAiHttpResponse response = await CreateClient(http, authProvider: null)
            .GetAsync("/v3/client/ai/skills", [], TestContext.Current.CancellationToken);

        // A gateway's HTML page is not a message worth surfacing to a caller.
        response.ErrorMessage.ShouldBe("Bad Gateway");
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body),
    };

    private static NacosAiHttpClient CreateClient(
        HttpMessageHandler handler,
        FakeAuthProvider? authProvider,
        IReadOnlyList<string>? servers = null,
        TimeSpan? timeout = null)
    {
        IReadOnlyList<NacosServerAddress> addresses = NacosServerAddress.ParseAll(servers ?? ["127.0.0.1:8848"]);

        return new NacosAiHttpClient(
            addresses,
            new HttpClient(handler),
            timeout ?? Timeout,
            NullLogger.Instance,
            authProvider);
    }

    /// <summary>
    /// A handler that never answers within the configured timeout.
    /// </summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
