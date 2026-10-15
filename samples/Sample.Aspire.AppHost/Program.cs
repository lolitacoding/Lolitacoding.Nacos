using Lolitacoding.Aspire.Hosting.Nacos;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// A standalone Nacos server for local development. Its data lives in a named volume so
// configuration survives a restart of the AppHost.
IResourceBuilder<NacosResource> nacos = builder.AddNacos("nacos")
    .WithDataVolume();

// The sample API registers itself with Nacos and pulls its application settings from it. Referencing
// the resource injects the connection string the API reads through AddNacosClient.
builder.AddProject<Projects.Sample_AspNetCore>("sample-api")
    .WithReference(nacos)
    .WaitFor(nacos);

builder.Build().Run();
