using Lolitacoding.Aspire.Hosting.Nacos;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// A standalone Nacos server for local development. Its data lives in a named volume so
// configuration survives a restart of the AppHost. Host ports are left to Aspire; the resource
// publishes the actual gRPC port so AddNacosClient can compute the offset.
IResourceBuilder<NacosResource> nacos = builder.AddNacos("nacos")
    .WithImageTag("v3.2.4")
    .WithDataVolume();

// The sample API registers itself with Nacos and pulls its application settings from it. Referencing
// the resource injects the connection string the API reads through AddNacosClient.
builder.AddDotnetProject("sample-api", "../Sample.AspNetCore/Sample.AspNetCore.csproj")
    .WithReference(nacos)
    .WaitFor(nacos);

builder.Build().Run();
