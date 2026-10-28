using Lolitacoding.Aspire.Nacos;
using Lolitacoding.Nacos.AspNetCore;
using Lolitacoding.Nacos.Extensions.Configuration;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Log to the console only; the sample is meant to be run interactively.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();

// Under Aspire the AppHost injects a "nacos" connection string. When the sample is run on its own,
// fall back to a local server so the same code path is exercised either way.
string serverAddress =
    builder.Configuration.GetConnectionString(NacosClientHostApplicationBuilderExtensions.DefaultConnectionName)
    ?? builder.Configuration["Nacos:ServerAddress"]
    ?? "http://127.0.0.1:8848";

builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:nacos"] = serverAddress,
});

// 1. Nacos clients for programmatic config and naming access, bound from the connection string.
builder.AddNacosClient(configure: options =>
{
    options.Namespace = "public";
    // Start even when Nacos is unreachable, so the sample can be run before the server is up.
    options.Optional = true;
});

// 2. Pull application settings from Nacos, with hot reload. The host overload connects the
//    source's diagnostics to the logging configured above, so a change is visible without any
//    extra wiring.
builder.AddNacosConfiguration(options =>
{
    options.ServerAddresses = [serverAddress];
    options.Namespace = "public";
    options.DataIds = ["sample-api.json"];
    options.Optional = true;
});

// 3. Register this process as a Nacos instance for the lifetime of the application.
//    The address is inferred from the bound server addresses unless overridden here.
builder.Services.AddNacosServiceRegistration(options =>
{
    options.ServiceName = "sample-api";
    options.GroupName = "DEFAULT_GROUP";
    // options.Ip = "10.0.0.5";
    // options.Port = 8080;
});

WebApplication app = builder.Build();

app.MapGet("/", (IConfiguration configuration) => new
{
    service = "sample-api",
    greeting = configuration["App:Greeting"] ?? "(unset)",
    source = "nacos",
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

await app.RunAsync();
