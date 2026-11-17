using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Config;
using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies authentication against a real, auth-enabled Nacos server.
/// </summary>
/// <remarks>
/// These tests exist because the login contract is the part of the client that cannot be trusted
/// to unit tests alone: the endpoint the server answers on, the exact response shape, and how a
/// wrong password is reported are all properties of the server, not of this client.
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AuthIntegrationTests : IAsyncLifetime
{
    private NacosContainer _container = null!;

    public async ValueTask InitializeAsync()
    {
        // This class needs the real auth plugin, not the anonymous default.
        _container = await NacosContainer.StartAsync(NacosIntegrationImages.V3Image, authEnabled: true);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task Login_WithValidCredentials_ObtainsAToken()
    {
        HttpClient http = new();
        using (http)
        {
            string? token = await NacosHttp.TryLoginAsync(
                http,
                _container.HttpAddress,
                NacosIntegrationImages.DefaultUserName,
                NacosIntegrationImages.DefaultPassword,
                TestContext.Current.CancellationToken);

            // A missing token here means either the login endpoint moved or the response shape
            // changed; both are breaking changes this client must notice.
            token.ShouldNotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task TokenProvider_AuthenticatesAndCachesTheToken()
    {
        using var http = new HttpClient();
        NacosOptions options = CreateOptions();

        using NacosTokenProvider provider = CreateProvider(http, options);

        string? first = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        string? second = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        first.ShouldNotBeNullOrEmpty();
        second.ShouldBe(first);
    }

    [Fact]
    public async Task TokenProvider_ThrowsAuthExceptionForAWrongPassword()
    {
        // PLAN.md calls this out explicitly: a wrong password must surface as NacosAuthException,
        // not as a generic failure or a silent anonymous request.
        using var http = new HttpClient();
        NacosOptions options = CreateOptions();
        options.Password = "definitely-not-the-password";

        using NacosTokenProvider provider = CreateProvider(http, options);

        await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task TokenProvider_ThrowsAuthExceptionForAnUnknownUser()
    {
        using var http = new HttpClient();
        NacosOptions options = CreateOptions();
        options.UserName = "no-such-user-" + Guid.NewGuid().ToString("N")[..8];

        using NacosTokenProvider provider = CreateProvider(http, options);

        await Should.ThrowAsync<NacosAuthException>(
            () => provider.GetAccessTokenAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task AuthenticatedRequest_IsAcceptedWithAToken()
    {
        // End to end: obtain a token through the client, then use the OpenAPI with it. A token
        // that the login call returns but the API rejects would be a real bug that a login-only
        // assertion could not catch.
        using var http = new HttpClient();

        string? token = await NacosHttp.TryLoginAsync(
            http,
            _container.HttpAddress,
            NacosIntegrationImages.DefaultUserName,
            NacosIntegrationImages.DefaultPassword,
            TestContext.Current.CancellationToken);

        token.ShouldNotBeNullOrEmpty();

        string dataId = NacosHttp.UniqueId("auth-it");
        string content = NacosHttp.ConfigContent("authenticated");

        await NacosHttp.PublishConfigAsync(
            http, _container.HttpAddress, dataId, "DEFAULT_GROUP", content, token,
            TestContext.Current.CancellationToken);

        string? read = await NacosHttp.GetConfigAsync(
            http, _container.HttpAddress, dataId, "DEFAULT_GROUP", token,
            TestContext.Current.CancellationToken);

        read.ShouldBe(content);
    }

    [Fact]
    public async Task UnauthenticatedRequest_IsRejectedWhenAuthIsEnabled()
    {
        // The control for the test above: without this, the authenticated assertions would pass
        // even if the server had authentication switched off entirely.
        using var http = new HttpClient();

        string dataId = NacosHttp.UniqueId("noauth-it");

        await Should.ThrowAsync<HttpRequestException>(() => NacosHttp.PublishConfigAsync(
            http,
            _container.HttpAddress,
            dataId,
            "DEFAULT_GROUP",
            "x",
            accessToken: null,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Client_WorksWithAPreIssuedTokenAndNoCredentials()
    {
        // The whole point of NacosOptions.AccessToken: a process that was handed a token by
        // something else connects and talks to a secured server without ever logging in. The token
        // is obtained over HTTP here only because a test has no sidecar to mint one.
        using var http = new HttpClient();

        string? token = await NacosHttp.TryLoginAsync(
            http,
            _container.HttpAddress,
            NacosIntegrationImages.DefaultUserName,
            NacosIntegrationImages.DefaultPassword,
            TestContext.Current.CancellationToken);

        token.ShouldNotBeNullOrEmpty();

        var options = new NacosOptions
        {
            ServerAddresses = [_container.HttpAddress],
            AccessToken = token,
            ConnectionTimeout = TimeSpan.FromSeconds(15),
            RequestTimeout = TimeSpan.FromSeconds(10),
        };

        await using NacosClientFactory client = NacosClientFactory.Create(
            options,
            httpClient: null,
            channelFactory: null,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance);

        await client.ConnectAsync(TestContext.Current.CancellationToken);

        var key = new NacosConfigKey(NacosHttp.UniqueId("preissued-token-it"), "DEFAULT_GROUP");
        string content = NacosHttp.ConfigContent("pre-issued");

        await client.Config.PublishConfigAsync(
            key, content, "text", ct: TestContext.Current.CancellationToken);

        NacosConfigResult value = await NacosEventually.BecomesAsync(
            () => client.Config.GetConfigAsync(key, TestContext.Current.CancellationToken),
            result => result.Exists,
            TestContext.Current.CancellationToken);

        value.Content.ShouldBe(content);
    }

    [Fact]
    public async Task Client_RejectsAPreIssuedTokenTheServerDoesNotAccept()
    {
        // A token that was not minted by this server must surface as a rejection rather than be
        // retried forever, because the client cannot refresh a token it did not obtain. The
        // handshake may be accepted and the first real request refused, so both are inside the
        // assertion.
        var options = new NacosOptions
        {
            ServerAddresses = [_container.HttpAddress],
            AccessToken = "not-a-token-this-server-issued",
            ConnectionTimeout = TimeSpan.FromSeconds(15),
            RequestTimeout = TimeSpan.FromSeconds(10),
        };

        await using NacosClientFactory client = NacosClientFactory.Create(
            options,
            httpClient: null,
            channelFactory: null,
            timeProvider: TimeProvider.System,
            loggerFactory: NullLoggerFactory.Instance);

        await Should.ThrowAsync<NacosServerException>(async () =>
        {
            await client.ConnectAsync(TestContext.Current.CancellationToken);

            await client.Config.PublishConfigAsync(
                new NacosConfigKey(NacosHttp.UniqueId("preissued-bad-token-it"), "DEFAULT_GROUP"),
                NacosHttp.ConfigContent("rejected"),
                "text",
                ct: TestContext.Current.CancellationToken);
        });
    }

    private NacosOptions CreateOptions() => new()
    {
        ServerAddresses = [_container.HttpAddress],
        UserName = NacosIntegrationImages.DefaultUserName,
        Password = NacosIntegrationImages.DefaultPassword,
        RequestTimeout = TimeSpan.FromSeconds(10),
    };

    private static NacosTokenProvider CreateProvider(HttpClient http, NacosOptions options)
    {
        IReadOnlyList<NacosServerAddress> servers =
            NacosServerAddress.ParseAll(options.ServerAddresses);

        return new NacosTokenProvider(
            options,
            servers,
            new NacosHttpLoginClient(http),
            TimeProvider.System,
            NullLogger.Instance);
    }
}
