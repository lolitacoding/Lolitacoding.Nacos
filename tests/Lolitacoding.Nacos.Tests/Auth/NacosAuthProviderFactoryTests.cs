using Lolitacoding.Nacos.Auth;
using Lolitacoding.Nacos.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lolitacoding.Nacos.Tests.Auth;

/// <summary>
/// Verifies which authentication provider a configuration selects.
/// </summary>
public sealed class NacosAuthProviderFactoryTests
{
    private static readonly NacosServerAddress Server = NacosServerAddress.Parse("127.0.0.1:8848");

    [Fact]
    public void Create_ReturnsAnonymousProviderWhenNoCredentialsAreConfigured()
    {
        // Nacos defaults the client OpenAPI scope to unauthenticated, so an unconfigured client
        // must not pay for a login round trip on startup.
        NacosOptions options = new() { ServerAddresses = [Server.Original] };

        INacosAuthProvider provider = Create(options);

        provider.ShouldBeOfType<AnonymousNacosAuthProvider>();
    }

    [Fact]
    public void Create_ReturnsAnonymousProviderWhenOnlyTheUserNameIsSet()
    {
        // Half a credential pair cannot authenticate, so attempting a login would only produce a
        // confusing failure at request time.
        NacosOptions options = new() { UserName = "nacos", ServerAddresses = [Server.Original] };

        INacosAuthProvider provider = Create(options);

        provider.ShouldBeOfType<AnonymousNacosAuthProvider>();
    }

    [Fact]
    public void Create_ReturnsAnonymousProviderWhenOnlyThePasswordIsSet()
    {
        NacosOptions options = new() { Password = "secret", ServerAddresses = [Server.Original] };

        INacosAuthProvider provider = Create(options);

        provider.ShouldBeOfType<AnonymousNacosAuthProvider>();
    }

    [Fact]
    public void Create_ReturnsTokenProviderWhenBothCredentialsAreSet()
    {
        NacosOptions options = new()
        {
            UserName = "nacos",
            Password = "secret",
            ServerAddresses = [Server.Original],
        };

        INacosAuthProvider provider = Create(options);

        provider.ShouldBeOfType<NacosTokenProvider>();
    }

    [Fact]
    public void Create_ReturnsTheCallersProviderUnchanged()
    {
        // The caller owns the instance: wrapping or replacing it would hide it from the code that
        // intends to refresh and dispose it.
        var provided = new FakeAuthProvider("from-the-caller");
        NacosOptions options = new()
        {
            AuthProvider = provided,
            ServerAddresses = [Server.Original],
        };

        INacosAuthProvider provider = Create(options);

        provider.ShouldBeSameAs(provided);
    }

    [Fact]
    public void Create_ReturnsAStaticProviderForAPreIssuedToken()
    {
        NacosOptions options = new()
        {
            AccessToken = "minted-elsewhere",
            ServerAddresses = [Server.Original],
        };

        INacosAuthProvider provider = Create(options);

        provider.ShouldBeOfType<StaticNacosAuthProvider>();
    }

    [Fact]
    public void Create_DoesNotNeedAServerForAPreIssuedToken()
    {
        // A token handed to the process needs no login round trip, so an empty server list is not
        // a reason to refuse the provider the way it is for a login.
        NacosOptions options = new() { AccessToken = "minted-elsewhere" };

        INacosAuthProvider provider = NacosAuthProviderFactory.Create(
            options,
            [],
            new HttpClient(),
            TimeProvider.System,
            NullLogger.Instance,
            new Random(1234));

        provider.ShouldBeOfType<StaticNacosAuthProvider>();
    }

    [Fact]
    public async Task StaticProvider_KeepsReturningTheTokenAfterInvalidate()
    {
        // A pre-issued token cannot be re-minted here, so a rejection must surface rather than turn
        // into an endless loop presenting nothing.
        using var provider = new StaticNacosAuthProvider("minted-elsewhere");

        provider.Invalidate();
        string? token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe("minted-elsewhere");
    }

    [Fact]
    public void Create_ReturnsAnOidcProviderWhenOidcIsConfigured()
    {
        NacosOptions options = new()
        {
            Oidc = new NacosOidcOptions
            {
                IssuerUri = "https://idp.example.com",
                ClientId = "the-client",
                ClientSecret = "the-secret",
            },
            ServerAddresses = [Server.Original],
        };

        INacosAuthProvider provider = Create(options);

        provider.ShouldBeOfType<NacosOidcAuthProvider>();
    }

    [Fact]
    public void Create_DoesNotNeedAServerForOidc()
    {
        // The token comes from the identity provider, so the Nacos server list plays no part in
        // authentication.
        NacosOptions options = new()
        {
            Oidc = new NacosOidcOptions
            {
                IssuerUri = "https://idp.example.com",
                ClientId = "the-client",
                ClientSecret = "the-secret",
            },
        };

        INacosAuthProvider provider = NacosAuthProviderFactory.Create(
            options,
            [],
            new HttpClient(),
            TimeProvider.System,
            NullLogger.Instance,
            new Random(1234));

        provider.ShouldBeOfType<NacosOidcAuthProvider>();
    }

    [Fact]
    public async Task AnonymousProvider_NeverReturnsAToken()
    {
        string? token = await AnonymousNacosAuthProvider.Instance
            .GetAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBeNull();
    }

    [Fact]
    public void AnonymousProvider_InvalidateAndDisposeAreSafeToCall()
    {
        // Both are called unconditionally by the transport and its disposal path, so the
        // anonymous provider must tolerate them rather than throwing.
        Should.NotThrow(() =>
        {
            AnonymousNacosAuthProvider.Instance.Invalidate();
            AnonymousNacosAuthProvider.Instance.Dispose();
        });
    }

    private static INacosAuthProvider Create(NacosOptions options) =>
        NacosAuthProviderFactory.Create(
            options,
            [Server],
            new HttpClient(),
            TimeProvider.System,
            NullLogger.Instance,
            new Random(1234));
}
