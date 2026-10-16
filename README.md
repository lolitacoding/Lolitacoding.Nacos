# Lolitacoding.Nacos

Modern **.NET 10** client SDK for [Nacos](https://nacos.io) 3.x — configuration center,
service registration & discovery, and distributed locking.

[![CI](https://github.com/lolitacoding/Lolitacoding.Nacos/actions/workflows/ci.yml/badge.svg)](https://github.com/lolitacoding/Lolitacoding.Nacos/actions/workflows/ci.yml)

## Why another Nacos client?

- **Only .NET 10.** No multi-targeting, no legacy `netstandard` shims — C# 14, `System.Text.Json`
  source generation, `Span<T>`, `Channel<T>`, `TimeProvider` and `IAsyncEnumerable` are used directly.
- **Native AOT and trimming friendly.** No reflection-based serialization, no runtime code
  generation; every `IL2xxx`/`IL3xxx` warning is an error in this repository.
- **First-class .NET integration.** `IConfiguration`, dependency injection, `IHostedService`,
  `IHttpClientFactory`, and hot-reload through `IOptionsMonitor<T>`.
- **Nacos 3.x.** The transport speaks the Nacos gRPC protocol and the v3 HTTP OpenAPI for
  authentication. Encryption in transit is available by pointing `ServerAddresses` at `https://`.

## Project layout

| Project | Purpose |
| --- | --- |
| `Lolitacoding.Nacos` | Core: protocol, transport, Config, Naming and Lock clients. |
| `Lolitacoding.Nacos.Extensions.DependencyInjection` | `AddNacos(...)`, options binding and validation, lifetime management. |
| `Lolitacoding.Nacos.Extensions.Configuration` | `AddNacosConfiguration(...)` configuration provider with change tokens. |
| `Lolitacoding.Nacos.Extensions.ServiceDiscovery` | `AddNacosServiceDiscovery()` integration for `Microsoft.Extensions.ServiceDiscovery`. |
| `Lolitacoding.Nacos.AspNetCore` | Automatic instance registration and deregistration for ASP.NET Core apps. |
| `Lolitacoding.Aspire.Hosting.Nacos` | Aspire hosting integration: orchestrate a Nacos server container from an AppHost. |
| `Lolitacoding.Aspire.Nacos` | Aspire client integration: `AddNacosClient(...)` binds the Nacos clients to the injected connection string. |

## Quick start

### Configuration center

```csharp
using Lolitacoding.Nacos;
using Lolitacoding.Nacos.Client;
using Lolitacoding.Nacos.Config;

var options = new NacosOptions
{
    ServerAddresses = ["http://127.0.0.1:8848"],
    Namespace = NacosConstants.DefaultNamespace,   // "public"
    AppName = "my-service",
};

// Construction does not connect; call ConnectAsync before issuing requests.
await using NacosClient client = NacosClient.Create(options);
await client.ConnectAsync();

var key = new NacosConfigKey("appsettings.json", NacosConstants.DefaultGroup);

// Read a value. A missing configuration is not an error: check result.Exists.
NacosConfigResult result = await client.Config.GetConfigAsync(key);
Console.WriteLine(result.Exists ? result.Content : "(not set)");

// Publish a value.
await client.Config.PublishConfigAsync(
    key, """{"Logging":{"LogLevel":"Debug"}}""", contentType: "json");

// React to changes. The sequence never completes unless the CancellationToken fires.
await foreach (NacosConfigChange change in client.Config.ListenAsync(key))
{
    Console.WriteLine($"{change.Key} (previous md5 {change.PreviousMd5}): {change.Content}");
}
```

### Service registration and discovery

```csharp
using Lolitacoding.Nacos.Naming;

var service = new NacosServiceKey("order-service", NacosConstants.DefaultGroup);

await client.Naming.RegisterInstanceAsync(
    service,
    new NacosInstance(ipAddress: "10.0.0.5", port: 8080, weight: 1.0));

NacosServiceInfo info = await client.Naming.GetInstancesAsync(service);
IReadOnlyList<NacosInstance> instances = info.HealthyInstances;
```

### Dependency injection

```csharp
builder.Services.AddNacos(options =>
{
    options.ServerAddresses = ["http://127.0.0.1:8848"];
    options.Namespace = "public";
});
```

### Configuration provider with hot reload

```csharp
builder.Configuration.AddNacosConfiguration(options =>
{
    options.ServerAddresses = ["http://127.0.0.1:8848"];
    options.DataIds = ["appsettings.json"];
    options.Optional = true;
});
```

### Service discovery for `HttpClient`

```csharp
using Lolitacoding.Nacos.Extensions.ServiceDiscovery;

builder.Services.AddNacos(options => options.ServerAddresses = ["http://127.0.0.1:8848"]);
builder.Services.AddNacosServiceDiscovery();

builder.Services.AddHttpClient("orders", client => client.BaseAddress = new Uri("http+nacos://order-service"))
    .AddServiceDiscovery();
```

The `+nacos` suffix selects the Nacos provider while `http` remains the protocol, and the resolved
instance address is substituted before the request is sent. A bare `order-service` URI resolves
through Nacos too; see the [quick start](docs/quickstart.md) and
[configuration reference](docs/configuration.md) for the options that scope resolution.

### Aspire orchestration

Run a Nacos server for local development from an [Aspire](https://learn.microsoft.com/dotnet/aspire/)
AppHost:

```csharp
// Sample.Aspire.AppHost/Program.cs
using Lolitacoding.Aspire.Hosting.Nacos;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<NacosResource> nacos = builder.AddNacos("nacos")
    .WithDataVolume();

builder.AddProject<Projects.Sample_AspNetCore>("sample-api")
    .WithReference(nacos)
    .WaitFor(nacos);

builder.Build().Run();
```

The referenced service consumes the injected connection string through the matching client helper:

```csharp
using Lolitacoding.Aspire.Nacos;

builder.AddNacosClient(options => options.Optional = true);
```

`AddNacosClient` throws at startup when the connection string is missing, so a misconfigured
reference fails fast. See the [Aspire guide](docs/aspire.md) for the options `AddNacos` exposes.

### Distributed locking

```csharp
using Lolitacoding.Nacos.Locking;

var key = new NacosLockKey("nightly-reindex");

NacosLockResult acquired = await client.Lock.AcquireAsync(key, new NacosLockAcquireOptions
{
    WaitTime = TimeSpan.FromSeconds(30),
    LeaseDuration = TimeSpan.FromSeconds(15),
});

if (acquired.Success)
{
    try
    {
        // ... the work this lock protects ...
    }
    finally
    {
        await client.Lock.ReleaseAsync(key);
    }
}
```

The lock is a lease the server grants to an owner: it survives a caller that dies by expiring, and
`RenewAsync` extends it for work that outlives the lease. The lock connection is opened on first
use. See the [quick start](docs/quickstart.md#10-take-a-distributed-lock) for the details.

### Observability

```csharp
using Lolitacoding.Nacos.Diagnostics;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(NacosDiagnostics.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(NacosDiagnostics.MeterName));
```

## Documentation

- [Quick start](docs/quickstart.md)
- [Configuration reference](docs/configuration.md)
- [Aspire orchestration](docs/aspire.md)
- [Migration from `nacos-sdk-csharp`](docs/migration-from-nacos-sdk-csharp.md)
- [FAQ](docs/faq.md)
- [Protocol notes](docs/protocol-notes.md) — upstream references for every protocol detail.

## Building

```bash
dotnet build -c Release
dotnet test tests/Lolitacoding.Nacos.Tests
```

Integration tests need Docker and run a real Nacos container:

```bash
docker compose -f tests/docker-compose.yml up -d
dotnet test tests/Lolitacoding.Nacos.IntegrationTests
```

## License

[Apache-2.0](LICENSE). The gRPC protocol definition in
`src/Lolitacoding.Nacos/Protocol/Proto/nacos_grpc_service.proto` is a verbatim copy of the
upstream file from [alibaba/nacos](https://github.com/alibaba/nacos), also Apache-2.0.
