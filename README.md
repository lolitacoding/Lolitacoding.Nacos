# Lolitacoding.Nacos

Modern **.NET 10** client SDK for [Nacos](https://nacos.io) 2.x / 3.x — configuration center
and service registration & discovery.

[![CI](https://github.com/lolitacoding/Lolitacoding.Nacos/actions/workflows/ci.yml/badge.svg)](https://github.com/lolitacoding/Lolitacoding.Nacos/actions/workflows/ci.yml)

## Why another Nacos client?

- **Only .NET 10.** No multi-targeting, no legacy `netstandard` shims — C# 14, `System.Text.Json`
  source generation, `Span<T>`, `Channel<T>`, `TimeProvider` and `IAsyncEnumerable` are used directly.
- **Native AOT and trimming friendly.** No reflection-based serialization, no runtime code
  generation; every `IL2xxx`/`IL3xxx` warning is an error in this repository.
- **First-class .NET integration.** `IConfiguration`, dependency injection, `IHostedService`,
  `IHttpClientFactory`, and hot-reload through `IOptionsMonitor<T>`.
- **Nacos 2.x and 3.x.** The transport speaks the Nacos gRPC protocol and the HTTP OpenAPI,
  selecting the correct API version for the server it talks to.

## Project layout

| Project | Purpose |
| --- | --- |
| `Lolitacoding.Nacos` | Core: protocol, transport, Config and Naming clients. |
| `Lolitacoding.Nacos.Extensions.DependencyInjection` | `AddNacos(...)`, options binding and validation, lifetime management. |
| `Lolitacoding.Nacos.Extensions.Configuration` | `AddNacosConfiguration(...)` configuration provider with change tokens. |
| `Lolitacoding.Nacos.AspNetCore` | Automatic instance registration and deregistration for ASP.NET Core apps. |

## Quick start

### Configuration center

```csharp
using Lolitacoding.Nacos;

await using var client = new NacosClient(new NacosOptions
{
    ServerAddresses = ["http://127.0.0.1:8848"],
    Namespace = "public",
});

// Read a value.
string? content = await client.Config.GetConfigAsync("appsettings.json", "DEFAULT_GROUP");

// Publish a value.
await client.Config.PublishConfigAsync("appsettings.json", """{"Logging":{"LogLevel":"Debug"}}""");

// React to changes. The sequence never completes unless the CancellationToken fires.
await foreach (ConfigChange change in client.Config.ListenAsync("appsettings.json", "DEFAULT_GROUP"))
{
    Console.WriteLine($"{change.DataId}/{change.Group}: {change.NewContent}");
}
```

### Service registration and discovery

```csharp
using Lolitacoding.Nacos;

await using var client = new NacosClient(new NacosOptions
{
    ServerAddresses = ["http://127.0.0.1:8848"],
});

await client.Naming.RegisterInstanceAsync(new NacosInstance
{
    ServiceName = "order-service",
    Ip = "10.0.0.5",
    Port = 8080,
});

IReadOnlyList<NacosInstance> instances =
    await client.Naming.GetInstancesAsync("order-service", healthyOnly: true);
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

## Documentation

- [Quick start](docs/quickstart.md)
- [Configuration reference](docs/configuration.md)
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
