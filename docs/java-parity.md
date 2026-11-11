# Java SDK parity

This document records what the official Nacos Java client can do that this library could not,
ranked by how much a .NET application is hurt by the gap. It is the working list the parity
slices are cut from; each implemented slice removes a row and is recorded in `PLAN.md`.

## What was compared

The comparison is surface by surface, against a local checkout of the upstream `nacos-develop`
branch (see `.gitignore`: it is a reference clone for reading the protocol, not a build dependency):

| Java module | What it contributes |
| --- | --- |
| `api` | `ConfigService`, `NamingService`, `NamingMaintainService`, `LockService`, `AiService` and the listener/selector/model types they expose. |
| `client` | The implementations: gRPC proxies, caches, redo services, filters, fuzzy watch holders, AI transports. |
| `client-basic` | `NacosFactory` and the property keys a client application sets. |
| `maintainer-client` | `AiMaintainerService` and the naming maintainer client: the administrative surface, not the runtime client. |
| `specs/en` | The normative description of each protocol, used to decide what a gap actually is. |

Java spells the AI module `Ai` — `AiService`, `AiConstants`, `AiRemoteConstants` — while this
library spells the same acronym `AI` (`Lolitacoding.Nacos.AI`, `INacosAIClient`). Two-letter
acronyms are upper-cased in .NET, and the neighbouring `Microsoft.Extensions.AI` uses the same
spelling. Java names are quoted verbatim here and in the source comments.

The scope is deliberately **Nacos 3.x runtime client behaviour**. Anything that only exists to
administer a server, to render the console, or to integrate with Spring or the JVM is listed at
the bottom with the reason it is not implemented here.

## Ranked gaps

| # | Capability | Java entry point | Status here | Why it matters |
| --- | --- | --- | --- | --- |
| P0-1 | Configuration fuzzy watch | `ConfigService.fuzzyWatch(...)`, `fuzzyWatchWithGroupKeys(...)`, `cancelFuzzyWatch(...)` | **Done** — `INacosConfigClient.WatchAsync`, `NacosConfigWatchEvent`, `Config.FuzzyWatchResyncInterval` | The only protocol capability this client explicitly told the server it could not do. It is how a process discovers configuration keys it does not know by name. |
| P0-2 | Naming fuzzy watch | `NamingService.fuzzyWatch(...)`, `fuzzyWatchWithGroupKeys(...)`, `cancelFuzzyWatch(...)` | **Done** — `INacosNamingClient.WatchAsync`, `NacosServiceWatchEvent`, `Naming.FuzzyWatchResyncInterval` | Discovers services by pattern and keeps that set current, without polling a service list. |
| P1-3 | Client-side naming selectors | `NamingService.subscribe(serviceName, groupName, NamingSelector, listener)` and the matching `unsubscribe` | **Done** — `INacosNamingClient.SubscribeAsync(NacosServiceKey, INacosInstanceSelector)`, `NacosInstanceSelectors`, `NacosInstanceSelectorContext` | Zone and label routing is the normal way to keep traffic local; without it every consumer sees every instance. |
| P1-4 | Configuration filter chain and encrypted data key | `ConfigService.addConfigFilter(IConfigFilter)`, built-in `ConfigEncryptionFilter`, `ConfigQueryResponse.EncryptedDataKey` | **Done** — `INacosConfigFilter`, `INacosConfigClient.AddConfigFilter`, `NacosConfigEncryptionFilter`, `INacosConfigEncryptionProvider`, `NacosConfigResult.EncryptedDataKey` | The published extension point for encrypting or transforming configuration on the way in and out. Dropping the key also makes an encrypted configuration unreadable. |
| P1-5 | Service list paging | `NamingService.getServicesOfServer(pageNo, pageSize, groupName, selector)` | **Done** — `INacosNamingClient.GetServicesAsync`, `NacosServiceListPage` (selector omitted: the server ignores it) | Enumerates a namespace a page at a time; the fallback for anything fuzzy watch cannot cover. |
| P2-6 | Batch instance registration | `NamingService.batchRegisterInstance(...)` / `batchDeregisterInstance(...)` (`BatchInstanceRequest`, type `BATCH_REGISTER_INSTANCE`) | **Done** — `INacosNamingClient.RegisterInstancesAsync` / `DeregisterInstancesAsync` | Registers many instances of one service in one round trip. |
| P2-7 | AI agent batch watch over HTTP long-poll | `AgentWatchBatchRequest`/`Response`, `AgentWatchTransportRouter` | **Done** — `NacosAIOptions.AgentWatchTransport`, `NacosAgentWatchTransport`, `NacosAgentDiscoveryCanonicalizer` | The watch path for deployments where the gRPC push channel is unavailable. |
| P2-8 | Encrypted configuration snapshot at rest | `LocalEncryptedDataKeyProcessor` and the encryption filter | **Done** — closed by P1-4; a snapshot keeps the stored ciphertext and the wrapped key | A process that fails over to disk must not write the plaintext of an encrypted configuration there. |
| P2-9 | Address-server discovery | `PropertyKeyConst.ENDPOINT`, `ServerListManager` address-server polling | **Done** — `NacosOptions.Endpoint` and its five companions, `NacosServerList`, `NacosEndpointServerListProvider` | Deployments that hand out the server list through an address server cannot use this client. |
| P2-10 | Server status | `ConfigService.getServerStatus()`, `NamingService.getServerStatus()` | **Done** — `INacosConfigClient.ServerStatus` / `INacosNamingClient.ServerStatus`, `NacosServerStatus` | A health check needs to answer "am I connected" without issuing a request. |
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
`NacosConfigWatchEvent` is a union of `NacosConfigWatchAdded`, `NacosConfigWatchModified`,
`NacosConfigWatchRemoved` and `NacosConfigWatchCompleted`, so a `switch` over an event is
exhaustive and the marker cannot carry a configuration by construction; `Kind`, `Key` and
`GroupKeyPattern` remain as convenience properties.

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
has the same two overloads as the configuration watch and returns `NacosServiceWatchEvent`, a union
whose cases are `NacosServiceWatchAdded`, `NacosServiceWatchInstancesChanged`,
`NacosServiceWatchRemoved` and `NacosServiceWatchCompleted` (`Kind` still reports `Added`,
`InstancesChanged`, `Removed` or `InitialSyncCompleted`). The group-only overload requires the token,
for the same ambiguity reason as configuration.

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

Java runs every publish and query through a chain of `IConfigFilter`s
(`ConfigFilterChainManager`), and `ConfigEncryptionFilter` is one of them. This library mirrors the
extension point as `INacosConfigFilter`:

| Java | Here |
| --- | --- |
| `ConfigService.addConfigFilter(IConfigFilter)` | `INacosConfigClient.AddConfigFilter(INacosConfigFilter)` |
| `IConfigFilter.doFilter(request, response, chain)` | `INacosConfigFilter.InvokeAsync(NacosConfigFilterContext, CancellationToken)` |
| `ConfigRequest` / `ConfigResponse` | `NacosConfigFilterContext` with `Stage` = `Publish` / `Read` |
| `EncryptionPluginService` SPI | `INacosConfigEncryptionProvider` |
| `ConfigEncryptionFilter` | `NacosConfigEncryptionFilter`, seeded from `NacosConfigOptions.EncryptionProviders` |
| `ConfigQueryResponse.encryptedDataKey` | `NacosConfigResult.EncryptedDataKey` |

The chain is flat. Filters run in `Order` order, lowest first, and filters with equal orders keep
their insertion order; each sees the previous filter's mutations. There is no `next` delegate — a
filter that must stop the operation throws. Adding a filter whose `Name` is already registered is
ignored, so the first registration wins, as in Java.

Encryption follows the Java rules exactly:

- A data id opts in by starting with `cipher-` but not being exactly `cipher-`.
- The algorithm is the segment between the first and second `-`:
  `cipher-AES-application.json` selects the provider whose `AlgorithmName` is `AES`. Matching is
  ordinal, so `aes` does not select `AES`.
- No provider for that name means pass-through with a warning (event `4019`). A missing plugin must
  not turn a publish into an outage.
- On publish the filter generates a data key, encrypts the content, and puts the provider-wrapped
  key in `additionMap["encryptedDataKey"]` (`""` when nothing was encrypted). Both the content type
  and the key are fields the server reads out of the addition map, not top-level wire fields.
- On read the filter decrypts the stored content with the stored key and leaves
  `EncryptedDataKey` as the stored (wrapped) key. Java replaces it with the plaintext key
  internally; exposing that here would leak the key through `NacosConfigResult.EncryptedDataKey`.

Where the chain runs:

- **Publish** runs it before the request is sent. The snapshot keeps the filtered (for an encrypted
  configuration, ciphertext) content and the wrapped key, because that is the pair the server
  stored.
- **Read** runs it after the raw MD5 is verified and after the raw pair is written to the snapshot.
  This includes a read served from a local snapshot. The returned `Md5` stays the hash of the stored
  value, so change detection is unaffected.
- **Listener push** runs it after the raw MD5 comparison and the snapshot update. A filter that
  throws is logged (event `4020`) and the delivery is skipped; the subscription survives. On an
  explicit read or publish the exception reaches the caller.

`NacosConfigResult.EncryptedDataKey` is the stored key, `null` for a configuration that never had
one. The snapshot metadata is versioned (`v2`) and base64-encodes both the content type and the key;
a file written before this version still loads, with no key.

Encryption is enabled through `NacosConfigOptions.EncryptionProviders`, because .NET has no
equivalent of the Java service loader for arbitrary assemblies. `AddConfigFilter` remains the entry
point for every other transformation, and for assembling the chain explicitly.

## P1-5: service list paging

`ServiceListRequest{pageNo, pageSize}` returns a page of service names. The .NET surface is
`GetServicesAsync(pageNo, pageSize, groupName, ct)` returning `NacosServiceListPage`:

| Java | Here |
| --- | --- |
| `getServicesOfServer(pageNo, pageSize)` | `GetServicesAsync(pageNo, pageSize)` (group defaults to `DEFAULT_GROUP`) |
| `getServicesOfServer(pageNo, pageSize, groupName)` | `GetServicesAsync(pageNo, pageSize, groupName)` |
| `getServicesOfServer(..., AbstractSelector)` | Not reproduced — see below |
| `ListView<String>.getCount()` / `getData()` | `NacosServiceListPage.Count` / `Services` |

- `Count` is the group total the server computed **before** paging, so `ceil(Count / pageSize)` is
  the number of pages. A page beyond the last one is not an error: it has no names and still
  reports the total.
- Each name is bare. The server pages over `group@@service` keys and then strips the prefix
  (`ServiceUtil.pageServiceName`), so the group passed to the call is the group of every name.
- Group matching is exact; there is no wildcard. Use `WatchAsync(groupPattern)` to follow a pattern.
- `pageNo` and `pageSize` below one are rejected with `ArgumentOutOfRangeException`. Java sends them
  as given; the server clamps a non-positive page number to the first page and fails on a
  non-positive page size inside `subList`, so validating at the call site turns both into one clear
  error.
- There is no local-cache failover, unlike `GetInstancesAsync`: the list describes the namespace, and
  serving a stale copy during an outage would silently hide services created in the meantime.
- **The selector is left out.** The Java client can send a label selector, but the Nacos 3.x
  `ServiceListRequestHandler` ignores the field (`// TODO select service by selector`), and the CMDB
  label/expression selectors were removed from the 3.x client. Exposing an argument that changes
  nothing would be worse than omitting it; when the server implements the filter, a selector
  overload can be added without changing this method.

## P2-6: batch instance registration

`NamingService.batchRegisterInstance(serviceName, groupName, instances)` sends one
`BatchInstanceRequest{namespace, serviceName, groupName, type = "batchRegisterInstance", instances}`.
The .NET surface is `RegisterInstancesAsync(service, instances, ct)`, with
`DeregisterInstancesAsync` for the companion operation:

| Java | Here |
| --- | --- |
| `batchRegisterInstance(serviceName, groupName, List<Instance>)` | `RegisterInstancesAsync(service, instances)` |
| `batchDeregisterInstance(serviceName, groupName, List<Instance>)` | `DeregisterInstancesAsync(service, instances)` |
| `NamingUtils.checkInstanceIsEphemeral` / `checkInstanceIsLegal` | `ArgumentException` at the call site |

- The server keeps **one published instance set per service per connection**
  (`AbstractClient.publishers`, keyed by service). A batch publishes the whole set in one request;
  a later single registration or batch replaces it, and a deregistration removes it. A deployment
  that must publish several instances of one service therefore either batch-registers them from
  one connection or opens one connection per instance. The client's replay state is keyed the same
  way, so after a reconnect it replays the last registration for each service rather than a
  mixture of superseded ones. A refused registration is not replayed, but the registration it
  replaced is restored to the replay state: that is the set the server still holds, and forgetting
  it would lose an instance at the next reconnect.
- Only ephemeral instances are accepted, as in Java: the server's batch handler
  (`BatchInstanceRequestHandler`) is built on `EphemeralClientOperationServiceImpl`, and the CP
  store has no batch path (`// TODO PersistentClientOperationServiceImpl Nacos batchRegister`).
- An empty batch is refused. Java sends it with a warning; on the server it replaces the published
  set with nothing, which is a deregistration spelled as a registration.
- **Batch deregistration is not a loop in 3.x.** `NamingGrpcClientProxy.batchDeregisterService`
  takes the batch this client last registered, subtracts the named instances by IP and port, and
  publishes the remainder as a new batch. It refuses a service that was not batch-registered
  (`INVALID_PARAM`). `DeregisterInstancesAsync` does the same and throws `InvalidOperationException`
  in that case; when nothing remains it sends an ordinary deregistration instead of Java's empty
  batch, which leaves the same server state without a lingering empty published set.

## P2-7: AI agent batch watch over HTTP long-poll

The gRPC watch needs the connection to accept a server-initiated hint. A deployment can lose that
ability without losing discovery — a proxy that drops the push stream, a server that states no
`radWatchV1` — so Java adds a second way to follow an agent: one HTTP request carrying the client's
**whole** watch set, held open by the server until one of the fingerprints the client sent no longer
matches, or until the timeout elapses.

The .NET surface is the same `WatchAgentAsync` with a transport preference:

| Java | Here |
| --- | --- |
| `AgentTransportMode` (`GRPC`, `HTTP`, `AUTO`) on the AI client config | `NacosAIOptions.AgentWatchTransport` (`NacosAgentWatchTransport.Auto`, `Grpc`, `Http`, `Polling`) |
| `AgentWatchBatchRequest` / `AgentWatchBatchResponse` | internal `AgentWatchBatchItem` / `AgentWatchBatchResponse` |
| `AgentDiscoveryCanonicalizer` / `EndpointCanonicalizer` | `NacosAgentDiscoveryCanonicalizer` / `NacosAgentEndpointCanonicalizer` |
| `PollingAgentWatchTransport` | `Polling` mode: re-read at `AI.CacheRefreshInterval` |

- `Auto` (the default) installs the gRPC watch when the connection advertises `radWatchV1`, falls
  back to the batch when the gRPC install fails for a reason another transport could fix, and falls
  back to polling when neither is usable. `Grpc` refuses a server without the ability (501) instead
  of falling back, `Http` never subscribes over gRPC, and `Polling` never installs a watch at all.
- The batch is `POST /v3/client/ai/agents/watch` as `application/x-www-form-urlencoded`, with
  `generation`, `timeoutMillis` and `watches` (a JSON list of
  `{clientWatchId, discoveryRequest, materializedFingerprint}`), plus the `X-Nacos-Client-Id` and
  `Request-Module: ai` headers. It is answered with the usual v3 `Result` envelope around
  `{generation, changed, changedClientWatchIds}`.
- The fingerprint is the part that has to be exact: `materializedFingerprint` is
  `sha256-canonical-json-v1:` followed by the SHA-256 of the server's canonical JSON of the result
  the caller holds. The canonical form is a port of `AgentDiscoveryCanonicalizer`, including its
  key ordering, number normalisation, string escaping and URI canonicalisation, and it is pinned by
  a test against the vector the Java implementation produces. A fingerprint the client computes
  differently from the server would make every poll report a change.
- Only the changes the server names are acted on: the manager reads the state again, and the
  fingerprint of that read is handed back with `Update`. A watch whose change is still being read is
  left out of the next batch, so the same projection is not reported twice.
- A watch set change increments the generation and cancels the in-flight request; an answer carrying
  another generation is discarded and retried, because its identifiers may describe a set the client
  no longer has.

## P2-8: encrypted configuration snapshot at rest

This needed no new code: P1-4 already made the snapshot store keep exactly what the server
returned. A snapshot record holds the ciphertext together with the wrapped data key
(`EncryptedDataKey`), and the key is unwrapped in memory only while a read is being served, so a
process that fails over to its local cache never writes the plaintext of an encrypted
configuration to disk.

Difference from Java: `LocalEncryptedDataKeyProcessor` keeps the wrapped key in its own file next
to the cache; here the key travels inside the snapshot file. One file cannot be paired with the
wrong cached entry, and copying or deleting a snapshot takes its key with it.

## P2-9: address-server discovery

Java: `PropertyKeyConst.ENDPOINT`, `ENDPOINT_PORT`, `ENDPOINT_CONTEXT_PATH`, `ENDPOINT_QUERY_PARAMS`,
`ENDPOINT_REFRESH_INTERVAL` and `ServerListManager`'s address-server provider. The reference
implementation is `EndpointServerListProvider`.

- **Options**: six flat settings on `NacosOptions` — `Endpoint`, `EndpointPort` (8080),
  `EndpointContextPath` (defaults to `ContextPath`), `EndpointServerListName` (`serverlist`),
  `EndpointQueryParams`, `EndpointRefreshInterval` (30 s). A configuration with neither an endpoint
  nor a server address is rejected when the client is built, as is an endpoint that looks like a
  URL, contains whitespace, or carries a path.
- **URL**: `http://{Endpoint}:{EndpointPort}{contextPath}/{EndpointServerListName}`, with
  `namespace=` added only when the caller chose a namespace and `EndpointQueryParams` appended
  verbatim afterwards. This is the shape the Java client requests.
- **Precedence**: an endpoint replaces the configured addresses rather than adding to them; setting
  both logs event 1008 and the endpoint wins, and the addresses are not parsed at all, so a stale
  entry cannot stop a client that will never dial it. Client creation never blocks on the endpoint:
  the list starts empty and the connections wait for it for up to `ConnectionTimeout`.
- **Seeding**: five attempts, the nth followed by a delay of `n * 100 ms`. Java throws when all
  five fail; here event 1007 is logged and the poll loop keeps trying, so a client whose address
  server is slower than its own startup recovers instead of failing forever. The connection that
  needs an address reports the failure, naming the address server URL.
- **Refresh**: a `PeriodicTimer` on `EndpointRefreshInterval`, with at most one applied change every
  30 seconds regardless of the interval. A flapping address server must not make the client
  reconnect in a tight loop; the Java client has the same floor. While the gate is closed the
  endpoint is not fetched at all.
- **Applying a change**: `NacosServerList` replaces its snapshot only when the set of addresses
  differs (order-insensitive) and raises `Changed`. Every connection, the token provider and the AI
  HTTP client take one snapshot per attempt, so a replacement mid-failover cannot address a server
  twice or skip one. A connected connection reconnects only when its current server has left the
  list — an added node costs no healthy connection and is picked up by the next natural reconnect,
  which is what `onServerListChange` does. The membership of a freshly dialled server is re-checked
  after the handshake, so a server withdrawn while it was being dialled is not kept.
- **Parsing**: one address per line, blank lines skipped, a bare host defaulting to 8848, and a line
  that starts with a scheme keeping that scheme (so `https://` still means TLS). One unusable line
  rejects the whole answer and the previous list stays in force: half a cluster is worse than the
  list already in use. An empty answer is treated the same way.
- **Deviations from Java**: no `${...}` placeholder resolution in the endpoint settings; a published
  line may carry an explicit `https://` scheme (Java always prepends `http://`); the namespace is
  percent-encoded rather than interpolated raw, and is only sent when the caller set one.
- **Log events** 1003–1008: unreachable, rejected status, empty answer, list replaced, seeding gave
  up, endpoint overrides the configured addresses.

## P2 items

All three P2 rows are closed: P2-8 by P1-4, P2-9 and P2-10 by their own slices. What remains is the
P3 list, which is closed by design rather than by work.

## Standing non-goals

These are not gaps to close; they are surfaces this library deliberately does not reproduce.

| Surface | Reason |
| --- | --- |
| `NamingMaintainService` and `/v3/admin/ns/*` | Administrative API, not a runtime client; needs a bearer token. |
| AI drafts, labels, versions, import, resource search, pipelines, MCP registry models | `maintainer-client` and console surface. The runtime `AiService` is already covered. |
| Spring annotations, JMX, logging adapters | Replaced by `IConfiguration`, DI, `ILogger` and OpenTelemetry. |
| Automatic gRPC to HTTP AI transport fallback for discovery, MCP and prompts | Requires the HTTP publication contract; the agent watch is the one surface with an HTTP transport, and it is covered by P2-7. See `protocol-notes.md`. |
| Nacos 2.x protocol compatibility | The project targets Nacos 3.x only. |

## Already at parity

The comparison also confirmed what is already covered, so it is not re-opened by later slices:

- **Configuration**: read, publish, compare-and-swap publish, remove, exact listeners with
  reconnect replay, server-push handling, local snapshot failover, MD5 verification and the
  `304` fast path.
- **Naming**: register, deregister, batch registration and deregistration, ephemeral and persistent
  instances, subscribe and unsubscribe, instance queries with cluster and health filters, paged
  service listing, reconnect replay of registrations and subscriptions, local snapshot failover,
  load balancing.
- **Lock**: acquire with wait semantics, reentrancy, watchdog renewal, release and notifications.
- **AI registry**: prompt, skill, agent-spec, MCP server, agent-card and RAD agent reads and
  subscriptions; MCP release with `createDraft`; endpoint registration with reconnect replay;
  agent publish/search/discover; RAD watch over gRPC; local snapshot failover.
- **Client basics**: multi-server failover, ability negotiation, TLS, authentication and token
  refresh, metrics and tracing through `NacosDiagnostics`.

Metrics deserve a note: Java registers Micrometer meters; this library publishes an
`ActivitySource` and a `Meter` under `Lolitacoding.Nacos`, which is the .NET equivalent and needs
no port.
