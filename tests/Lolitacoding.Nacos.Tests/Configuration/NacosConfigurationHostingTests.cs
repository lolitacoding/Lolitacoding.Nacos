using Lolitacoding.Nacos.Extensions.Configuration;
using Lolitacoding.Nacos.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lolitacoding.Nacos.Tests.Configuration;

/// <summary>
/// Verifies the host-builder overloads, which are what connect the source's diagnostics to the
/// host's logging.
/// </summary>
public sealed class NacosConfigurationHostingTests
{
    /// <summary>A server address that is guaranteed to refuse a connection.</summary>
    private const string Unreachable = "http://127.0.0.1:1";

    [Fact]
    public void AddNacosConfiguration_DefersTheLoggerFactoryAndRegistersTheBinder()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.AddNacosConfiguration(ConfigureUnreachable);

        NacosConfigurationSource source = FindSource(builder);
        source.Options.LoggerFactory.ShouldBeOfType<NacosDeferredLoggerFactory>();
        builder.Services.ShouldContain(
            descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddNacosConfiguration_KeepsAnExplicitLoggerFactory()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        var explicitFactory = new RecordingLoggerFactory();

        builder.AddNacosConfiguration(options =>
        {
            ConfigureUnreachable(options);
            options.LoggerFactory = explicitFactory;
        });

        FindSource(builder).Options.LoggerFactory.ShouldBeSameAs(explicitFactory);

        // Nothing has to be deferred when the caller already supplied the factory.
        builder.Services.ShouldNotContain(
            descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddNacosConfiguration_FromASection_DefersTheLoggerFactory()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nacos:ServerAddresses:0"] = Unreachable,
            ["Nacos:DataIds:0"] = "app.json",
            ["Nacos:Optional"] = "true",
        });

        builder.AddNacosConfiguration(builder.Configuration.GetSection("Nacos"));

        FindSource(builder).Options.LoggerFactory.ShouldBeOfType<NacosDeferredLoggerFactory>();
    }

    [Fact]
    public async Task AddNacosConfiguration_ReplaysStartupDiagnosticsWhenTheHostStarts()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        var recording = new RecordingLoggerFactory();

        // The recording factory replaces the host's, so the replay is observable without a console.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ILoggerFactory>(recording);

        builder.AddNacosConfiguration(ConfigureUnreachable);

        // The source loads while the host is being built, which is before the binder can run, so
        // nothing has reached the real factory yet.
        recording.Entries.ShouldBeEmpty();

        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        // The initial load summary and the failed subscription are the lines a host needs to see
        // without raising its minimum level to Debug.
        RecordedLogEntry loaded = recording.ForEvent(4103).ShouldHaveSingleItem();
        loaded.Level.ShouldBe(LogLevel.Information);
        loaded.Message.ShouldBe("Loaded 0 configuration key(s) from 1 Nacos data id(s).");

        recording.ForEvent(4102).ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);

        // The client the source created got the same deferred factory, so the transport failure is
        // replayed as well.
        recording.ForEvent(1001).ShouldNotBeEmpty();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static void ConfigureUnreachable(NacosConfigurationSourceOptions options)
    {
        options.ServerAddresses = [Unreachable];
        options.DataIds = ["app.json"];
        options.Optional = true;
    }

    private static NacosConfigurationSource FindSource(HostApplicationBuilder builder) =>
        builder.Configuration.Sources.OfType<NacosConfigurationSource>().ShouldHaveSingleItem();
}
