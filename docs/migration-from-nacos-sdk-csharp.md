# Migration from `nacos-sdk-csharp`

`Lolitacoding.Nacos` is a clean re-implementation, not a fork. The concepts transfer directly, but
the shape of the API is deliberately different in a few places, and those differences are the ones
worth reading this page for. Exact type names in other clients vary by version, so treat the
left-hand column as "the concept you are used to".

| You used (`nacos-sdk-csharp`) | You use now (`Lolitacoding.Nacos`) |
| --- | --- |
| `NacosFactory.CreateConfigService(...)` | `NacosClient.Create(options).Config` |
| `NacosFactory.CreateNamingService(...)` | `NacosClient.Create(options).Naming` |
| `INacosConfigService.GetConfigAsync(dataId, group, ct)` | `INacosConfigClient.GetConfigAsync(NacosConfigKey, ct)` |
| `PublishConfigAsync(dataId, content, group, type, ct)` | `PublishConfigAsync(NacosConfigKey, content, contentType, casMd5, ct)` |
| `RemoveConfigAsync(dataId, group, ct)` | `RemoveConfigAsync(NacosConfigKey, ct)` |
| `AddListener(dataId, group, callback)` | `AddListenerAsync(NacosConfigKey, callback, ct)` or `ListenAsync(NacosConfigKey, ct)` |
| `INamingService.RegisterInstanceAsync(serviceName, ip, port)` | `RegisterInstanceAsync(NacosServiceKey, NacosInstance)` |
| `GetAllInstancesAsync(serviceName, group, ct)` | `GetInstancesAsync(NacosServiceKey, cluster, healthyOnly, ct)` |
| `SubscribeAsync(serviceName, callback)` | `SubscribeAsync(NacosServiceKey, clusters, ct)` returning an `IAsyncEnumerable<NacosServiceChange>` |
| `NacosSdkOptions` / `NacosOptions` | `NacosOptions` |
| `AddNacosAspNet(...)` | `AddNacosServiceRegistration(...)` |
| `IConfigurationBuilder.AddNacosConfiguration(...)` | `AddNacosConfiguration(...)` (same name, different options type) |

## One client, one connection per module

The clearest structural change: there is a single entry point, and the client manages the
connections for you.

```csharp
await using NacosClient client = NacosClient.Create(new NacosOptions
{
    ServerAddresses = ["http://127.0.0.1:8848"],
});

INacosConfigClient config = client.Config;
INacosNamingClient naming = client.Naming;
INacosLockClient @lock = client.Lock;
```

Under the hood the client opens one gRPC connection per module. Nacos routes a connection by the
`module` label it declares during the handshake, and the naming, lock and AI servers discard
connections that did not declare them, so configuration and naming cannot share a stream.

Configuration and naming are connected when the client is; the lock connection is opened the first
time a lock is used, so an application that never takes one does not pay for it.

Construct with `NacosClient.Create(...)` — the constructor is private — and dispose it when the
process exits.

## Coordinates became records

Data ids, groups and namespaces are grouped into an immutable `NacosConfigKey`, and service names,
groups and namespaces into a `NacosServiceKey`. The namespace is optional on both, so the common
single-namespace deployment does not repeat the configured default on every call:

```csharp
var configKey = new NacosConfigKey("my-service.json", NacosConstants.DefaultGroup);
var serviceKey = new NacosServiceKey("order-service", NacosConstants.DefaultGroup);
```

Instances are an immutable `NacosInstance` rather than a mutable object with a service name and an
address. The service name lives on the key, not on the instance:

```csharp
var instance = new NacosInstance(
    ipAddress: "10.0.0.5",
    port: 8080,
    weight: 1.0,
    clusterName: "zone-a",
    metadata: new Dictionary<string, string> { ["version"] = "1.2.3" });
```

## Results instead of exceptions for "not found"

`GetConfigAsync` returns a `NacosConfigResult`, not a `string?`. A configuration that does not exist
sets `Exists` to `false` and `Content` to `null`; it is not reported as an error.

```csharp
NacosConfigResult result = await client.Config.GetConfigAsync(configKey);
if (result.Exists)
{
    Console.WriteLine(result.Content);
}
```

`result.FromLocalCache` tells you whether the value came from the failover snapshot because the
server was unreachable. In `nacos-sdk-csharp` that fallback was controlled separately and was easy
to miss.

## Listeners are async and disposable

The callback API returns an `IAsyncDisposable` handle, and the streaming API is an
`IAsyncEnumerable`:

```csharp
// Callback; dispose to stop.
await using IAsyncDisposable handle = await client.Config.AddListenerAsync(
    configKey, (change, ct) => { /* ... */ return Task.CompletedTask; });

// Stream.
await foreach (NacosConfigChange change in client.Config.ListenAsync(configKey, ct))
{
    Console.WriteLine(change.Content);
}
```

Change objects carry the whole result plus `PreviousMd5`. Duplicate notifications are suppressed by
comparing MD5 values, so a listener only runs when the value actually changed.

## Distributed locking

Locking is its own module on the client, `client.Lock`, and the lock is a server-side lease rather
than a local mutex. The closest thing to `NacosLock.lock()` is a lock handle that waits, renews and
releases on its own:

```csharp
NacosLock nightlyLock = client.Lock.GetLock(
    new NacosLockKey("nightly-reindex"),
    new NacosLockOptions { LeaseDuration = TimeSpan.FromSeconds(15) });

await using NacosLockLease lease = await nightlyLock.AcquireAsync(ct);

// ... work ...

if (!lease.IsHeld)
{
    // The lease was lost while the work ran.
}
```

The two clients line up like this:

| Nacos Java client | here |
| --- | --- |
| `NacosLock.lock()` | `NacosLock.AcquireAsync` |
| `NacosLock.tryLock()` | `NacosLock.TryAcquireAsync(TimeSpan.Zero)` |
| `NacosLock.tryLock(time, unit)` | `NacosLock.TryAcquireAsync(wait)` |
| `NacosLock.unlock()` | `NacosLockLease.DisposeAsync` |
| `NacosLockService.getReentrantLock(key)` | `GetLock(new NacosLockKey(key, NacosLockTypes.Reentrant))` |
| `NacosLockService.getNonReentrantLock(key)` | `GetLock(new NacosLockKey(key, NacosLockTypes.NonReentrant))` |
| `NacosLockService.remoteTryLock` / `unLock` / `renew` | `INacosLockClient.AcquireAsync` / `ReleaseAsync` / `RenewAsync` |
| `NacosLockWatchdog` | internal, started and stopped with the lock |

What is worth knowing before using it:

- Ownership is per asynchronous **flow**, not per thread. Java keeps the reentrant count in a
  `ThreadLocal`, which says nothing about work that hops between threads; here a nested call from the
  flow that holds the lock re-enters. A lock type that forbids re-entrancy throws
  `LockRecursionException`, this client's counterpart of Java's `IllegalMonitorStateException` guard
  for the same case.
- Re-entering does not ask the server. Java sends one `ACQUIRE` per nesting level; here the depth is
  local, the server sees one acquisition, and a nested acquisition therefore cannot end up queued
  behind its own holder.
- `unlock()` is `DisposeAsync`, and it does not throw where Java's does. Releasing a lock this client
  no longer holds, or a release the server refuses, is logged rather than thrown out of a dispose —
  nothing useful can be done about it there, and the lease expires anyway. Disposing the same lease
  twice releases once, so the `await using` form and an explicit release cannot collide.
- `Ended` completes both when a lease is released and when it is lost, and `IsHeld` tells the two
  apart. That is the question a critical section should ask: was the lock still mine when the work
  finished?
- The result is a value rather than a boolean: `NacosLockResult` carries the reentrant depth and the
  queue position, and a refused acquisition is an unsuccessful result rather than an exception.
  Exceptions are reserved for a request that could not be carried out at all — an unreachable
  server, a rejected token, a server that does not implement locking.
- The owner defaults to an identifier this client generates, which is the right default for a
  process acquiring its own locks. The low-level operations are for a lock that spans processes, or
  for renewing and releasing a lock this client did not take, where the owner has to be named.
- There is no fencing token. A holder whose lease expired can still act on what the lock guarded
  until it notices, so only guard work whose effects are idempotent or that checks its own lease
  before committing.

## Options are validated up front

Options are validated when the client is built, so a missing server address or a half-supplied
credential pair fails immediately with a `NacosConfigurationException` and a message that names the
setting. There is no "it connects and then behaves strangely" mode.

## Startup tolerance is one switch

`NacosOptions.Optional` is the single control for tolerating an unreachable server at startup. The
same switch exists on `NacosConfigurationSourceOptions`, where it decides whether a failed read
aborts host building. When it is `false` (the default), the client fails fast.

## Observability and AOT

Two capabilities that the older client does not offer in the same form:

- Traces and metrics are published under `Lolitacoding.Nacos` via `NacosDiagnostics`
  (`ActivitySource` + `Meter`). See the [quick start](quickstart.md#9-observability).
- The core library is `IsAotCompatible` and free of reflection-based serialization or runtime code
  generation, so it can be published with Native AOT.

## What is intentionally not implemented (v1)

- The Nacos **admin/console API** (creating namespaces, managing users).
- **AI registry** features (MCP, Prompt, Skill, AgentSpec and Agent discovery).
- **Address-server (endpoint) discovery.** Supply `ServerAddresses` explicitly.
- **Aliyun MSE AccessKey/SecretKey** authentication. The auth layer leaves room for it, but only
  user name / password is supported today.
- Multi-targeting to `netstandard` / .NET 8 and below.
