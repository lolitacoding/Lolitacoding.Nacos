# FAQ

## Which Nacos versions are supported?

Nacos **2.x and 3.x**. The transport speaks the Nacos gRPC protocol, which is unchanged across both
lines, and the HTTP OpenAPI for authentication. CI runs the integration suite against
`nacos/nacos-server:v2.5.1` and `nacos/nacos-server:v3.0.2`.

The `test` type label and the payload type names differ subtly between upstream versions and Java
SDK releases; the details, with file references, are in
[the protocol spec](protocol-grpc-spec.md).

## Which .NET versions are supported?

Only **.NET 10** (`net10.0`), with `LangVersion=latest`. There is no `netstandard` build and no
multi-targeting. If you need an older runtime, this library is not a fit.

## Which ports does the client use?

The gRPC endpoint is the HTTP port plus **1000** (`8848` → `9848`), matching the official client.
The TLS gRPC offset is the HTTP port plus 1001, but TLS is not implemented in v1. Make sure both
ports are reachable from the application; a firewall that allows `8848` but not `9848` produces a
connection failure.

## How do I authenticate?

Set `UserName` and `Password` on `NacosOptions` (or on `NacosConfigurationSourceOptions`):

```csharp
options.UserName = "nacos";
options.Password = "nacos";
```

Both must be supplied together. The client logs in over the HTTP OpenAPI, caches the access token,
refreshes it before it expires, and retries a request once if the server rejects the token. A wrong
password throws `NacosAuthException`.

Aliyun MSE `AccessKey`/`SecretKey` authentication is **not** implemented in v1.

## Is TLS supported?

No. The transport uses plaintext gRPC. Run Nacos on a trusted network, or terminate TLS in front of
it, until a later release adds native support.

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

## Why does a reconnect not lose my listeners and registrations?

Nacos discards connection-scoped state — configuration listeners, service subscriptions and
ephemeral instance registrations — when a connection drops. The client records that state and
replays it after a reconnect, so subscriptions keep working. Check the logs at `Information` level
to see the replay; replayed listeners are counted, and a subscriber that fails to restore logs an
error without taking the connection down.

## Where are cached values stored?

Under `NacosOptions.LocalCacheDirectory`. When it is unset, a per-user folder is used:

```
%LOCALAPPDATA%\Lolitacoding.Nacos\<namespace>\        (Windows)
~/.local/share/Lolitacoding.Nacos/<namespace>/        (Linux, per XDG)
```

Set `Config.EnableLocalCache = false` or `Naming.EnableLocalCache = false` to disable caching.
Cached values are only used when the server is unreachable; `NacosConfigResult.FromLocalCache` and
`NacosServiceInfo.FromLocalCache` tell you when that happened.

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

The DI, configuration and ASP.NET Core packages are ordinary `Microsoft.Extensions.*` integrations
and are not part of the AOT guarantee.

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
reconnect decisions, naming and configuration logic, the DI registrations and the observability
wiring. They use in-memory fakes and never touch the network. The integration tests boot a real
Nacos container with Testcontainers and are marked `Category=Integration`; they are what proves
end-to-end behaviour against a live server.
