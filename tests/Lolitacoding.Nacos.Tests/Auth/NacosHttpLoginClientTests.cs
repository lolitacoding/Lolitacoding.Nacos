using System.Net;
using System.Text;
using System.Text.Json;
using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Time.Testing;

namespace Lolitacoding.Nacos.Tests.Auth;

/// <summary>
/// Verifies the endpoint selection and response handling of <see cref="NacosHttpLoginClient"/>.
/// </summary>
/// <remarks>
/// These tests pin the exact HTTP contract: which endpoint is called first, which status codes
/// trigger the legacy fallback, and which response shapes yield a usable token. That contract is
/// what lets one client work against both Nacos 2.x and 3.x.
/// </remarks>
public sealed class NacosHttpLoginClientTests
{
    private static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:8848");

    [Fact]
    public async Task Login_CallsTheV3EndpointFirst()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, V3Body("token-1", 18000)));
        NacosHttpLoginClient client = CreateClient(handler);

        NacosLoginResponse? response = await client.LoginAsync(
            Server, "nacos", "secret", TestContext.Current.CancellationToken);

        response!.AccessToken.ShouldBe("token-1");
        response.TokenTtl.ShouldBe(18000);

        // The v3 endpoint must be preferred; the legacy one exists only for older servers.
        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].AbsolutePath.ShouldBe("/nacos/v3/auth/user/login");
    }

    [Fact]
    public async Task Login_ParsesTheFlatV3TokenObject()
    {
        // The login endpoints are the one place Nacos does not wrap the body in the standard
        // {code, message, data} envelope, so the flat shape must parse directly.
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.OK,
            """{"accessToken":"abc.def.ghi","tokenTtl":18000,"globalAdmin":true,"username":"nacos"}"""));

        NacosHttpLoginClient client = CreateClient(handler);

        NacosLoginResponse? response = await client.LoginAsync(
            Server, "nacos", "secret", TestContext.Current.CancellationToken);

        response!.AccessToken.ShouldBe("abc.def.ghi");
        response.TokenTtl.ShouldBe(18000);
        response.GlobalAdmin.ShouldBeTrue();
        response.UserName.ShouldBe("nacos");
    }

    [Fact]
    public async Task Login_FallsBackToTheLegacyEndpointOnNotFound()
    {
        // A Nacos 2.x server has no v3 login route at all and answers 404.
        var handler = new RecordingHandler(request =>
            IsV3(request)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(HttpStatusCode.OK, V3Body("token-legacy", 18000)));

        NacosHttpLoginClient client = CreateClient(handler);

        NacosLoginResponse? response = await client.LoginAsync(
            Server, "nacos", "secret", TestContext.Current.CancellationToken);

        response!.AccessToken.ShouldBe("token-legacy");
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].AbsolutePath.ShouldBe("/nacos/v3/auth/user/login");
        handler.Requests[1].AbsolutePath.ShouldBe("/nacos/v1/auth/users/login");
    }

    [Fact]
    public async Task Login_FallsBackToTheLegacyEndpointOnNotImplemented()
    {
        var handler = new RecordingHandler(request =>
            IsV3(request)
                ? new HttpResponseMessage(HttpStatusCode.NotImplemented)
                : Json(HttpStatusCode.OK, V3Body("token-legacy", 18000)));

        NacosHttpLoginClient client = CreateClient(handler);

        NacosLoginResponse? response = await client.LoginAsync(
            Server, "nacos", "secret", TestContext.Current.CancellationToken);

        response!.AccessToken.ShouldBe("token-legacy");
    }

    [Fact]
    public async Task Login_DoesNotFallBackOnSuccess()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, V3Body("token-1", 18000)));
        NacosHttpLoginClient client = CreateClient(handler);

        await client.LoginAsync(Server, "nacos", "secret", TestContext.Current.CancellationToken);

        // A single request proves the fallback is driven by the status code, not by a token
        // field being absent.
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Login_ReturnsNullWhenTheServerRejectsTheCredentials()
    {
        // The Nacos auth plugin returns 403 for a wrong password.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        NacosHttpLoginClient client = CreateClient(handler);

        NacosLoginResponse? response = await client.LoginAsync(
            Server, "nacos", "wrong", TestContext.Current.CancellationToken);

        response.ShouldBeNull();
    }

    [Fact]
    public async Task Login_ReturnsNullOnUnauthorized()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        NacosHttpLoginClient client = CreateClient(handler);

        NacosLoginResponse? response = await client.LoginAsync(
            Server, "nacos", "wrong", TestContext.Current.CancellationToken);

        response.ShouldBeNull();
    }

    [Fact]
    public async Task Login_ReadsTheBearerTokenFromTheAuthorizationHeader()
    {
        // The legacy Spring Security path behind the v1 endpoint returns the token only as a
        // header, so the header must be honoured when the body carries no token object.
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"code":200,"message":"success","data":"login ok"}"""),
        };

        response.Headers.TryAddWithoutValidation("Authorization", "Bearer header-token");

        var handler = new RecordingHandler(_ => response);
        NacosHttpLoginClient client = CreateClient(handler);

        NacosLoginResponse? login = await client.LoginAsync(
            Server, "nacos", "secret", TestContext.Current.CancellationToken);

        login!.AccessToken.ShouldBe("header-token");

        // The token is usable but its true lifetime is unreported, so a conservative value must
        // be assumed rather than treating it as non-expiring.
        login.TokenTtl.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Login_SendsCredentialsInTheRequestBody()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, V3Body("token-1", 18000)));
        NacosHttpLoginClient client = CreateClient(handler);

        await client.LoginAsync(Server, "user+name", "p@ss word", TestContext.Current.CancellationToken);

        handler.Bodies[0].ShouldContain("username=user%2Bname");
        handler.Bodies[0].ShouldContain("password=p%40ss+word");

        // The user name must not leak into the query string, where it would end up in access logs.
        handler.Requests[0].Query.ShouldBeEmpty();
    }

    [Fact]
    public async Task Login_ThrowsConnectionExceptionWhenTheServerIsUnreachable()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        NacosHttpLoginClient client = CreateClient(handler);

        await Should.ThrowAsync<NacosConnectionException>(
            () => client.LoginAsync(Server, "nacos", "secret", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Login_ThrowsConnectionExceptionOnAnUnexpectedStatus()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        NacosHttpLoginClient client = CreateClient(handler);

        NacosConnectionException ex = await Should.ThrowAsync<NacosConnectionException>(
            () => client.LoginAsync(Server, "nacos", "secret", TestContext.Current.CancellationToken).AsTask());

        ex.Message.ShouldContain("502");
    }

    [Fact]
    public async Task Login_BuildsTheUrlFromTheConfiguredContextPath()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, V3Body("token-1", 18000)));
        NacosHttpLoginClient client = CreateClient(handler);

        // An explicit context path in the server address must be preserved.
        NacosServerAddress custom = NacosServerAddress.Parse("http://10.0.0.5:8848/custom");

        await client.LoginAsync(custom, "nacos", "secret", TestContext.Current.CancellationToken);

        handler.Requests[0].AbsolutePath.ShouldBe("/custom/v3/auth/user/login");
    }

    private static string V3Body(string token, long ttl) => JsonSerializer.Serialize(new
    {
        accessToken = token,
        tokenTtl = ttl,
        globalAdmin = false,
        username = "nacos",
    });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>Determines whether a request targets the v3 login route.</summary>
    /// <param name="request">The outgoing request.</param>
    /// <returns><see langword="true"/> for the v3 endpoint.</returns>
    private static bool IsV3(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.Contains("/v3/", StringComparison.Ordinal);

    private static NacosHttpLoginClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that scripts responses and records every request.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        /// <summary>Gets the request URIs that were requested, in order.</summary>
        public List<Uri> Requests { get; } = [];

        /// <summary>Gets the request bodies that were sent, in order.</summary>
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);

            // SendAsync disposes the request content afterwards, so it is read here.
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return _responder(request);
        }
    }

    /// <summary>An <see cref="HttpMessageHandler"/> that always fails.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(_exception);
    }
}

