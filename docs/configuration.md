# Configuration reference

This page lists every option, its default, and the rules the library enforces. Options are plain
classes with settable properties, so they can be configured in code, bound from an
`IConfiguration` section, or set through any other `IOptions<T>` source.

Validation runs when a client is built (or, for the configuration source, when the source is
added). An invalid option fails immediately with a `NacosConfigurationException` naming the
offending setting, rather than surfacing later as a confusing connection failure.

## `NacosOptions`

Shared by the configuration centre and the naming service. Per-subsystem tuning lives on `Config`
and `Naming`.

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `ServerAddresses` | `IList<string>` | empty | **Required.** One or more `host:port` or `http://host:port` values. Multiple addresses enable failover. The gRPC endpoint is the HTTP port + 1000. |
| `Namespace` | `string` | `"public"` | The namespace (tenant) all operations default to. An empty value is normalised to `public`. Must be ASCII. |
| `UserName` | `string?` | `null` | Credential for an auth-enabled server. Must be set together with `Password`. |
| `Password` | `string?` | `null` | Must be set together with `UserName`. |
| `RequestTimeout` | `TimeSpan` | 5 s | Timeout for a single request, and the budget a request waits for a connection when it arrives during a reconnect (see `docs/faq.md`). Must be positive. |
| `ConnectionTimeout` | `TimeSpan` | 10 s | Timeout for establishing the connection and finishing the gRPC handshake. Must be positive. |
| `HealthCheckInterval` | `TimeSpan` | 5 s | How long a connection may sit idle before it is probed. Mirrors the official client's keep-alive. |
| `HealthCheckTimeout` | `TimeSpan` | 3 s | Timeout for a single health check. |
| `LocalCacheDirectory` | `string?` | `null` | Where snapshots and failover files are written. When unset, a per-user folder under `LocalApplicationData/Lolitacoding.Nacos/<namespace>` is used. |
| `ContextPath` | `string` | `"/nacos"` | HTTP context path of the server. |
| `AppName` | `string?` | `null` | Reported to the server as the connection's application label. |
| `LocalClientIp` | `string?` | `null` | Hint reported to the server. The server derives the authoritative address from the connection. |
| `Optional` | `bool` | `false` | When `true`, an unreachable server is tolerated at startup; the client keeps retrying and serves cached values. When `false`, the first connect fails fast. |
| `Reconnect` | `NacosReconnectOptions` | see below | Reconnect policy. |
| `Config` | `NacosConfigOptions` | see below | Configuration-centre options. |
| `Naming` | `NacosNamingOptions` | see below | Naming options. |
| `Ai` | `NacosAiOptions` | see below | AI registry options. |

### `NacosReconnectOptions`

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `InitialDelay` | `TimeSpan` | 1 s | Delay before the first reconnect attempt. Cannot be negative. |
| `MaxDelay` | `TimeSpan` | 30 s | Upper bound of the exponential delay. Must be ≥ `InitialDelay`. |
| `Multiplier` | `double` | `2.0` | Factor applied after each failed attempt. Must be ≥ 1.0 so delays never shrink. |
| `MaxAttempts` | `int` | `0` | Consecutive attempts before giving up. `0` retries indefinitely. Cannot be negative. |
| `JitterFactor` | `double` | `0.2` | Maximum random jitter as a fraction of the delay (0.0–1.0). Spreads a reconnect storm across a fleet. |

After a reconnect the client replays every configuration listener, service subscription and
ephemeral instance registration, because Nacos discards connection-scoped state when a connection
drops.

### `NacosConfigOptions`

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `LongPollingTimeout` | `TimeSpan` | 30 s | Timeout for a long-polling listener request. Must be positive. |
| `VerifyMd5` | `bool` | `true` | Verify the content MD5 against the value the server reported. A mismatch is an error, not a warning. |
| `EnableLocalCache` | `bool` | `true` | Write successfully read configurations to the snapshot directory for failover. |
| `DefaultContentType` | `string` | `"text"` | Content type applied when publishing and no explicit type is given. Cannot be empty. |
| `FuzzyWatchResyncInterval` | `TimeSpan` | 3 min | How often every pattern watch is re-sent so a push lost on a healthy connection is repaired. The server answers a re-sent watch with a diff, so the repetition is idempotent. Must be positive. |

### `NacosNamingOptions`

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `HeartbeatInterval` | `TimeSpan` | 5 s | Heartbeat interval for ephemeral instances. Must be positive. |
| `DefaultCluster` | `string` | `"DEFAULT"` | Cluster attached to registered instances when none is given. Cannot be empty. |
| `Ephemeral` | `bool` | `true` | Register instances as ephemeral by default. |
| `EnableLocalCache` | `bool` | `true` | Write the last known service list to disk so a restart during an outage can still resolve it. |
| `FuzzyWatchResyncInterval` | `TimeSpan` | 3 min | How often every naming pattern watch is re-sent so a push lost on a healthy connection is repaired. The server answers a re-sent watch with a diff, so the repetition is idempotent. Must be positive. |

### `NacosAiOptions`

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `EnableLocalCache` | `bool` | `true` | Keep the AI resources a subscription holds on disk, so a restart during a control-plane outage can still find the MCP servers, prompts, skills, agent specifications and cards it needs. Only a subscription writes a copy and only an unreachable server makes the client read one, and a resource the server reports as withdrawn has its copy removed. Stores under `NacosOptions.LocalCacheDirectory`, alongside the configuration and service snapshots. |
| `CacheRefreshInterval` | `TimeSpan` | 10 s | How often a subscribed resource is re-read. Nacos has no AI change push, so this interval is the only thing that makes a subscription fire: the client re-reads the server and reports the detail again when it differs from the cached copy. The MCP, prompt, skill and agent-specification subscriptions share it. Must be positive. |

The cache is what a subscription populates; a plain query is served directly from the server and
always reports what the server currently holds. The RAD agent watch is not part of the disk cache:
its recovery after a reconnect is in-memory.

## `NacosConfigurationSourceOptions`

Used by `builder.Configuration.AddNacosConfiguration(...)`. Deliberately smaller than
`NacosOptions`: a configuration source only needs to reach the server and name the data ids it
layers, so it does not expose transport tuning.

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `ServerAddresses` | `IList<string>` | empty | **Required.** Same rules as `NacosOptions.ServerAddresses`. |
| `Namespace` | `string` | `"public"` | Namespace the data ids are read from. |
| `GroupName` | `string` | `"DEFAULT_GROUP"` | Group the data ids belong to. Cannot be empty. |
| `DataIds` | `IList<string>` | empty | **Required.** Data ids to layer, in application order. Later data ids override earlier ones. |
| `UserName` | `string?` | `null` | Must be set together with `Password`. |
| `Password` | `string?` | `null` | Must be set together with `UserName`. |
| `LocalCacheDirectory` | `string?` | `null` | Snapshot directory for failover. |
| `Optional` | `bool` | `false` | When `false`, a failure to read the configuration aborts startup. |
| `LoggerFactory` | `ILoggerFactory?` | `null` | Optional. The provider has no container to resolve a logger from, so pass one to see connection failures. |

### Supported document formats

The format is chosen from the server-reported content type, then the data id extension, then a
content sniff:

| Format | Recognised content types / extensions |
| --- | --- |
| JSON | `json`, `application/json`, `.json` |
| YAML | `yaml`, `yml`, `.yaml`, `.yml` |
| Properties | `properties`, `.properties` |
| Text | `text`, `txt`, `.txt`, or anything unrecognised |

Structured documents are flattened with `:` and numeric array indices. The raw document is always
stored verbatim under its data id. A malformed structured document degrades to its raw value rather
than failing the application.

## `NacosServiceRegistrationOptions`

Used by `services.AddNacosServiceRegistration(...)`.

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `ServiceName` | `string?` | `null` | The name to register under. Falls back to the entry assembly name. |
| `GroupName` | `string` | `"DEFAULT_GROUP"` | Group the service is registered in. |
| `ClusterName` | `string?` | `null` | Cluster for the instance; unset means the server's default cluster. |
| `Ip` | `string?` | `null` | Address to advertise. Inferred from the bound server addresses when unset. |
| `Port` | `int?` | `null` | Port to advertise. Inferred from the bound server addresses when unset. |
| `Weight` | `double` | `1.0` | Load-balancing weight. |
| `Ephemeral` | `bool` | `true` | Ephemeral instances are removed when the connection is lost. |
| `DeregisterOnStop` | `bool` | `true` | Deregister during graceful shutdown instead of waiting for the server to notice. |
| `HealthCheckPath` | `string?` | `null` | Published under the `health.check.path` metadata key. |
| `Metadata` | `IDictionary<string, string>` | empty | Additional instance metadata. Library keys are added on top. |

`NacosServiceRegistrationOptions` has no `IConfiguration`-section overload; bind one explicitly if
you prefer configuration over code:

```csharp
builder.Services.Configure<NacosServiceRegistrationOptions>(
    builder.Configuration.GetSection("Nacos:Registration"));
builder.Services.AddNacosServiceRegistration();
```

## `NacosServiceDiscoveryOptions`

Used by `services.AddNacosServiceDiscovery(...)`. Controls which service names resolve through
Nacos and how the instances are looked up.

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `Schemes` | `IList<string>` | `[ "nacos" ]` | URI schemes that select the Nacos provider. A name carrying one of these schemes is always claimed. |
| `ResolveAllServices` | `bool` | `true` | When `true`, a name with no scheme at all resolves through Nacos. Set to `false` to require an explicit scheme. |
| `GroupName` | `string` | `"DEFAULT_GROUP"` | Group the resolved services belong to. Cannot be empty. |
| `Cluster` | `string?` | `null` | Restrict resolution to one cluster. Must not be whitespace when set. |
| `HealthyOnly` | `bool` | `true` | When `true`, only healthy, enabled instances become endpoints. |

A service discovery URI may combine a transport scheme with a discovery scheme using `+`, as in
`http+nacos://order-service`. The leading scheme decides the protocol of the resulting request; the
`nacos` segment is what makes this provider responsible for it. Naming a transport scheme in
`Schemes` instead — for example `[ "http" ]` — lets a caller write `http://order-service` directly.

Only a name with **no** scheme is affected by `ResolveAllServices`: an explicit scheme is always
matched against `Schemes` and never claimed by accident. This is deliberate, because claiming a name
promises to resolve it, and a name the registry does not know would otherwise have been handled by
the pass-through provider.

## Binding from configuration

`AddNacos(section)` binds a section to `NacosOptions` when the options are resolved, so it observes
configuration added later (including configuration that came from Nacos). Property names map
directly, and list properties use numeric indices:

```json
{
  "Nacos": {
    "ServerAddresses": [ "http://127.0.0.1:8848" ],
    "Namespace": "public",
    "AppName": "my-service",
    "Optional": true,
    "Reconnect": { "InitialDelay": "00:00:01", "MaxDelay": "00:00:30" },
    "Config": { "VerifyMd5": true }
  }
}
```

```csharp
builder.Services.AddNacos(builder.Configuration.GetSection("Nacos"));
```

`AddNacosConfiguration(section)` binds immediately, because a configuration provider cannot defer
its read — the very next source may depend on the values this one produces.

## Precedence and reload

- The Nacos configuration source is appended to the builder, so it overrides sources added before
  it (`appsettings.json`, environment variables, and so on).
- Within the source, `DataIds` are merged in order; a later data id overrides an earlier one for the
  same key.
- A change reloads the merged view and fires the configuration reload token, which drives
  `IOptionsMonitor<T>` and `IOptionsSnapshot<T>` refresh.
