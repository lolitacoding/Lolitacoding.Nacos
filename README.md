# Lolitacoding.Nacos

Modern **.NET 10** client SDK for [Nacos](https://nacos.io) 3.x — configuration center,
service registration & discovery, distributed locking, and the AI registry.

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
| `Lolitacoding.Nacos` | Core: protocol, transport, Config, Naming, Lock and AI clients. |
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

`AddNacos` registers `NacosClient` and each feature client — `INacosConfigClient`, `INacosNamingClient`,
`INacosLockClient`, `INacosAiClient` — as singletons that resolve to the instance the shared client
holds, so a second registration never opens a second connection.

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

NacosLock nightlyLock = client.Lock.GetLock(
    new NacosLockKey("nightly-reindex"),
    new NacosLockOptions { LeaseDuration = TimeSpan.FromSeconds(15) });

await using NacosLockLease lease = await nightlyLock.AcquireAsync(cancellationToken);

// ... the work this lock protects ...

if (!lease.IsHeld)
{
    // The lease was lost while the work ran, so its effect may not have been exclusive.
}
```

`GetLock` returns a lock that waits for itself: the lease is renewed while it is held, released when
the lease is disposed, and a nested call from the same asynchronous flow re-enters instead of waiting
for itself. A lock type that forbids re-entrancy is refused with `LockRecursionException` rather than
deadlocking.

The lock is a lease the server grants to an owner, so it survives a caller that dies — usually
released at once, because the connection goes with the process. There is no fencing token, so a
critical section that must not outlive its lock checks `lease.IsHeld` before committing. The lock
connection is opened on first use, and `client.Lock` also exposes the individual operations for a
caller that needs its own owner. See
[quick start §10](docs/quickstart.md#10-take-a-distributed-lock) for both forms.

The lock service this speaks to is the one Nacos **3.3** introduced. A 3.2 server's lock module only
acquires and releases, so renewals and queued waiting need a 3.3 server;
[the FAQ](docs/faq.md#which-nacos-versions-are-supported) says what happens without one.

### AI registry

```csharp
using Lolitacoding.Nacos.Ai;

// What the server currently serves, with the tools, resources and endpoints it resolved.
NacosMcpServerDetail server = await client.Ai.Mcp.GetServerAsync("weather", cancellationToken);

foreach (NacosMcpTool tool in server.ToolSpec?.Tools ?? [])
{
    Console.WriteLine(tool.Name);
}

// Nacos has no MCP change push, so a subscription re-reads the server on the configured interval
// and yields the detail again whenever it changed.
await foreach (NacosMcpServerDetail change in
    client.Ai.Mcp.SubscribeServerAsync("weather", ct: cancellationToken))
{
    Console.WriteLine(change.VersionDetail?.Version);
}

// A prompt read resolves the version the server currently points at — a version, a label, or latest —
// and a subscription is the same poll, with a null element reporting that the prompt is gone.
await foreach (NacosPrompt? prompt in
    client.Ai.Prompt.SubscribePromptAsync("greeting", label: "stable", ct: cancellationToken))
{
    Console.WriteLine(prompt?.Template);
}

// A skill is a directory served as a ZIP archive, so the read hands back the archive together with
// the version it resolved to and the hash of the published content.
NacosSkill skill = await client.Ai.Skill.GetSkillAsync("release-notes", ct: cancellationToken);

Console.WriteLine($"{skill.Version} ({skill.Md5}), {skill.Archive.Length} bytes");

// An agent specification is a package — a manifest plus the files the worker loads — served as one
// JSON object, with the version and the hash in the response headers rather than in the body.
NacosAgentSpec spec =
    await client.Ai.AgentSpec.GetAgentSpecAsync("support-worker", ct: cancellationToken);

Console.WriteLine($"{spec.Version} ({spec.Md5}), {spec.Resource?.Count} resource(s)");
Console.WriteLine(spec.Content);   // the manifest, exactly as the registry stored it

// An agent card is how an A2A agent advertises itself; reading one opens the AI connection, and an
// endpoint registered against a service-backed card is what its address is assembled from.
NacosAgentCardDetail card =
    await client.Ai.AgentCard.GetAgentCardAsync("support-agent", ct: cancellationToken);

Console.WriteLine($"{card.Name} {card.Version} -> {card.Url}");

// A RAD agent can be followed instead of polled. Nacos 3.3 pushes a hint when the answer may have
// changed, the client reads it again and reports only what the read found, and a null element says
// the agent stopped being served.
await foreach (NacosAgentDiscoveryResult? agent in client.Ai.Agent.WatchAgentAsync(
    new NacosAgentReference { AgentName = "support-agent" }, ct: cancellationToken))
{
    Console.WriteLine(agent?.Version);
}
```

`client.Ai` reaches the Nacos AI registry. This release covers the MCP server registry —
`GetServerAsync` reads one server, by name and optionally by version, `ReleaseServerAsync` publishes
one version of one with its tool list, `SubscribeServerAsync` follows one and reports the changes, and
`RegisterServerEndpointAsync` / `DeregisterServerEndpointAsync` publish the address a server is
reachable at — the prompt registry, where `GetPromptAsync` reads a prompt by version or by label and
`SubscribePromptAsync` follows one, the skill registry, where `GetSkillAsync` reads a skill's archive
`SubscribeSkillAsync` follows one, the agent-specification registry, where `GetAgentSpecAsync` reads
one version of a package and `SubscribeAgentSpecAsync` follows one, the A2A agent card registry, where
`GetAgentCardAsync` reads a card, `ReleaseAgentCardAsync` publishes one, `RegisterAgentEndpointAsync`
makes a service-backed agent callable, and `SubscribeAgentCardAsync` follows one, and the RAD agent
registry that Nacos 3.3 adds, where `PublishAgentAsync` publishes a version, `SearchAgentsAsync`
browses, `DiscoverAgentAsync` resolves where to call an agent, `RegisterEndpointsAsync` publishes
the addresses it runs at, and `WatchAgentAsync` follows one without polling — the server pushes a
hint when the answer may have changed rather than the answer itself. The MCP servers are the part the
transport and the models were built for first.

The models are the MCP protocol's own objects rather than a projection of them — a tool's JSON
Schema stays JSON, and MCP's `_meta` member keeps its underscore — because Nacos stores what it is
given and serves it back to MCP clients unchanged. A read is a direct query, so it reports the
server's current state and needs the server to be reachable; a subscription is the one operation with
a local copy, and an endpoint is a Naming instance rather than an AI record, so it is discovered and
health checked like any other instance. A prompt read is always the server's answer, including the
version a label resolves to, while a prompt subscription re-reads on the interval and therefore has
the same local copy an MCP subscription has. A skill and an agent specification are the two resources
read over HTTP rather than over the AI connection, because the server exposes them as OpenAPI
endpoints; both subscriptions are the same kind of poll, and both report a resource that stops being
served the way a prompt is. An agent specification is the one resource whose selector resolves a label
*before* a version, and whose version and hash are reported in the response headers rather than in the
payload — so a caller always learns which version it actually received. The RAD agent watch is the
one AI surface the server pushes to: a hint says the answer may have changed rather than what it
changed to, so the client reads again and reports nothing when the read found the state it already
had, and it re-reads after a reconnect rather than assuming its copy survived the outage.

The AI registry is served from Nacos **3.0.3** on, the agent card registry from **3.1.0** on, the
prompt, skill and agent-specification registries from **3.2.0** on, and the RAD agent registry and
its watch from **3.3** on;
[the FAQ](docs/faq.md#which-nacos-versions-are-supported)
says which server each module needs. The AI connection is opened on first use, like the lock
connection, and carries the `ai` module label plus the per-client identifier Nacos records with
everything the client publishes. See
[quick start §11](docs/quickstart.md#11-use-the-ai-registry) and `samples/Sample.AiRegistry`, which
reads every resource from a host and follows an MCP server.

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
