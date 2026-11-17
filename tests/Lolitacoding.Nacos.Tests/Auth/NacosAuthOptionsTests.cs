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

    [Fact]
    public void Validate_AcceptsOidcWithAnIssuerOnItsOwn()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            Oidc = new NacosOidcOptions
            {
                IssuerUri = "https://idp.example.com/realms/nacos",
                ClientId = "the-client",
                ClientSecret = "the-secret",
            },
        };

        Should.NotThrow(options.Validate);
    }

    [Fact]
    public void Validate_AcceptsOidcWithADirectTokenEndpoint()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            Oidc = new NacosOidcOptions
            {
                TokenEndpoint = "https://idp.example.com/token",
                ClientId = "the-client",
                ClientSecret = "the-secret",
            },
        };

        Should.NotThrow(options.Validate);
    }

    [Fact]
    public void Validate_DefaultsTheOidcScopeToOpenid()
    {
        var oidc = new NacosOidcOptions();

        oidc.Scope.ShouldBe("openid");
    }

    [Fact]
    public void Validate_RejectsOidcWithoutAClientId()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            Oidc = new NacosOidcOptions
            {
                IssuerUri = "https://idp.example.com",
                ClientSecret = "the-secret",
            },
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("ClientId");
    }

    [Fact]
    public void Validate_RejectsOidcWithoutAClientSecret()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            Oidc = new NacosOidcOptions
            {
                IssuerUri = "https://idp.example.com",
                ClientId = "the-client",
            },
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("ClientSecret");
    }

    [Fact]
    public void Validate_RejectsOidcWithoutAnIssuerOrTokenEndpoint()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            Oidc = new NacosOidcOptions
            {
                ClientId = "the-client",
                ClientSecret = "the-secret",
            },
        };

        NacosConfigurationException exception =
            Should.Throw<NacosConfigurationException>(options.Validate);

        exception.Message.ShouldContain("IssuerUri");
        exception.Message.ShouldContain("TokenEndpoint");
    }

    [Fact]
    public void Validate_RejectsARelativeOidcEndpoint()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            Oidc = new NacosOidcOptions
            {
                TokenEndpoint = "/token",
                ClientId = "the-client",
                ClientSecret = "the-secret",
            },
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("absolute");
    }

    [Fact]
    public void Validate_RejectsOidcCombinedWithAToken()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AccessToken = "minted-elsewhere",
            Oidc = ValidOidc(),
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("mutually exclusive");
    }

    [Fact]
    public void Validate_RejectsOidcCombinedWithCredentials()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            UserName = "nacos",
            Password = "nacos",
            Oidc = ValidOidc(),
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("mutually exclusive");
    }

    [Fact]
    public void Validate_RejectsOidcCombinedWithAProvider()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AuthProvider = new FakeAuthProvider("minted-elsewhere"),
            Oidc = ValidOidc(),
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("mutually exclusive");
    }

    [Fact]
    public void Validate_AcceptsRamWithAnAccessKeyAndSecretKey()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            Ram = new NacosRamOptions
            {
                AccessKey = "the-ak",
                SecretKey = "the-sk",
            },
        };

        Should.NotThrow(options.Validate);
    }

    [Fact]
    public void Validate_RejectsRamWithoutAnAccessKey()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            Ram = new NacosRamOptions { SecretKey = "the-sk" },
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("AccessKey");
    }

    [Fact]
    public void Validate_RejectsRamWithoutASecretKey()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            Ram = new NacosRamOptions { AccessKey = "the-ak" },
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("SecretKey");
    }

    [Fact]
    public void Validate_RejectsRamCombinedWithAToken()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AccessToken = "minted-elsewhere",
            Ram = ValidRam(),
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("mutually exclusive");
    }

    [Fact]
    public void Validate_RejectsRamCombinedWithCredentials()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            UserName = "nacos",
            Password = "nacos",
            Ram = ValidRam(),
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("mutually exclusive");
    }

    [Fact]
    public void Validate_RejectsRamCombinedWithAProvider()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            AuthProvider = new FakeAuthProvider("minted-elsewhere"),
            Ram = ValidRam(),
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("mutually exclusive");
    }

    [Fact]
    public void Validate_RejectsRamCombinedWithOidc()
    {
        var options = new NacosOptions
        {
            ServerAddresses = ["127.0.0.1:8848"],
            Oidc = ValidOidc(),
            Ram = ValidRam(),
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("mutually exclusive");
    }

    private static NacosRamOptions ValidRam() => new()
    {
        AccessKey = "the-ak",
        SecretKey = "the-sk",
    };

    private static NacosOidcOptions ValidOidc() => new()
    {
        IssuerUri = "https://idp.example.com",
        ClientId = "the-client",
        ClientSecret = "the-secret",
    };
}
