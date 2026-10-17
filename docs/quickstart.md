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
| `Lolitacoding.Aspire.Hosting.Nacos` | `AddNacos(...)` to orchestrate a Nacos server from an Aspire AppHost. |
| `Lolitacoding.Aspire.Nacos` | `AddNacosClient(...)` to consume the AppHost's injected connection string. |

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

## 10. Take a distributed lock

`client.Lock` talks to the Nacos lock service, which is the one Nacos **3.3** introduced: the module a
3.2 server ships only acquires and releases, so renewing and waiting need a 3.3 server (the
[FAQ](faq.md#which-nacos-versions-are-supported) has the details). The lock is a lease the server
grants to an owner, so it has to be renewed to outlive the lease and released when the work is done —
`GetLock` returns a lock that does both by itself:

```csharp
using Lolitacoding.Nacos.Locking;

NacosLock nightlyLock = client.Lock.GetLock(
    new NacosLockKey("nightly-reindex"),

    // The server frees the lock on its own if this client dies, and the client renews it while it is
    // held: three times per lease, or every ten seconds, whichever is less often.
    new NacosLockOptions { LeaseDuration = TimeSpan.FromSeconds(15) });

// Waits for as long as it takes. TryAcquireAsync(wait) gives up instead and returns null, and
// TryAcquireAsync(TimeSpan.Zero) asks the server once without queueing.
await using NacosLockLease lease = await nightlyLock.AcquireAsync(cancellationToken);

// ... the work this lock protects ...

if (!lease.IsHeld)
{
    // A renewal failed while the work ran, so the lock may be somebody else's by now.
}
```

The lock is released when the last lease of the holder is disposed, which is the end of the scope
above. Re-entrancy is decided by the client rather than asked of the server: a nested call — or a
helper that takes the same lock — from the flow that already holds it returns another lease and only
increases a local count, and a lock type that forbids re-entrancy
(`NacosLockTypes.Mutex`, `NacosLockTypes.NonReentrant`) throws `LockRecursionException` instead of
deadlocking against itself.

Ownership follows the asynchronous flow, so *concurrent* work that must exclude the holder rather
than re-enter it — a task started while the lock is held inherits the flow — has to start in a flow
of its own, or use the low-level operations below with an owner of its own.

Three things are worth knowing before relying on this:

- The lease is a lease: a holder that dies loses its locks within one lease, and usually at once,
  because the connection carrying the renewals goes with it.
- There is no fencing token. A holder whose lease expired can still act on what the lock guarded, so
  a critical section whose effect must not outlive its lock checks `lease.IsHeld` before committing,
  or makes its effect idempotent.
- `IsHeld` turns false when the last lease is disposed, when the renewal fails, and when the client is
  disposed. `lease.Ended` completes for all of those, and is what a background task can wait on.

### Low-level access

The individual operations are on `client.Lock` as well, for the cases a lease does not cover: an
owner shared with another process, a renewal or release of a lock this client did not take, or an
acquisition that is abandoned somewhere other than the call that made it.

```csharp
NacosLockResult acquired = await client.Lock.AcquireAsync(key, new NacosLockAcquireOptions
{
    // Who holds the lock. The default is an identifier this client generates.
    Owner = "order-42-worker",

    // Join the server's first-in-first-out queue instead of failing immediately.
    WaitTime = TimeSpan.FromSeconds(30),

    // The server releases the lock on its own if this client dies.
    LeaseDuration = TimeSpan.FromSeconds(15),
});

if (!acquired.Success)
{
    // The queue ran out, or another owner holds the lock.
    Console.WriteLine(acquired.ErrorMessage);
    return;
}

try
{
    // ... the work this lock protects ...
    await client.Lock.RenewAsync(key, owner: "order-42-worker");
}
finally
{
    await client.Lock.ReleaseAsync(key, owner: "order-42-worker");
}
```

The lock connection is opened on first use, so an application that never takes one does not carry a
connection for it.

## 11. Use the AI registry

`client.Ai` reaches the AI registry, the part of Nacos that holds MCP servers, prompts, skills, agent
specifications and agents. This release covers the MCP server registry — reading a released server,
releasing one, following one, and publishing the address it is reachable at — and the prompt registry and the skill registry.

```csharp
using Lolitacoding.Nacos.Ai;

// A read is a direct query: it reports what the server holds at that moment. The version is
// optional — leaving it out asks for the one the server currently serves.
NacosMcpServerDetail server = await client.Ai.Mcp.GetServerAsync("weather", cancellationToken);

foreach (NacosMcpTool tool in server.ToolSpec?.Tools ?? [])
{
    Console.WriteLine(tool.Name);
}

foreach (NacosMcpEndpointInfo endpoint in server.BackendEndpoints ?? [])
{
    Console.WriteLine($"{endpoint.Protocol}://{endpoint.Address}:{endpoint.Port}{endpoint.Path}");
}
```

A release publishes one version of a server. The version is what the release creates and what a later
read pins, so it is required, and the tools and resources travel as the MCP protocol's own objects:

```csharp
var specification = new NacosMcpServer
{
    Name = "weather",
    Protocol = NacosMcpProtocol.Streamable,
    Description = "Weather tools for the support agent.",
    VersionDetail = new NacosMcpVersionDetail { Version = "1.2.0", IsLatest = true },
    Version = "1.2.0", // the plain string older servers read
    RemoteServerConfig = new NacosMcpRemoteServiceConfig
    {
        ServiceRef = new NacosMcpServiceRef { ServiceName = "weather-backend" },
    },
};

var tools = new NacosMcpToolSpecification
{
    Tools = [new NacosMcpTool { Name = "forecast", Description = "Tomorrow's weather." }],
};

string? id = await client.Ai.Mcp.ReleaseServerAsync(specification, tools, cancellationToken);
```

Following a server is a subscription. Nacos pushes configuration and service changes but has no MCP
change notification, so the client keeps the last detail it read and re-reads the server on an
interval. The first element is the current state, so a consumer initialises and updates along one
path:

```csharp
await foreach (NacosMcpServerDetail change in
    client.Ai.Mcp.SubscribeServerAsync("weather", ct: cancellationToken))
{
    Console.WriteLine($"{change.Name} {change.VersionDetail?.Version} (latest: {change.VersionDetail?.IsLatest})");
}
```

A server reached over the network is published as a Naming service, and its endpoint is an instance of
it, so it becomes discoverable and health checked like any other instance:

```csharp
await client.Ai.Mcp.RegisterServerEndpointAsync("weather", "10.0.0.7", 8080, "1.2.0", cancellationToken);
```

### Prompts

A prompt is a template plus the placeholders it uses. A read names a key and, optionally, a selector: a
version, or a label the server moves between versions. Leave both out and the server answers with
whatever version it labels `latest`; the version in the answer is the one that was resolved, so a label
can be read without knowing where it points.

```csharp
NacosPrompt prompt = await client.Ai.Prompt.GetPromptAsync(
    "greeting", label: "stable", ct: cancellationToken);

Console.WriteLine(prompt.Version);    // the version the label resolved to
Console.WriteLine(prompt.Template);   // "Hello {{name}}"
Console.WriteLine(prompt.Md5);        // the content hash, for change detection
```

Following a prompt is the same kind of poll as following a server — Nacos pushes neither — with one
difference: a prompt that stops being served is reported, as a `null` element, because a missing prompt
cannot be confused with one that has not appeared yet.

```csharp
await foreach (NacosPrompt? change in
    client.Ai.Prompt.SubscribePromptAsync("greeting", label: "stable", ct: cancellationToken))
{
    Console.WriteLine(change is null ? "(withdrawn)" : $"{change.Version}: {change.Template}");
}
```

The prompt registry is served from Nacos **3.2.0** on. There is no ability key for it, so the client
cannot refuse an older server up front: such a server answers a query with `RequestHandler Not Found`.

### Skills

A skill is a directory — `SKILL.md` plus the files it refers to — and Nacos serves it as a ZIP archive
rather than as JSON. Reading one therefore needs a reachable HTTP endpoint and does not open the AI
connection at all:

```csharp
NacosSkill skill = await client.Ai.Skill.GetSkillAsync(
    "release-notes", label: "stable", ct: cancellationToken);

Console.WriteLine(skill.Version);   // the version the label resolved to
Console.WriteLine(skill.Md5);       // the published content hash

using var archive = new ZipArchive(new MemoryStream(skill.Archive.ToArray()));

foreach (ZipArchiveEntry entry in archive.Entries)
{
    // "release-notes/SKILL.md", "release-notes/reference/notes.txt", ...
    Console.WriteLine(entry.FullName);
}
```

The archive is checked before it is handed over: a body that is not a ZIP, or one whose entries would
extract outside the skill's directory, is refused rather than passed to an extractor. Following a
skill is the same poll as following a prompt, with the same `null` element for one that stopped being
served:

```csharp
await foreach (NacosSkill? change in
    client.Ai.Skill.SubscribeSkillAsync("release-notes", ct: cancellationToken))
{
    Console.WriteLine(change is null ? "(withdrawn)" : change.Version);
}
```

One selector rule differs from a prompt's: a skill label that does not resolve is reported as a missing
version rather than answered with the latest one, so a typo is an error instead of a silent
substitution. Skills are served from Nacos **3.2.0** on, by `/v3/client/ai/skills`.

Five things are worth knowing before relying on this:

- The MCP registry is served from Nacos **3.0.3** on, and the prompt and skill registries from
  **3.2.0** on. An older server answers a query with `RequestHandler Not Found`; the
  [FAQ](faq.md#which-nacos-versions-are-supported) says which server each module needs.
- `GetServerAsync` is a direct query, so it always reports the server's current state — and needs the
  server to be reachable. `SubscribeServerAsync` is the exception: it caches the last detail it read
  and re-reads the server every `NacosAiOptions.CacheRefreshInterval` (10 s by default), because there
  is no push to subscribe to. A failed re-read leaves the cached copy in place and is retried, so a
  watcher outlives a control-plane outage.
- An endpoint lives and dies with the connection that published it, so the client replays it after a
  reconnect, and the version it was published for is remembered so that removing it removes it from the
  service the registration used.
- The AI connection is opened on first use, like the lock connection, and carries the `ai` module
  label plus a per-client `aiClientUuid` that Nacos records with everything the client publishes.
- A skill is read over HTTP rather than over that connection, so a caller that only reads or follows
  skills never opens it. The HTTP path presents the same access token, in the `accessToken` header the
  client OpenAPI reads rather than as a bearer header.

## Next steps

- [Configuration reference](configuration.md) — every option, with defaults.
- [Aspire orchestration](aspire.md) — run Nacos with `Aspire.Hosting`.
- [Migration from `nacos-sdk-csharp`](migration-from-nacos-sdk-csharp.md).
- [FAQ](faq.md).
- [Protocol notes](protocol-notes.md).
