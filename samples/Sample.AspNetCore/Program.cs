using Lolitacoding.Nacos.AspNetCore;
using Lolitacoding.Nacos.Extensions.Configuration;
using Lolitacoding.Nacos.Extensions.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string serverAddress = builder.Configuration["Nacos:ServerAddress"] ?? "http://127.0.0.1:8848";

// 1. Nacos clients for programmatic config and naming access.
builder.Services.AddNacos(options =>
{
    options.ServerAddresses = [serverAddress];
    options.Namespace = "public";
});

// 2. Pull application settings from Nacos, with hot reload.
builder.Configuration.AddNacosConfiguration(options =>
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
