# Quick start

This guide takes you from an empty project to a running application that reads configuration from
Nacos, watches it for changes, and registers itself for service discovery.

> **Target framework:** .NET 10 (`net10.0`). The packages do not target older frameworks.

## 1. Install the packages

| Package | Use it for |
| --- | --- |
| `Lolitacoding.Nacos` | Programmatic config and naming clients. No framework dependencies beyond `Microsoft.Extensions.*` abstractions. |
| `Lolitacoding.Nacos.Extensions.DependencyInjection` | `services.AddNacos(...)`, options binding and lifetime management. |
| `Lolitacoding.Nacos.Extensions.Configuration` | `builder.Configuration.AddNacosConfiguration(...)` with hot reload. |
| `Lolitacoding.Nacos.Extensions.ServiceDiscovery` | `AddNacosServiceDiscovery()` for `Microsoft.Extensions.ServiceDiscovery`. |
| `Lolitacoding.Nacos.AspNetCore` | Automatic instance registration and deregistration. |

```bash
dotnet add package Lolitacoding.Nacos
dotnet add package Lolitacoding.Nacos.Extensions.DependencyInjection
dotnet add package Lolitacoding.Nacos.Extensions.Configuration
dotnet add package Lolitacoding.Nacos.Extensions.ServiceDiscovery
dotnet add package Lolitacoding.Nacos.AspNetCore
```

The gRPC endpoint of Nacos defaults to the HTTP port plus 1000 (8848 → 9848). Open both ports when
the server is behind a firewall.

## 2. Read and publish configuration

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

// Construction does not connect. Optional = false (the default) makes ConnectAsync fail fast.
await using NacosClient client = NacosClient.Create(options);
await client.ConnectAsync();

var key = new NacosConfigKey("my-service.json", NacosConstants.DefaultGroup);

NacosConfigResult current = await client.Config.GetConfigAsync(key);
Console.WriteLine(current.Exists ? current.Content : "(not set)");

// Publish or overwrite. The content type is metadata; it drives how a reader interprets the text.
await client.Config.PublishConfigAsync(key, """{"App":{"Greeting":"hello"}}""", contentType: "json");
```

`GetConfigAsync` returns a `NacosConfigResult`; inspect `Content` (may be `null`), `Exists`, `Md5`
and `FromLocalCache`. A missing configuration is not an error.

Set `Optional = true` to tolerate an unreachable server at startup. The client connects in the
background and serves configuration and service lists from its local cache until the server returns.

## 3. React to configuration changes

Both an `IAsyncEnumerable` and a callback style are available.

```csharp
// Streaming style: the sequence never completes unless the token fires. The first item is the
// value currently known, so initialisation uses the same code path as later changes.
await foreach (NacosConfigChange change in client.Config.ListenAsync(key, cancellationToken))
{
    Console.WriteLine($"{change.Key} changed (previous md5: {change.PreviousMd5}):");
    Console.WriteLine(change.Content);
}

// Callback style: dispose the handle to stop listening.
await using IAsyncDisposable handle = await client.Config.AddListenerAsync(
    key,
    (change, ct) =>
    {
        Console.WriteLine($"New value: {change.Content}");
        return Task.CompletedTask;
    },
    cancellationToken);
```

Duplicate notifications are suppressed by comparing the MD5 of the new value with the last one
delivered, so a listener only fires when the value actually changed.

## 4. Register and discover services

```csharp
using Lolitacoding.Nacos.Naming;

var service = new NacosServiceKey("order-service", NacosConstants.DefaultGroup);
var instance = new NacosInstance(
    ipAddress: "10.0.0.5",
    port: 8080,
    weight: 1.0,
    metadata: new Dictionary<string, string> { ["version"] = "1.2.3" });

await client.Naming.RegisterInstanceAsync(service, instance);

NacosServiceInfo info = await client.Naming.GetInstancesAsync(service);
foreach (NacosInstance available in info.HealthyInstances)   // healthy and enabled
{
    Console.WriteLine(available.Endpoint);   // host:port
}

await client.Naming.DeregisterInstanceAsync(service, instance);
```

To follow a service as instances come and go:

```csharp
await foreach (NacosServiceChange change in client.Naming.SubscribeAsync(service))
{
    Console.WriteLine($"{change.Key} now has {change.Instances.Count} instance(s).");
}
```

Optional load-balancing helpers are provided but are not wired into the client:

```csharp
using Lolitacoding.Nacos.Naming;

INacosLoadBalancer balancer = new NacosRoundRobinLoadBalancer();          // equal weights
// INacosLoadBalancer balancer = new NacosWeightedRandomLoadBalancer();   // honours Weight
// INacosLoadBalancer balancer = new NacosClusterAwareLoadBalancer("zone-a");

NacosInstance? chosen = balancer.Select(info.HealthyInstances);
```

## 5. Dependency injection

```csharp
using Lolitacoding.Nacos.Extensions.DependencyInjection;

builder.Services.AddNacos(options =>
{
    options.ServerAddresses = ["http://127.0.0.1:8848"];
    options.Namespace = "public";
    options.AppName = "my-service";
});

// Later, anywhere:
public sealed class MyService(NacosClient nacos)
{
    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        NacosConfigResult result = await nacos.Config.GetConfigAsync(
            new NacosConfigKey("my-service.json", "DEFAULT_GROUP"), ct);

        return result.Content;
    }
}
```

`AddNacos` registers `NacosClient`, `INacosConfigClient` and `INacosNamingClient` as singletons and
adds a hosted service that connects on startup and closes the connection on shutdown. There are
three overloads:

```csharp
services.AddNacos(options => { /* configure in code */ });   // delegate
services.AddNacos(configuration.GetSection("Nacos"));       // bind a section
services.AddNacos();                                        // configured elsewhere
```

### Service discovery with `Microsoft.Extensions.ServiceDiscovery`

`AddNacosServiceDiscovery()` registers a Nacos endpoint provider alongside the providers
`AddServiceDiscovery()` installs. A request URI names the service, and the provider replaces its
host and port with a healthy instance before the request is sent:

```csharp
using Lolitacoding.Nacos.Extensions.ServiceDiscovery;

builder.Services.AddNacos(options => options.ServerAddresses = ["http://127.0.0.1:8848"]);
builder.Services.AddNacosServiceDiscovery();

builder.Services.AddHttpClient("orders", client => client.BaseAddress = new Uri("http+nacos://order-service"))
    .AddServiceDiscovery();

// Later, anywhere:
public sealed class OrderClient(IHttpClientFactory factory)
{
    public Task<HttpResponseMessage> GetAsync(CancellationToken ct) =>
        factory.CreateClient("orders").GetAsync("/orders/1", ct);
}
```

A URI may combine a transport scheme with `nacos` using `+`; the leading scheme decides the
protocol of the request and `nacos` selects the provider. A bare name such as `order-service`
resolves through Nacos as well, which is the default for an application whose services all live in
Nacos. Set `ResolveAllServices = false` to require an explicit scheme:

```csharp
builder.Services.AddNacosServiceDiscovery(options =>
{
    options.GroupName = "DEFAULT_GROUP";   // services carry no group in the URI
    // options.Cluster = "zone-a";         // restrict resolution to one cluster
    // options.ResolveAllServices = false; // only claim names with an explicit scheme
});
```

Resolution reuses the naming client, so it inherits its in-memory and on-disk failover: a service
that was resolved once still resolves after a restart during a control-plane outage, with
`NacosServiceInfo.FromLocalCache` set on the snapshot.

## 6. Use Nacos as an `IConfiguration` source

```csharp
using Lolitacoding.Nacos.Extensions.Configuration;

builder.Configuration.AddNacosConfiguration(options =>
{
    options.ServerAddresses = ["http://127.0.0.1:8848"];
    options.Namespace = "public";
    options.GroupName = "DEFAULT_GROUP";
    options.DataIds = ["shared.json", "my-service.json"];   // later overrides earlier
    options.Optional = true;
});
```

The source is appended, so it overrides sources added before it (for example `appsettings.json`).
JSON, YAML and Java-style `.properties` documents are flattened with the `:` separator, and the raw
document is always available under its own data id:

```csharp
string? nested = builder.Configuration["App:Greeting"];   // from {"App":{"Greeting":"hi"}}
string? raw    = builder.Configuration["my-service.json"]; // the whole document
```

Changes reload the configuration and fire `IOptionsMonitor<T>` and `IOptionsSnapshot<T>` updates
without a restart. See [the configuration reference](configuration.md) for the full option list.

## 7. Automatic registration in ASP.NET Core

```csharp
using Lolitacoding.Nacos.AspNetCore;

builder.Services.AddNacos(options =>
{
    options.ServerAddresses = ["http://127.0.0.1:8848"];
});

builder.Services.AddNacosServiceRegistration(options =>
{
    options.ServiceName = "my-api";
    options.GroupName = "DEFAULT_GROUP";
    // Ip and Port are inferred from the bound server addresses; override to change them.
    // options.Ip = "10.0.0.5";
    // options.Port = 8080;
    // options.HealthCheckPath = "/health";
});
```

The instance is registered after the server has started and bound its addresses, and deregistered
during graceful shutdown. `AddNacosServiceRegistration` also registers the Nacos clients, so a
service that only wants registration does not have to call `AddNacos` separately.

## 8. Native AOT

The core `Lolitacoding.Nacos` package is annotated `IsAotCompatible` and uses `System.Text.Json`
source generation throughout. The `Sample.Aot` project in this repository is the proof:

```bash
dotnet publish samples/Sample.Aot -c Release -r win-x64
```

A Native AOT publish needs the platform C++ toolchain (the "Desktop development with C++" workload
on Windows). Trim and AOT analyzer warnings are errors in this repository, so a clean build is the
gate that runs on every pull request.

## 9. Observability

Traces and metrics are published under the name `Lolitacoding.Nacos`:

```csharp
using Lolitacoding.Nacos.Diagnostics;

NacosDiagnostics.ActivitySourceName;   // "Lolitacoding.Nacos"
NacosDiagnostics.MeterName;            // "Lolitacoding.Nacos"
```

With OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(NacosDiagnostics.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(NacosDiagnostics.MeterName));
```

Spans: `Nacos.Client.Connect`, `Nacos.Client.Request`. Metrics:
`nacos.client.requests`, `nacos.client.request.duration`, `nacos.client.reconnects`,
`nacos.client.pushes`, `nacos.client.connection.state.changes`.

## Next steps

- [Configuration reference](configuration.md) — every option, with defaults.
- [Migration from `nacos-sdk-csharp`](migration-from-nacos-sdk-csharp.md).
- [FAQ](faq.md).
- [Protocol notes](protocol-notes.md).
