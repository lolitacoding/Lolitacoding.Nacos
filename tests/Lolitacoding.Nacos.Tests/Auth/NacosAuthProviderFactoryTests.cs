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
