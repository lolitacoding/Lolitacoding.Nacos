using Lolitacoding.Nacos.Extensions.Configuration;
using Microsoft.Extensions.Configuration;

namespace Lolitacoding.Nacos.Tests.Configuration;

/// <summary>
/// Verifies how the Nacos configuration source is attached and validated.
/// </summary>
public sealed class NacosConfigurationExtensionsTests
{
    private const string Server = "http://127.0.0.1:8848";

    [Fact]
    public void AddNacosConfiguration_AddsASource()
    {
        var builder = new ConfigurationBuilder();

        builder.AddNacosConfiguration(options =>
        {
            options.ServerAddresses = [Server];
            options.DataIds = ["app.json"];
        });

        builder.Sources.ShouldHaveSingleItem().ShouldBeOfType<NacosConfigurationSource>();
    }

    [Fact]
    public void AddNacosConfiguration_RequiresADataId()
    {
        var builder = new ConfigurationBuilder();

        Should.Throw<NacosConfigurationException>(() =>
            builder.AddNacosConfiguration(options => options.ServerAddresses = [Server]));
    }

    [Fact]
    public void AddNacosConfiguration_RequiresAServerAddress()
    {
        var builder = new ConfigurationBuilder();

        Should.Throw<NacosConfigurationException>(() =>
            builder.AddNacosConfiguration(options => options.DataIds = ["app.json"]));
    }

    [Fact]
    public void AddNacosConfiguration_RejectsWhitespaceDataIds()
    {
        var builder = new ConfigurationBuilder();

        Should.Throw<NacosConfigurationException>(() =>
            builder.AddNacosConfiguration(options =>
            {
                options.ServerAddresses = [Server];
                options.DataIds = ["app.json", "   "];
            }));
    }

    [Fact]
    public void AddNacosConfiguration_RequiresAGroupName()
    {
        var builder = new ConfigurationBuilder();

        Should.Throw<NacosConfigurationException>(() =>
            builder.AddNacosConfiguration(options =>
            {
                options.ServerAddresses = [Server];
                options.DataIds = ["app.json"];
                options.GroupName = " ";
            }));
    }

    [Fact]
    public void AddNacosConfiguration_BindsFromASection()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nacos:ServerAddresses:0"] = Server,
                ["Nacos:GroupName"] = "CUSTOM_GROUP",
                ["Nacos:DataIds:0"] = "app.json",
                ["Nacos:DataIds:1"] = "override.json",
            })
            .Build();

        var builder = new ConfigurationBuilder();
        builder.AddNacosConfiguration(configuration.GetSection("Nacos"));

        builder.Sources.ShouldHaveSingleItem();
    }

    [Fact]
    public void AddNacosConfiguration_ThrowsWhenTheBoundSectionIsIncomplete()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nacos:ServerAddresses:0"] = Server,
            })
            .Build();

        var builder = new ConfigurationBuilder();

        Should.Throw<NacosConfigurationException>(() =>
            builder.AddNacosConfiguration(configuration.GetSection("Nacos")));
    }
}
