namespace Lolitacoding.Nacos.Tests;

/// <summary>
/// Verifies the validation of the address server settings on <see cref="NacosOptions"/>.
/// </summary>
/// <remarks>
/// A misconfigured address server would otherwise surface as an unreachable endpoint long after
/// startup, so every setting that feeds the URL is checked when the client is built.
/// </remarks>
public sealed class NacosEndpointOptionsTests
{
    [Fact]
    public void Validate_AcceptsAnEndpointWithoutAnyConfiguredAddress()
    {
        var options = new NacosOptions { Endpoint = "address.example.com" };

        Should.NotThrow(options.Validate);
    }

    [Fact]
    public void Validate_DoesNotCheckAddressesThatTheEndpointReplaces()
    {
        // A stale address left in configuration must not stop a client that will never dial it;
        // the endpoint is the authority, and setting both is only a warning.
        var options = new NacosOptions
        {
            Endpoint = "address.example.com",
            ServerAddresses = ["not a url"],
        };

        Should.NotThrow(options.Validate);
    }

    [Fact]
    public void Validate_RejectsAConfigurationWithNeitherAddressesNorAnEndpoint()
    {
        var options = new NacosOptions();

        NacosConfigurationException exception =
            Should.Throw<NacosConfigurationException>(options.Validate);

        exception.Message.ShouldContain("NacosOptions.Endpoint");
    }

    [Fact]
    public void Validate_RejectsAnEndpointThatLooksLikeAUrl()
    {
        var options = new NacosOptions { Endpoint = "http://address.example.com" };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("host name");
    }

    [Fact]
    public void Validate_RejectsAnEndpointWithAPath()
    {
        var options = new NacosOptions { Endpoint = "address.example.com/nacos" };

        Should.Throw<NacosConfigurationException>(options.Validate);
    }

    [Fact]
    public void Validate_RejectsAnEndpointWithWhitespace()
    {
        var options = new NacosOptions { Endpoint = " address.example.com" };

        Should.Throw<NacosConfigurationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validate_RejectsAnEndpointPortOutOfRange(int port)
    {
        var options = new NacosOptions { Endpoint = "address.example.com", EndpointPort = port };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("EndpointPort");
    }

    [Fact]
    public void Validate_RejectsAnEmptyServerListName()
    {
        var options = new NacosOptions
        {
            Endpoint = "address.example.com",
            EndpointServerListName = "/",
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("EndpointServerListName");
    }

    [Fact]
    public void Validate_TrimsTheServerListName()
    {
        var options = new NacosOptions
        {
            Endpoint = "address.example.com",
            EndpointServerListName = " /serverlist ",
        };

        options.Validate();

        options.EndpointServerListName.ShouldBe("serverlist");
    }

    [Fact]
    public void Validate_RejectsANonPositiveRefreshInterval()
    {
        var options = new NacosOptions
        {
            Endpoint = "address.example.com",
            EndpointRefreshInterval = TimeSpan.Zero,
        };

        Should.Throw<NacosConfigurationException>(options.Validate)
            .Message.ShouldContain("EndpointRefreshInterval");
    }

    [Fact]
    public void Validate_StripsTheQueryParameterSeparator()
    {
        var options = new NacosOptions
        {
            Endpoint = "address.example.com",
            EndpointQueryParams = "?cluster=alpha",
        };

        options.Validate();

        options.EndpointQueryParams.ShouldBe("cluster=alpha");
    }

    [Fact]
    public void Validate_DoesNotTreatTheDefaultedNamespaceAsChosen()
    {
        // The address server is only asked for a namespace the caller picked; defaulting to the
        // public one must not add a parameter the Java client would leave off.
        var options = new NacosOptions { Endpoint = "address.example.com" };

        options.Validate();

        options.Namespace.ShouldBe(NacosConstants.DefaultNamespace);
        options.NamespaceWasConfigured.ShouldBeFalse();
    }

    [Fact]
    public void Namespace_RemembersAnExplicitChoice()
    {
        var options = new NacosOptions { Endpoint = "address.example.com", Namespace = "tenant" };

        options.Validate();

        options.NamespaceWasConfigured.ShouldBeTrue();
    }

    [Fact]
    public void Namespace_ForgetsTheChoiceWhenItIsCleared()
    {
        var options = new NacosOptions { Endpoint = "address.example.com", Namespace = "tenant" };

        options.Namespace = string.Empty;
        options.Validate();

        options.Namespace.ShouldBe(NacosConstants.DefaultNamespace);
        options.NamespaceWasConfigured.ShouldBeFalse();
    }
}
