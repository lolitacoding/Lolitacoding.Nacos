# FAQ

## Which Nacos versions are supported?

Nacos **3.x**. The transport speaks the Nacos gRPC protocol, which is unchanged across the 3.x line,
and the v3 HTTP OpenAPI for authentication. By default the integration suite runs against
`nacos/nacos-server:v3.0.2`, the oldest supported release, and `NACOS_TEST_IMAGE` moves a run to
another server; CI runs the base suite against every version in the matrix below.

The surfaces did not all arrive at once, so each is pinned to the oldest release it was verified
against — which is also what an override lets a CI run move:

| Surface | Served from | Verified against | Override |
| --- | --- | --- | --- |
| Transport, Config, Naming, Auth, TLS | 3.0.2 | `nacos/nacos-server:v3.0.2` | `NACOS_TEST_IMAGE` |
| MCP server registry | 3.0.3 | `nacos/nacos-server:v3.2.4` | `NACOS_AI_TEST_IMAGE` |
| A2A agent card registry | 3.1.0 | `nacos/nacos-server:v3.2.4` | `NACOS_AI_TEST_IMAGE` |
| Prompt, Skill and AgentSpec registries | 3.2.0 | `nacos/nacos-server:v3.2.4` | `NACOS_AI_TEST_IMAGE` |
| RAD agent registry | 3.3.0 | `nacos/nacos-server:v3.3.0-RC` | `NACOS_AGENT_TEST_IMAGE` |
| RAD agent watch | 3.3.0 | `nacos/nacos-server:v3.3.0-RC` | `NACOS_AGENT_TEST_IMAGE` |
| Distributed lock, full contract | 3.3.0 | `nacos/nacos-server:v3.3.0-RC` | `NACOS_LOCK_TEST_IMAGE` |

Where a surface is *served from* an older release than it is *verified against*, the older release
has not been put through the suite yet; "served from" is the release the upstream contract names,
"verified against" is the oldest one a run has actually passed. The reasons are below.

Several surfaces are younger than that base release, so their tests choose a server of their own:

- The **AI registry** is served in two waves. The **MCP server registry** is the first: Nacos **3.0.3**
  on, because the official API marks `getMcpServer` and `releaseMcpServer` `@Since("3.0.3")`, and a
  3.0.2 server answers either one with `RequestHandler Not Found`. The **A2A agent card registry**
  sits between the two: Nacos **3.1.0** on, because `QueryAgentCardRequestHandler`,
  `ReleaseAgentCardRequestHandler` and `AgentEndpointRequestHandler` are all marked `@Since("3.1.0")`
  (and the batch endpoint handler `@Since("3.1.1")`). Like the MCP registry it is served over the AI
  connection. The **prompt**, **skill** and **agent-specification** registries are the third: Nacos
  **3.2.0** on (`QueryPromptRequestHandler`
  and the `GET /v3/client/ai/prompt` handler behind it are both marked `@Since("3.2.0")`, as are
  `SkillClientController.get` and `AgentSpecClientController.get`). There is no ability key for any of
  them, so an older server is not refused in advance — it answers `RequestHandler Not Found`, or, for
  a skill or an agent specification, does not serve the endpoint at all. Those two are read over HTTP
  rather than over the AI connection, which is how the server exposes them. The AI tests choose their
  server with `NACOS_AI_TEST_IMAGE`, which defaults to `nacos/nacos-server:v3.2.4`, the oldest release
  all five parts have been checked against.
- The **RAD agent registry** is younger again: the publish, search, discovery and endpoint handlers are
  all `@Since("3.3.0")`, so its tests run against `nacos/nacos-server:v3.3.0-RC` and are chosen with
  `NACOS_AGENT_TEST_IMAGE`. Unlike the other AI resources it is negotiated strictly — a server has to
  state the RAD ability before the client sends anything — so a 3.2 server is refused with the
  not-implemented code rather than sent a payload it may not route. The **RAD agent watch** — the
  push half of discovery — travels on the same connection and is negotiated on its own ability,
  `radWatchV1`, which the same `v3.3.0-RC` server states: like the registry it is refused rather than
  downgraded when the ability is absent or declined, because a watch whose hints are silently dropped
  would stop reporting changes without saying so.
- The distributed **lock** module belongs to Nacos **3.3**: lease renewal, the wait queue, explicit
  owners and the reentrant count are all from that release, and the lock module a 3.2 server ships
  only acquires and releases. Against a 3.2 server this client can therefore acquire and release a
  lock, but it cannot renew one — a refused renewal ends the lease locally, so the caller learns about
  it rather than holding a lock the server has already dropped — and a request that would queue is
  refused instead. The lock tests choose their server with `NACOS_LOCK_TEST_IMAGE`, which defaults to
  `nacos/nacos-server:v3.3.0-RC`. Which is which is described in
  [the protocol notes](protocol-notes.md#distributed-lock-specifics).

The `test` type label and the payload type names differ subtly between upstream versions and Java
SDK releases; the details, with file references, are in
[the protocol spec](protocol-grpc-spec.md).

## Which .NET versions are supported?

Only **.NET 10** (`net10.0`), with `LangVersion=latest`. There is no `netstandard` build and no
multi-targeting. If you need an older runtime, this library is not a fit.

## Which ports does the client use?

The gRPC endpoint is the HTTP port plus **1000** (`8848` → `9848`), matching the official client.
TLS does not move it: a Nacos server negotiates TLS and plaintext on that same gRPC port, so an
`https://` address still dials `9848`. Make sure both ports are reachable from the application; a
firewall that allows `8848` but not `9848` produces a connection failure.

## How do I authenticate?

Set `UserName` and `Password` on `NacosOptions` (or on `NacosConfigurationSourceOptions`):

```csharp
options.UserName = "nacos";
options.Password = "nacos";
```

Both must be supplied together. The client logs in over the HTTP OpenAPI, caches the access token,
refreshes it before it expires, and retries a request once if the server rejects the token. A wrong
password throws `NacosAuthException`.

The account has to exist first: a Nacos 3.x server ships no users at all, not even a default
administrator. Create one once — through the console on its first run, or by posting a `password` to
`/nacos/v3/auth/user/admin` on the server's main port:

```powershell
curl.exe -X POST "http://localhost:8848/nacos/v3/auth/user/admin?password=<password>"
```

Until that call has been made, every login is rejected whether or not the user exists, and the
endpoint itself answers `409` once an administrator is in place. Note also that this endpoint is the
only way to write a configuration over HTTP: the v3 administration API needs a token even when the
authentication plugin is switched off, and the client-facing v3 API reads only.

Aliyun MSE `AccessKey`/`SecretKey` authentication is **not** implemented in v1.

## Is TLS supported?

Yes. Put `https://` in `ServerAddresses` to enable it; the client negotiates TLS on both the HTTP and
gRPC endpoints. `NacosTlsOptions` (on `NacosOptions.Tls`) then controls how the handshake is
performed:

- `AllowUntrustedCertificate` accepts any server certificate — for development servers with
  self-signed certificates only.
- `ServerCertificateAuthority` / `ServerCertificateAuthorityFile` trust a private certificate
  authority.
- `ClientCertificate` / `ClientCertificateFile` plus `ClientCertificateKey` /
  `ClientCertificateKeyFile` present a client certificate for mutual TLS.
- `EnabledSslProtocols` and `CipherSuites` pin the accepted protocol versions and cipher suites.

The default validates the server certificate against the operating system trust store, which is the
correct choice for production.

## Why does the application fail to start when Nacos is down?

Because `Optional` defaults to `false`. This is deliberate: an application that silently starts
without the settings it was told to load is usually worse off than one that refuses to start.

Set `Optional = true` to tolerate an unreachable server:

```csharp
options.Optional = true;
```

The client then connects in the background, keeps retrying with exponential backoff, and serves
configuration and service lists from its local cache in the meantime.

## What is the difference between `Optional` and the reconnect settings?

`Optional` controls the **first** connection attempt. Reconnect settings (`NacosOptions.Reconnect`)
control how the client recovers after a connection that was already established is lost. A client
with `Optional = false` still reconnects automatically if the server goes away after startup — it
just fails fast if the server was never reachable in the first place.

## How does a health check know whether the client is connected?

Read `client.Config.ServerStatus` or `client.Naming.ServerStatus`. Both return a
`NacosServerStatus`: `Up` when that client's connection is established and the handshake completed,
`Down` otherwise. The value comes from the transport's own state, so a readiness probe costs nothing
and never blocks on the network; there is no request to time out.

These are the counterparts of the Java client's `getServerStatus()`, which returns the strings
`UP`/`DOWN`; the mapping is direct. `Up` says the connection is usable — it does not promise that a
particular configuration exists or that every request will succeed, so a caller that needs an
authoritative answer should still read the value it depends on. The AI registry and the lock client
open their own connections lazily and are not covered by these two properties.

## How do I point the client at an address server?

Set `NacosOptions.Endpoint` to the address server's host and leave `ServerAddresses` empty:

```csharp
var options = new NacosOptions
{
    Endpoint = "address-server.example.com",
    EndpointPort = 8080,
};
```

The client requests `http://address-server.example.com:8080/nacos/serverlist` — the port, context
path and resource name are configurable — expects one Nacos server per line, and dials the servers
it is given. Seeding runs in the background, so creating a client never blocks on the endpoint; the
first connect waits up to `ConnectionTimeout` for the list and then fails with a message that names
the address server. The list is afterwards polled every `EndpointRefreshInterval` (30 s by default),
and a change is applied at most once every 30 s.

`Endpoint` replaces `ServerAddresses`: if both are set, the configured addresses are ignored and a
warning (event 1008) is logged. A published line may be `host`, `host:port` or
`https://host:port`; one unparsable line rejects the whole answer, and an empty answer does too, so
the list already in use stays in force. A connection is only replaced when its current server has
left the list — adding a node never costs a healthy connection.

## Why does a reconnect not lose my listeners and registrations?

Nacos discards connection-scoped state — configuration listeners, service subscriptions and
ephemeral instance registrations — when a connection drops. The client records that state and
replays it after a reconnect, so subscriptions keep working. Check the logs at `Information` level
to see the replay; replayed listeners are counted, and a subscriber that fails to restore logs an
error without taking the connection down.

## What happens to a request made while the client is reconnecting?

It waits for the connection instead of failing against one that is momentarily absent.
`NacosOptions.RequestTimeout` — the same budget a request gets anyway, 5 seconds by default — bounds
the wait, and the wait ends with the **first** attempt the recovery makes. If that attempt
succeeds, the request is sent; if the server refuses it, the request fails with
`NacosConnectionException` right away, so a caller with a local copy is not held any longer than the
attempt took. Two cases do not wait at all: a client that has **never** connected, because there is
no connection to wait for (that is `Optional`'s fail-fast startup), and a request that arrives after
a reconnect attempt was already refused, because the outage is then not a blip to sit out.

## What is the difference between `WatchAsync` and `ListenAsync`?

`ListenAsync` follows **one** configuration and reports its content. `WatchAsync` (the fuzzy watch)
follows a **pattern** — every configuration whose data id matches — and reports which configurations
match it: `Added`, `Removed`, and `InitialSyncCompleted` once the initial catch-up is done, so a
consumer can build the set from the stream alone. `Modified` exists as a defensive mapping but a
Nacos server does not currently send it: the server tracks membership, not content, and a change to a
configuration that already matches is not pushed through a fuzzy watch. Use `ListenAsync` when the
value matters, and `WatchAsync` when you need to know *which* configurations exist — for example to
watch every `feature.*` flag without knowing their names.

Naming has the same split: `SubscribeAsync` follows one service's instance list, while
`INacosNamingClient.WatchAsync` follows the set of services whose name and group match a pattern. Its
events are `Added`, `Removed` and `InitialSyncCompleted` (plus a defensive `InstancesChanged`), and
they name a service rather than carrying its instances; subscribe to a service it reports when you
need the instances. Like the configuration watch, a naming watch tracks membership only: the server
does not push an instance change for a service that stays matched.

The server limits how many patterns a connection may watch (`50310`) and how many resources one
pattern may match (`50311`); both arrive as `NacosServerException` with that code. A watch is re-sent
every `Config.FuzzyWatchResyncInterval` / `Naming.FuzzyWatchResyncInterval` (3 minutes by default) so
a lost push is repaired.

## How do I list every service in a group?

Use `GetServicesAsync`. It asks the server for one page at a time and reports the group total, so
paging is a loop over `Count`:

```csharp
int pageNo = 1;
while (true)
{
    NacosServiceListPage page = await client.Naming.GetServicesAsync(
        pageNo: pageNo, pageSize: 100, groupName: "DEFAULT_GROUP", ct: ct);

    foreach (string name in page.Services)
    {
        Console.WriteLine(name);   // the bare service name
    }

    if (pageNo * 100 >= page.Count)
    {
        break;
    }

    pageNo++;
}
```

`page.Count` is the total for the group, not the size of the page, and `page.Services` holds bare
names because the server strips the group prefix. The group must match exactly; there is no wildcard.
Use `WatchAsync(groupPattern)` instead when the set has to stay current, and note that the list is
not served from a local cache during an outage — a stale list would hide services that appeared
meanwhile.

## How do I register several instances of one service from one client?

Use `RegisterInstancesAsync`. It sends the whole list in one request:

```csharp
var service = new NacosServiceKey("order-service", "DEFAULT_GROUP");

await client.Naming.RegisterInstancesAsync(service,
[
    new NacosInstance("10.0.0.5", 8080),
    new NacosInstance("10.0.0.6", 8080),
], ct);
```

The Nacos server keeps **one published instance set per service per connection**, so this is the
only way one client publishes several instances of the same service: calling
`RegisterInstanceAsync` twice for the same service replaces the first instance instead of adding
the second. A later single registration or batch also replaces the whole set, and
`DeregisterInstanceAsync` removes the whole set.

To remove part of a batch, use `DeregisterInstancesAsync`. It republishes the remainder, because
the protocol has no batch-deregistration message; instances are matched by address and port, and
the service must have been registered with `RegisterInstancesAsync` first:

```csharp
await client.Naming.DeregisterInstancesAsync(service,
    [new NacosInstance("10.0.0.5", 8080)], ct);
```

Only ephemeral instances can be batch-registered. For persistent instances, register each one
individually from its own client connection.

## How do I follow only some instances of a service?

Use the selector overload of `SubscribeAsync`. It keeps a subset of what the server reports, so a
consumer can follow one zone or one label without asking the server for a narrower list:

```csharp
var selector = NacosInstanceSelectors.Metadata(
    new Dictionary<string, string> { ["tier"] = "gold" });

await foreach (NacosServiceChange change in client.Naming
    .SubscribeAsync(service, selector)
    .WithCancellation(ct))
{
    // change.Instances holds only the instances labelled tier=gold.
}
```

`NacosInstanceSelectors` mirrors the Java 3.x factory: `All`, `Healthy`, `Clusters`, `IpAddress` and
`Metadata`, plus `From` for a custom filter. The filter runs on the client, against the list the
server pushed, so the subscription asks for every cluster. A push that only changes instances the
selector excludes does not wake the consumer, and the first element is filtered too. Two consumers of
the same service may use different selectors: the server subscription is shared, the narrowing is
per consumer.

The selector overload has no token parameter; pass one with `WithCancellation` as above, or leave the
enumeration to unsubscribe. Use the plain `SubscribeAsync(service, clusters)` overload when the
cluster is the only criterion, because the server then does the narrowing.

## How do I encrypt configuration values?

Publish under a data id that starts with `cipher-`, and register the algorithm implementation:

```csharp
var options = new NacosOptions { ServerAddresses = ["http://127.0.0.1:8848"] };
options.Config.EncryptionProviders.Add(new MyAesEncryptionProvider());

await using NacosClient client = NacosClient.Create(options);
await client.ConnectAsync();

var key = new NacosConfigKey("cipher-AES-db.json", NacosConstants.DefaultGroup);
await client.Config.PublishConfigAsync(key, connectionString, contentType: "text");
```

The client encrypts before sending, stores the provider-wrapped data key beside the content, and
decrypts on read and on every pushed change. The data id selects the provider: `cipher-AES-...`
uses the provider whose `AlgorithmName` is `AES`, matched case-sensitively. A client that does not
register the provider sees the ciphertext and the wrapped key, because encryption is client-side and
the server stores exactly what it was given.

The snapshot keeps the stored ciphertext and the wrapped key, so a failover read is decrypted the
same way. `NacosConfigResult.EncryptedDataKey` exposes the stored key. A data id with the `cipher-`
prefix but no matching provider is passed through unchanged with a warning (event `4019`), which is
what the Java client does.

For a transformation that is not encryption, implement `INacosConfigFilter` and add it with
`client.Config.AddConfigFilter(...)`. Filters run in `Order` order on publish, read and push; a
filter that throws fails an explicit read or publish, and is logged and skipped on a push (event
`4020`).

## Can a change to a subscribed service slip through?

No. `ListenAsync` / `SubscribeAsync` first hand you the state as it was read, and only then register
the listener or subscription on the server; the answer to that registration carries the state at the
instant the registration took effect, and it is applied exactly like a push. A change made in the
window between the read and the registration is therefore delivered too, and a registration whose
answer repeats a state you already have is filtered out rather than reported twice. The same holds
after a reconnect: replaying the registration is answered with the state at the instant it was
restored, which is what recovers a change that happened while the connection was down and had nobody
to be pushed to.

## Where are cached values stored?

Under `NacosOptions.LocalCacheDirectory`. When it is unset, a per-user folder is used:

```
%LOCALAPPDATA%\Lolitacoding.Nacos\<namespace>\        (Windows)
~/.local/share/Lolitacoding.Nacos/<namespace>/        (Linux, per XDG)
```

Set `Config.EnableLocalCache = false`, `Naming.EnableLocalCache = false` or
`Ai.EnableLocalCache = false` to disable caching.
Cached values are only used when the server is unreachable; `NacosConfigResult.FromLocalCache` and
`NacosServiceInfo.FromLocalCache` tell you when that happened.

## Does the service list cache survive a restart?

Yes. A successful query and a server push both write the service's last known instance list to the
snapshot directory, and a lookup that finds no in-memory entry falls back to that file. The
in-memory cache only helps a process that stays up; the on-disk snapshot is what keeps a rolling
restart during a Nacos outage from becoming an application outage. It is written atomically, so a
process killed mid-write leaves either the previous snapshot or the new one, never a truncated file.

## Does the AI registry cache survive a restart?

Yes, when `Ai.EnableLocalCache` is on (the default). An MCP, prompt, skill, agent-specification or
agent-card **subscription** writes the resource it read to the snapshot directory, and a client that
starts while Nacos is unreachable is answered from that copy instead of failing — which is the
difference between an agent host that comes back up during an outage and one that cannot start.
Nothing else writes a copy, nothing but an unreachable server makes the client read one, and a
resource the server reports as withdrawn has its copy removed, so the file is never a better answer
than the server. The RAD agent watch is the exception: its recovery after a reconnect is in memory.

The official Java client keeps all of these caches in memory only, so this is an extension rather than
a port; [the protocol notes](protocol-notes.md#the-local-failover-cache) record it as the intended
deviation.

## Why does an agent watch need HTTP long polling?

Because the push half of discovery needs a channel the server can write to, and some deployments do
not have one: a proxy can drop server-initiated messages, and a server can state that it does not
implement the `radWatchV1` hint push at all. Refusing to watch there would leave the caller with
nothing; watching silently would leave it with a watch whose hints never arrive.

The HTTP batch is the second contract: one `POST /v3/client/ai/agents/watch` carries the client's
whole watch set together with the fingerprint of the state it holds, and the server holds the request
open until one of those fingerprints no longer matches or the long-poll timeout elapses. It is the
same interest expressed in a way that survives a channel that cannot be pushed to.

`NacosAiOptions.AgentWatchTransport` decides which one is used. `Auto` (the default) prefers gRPC,
falls back to the batch when the push is not available, and falls back to re-reading at
`Ai.CacheRefreshInterval` when neither is; `Grpc`, `Http` and `Polling` pin one. Pin `Http` when the
push channel is unreliable but a long poll passes through the proxy, pin `Polling` when the proxy
holds long polls open or blocks them, and leave `Auto` alone otherwise. See
[the protocol notes](protocol-notes.md#rad-agent-watch) for the endpoint, the fingerprint and the
failure rules.

## How do I resolve services with `Microsoft.Extensions.ServiceDiscovery`?

Add the integration and use a service name as the request URI's host:

```csharp
builder.Services.AddNacos(options => options.ServerAddresses = ["http://127.0.0.1:8848"]);
builder.Services.AddNacosServiceDiscovery();

builder.Services.AddHttpClient("orders", c => c.BaseAddress = new Uri("http+nacos://order-service"))
    .AddServiceDiscovery();
```

The `nacos` scheme selects the Nacos provider and the leading scheme (`http`) stays the protocol of
the request. A bare `order-service` name resolves through Nacos by default; set
`ResolveAllServices = false` to require the explicit scheme. Resolution reuses the naming client, so
it benefits from the same in-memory and on-disk failover.

## How do I run Nacos with Aspire for local development?

Add `Lolitacoding.Aspire.Hosting.Nacos` to the AppHost and `Lolitacoding.Aspire.Nacos` to each
service:

```csharp
// AppHost.
builder.AddNacos("nacos").WithDataVolume();

builder.AddProject<Projects.MyApi>("my-api")
    .WithReference(nacos)
    .WaitFor(nacos);

// MyApi.
builder.AddNacosClient(options => options.Optional = true);
```

The AppHost runs a standalone `nacos/nacos-server` container, waits for its readiness check, and
injects `ConnectionStrings__nacos` into the referenced service. See the
[Aspire orchestration guide](aspire.md) for ports, persistence and authentication.

## How do I use multiple namespaces?

Each `NacosConfigKey` and `NacosServiceKey` takes an optional namespace. When it is omitted the
configured `NacosOptions.Namespace` applies. To talk to two namespaces, pass the namespace on the
key:

```csharp
var prodKey = new NacosConfigKey("my-service.json", "DEFAULT_GROUP", @namespace: "prod");
var devKey = new NacosConfigKey("my-service.json", "DEFAULT_GROUP", @namespace: "dev");
```

## How do I run several Nacos environments from one application?

Create more than one `NacosClient`, each with its own `NacosOptions`. Sharing one client across
namespaces is fine (use key-level namespaces), but sharing one across *servers* is not — the
connection, credentials and failover list all belong to the options object.

## How do I get hot reload into my options?

Add Nacos as a configuration source and bind your own options to a configuration section:

```csharp
builder.Configuration.AddNacosConfiguration(options =>
{
    options.ServerAddresses = ["http://127.0.0.1:8848"];
    options.DataIds = ["my-service.json"];
});

builder.Services.Configure<MyOptions>(builder.Configuration.GetSection("My"));
```

A change to the Nacos document reloads the configuration and fires the reload token, so
`IOptionsMonitor<MyOptions>` and `IOptionsSnapshot<MyOptions>` update without a restart.

## Why does my JSON document show up as one key?

Structured documents are flattened (`App:Greeting`), but the **raw** document is also always stored
under its own data id, so `configuration["my-service.json"]` returns the whole text. Both views are
available at once; this is not an either/or.

If a document is not being flattened, check the content type and the data id extension: the format
is chosen from the server-reported type, then the data id suffix, then a content sniff. A malformed
document degrades to its raw value rather than throwing.

## Can I use this with Native AOT or trimming?

Yes, for the core `Lolitacoding.Nacos` package. It is annotated `IsAotCompatible`, uses
`System.Text.Json` source generation, and contains no reflection-based serialization or runtime code
generation. The `Sample.Aot` project in this repository is published with `PublishAot=true` in CI,
and every `IL2xxx`/`IL3xxx` warning is an error.

The DI, configuration, service discovery and ASP.NET Core packages are ordinary
`Microsoft.Extensions.*` integrations and are not part of the AOT guarantee.

## What do the log event id ranges mean?

Source-generated messages are grouped by subsystem: connection lifecycle and address-server polling
in the `1000` range, push events in `2000`, reconnect recovery in `3000`, the configuration client
in the `4000` range, the naming client in the `5000` range, the service discovery integration in the
`6000` range, the distributed lock in the `7000` range, and the AI registry in the `8000` range.

## How do I turn on logs?

The client logs through `ILogger`. In a host, configure logging as usual:

```csharp
builder.Logging.AddSimpleConsole();
builder.Logging.SetMinimumLevel(LogLevel.Debug);
```

Source-generated messages are used throughout; connection lifecycle events are in the `1000` range,
push events in `2000`, reconnect recovery in `3000`.

## How do I turn on traces and metrics?

See [the quick start](quickstart.md#9-observability). Summarised:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(NacosDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(NacosDiagnostics.MeterName));
```

## Why is the package version `0.1.0-preview.*`?

MinVer derives the version from git tags. Before the first `v*` tag, builds are pre-release
`0.1.0-preview.0`. Tag a commit to publish a stable version.

## The integration tests need Docker. What does the unit suite cover?

The unit tests cover the protocol codec, handshake sequencing, request/response error handling,
reconnect decisions, naming and configuration logic, the lock and MCP client contracts, the DI
registrations and the observability wiring. They use in-memory fakes and never touch the network. The
integration tests boot a real Nacos container with Testcontainers and are marked `Category=Integration`;
they are what proves end-to-end behaviour against a live server. Which server each module runs against
is described in [Which Nacos versions are supported?](#which-nacos-versions-are-supported).

One environment note: if `HTTP_PROXY` / `HTTPS_PROXY` are set, the HTTP client honours them and tries
to tunnel the gRPC connections through the proxy, which fails with `unable to establish HTTP/2
connection` — while the tests that only speak HTTP keep passing, so the failure looks unrelated to the
environment. Add the container host to `NO_PROXY` (or unset the variables) for the test run.
