# Java SDK parity

This document records what the official Nacos Java client can do that this library could not,
ranked by how much a .NET application is hurt by the gap. It is the working list the parity
slices are cut from; each implemented slice removes a row and is recorded in `PLAN.md`.

## What was compared

The comparison is surface by surface, against the `nacos-develop` submodule:

| Java module | What it contributes |
| --- | --- |
| `api` | `ConfigService`, `NamingService`, `NamingMaintainService`, `LockService`, `AiService` and the listener/selector/model types they expose. |
| `client` | The implementations: gRPC proxies, caches, redo services, filters, fuzzy watch holders, AI transports. |
| `client-basic` | `NacosFactory` and the property keys a client application sets. |
| `maintainer-client` | `AiMaintainerService` and the naming maintainer client: the administrative surface, not the runtime client. |
| `specs/en` | The normative description of each protocol, used to decide what a gap actually is. |

The scope is deliberately **Nacos 3.x runtime client behaviour**. Anything that only exists to
administer a server, to render the console, or to integrate with Spring or the JVM is listed at
the bottom with the reason it is not implemented here.

## Ranked gaps

| # | Capability | Java entry point | Status here | Why it matters |
| --- | --- | --- | --- | --- |
| P0-1 | Configuration fuzzy watch | `ConfigService.fuzzyWatch(...)`, `fuzzyWatchWithGroupKeys(...)`, `cancelFuzzyWatch(...)` | **Done** — `INacosConfigClient.WatchAsync`, `NacosConfigWatchEvent`, `Config.FuzzyWatchResyncInterval` | The only protocol capability this client explicitly told the server it could not do. It is how a process discovers configuration keys it does not know by name. |
| P0-2 | Naming fuzzy watch | `NamingService.fuzzyWatch(...)`, `fuzzyWatchWithGroupKeys(...)`, `cancelFuzzyWatch(...)` | **Done** — `INacosNamingClient.WatchAsync`, `NacosServiceWatchEvent`, `Naming.FuzzyWatchResyncInterval` | Discovers services by pattern and keeps that set current, without polling a service list. |
| P1-3 | Client-side naming selectors | `NamingService.subscribe(serviceName, groupName, NamingSelector, listener)` and the matching `unsubscribe` | **Done** — `INacosNamingClient.SubscribeAsync(NacosServiceKey, INacosInstanceSelector)`, `NacosInstanceSelectors`, `NacosInstanceSelectorContext` | Zone and label routing is the normal way to keep traffic local; without it every consumer sees every instance. |
| P1-4 | Configuration filter chain and encrypted data key | `ConfigService.addConfigFilter(IConfigFilter)`, built-in `ConfigEncryptionFilter`, `ConfigQueryResponse.EncryptedDataKey` | Missing; the key is on the wire but dropped | The published extension point for encrypting or transforming configuration on the way in and out. Dropping the key also makes an encrypted configuration unreadable. |
| P1-5 | Service list paging | `NamingService.getServicesOfServer(pageNo, pageSize, groupName, selector)` | Missing; `ServiceListRequest`/`ServiceListResponse` already exist on the wire | Enumerates a namespace a page at a time; the fallback for anything fuzzy watch cannot cover. |
| P2-6 | Batch instance registration | `NamingService.batchRegisterInstance(...)` (`BatchInstanceRequest`, type `BATCH_REGISTER_INSTANCE`) | Missing; `BatchInstanceRequest` already exists on the wire | Registers many instances of one service in one round trip. `batchDeregisterInstance` is a client-side loop in Java, so only registration is a real gap. |
| P2-7 | AI agent batch watch over HTTP long-poll | `AgentWatchBatchRequest`/`Response`, `AgentWatchTransportRouter` | Missing; only the gRPC watch is implemented | The watch path for deployments where the gRPC push channel is unavailable. |
| P2-8 | Encrypted configuration snapshot at rest | `LocalEncryptedDataKeyProcessor` and the encryption filter | Missing; snapshots are plaintext | A process that fails over to disk writes secrets there in the clear. |
| P2-9 | Address-server discovery | `PropertyKeyConst.ENDPOINT`, `ServerListManager` address-server polling | Missing; server addresses are configured explicitly | Deployments that hand out the server list through an address server cannot use this client. |
| P2-10 | Server status | `ConfigService.getServerStatus()`, `NamingService.getServerStatus()` | Missing; the transport state exists but is not exposed | A health check needs to answer "am I connected" without issuing a request. |
| P3-11 | `NamingMaintainService` | `createService`, `updateService`, `deleteService`, `getService`, `updateInstance` | Missing by design | Administrative API under `/v3/admin/ns/*`; needs a bearer token even with auth disabled. It is not part of a runtime client. |
| P3-12 | AI administration and console models | MCP/agent drafts, labels, versions, import, resource search, pipelines, registry models, overview | Missing by design | Reachable through `maintainer-client` or the console API, not through the runtime `AiService`. |
| P3-13 | Spring and JVM idioms | `@NacosValue`, `@NacosConfigurationProperties`, JMX, logging adapters, `NacosFactory` property keys | Replaced, not ported | This library integrates through `IConfiguration`, dependency injection, `ILogger` and OpenTelemetry instead. |
| P3-14 | AI HTTP transport with automatic gRPC fallback | `McpTransportRouter`, `PromptTransportRouter`, `AgentTransportRouter`, `AiHttpClientProxy` | Documented non-goal | Automatic transport selection needs a server that advertises the HTTP publication contract; the gRPC path is the one Nacos 3.x actually routes. See `protocol-notes.md`. |

## P0-1: configuration fuzzy watch

An exact listener names one configuration. A fuzzy watch names a pattern, and the server keeps the
matching **set of group keys** in sync with the client:

```text
namespaceId >> groupPattern >> dataIdPattern
```

Pattern forms are `*`, `prefix*`, `*suffix`, `*text*` and a literal; a `*` in the middle of a
segment matches nothing, exactly as in `FuzzyGroupKeyPattern`. Group keys on the wire are
`dataId+group[+tenant]` with `+` escaped as `%2B`, the same codec as `GroupKey`.

The client sends `ConfigFuzzyWatchRequest{groupKeyPattern, receivedGroupKeys, watchType,
initializing}` and receives:

- `ConfigFuzzyWatchSyncRequest{groupKeyPattern, contexts[{groupKey, changedType}], syncType,
  totalBatch, currentBatch}` during initialisation (`FUZZY_WATCH_INIT_NOTIFY`), on a later diff
  (`FUZZY_WATCH_DIFF_SYNC_NOTIFY`), and a final `FINISH_FUZZY_WATCH_INIT_NOTIFY` that ends the
  initial phase;
- `ConfigFuzzyWatchChangeNotifyRequest{groupKey, changeType}` after initialisation, where
  `changeType` is `ADD_CONFIG`, `DELETE_CONFIG` or `CONFIG_CHANGED`.

`receivedGroupKeys` is the set the client already holds, which is what lets the server send a diff
instead of the whole match. The set is re-sent on every reconnect, because the server forgets the
registration with the connection, and every three minutes as a full resync so a missed push cannot
leave the set permanently wrong.

The .NET surface is an `IAsyncEnumerable` per pattern, mirroring `ListenAsync`:

```csharp
IAsyncEnumerable<NacosConfigWatchEvent> WatchAsync(
    string dataIdPattern,
    string groupPattern,
    CancellationToken ct = default);
```

with `Added`, `Modified`, `Removed` and a single `InitialSyncCompleted` marker per subscription.

Implemented. Three details are worth recording because they differ from a naive reading of the
protocol:

- The server ends the initial catch-up with `FINISH_FUZZY_WATCH_INIT_NOTIFY`: immediately when the
  diff is empty, and after the last batch has been acknowledged otherwise
  (`FuzzyWatchSyncNotifyCallback`). The client also ends the phase at the last
  `FUZZY_WATCH_INIT_NOTIFY` batch, so a caller does not wait for that extra round trip and a lost
  marker cannot leave it hanging; a marker arriving later changes nothing, because the subscriptions
  are already initialised.
- The server tracks **membership, not content**: `ConfigFuzzyWatchChangeNotifier` derives
  `CONFIG_CHANGED` from "the configuration still exists" and `DELETE_CONFIG` from "it no longer
  does", and `syncGroupKeyContext` only notifies when the key is not already in the pattern's set. A
  content change to a configuration that already matches is never pushed. `CONFIG_CHANGED` for a key
  the watch does not hold is reported as `Added` — the only way a creation can be recognised, and
  what Java does too, since it maps both `ADD_CONFIG` and `CONFIG_CHANGED` to an addition. Java drops
  a change push about a key it already holds; this client reports `Modified` for it instead so a
  push is never silently discarded, though no current server sends one. `ListenAsync` remains the API
  for content.
- `CANCEL_WATCH` is sent for the last subscriber only, and only while connected: with no connection
  the server has already dropped the watch with it.

## P0-2: naming fuzzy watch

The naming protocol is the same shape with service keys (`namespace@@group@@service`) and
`ADD_SERVICE`, `DELETE_SERVICE`, `INSTANCE_CHANGED`, `HEART_BEAT`. `INacosNamingClient.WatchAsync`
has the same two overloads as the configuration watch and returns `NacosServiceWatchEvent`, whose
kind is `Added`, `InstancesChanged`, `Removed` or `InitialSyncCompleted`. The group-only overload
requires the token, for the same ambiguity reason as configuration.

- The set is the server's, keyed by service key. A late subscriber is seeded from it, and a
  `CANCEL_WATCH` is sent for the last subscriber only while connected.
- `NacosNamingOptions.FuzzyWatchResyncInterval` (3 minutes) re-sends every watch, and a reconnect
  re-sends it with the keys the client still holds; `initializing` is `!registration.Initialized`.
- **The server tracks membership, not instances.** `syncServiceContext` returns `false` for
  `INSTANCE_CHANGED`, so a service whose instances change while it stays matched is not pushed;
  `ADD_SERVICE` and `DELETE_SERVICE` follow the first and last instance. `SubscribeAsync` remains the
  API for instances.
- `INSTANCE_CHANGED` is mapped defensively: a known service becomes `InstancesChanged` (Java folds
  it into an add and loses the distinction), an unknown one becomes `Added`; `HEART_BEAT` is ignored.
- `DELETE_SERVICE` removes the key from the set. Java's sync handler adds it back for every context
  type, which leaves its set wrong after a delete; this client follows the configuration watch and
  removes it.

## P1-3: naming selectors

In Nacos 3.x a selector exists on `subscribe`/`unsubscribe` only. The `getAllInstances(..., Selector)`
and `selectOneHealthyInstance(..., Selector)` overloads, and the `client.naming.selector` package
with its CMDB-label and expression selectors, were removed upstream; they are not reproduced here.
What remains is `com.alibaba.nacos.client.naming.selector.NamingSelectorFactory`, and this library
mirrors it in `NacosInstanceSelectors`:

| Java | Here | Keeps |
| --- | --- | --- |
| `EMPTY_SELECTOR` | `NacosInstanceSelectors.All` | everything, returning the list it was given |
| `HEALTHY_SELECTOR` | `NacosInstanceSelectors.Healthy` | `Instance::isHealthy` — the flag alone, not `IsSelectable` |
| `newClusterSelector(clusters)` | `Clusters(params string[])` | the named clusters; an empty set keeps everything |
| `newIpSelector(regex)` | `IpAddress(string)` | addresses matching the whole string, as `Pattern.matches` does |
| `newMetadataSelector(map, isAny)` | `Metadata(map, matchAny)` | all (default) or any of the entries; an empty map keeps everything |

`NacosInstanceSelectors.From(Func<NacosInstanceSelectorContext, IReadOnlyList<NacosInstance>>)` is the
counterpart of implementing the Java interface, and the context carries the service and the cluster
filter as well as the instances.

- **The narrowing is client-side.** The selector overload subscribes with an empty cluster filter —
  `Constants.NULL` in Java — so the server sends every instance and the filter decides. The plain
  clusters overload still asks the server to narrow, which is the cheaper option when the cluster is
  the only criterion.
- **Filtering and de-duplication are per consumer.** One server subscription and its replay are
  shared by every consumer of a service, including consumers that narrow it differently. Each
  consumer keeps the selected view it was last given and is notified only when that view changes, so
  a change to a filtered-out instance does not wake it. `Subscription.LastRefTime` advances only
  when at least one consumer was notified.
- **The first element is filtered too.** A consumer initialises from it, so it has the same shape as
  every later element. A selector that throws there fails the caller's own call, which is where its
  bug belongs.
- **A throwing selector is contained on the push path.** It is reported as event `5020` and the
  consumer keeps the view it last delivered; the failure is not rethrown, because that path is
  shared with the other consumers of the service.
- **The overload takes no token.** `SubscribeAsync(service, selector)` supplies the token through
  `WithCancellation` when the stream is enumerated. A second subscription overload ending in an
  optional token would not be distinguishable from the clusters overload, so the two differ by their
  second argument instead.

## P1-4: configuration filters and encrypted content

Java runs every publish and query through a chain of `IConfigFilter`s. The built-in one is
`ConfigEncryptionFilter`, which encrypts on publish, decrypts on query, and stores the data key
locally so an encrypted snapshot can be read back. The wire already carries
`ConfigQueryResponse.EncryptedDataKey`; this library currently ignores it, so an encrypted
configuration arrives as ciphertext with the key discarded.

## P1-5: service list paging

`ServiceListRequest{pageNo, pageSize, selector}` returns a page of service names. The server
handler filters strictly by group and currently ignores the selector, so the .NET surface will
expose paging and leave the selector out until the server honours it.

## P2 items

- **Batch registration** is one `BatchInstanceRequest` with `type = BATCH_REGISTER_INSTANCE`; the
  server accepts a list of instances of a single service. Deregistration stays a loop, as in Java.
- **AI batch watch** is the HTTP long-poll fallback: a request carrying the client watch ids and
  the fingerprints the client holds, answered when one of them changes. It only makes sense
  together with P3-14, so it is ranked below the gRPC-only watch this library already has.
- **Snapshot encryption** reuses the filter's key material; it is only meaningful once P1-4
  exists.
- **Address-server discovery** replaces the static server list with an HTTP endpoint that answers
  the current list. It is a deployment concern, not a protocol one.
- **Server status** is a read of the transport state. Java returns a string; here it will be a
  typed enum so a health check does not have to parse it.

## Standing non-goals

These are not gaps to close; they are surfaces this library deliberately does not reproduce.

| Surface | Reason |
| --- | --- |
| `NamingMaintainService` and `/v3/admin/ns/*` | Administrative API, not a runtime client; needs a bearer token. |
| AI drafts, labels, versions, import, resource search, pipelines, MCP registry models | `maintainer-client` and console surface. The runtime `AiService` is already covered. |
| Spring annotations, JMX, logging adapters | Replaced by `IConfiguration`, DI, `ILogger` and OpenTelemetry. |
| Automatic gRPC to HTTP AI transport fallback | Requires the HTTP publication contract; see `protocol-notes.md`. |
| Nacos 2.x protocol compatibility | The project targets Nacos 3.x only. |

## Already at parity

The comparison also confirmed what is already covered, so it is not re-opened by later slices:

- **Configuration**: read, publish, compare-and-swap publish, remove, exact listeners with
  reconnect replay, server-push handling, local snapshot failover, MD5 verification and the
  `304` fast path.
- **Naming**: register, deregister, ephemeral and persistent instances, subscribe and unsubscribe,
  instance queries with cluster and health filters, reconnect replay of registrations and
  subscriptions, local snapshot failover, load balancing.
- **Lock**: acquire with wait semantics, reentrancy, watchdog renewal, release and notifications.
- **AI registry**: prompt, skill, agent-spec, MCP server, agent-card and RAD agent reads and
  subscriptions; MCP release with `createDraft`; endpoint registration with reconnect replay;
  agent publish/search/discover; RAD watch over gRPC; local snapshot failover.
- **Client basics**: multi-server failover, ability negotiation, TLS, authentication and token
  refresh, metrics and tracing through `NacosDiagnostics`.

Metrics deserve a note: Java registers Micrometer meters; this library publishes an
`ActivitySource` and a `Meter` under `Lolitacoding.Nacos`, which is the .NET equivalent and needs
no port.
