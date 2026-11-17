using System.Net;
using System.Text;
using Lolitacoding.Nacos.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Lolitacoding.Nacos.Tests.Auth;

/// <summary>
/// Verifies the OpenID Connect provider's HTTP contract and its token lifetime handling.
/// </summary>
/// <remarks>
/// The parts worth pinning down are the ones a real identity provider reads literally: the
/// discovery path, the form the client-credentials grant posts, and the way a token's lifetime
/// decides when it is replaced. The suppression after a rejected token is also checked, because a
/// provider that kept re-requesting on every rejected request would be a login storm rather than a
/// recovery.
/// </remarks>
public sealed class NacosOidcAuthProviderTests
{
    private const string Issuer = "https://idp.example.com/realms/nacos";
    private const string TokenEndpoint = "https://idp.example.com/realms/nacos/token";

    [Fact]
    public async Task GetAccessTokenAsync_DiscoversTheEndpointThenRequestsAClientCredentialsToken()
    {
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/realms/nacos/.well-known/openid-configuration" =>
                Json(HttpStatusCode.OK, $$"""{"token_endpoint":"{{TokenEndpoint}}"}"""),
            "/realms/nacos/token" =>
                Json(HttpStatusCode.OK, """{"access_token":"oidc-token","expires_in":300}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        using NacosOidcAuthProvider provider = CreateProvider(
            handler, new FakeTimeProvider(), CreateDiscoveryOptions());

        string? token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("oidc-token");

        // Discovery is a GET; the token request is a form post with the grant parameters.
        handler.Requests[0].Method.ShouldBe(HttpMethod.Get);
        handler.Requests[1].Method.ShouldBe(HttpMethod.Post);
        handler.Requests[1].Uri.AbsoluteUri.ShouldBe(TokenEndpoint);

        string body = handler.Bodies[1];
        body.ShouldContain("grant_type=client_credentials");
        body.ShouldContain("client_id=the-client");
        body.ShouldContain("client_secret=the-secret");
        body.ShouldContain("scope=openid");
    }

    [Fact]
    public async Task GetAccessTokenAsync_UsesTheConfiguredEndpointWithoutDiscovery()
    {
        // A deployment that sets the endpoint directly must not pay for a discovery round trip.
        var handler = new ScriptedHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"oidc-token","expires_in":300}"""));

        NacosOidcOptions options = CreateOptions();
        options.IssuerUri = null;
        options.TokenEndpoint = TokenEndpoint;

        using NacosOidcAuthProvider provider = CreateProvider(handler, new FakeTimeProvider(), options);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Uri.AbsoluteUri.ShouldBe(TokenEndpoint);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReusesTheTokenUntilTheRefreshWindow()
    {
        var clock = new FakeTimeProvider();
        var handler = new ScriptedHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"token-1","expires_in":300}"""));

        using NacosOidcAuthProvider provider = CreateProvider(handler, clock);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        // The token is usable for ttl - [ttl/15, ttl/10], so at 100s it is still fresh.
        clock.Advance(TimeSpan.FromSeconds(100));

        string? token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("token-1");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshesOnceTheLifetimeIsSpent()
    {
        var clock = new FakeTimeProvider();
        var handler = new ScriptedHandler(_ =>
            Json(HttpStatusCode.OK, $$"""{"access_token":"token-{{Guid.NewGuid():N}}","expires_in":300}"""));

        using NacosOidcAuthProvider provider = CreateProvider(handler, clock);

        string? first = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(300));

        string? second = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        second.ShouldNotBe(first);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_KeepsTheRejectedTokenWhileARefreshIsSuppressed()
    {
        // The provider may have rejected a token it issued moments ago. Requesting another one
        // immediately would spin, so the rejected value is presented until the window passes.
        var clock = new FakeTimeProvider();
        int calls = 0;
        var handler = new ScriptedHandler(_ =>
        {
            calls++;
            return Json(HttpStatusCode.OK, $$"""{"access_token":"token-{{calls}}","expires_in":300}""");
        });

        using NacosOidcAuthProvider provider = CreateProvider(handler, clock);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        provider.Invalidate();

        string? suppressed = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        suppressed.ShouldBe("token-1");
        handler.Requests.Count.ShouldBe(1);

        // After the suppression window a rejection does buy a fresh token.
        clock.Advance(TimeSpan.FromSeconds(61));
        provider.Invalidate();

        string? refreshed = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        refreshed.ShouldBe("token-2");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ThrowsWhenTheEndpointRejectsTheClientCredentials()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        NacosOidcOptions options = CreateOptions();
        options.IssuerUri = null;
        options.TokenEndpoint = TokenEndpoint;

        using NacosOidcAuthProvider provider = CreateProvider(handler, new FakeTimeProvider(), options);

        NacosAuthException exception = await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());

        exception.Message.ShouldContain("rejected the client credentials");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ThrowsWhenDiscoveryNamesNoTokenEndpoint()
    {
        var handler = new ScriptedHandler(_ =>
            Json(HttpStatusCode.OK, """{"issuer":"https://idp.example.com"}"""));

        using NacosOidcAuthProvider provider = CreateProvider(
            handler, new FakeTimeProvider(), CreateDiscoveryOptions());

        NacosAuthException exception = await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());

        exception.Message.ShouldContain("token_endpoint");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ThrowsWhenTheResponseHasNoToken()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"expires_in":300}"""));

        NacosOidcOptions options = CreateOptions();
        options.IssuerUri = null;
        options.TokenEndpoint = TokenEndpoint;

        using NacosOidcAuthProvider provider = CreateProvider(handler, new FakeTimeProvider(), options);

        NacosAuthException exception = await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());

        exception.Message.ShouldContain("access_token");
    }

    [Fact]
    public async Task GetAccessTokenAsync_ThrowsWhenTheLifetimeIsNotUsable()
    {
        // Caching a token whose lifetime is unknown or zero would either serve an expired token or
        // refetch on every request, so it is reported instead.
        var handler = new ScriptedHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"token-1","expires_in":0}"""));

        NacosOidcOptions options = CreateOptions();
        options.IssuerUri = null;
        options.TokenEndpoint = TokenEndpoint;

        using NacosOidcAuthProvider provider = CreateProvider(handler, new FakeTimeProvider(), options);

        await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task GetAccessTokenAsync_AcceptsAStringLifetime()
    {
        // Identity providers disagree on whether expires_in is a number or a string.
        var clock = new FakeTimeProvider();
        var handler = new ScriptedHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"token-1","expires_in":"600"}"""));

        NacosOidcOptions options = CreateOptions();
        options.IssuerUri = null;
        options.TokenEndpoint = TokenEndpoint;

        using NacosOidcAuthProvider provider = CreateProvider(handler, clock, options);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(500));

        string? token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("token-1");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_DefaultsToFiveMinutesWhenTheLifetimeIsMissing()
    {
        var clock = new FakeTimeProvider();
        var handler = new ScriptedHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"token-1"}"""));

        NacosOidcOptions options = CreateOptions();
        options.IssuerUri = null;
        options.TokenEndpoint = TokenEndpoint;

        using NacosOidcAuthProvider provider = CreateProvider(handler, clock, options);

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        // 300s default minus at most 30s of refresh window leaves the token usable at 200s.
        clock.Advance(TimeSpan.FromSeconds(200));

        string? token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("token-1");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReportsAnUnreachableEndpointAsAnAuthFailure()
    {
        var handler = new ScriptedHandler(_ =>
            throw new HttpRequestException("connection refused"));

        NacosOidcOptions options = CreateOptions();
        options.IssuerUri = null;
        options.TokenEndpoint = TokenEndpoint;

        using NacosOidcAuthProvider provider = CreateProvider(handler, new FakeTimeProvider(), options);

        NacosAuthException exception = await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());

        exception.Message.ShouldContain(TokenEndpoint);
        exception.InnerException.ShouldBeOfType<HttpRequestException>();
    }

    private static NacosOidcOptions CreateOptions() => new()
    {
        TokenEndpoint = TokenEndpoint,
        ClientId = "the-client",
        ClientSecret = "the-secret",
    };

    private static NacosOidcOptions CreateDiscoveryOptions() => new()
    {
        IssuerUri = Issuer,
        ClientId = "the-client",
        ClientSecret = "the-secret",
    };

    private static NacosOidcAuthProvider CreateProvider(
        HttpMessageHandler handler,
        TimeProvider clock,
        NacosOidcOptions? options = null) =>
        new(
            options ?? CreateOptions(),
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) },
            clock,
            NullLogger.Instance,
            new Random(1234));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that scripts responses and records every request.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        /// <summary>Gets the requests that were sent, in order.</summary>
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

        /// <summary>Gets the request bodies that were sent, in order.</summary>
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!));

            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return _responder(request);
        }
    }
}
