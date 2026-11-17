namespace Lolitacoding.Nacos.Tests.Auth;

/// <summary>
/// Verifies the validation of the authentication settings on <see cref="NacosOptions"/>.
/// </summary>
/// <remarks>
/// The three ways of authenticating — a pre-issued token, a caller-supplied provider and a user
/// name with password — cannot be combined: each one names the single source of the token, and a
/// configuration that sets two of them would otherwise have to pick one silently. The check runs at
/// startup so the ambiguity is reported there instead of as a 403 later.
/// </remarks>
public sealed class NacosAuthOptionsTests
{
    [Fact]
    public void Validate_AcceptsAPreIssuedTokenOnItsOwn()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AccessToken = "minted-elsewhere",
        };

        Should.NotThrow(options.Validate);
    }

    [Fact]
    public void Validate_AcceptsACallerSuppliedProviderOnItsOwn()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AuthProvider = new FakeAuthProvider("minted-elsewhere"),
        };

        Should.NotThrow(options.Validate);
    }

    [Fact]
    public void Validate_RejectsATokenCombinedWithCredentials()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AccessToken = "minted-elsewhere",
            UserName = "nacos",
            Password = "nacos",
        };

        NacosConfigurationException exception =
            Should.Throw<NacosConfigurationException>(options.Validate);

        exception.Message.ShouldContain("AccessToken");
        exception.Message.ShouldContain("mutually exclusive");
    }

    [Fact]
    public void Validate_RejectsAProviderCombinedWithAToken()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AuthProvider = new FakeAuthProvider("minted-elsewhere"),
            AccessToken = "another-token",
        };

        NacosConfigurationException exception =
            Should.Throw<NacosConfigurationException>(options.Validate);

        exception.Message.ShouldContain("AuthProvider");
        exception.Message.ShouldContain("mutually exclusive");
    }

    [Fact]
    public void Validate_RejectsAProviderCombinedWithCredentials()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AuthProvider = new FakeAuthProvider("minted-elsewhere"),
            UserName = "nacos",
            Password = "nacos",
        };

        NacosConfigurationException exception =
            Should.Throw<NacosConfigurationException>(options.Validate);

        exception.Message.ShouldContain("AuthProvider");
        exception.Message.ShouldContain("mutually exclusive");
    }
}
