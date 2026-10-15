using Lolitacoding.Nacos.AspNetCore;
using Lolitacoding.Nacos.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Lolitacoding.Nacos.Tests.AspNetCore;

/// <summary>
/// Verifies how the address advertised to Nacos is determined.
/// </summary>
public sealed class NacosEndpointResolverTests
{
    [Fact]
    public void Resolve_ExplicitValuesWinOverInference()
    {
        var options = new NacosServiceRegistrationOptions
        {
            Ip = "10.0.0.5",
            Port = 8080,
        };

        (string ip, int port) = NacosEndpointResolver.Resolve(options, ["http://192.168.1.9:5000"]);

        ip.ShouldBe("10.0.0.5");
        port.ShouldBe(8080);
    }

    [Fact]
    public void Resolve_InfersFromTheBoundAddress()
    {
        var options = new NacosServiceRegistrationOptions();

        (string ip, int port) = NacosEndpointResolver.Resolve(options, ["http://127.0.0.1:5000"]);

        ip.ShouldBe("127.0.0.1");
        port.ShouldBe(5000);
    }

    [Fact]
    public void Resolve_ReplacesAWildcardAddressWithARoutableOne()
    {
        // Registering 0.0.0.0 would be useless to every caller, so a concrete address is chosen.
        var options = new NacosServiceRegistrationOptions();

        (string ip, int port) = NacosEndpointResolver.Resolve(options, ["http://0.0.0.0:5000"]);

        ip.ShouldNotBe("0.0.0.0");
        ip.ShouldBe(NacosEndpointResolver.TryFindLocalAddress());
        port.ShouldBe(5000);
    }

    [Fact]
    public void Resolve_ReplacesLocalhostWithARoutableAddress()
    {
        var options = new NacosServiceRegistrationOptions();

        (string ip, int port) = NacosEndpointResolver.Resolve(options, ["http://localhost:5000"]);

        ip.ShouldNotBe("localhost");
        port.ShouldBe(5000);
    }

    [Fact]
    public void Resolve_ThrowsWhenNoPortCanBeDetermined()
    {
        var options = new NacosServiceRegistrationOptions { Ip = "10.0.0.5" };

        Should.Throw<NacosConfigurationException>(() => NacosEndpointResolver.Resolve(options, []));
    }

    [Theory]
    [InlineData("[::1]", "::1")]
    [InlineData("  127.0.0.1  ", "127.0.0.1")]
    public void Normalize_StripsBracketsAndWhitespace(string input, string expected)
    {
        NacosEndpointResolver.Normalize(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("[::]")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("localhost")]
    [InlineData("   ")]
    public void Normalize_RejectsNonAdvertisableHosts(string input)
    {
        NacosEndpointResolver.Normalize(input).ShouldBeNull();
    }

    [Fact]
    public void OptionsValidator_RejectsAnOutOfRangePort()
    {
        var validator = new NacosServiceRegistrationOptionsValidator();

        validator.Validate(null, new NacosServiceRegistrationOptions { Port = 70000 })
            .Failed.ShouldBeTrue();
    }

    [Fact]
    public void OptionsValidator_RejectsANonPositiveWeight()
    {
        var validator = new NacosServiceRegistrationOptionsValidator();

        validator.Validate(null, new NacosServiceRegistrationOptions { Weight = 0 })
            .Failed.ShouldBeTrue();
    }

    [Fact]
    public void OptionsValidator_AcceptsTheDefaults()
    {
        var validator = new NacosServiceRegistrationOptionsValidator();

        validator.Validate(null, new NacosServiceRegistrationOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ResolveServiceName_FallsBackToTheEntryAssembly()
    {
        var options = new NacosServiceRegistrationOptions();

        options.ResolveServiceName().ShouldBe("Lolitacoding.Nacos.Tests");
    }

    [Fact]
    public void AddNacosServiceRegistration_RegistersEverythingItNeeds()
    {
        var services = new ServiceCollection();

        services.AddNacosServiceRegistration(options => options.ServiceName = "sample-api");

        services.ShouldContain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(NacosServiceRegistrationHostedService));

        services.ShouldContain(descriptor =>
            descriptor.ServiceType == typeof(IValidateOptions<NacosServiceRegistrationOptions>));

        // The registration is useless without the client, so the call brings it in.
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(NacosClient));
    }
}
