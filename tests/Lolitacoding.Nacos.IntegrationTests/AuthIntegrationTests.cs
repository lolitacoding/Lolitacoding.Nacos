using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.IntegrationTests;

/// <summary>
/// Verifies authentication against a real, auth-enabled Nacos server.
/// </summary>
/// <remarks>
/// These tests exist because the login contract is the part of the client that cannot be trusted
/// to unit tests alone: which endpoint answers, the exact response shape, and how a wrong password
/// is reported are all properties of the server, not of this client.
/// </remarks>
[Trait(NacosTraits.Category, NacosTraits.Integration)]
public sealed class AuthIntegrationTests : IAsyncLifetime
{
    private NacosContainer _container = null!;

    public async ValueTask InitializeAsync()
    {
        // This class needs the real auth plugin, not the anonymous default.
        _container = await NacosContainer.StartAsync(NacosIntegrationImages.V2Image, authEnabled: true);
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

        using HttpResponseMessage response = await http.PostAsync(
            string.Concat(
                _container.HttpAddress, "/nacos/v1/cs/configs?dataId=", dataId,
                "&group=DEFAULT_GROUP&content=x&type=text"),
            content: null,
            TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeFalse();
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
