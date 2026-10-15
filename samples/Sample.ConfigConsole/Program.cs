using Lolitacoding.Nacos;
using Lolitacoding.Nacos.Extensions.Configuration;
using Lolitacoding.Nacos.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Demonstrates the two ways to consume Nacos configuration from a console application:
// injecting a bound options object, and reading a raw value through the config provider
// with live reload.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

string serverAddress = builder.Configuration["Nacos:ServerAddress"] ?? "http://127.0.0.1:8848";
string dataId = builder.Configuration["Nacos:DataId"] ?? "sample-app.json";

// 1. Register the Nacos clients with the DI container.
builder.Services.AddNacos(options =>
{
    options.ServerAddresses = [serverAddress];
    options.Namespace = "public";
    options.LocalCacheDirectory = Path.Combine(AppContext.BaseDirectory, "nacos-cache");
});

// 2. Layer Nacos configuration on top of the local configuration sources.
//    Values published to Nacos override appsettings.json, and changes trigger
//    IOptionsMonitor/IOptionsSnapshot refresh without restarting the process.
builder.Configuration.AddNacosConfiguration(options =>
{
    options.ServerAddresses = [serverAddress];
    options.Namespace = "public";
    options.DataIds = [dataId];
    // Keep the application usable when the Nacos server is unreachable at startup.
    options.Optional = true;
});

using IHost host = builder.Build();

IConfiguration configuration = host.Services.GetRequiredService<IConfiguration>();
ILogger<Program> logger = host.Services.GetRequiredService<ILogger<Program>>();

logger.LogInformation("Watching Nacos dataId '{DataId}' on {Server}.", dataId, serverAddress);
logger.LogInformation("Current value: {Value}", configuration[dataId] ?? configuration["App:Greeting"] ?? "(none)");

// Reload tokens fire whenever the underlying Nacos configuration changes.
ChangeToken.OnChange(
    configuration.GetReloadToken,
    () => logger.LogInformation("Configuration reloaded. Greeting = {Greeting}", configuration["App:Greeting"]));

await host.RunAsync();
