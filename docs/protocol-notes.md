# Protocol notes

These notes summarise the Nacos wire behaviour this client relies on, and how each detail maps to
the implementation. They are a companion to
[the verified gRPC specification](protocol-grpc-spec.md), which records the upstream sources
(`github.com/alibaba/nacos`) each fact was read from. When the two disagree, the spec wins — it
carries the file references and the commit hashes.

## Ports

Nacos exposes a gRPC endpoint on the HTTP port plus a fixed offset.

| Constant | Value | Meaning |
| --- | --- | --- |
| `NacosConstants.DefaultPort` | 8848 | HTTP OpenAPI. |
| `NacosConstants.GrpcPortOffset` | 1000 | 8848 → 9848 for gRPC. |

The client derives the gRPC endpoint from each entry in `ServerAddresses` by adding the offset. See
`NacosServerAddress`.

The offset applies to TLS as well: a Nacos server negotiates TLS and plaintext on the same SDK gRPC
port (port unification), so an `https://` address dials the same `9848`, not a separate TLS port.
The endpoint one port above the SDK gRPC port (`main + 1001`) is the cluster-to-cluster gRPC port,
which an SDK client never dials.

## Address-server discovery

An address server is a plain HTTP resource that publishes the current list of Nacos servers. It is
not part of the Nacos protocol — any deployment can serve it — so the contract that matters is the
URL the client requests and the shape of the answer.

```text
GET http://{endpoint}:{endpointPort}{contextPath}/{serverListName}[?namespace={id}][&{queryParams}]
Request-Module: default
```

The defaults are `endpointPort` 8080, `contextPath` `/nacos`, `serverListName` `serverlist`, so the
common request is `GET http://address-server:8080/nacos/serverlist`. The `namespace` parameter is
only added when the caller configured a namespace, and it is percent-encoded. `queryParams` is
appended verbatim after it, already encoded. `Request-Module` is informational: the Java client
sends the module it fetches for, and the resource does not require it.

The answer is `text/plain` with one server per line; blank lines are ignored:

| Line | Meaning |
| --- | --- |
| `10.0.0.1` | `10.0.0.1:8848`, the default main port. |
| `10.0.0.1:8858` | Port 8858, gRPC at 9858. |
| `https://nacos.example.com:8443` | TLS on the main port, gRPC at 9443. |

One unusable line rejects the whole answer, and so does an empty answer: the list already in use
stays in force rather than being replaced by half a cluster.

The list is fetched five times at startup (the nth attempt 100 ms after the previous one) and then
once per `EndpointRefreshInterval` (30 s by default), with at most one applied change every 30
seconds. A replacement is applied only when the set of addresses differs, ignoring order.

## The payload envelope

Every message is a protobuf `Payload`: a `Metadata` (with `type`, `clientIp` and a header map) and an
`Any` whose `value` is UTF-8 JSON of the message, not a protobuf-encoded message body.

- `metadata.type` is the routing key. The server sets it to the simple Java class name of the
  message, and this client mirrors that: `PayloadTypeRegistry.GetTypeName` returns the CLR type
  name, which already matches the Java simple name for every payload.
- `type_url` on the `Any` is deliberately left empty; the official clients never set it and neither
  side reads it.
- Headers live in `metadata.headers`. The token is carried under a header named literally
  `accessToken`, not an `Authorization: Bearer` header, on both gRPC and HTTP.

`PayloadCodec` performs the encoding and decoding; `PayloadTypeRegistry` is an explicit,
reflection-free map from type name to `JsonTypeInfo`, so the mapping survives trimming and Native
AOT.

The client is **liberal on input**: redundant `headers`, `requestId` and `module` fields that can
appear inside a body are ignored because `metadata` is authoritative. It is byte-compatible on
output for the fields the server asserts on.

## The handshake

The order is fixed by the protocol and is not negotiable:

1. A **unary** `ServerCheckRequest` is sent first. Its response carries the connection identifier and
   whether the server supports ability negotiation.
2. The bi-directional stream is opened (`ServerRequest` / `Request` in the upstream proto).
3. `ConnectionSetupRequest` is written as the **first frame** on that stream. It carries the
   client version, the tenant, labels and an **ability table**.
4. The server acknowledges with `SetupAckRequest`, which is a *request*-typed message pushed
   server→client and is **not** answered by the client.

The ability table must be non-null, or the server sends no acknowledgement at all and the client
cannot tell when the connection became usable. The acknowledgement wait is only performed when the
server reported `SupportAbilityNegotiation`.

This client advertises only abilities it actually implements:

```text
fuzzyWatch = false
lock       = false
```

Advertising a capability it cannot honour would be worse than admitting it is unsupported, because
the server would route the corresponding work to a client that drops it.

See `NacosGrpcConnection.ConnectToAsync` and `BuildAbilityTable`.

## Connection modules and labels

`ConnectionSetupRequest` carries a `labels` map, and Nacos reads the **`module`** key from it to
decide which server-side manager owns the connection. The naming, lock and AI servers each keep a
registry of the connections that declared their module and **ignore every other connection**:
`ConnectionBasedClientManager` (naming), `LockConnectionEventListener` and
`AiConnectionBasedClientManager` all compare the label before they accept a client.

A connection can therefore serve exactly one module, so this client opens **one gRPC connection per
module** (`NacosConnectionModule`) rather than multiplexing every feature over a single stream. Each
connection also sends `source = sdk`, which is how the server tells an SDK connection apart from a
cluster-internal one.

| Label | Value | Purpose |
| --- | --- | --- |
| `module` | `config` / `naming` / `lock` / `ai` | Routes the connection to the manager that owns it. |
| `source` | `sdk` | Marks the connection as an SDK client rather than a cluster peer. |
| `AppName` | the configured application name | Reported in the console and used by visibility plugins. |
| `ClientVersion` | `Nacos-DotNet/<version>` | Diagnostic only. |
| `namespaceId` | the configured namespace | Diagnostic; the namespace the server honours is `tenant`. |

The body of the request still carries the redundant `module` field (`"internal"` for a transport
message), which the server ignores because the labels are authoritative. A module may add its own
labels, such as the AI client's UUID, through the `extraLabels` argument; they are merged over the
standard ones.

Configuration and naming are connected eagerly because nearly every application uses them. The lock
and AI connections are opened on first use instead: an application that never takes a lock and never
asks the registry for anything would otherwise carry a handshake, a heartbeat and one socket per
cluster node for a module it never calls.

## Server-initiated messages

Within one module, Nacos multiplexes its messages over a single stream, so the read loop classifies
each incoming frame.

| Message | Handling |
| --- | --- |
| `SetupAckRequest` | Records the server's ability table; resolves the handshake. No reply. |
| `ClientDetectionRequest` | Answered with `ClientDetectionResponse`. A liveness probe; failing to answer makes the server unregister the connection. |
| `ConnectResetRequest` | Answered with `ConnectResetResponse`, then the client moves to the indicated server (or the next one). |
| `PushAckRequest` | Acknowledges a client push. No reply. |
| Unknown type | Logged and ignored. A newer server may push a type this client predates; that must not tear down a healthy connection. |
| Anything else | A domain push, dispatched to the registered handlers. |

A domain push is answered by the feature that owns it before the real work happens:

- `ConfigChangeNotifyRequest` → `ConfigChangeNotifyResponse`, then the content is fetched.
- `ConfigFuzzyWatchSyncRequest` → `ConfigFuzzyWatchSyncResponse`, and the diff is applied to the
  matched set inline (it only writes to channels, so the acknowledgement is not delayed).
- `ConfigFuzzyWatchChangeNotifyRequest` → `ConfigFuzzyWatchChangeNotifyResponse`, and the one key it
  names is applied to every local watch whose pattern matches it.
- `NotifySubscriberRequest` → `NotifySubscriberResponse`, and the payload already carries the
  instance list.
- `LockNotificationRequest` → `LockNotificationResponse`, and the waiter the notification names is
  released.
- `AgentDiscoveryNotifyRequest` → `AgentDiscoveryNotifyResponse`, and the watch it names is read
  again. A hint for a watch the client does not own is answered with `accepted: false` instead of
  being ignored, because the server re-delivers what it is not told to stop sending.

The first non-null response any handler produces is the one written back, exactly as the official
client does, and the remaining handlers still run. A handler that returns `null` — one that only
observes a push, like a configuration listener — produces no reply.

Handlers run on the read loop and are isolated: one that throws is logged without stopping the loop
or the other features sharing the connection. See `NacosGrpcConnection.DispatchServerRequestAsync`.

## Configuration specifics

- **Publish** is a `ConfigPublishRequest`. The content type is not a field of its own: the server
  reads it from the free-form `additionMap` under the key `"type"`.
- **Encrypted content carries its key beside it, not inside it.** On publish the wrapped data key is
  `additionMap["encryptedDataKey"]`, an empty string when the value is not encrypted, because the
  server reads it with `getAdditionParam("encryptedDataKey")`. On read the key is the top-level
  `ConfigQueryResponse.encryptedDataKey`. Encryption and decryption are client-side: the server
  stores and returns whatever bytes it was given, so a client without the matching provider sees
  ciphertext and the wrapped key. See `java-parity.md` P1-4.
- **The filter chain** runs on publish (request), on reads including a snapshot or failover read
  (response), and on listener pushes after the raw hash comparison. The MD5 the client compares is
  always the hash of the stored value, never of the filtered value, or every push would look new.
- **MD5 verification** compares the content hash against the value the server reports. A mismatch is
  an error, not a warning, because applying corrupted content would silently misconfigure the
  application. Controlled by `NacosConfigOptions.VerifyMd5`.
- **Listeners** use `ConfigBatchListenRequest` (note: the request class is `ConfigBatchListenRequest`;
  its *response* is `ConfigChangeBatchListenResponse`). The hash the client already holds is sent so
  the server can skip notifying about a change the client has already applied, and a change whose
  MD5 matches the last one delivered is suppressed. The response's `changedConfigs` list is what the
  reconnect replay acts on — see *Reconnection and state replay*.

### Conditional reads and the `304` answer

`ConfigQueryRequest.localMd5` (3.3) says "I already hold the version with this hash". The server
compares it against the stored value's hash before reading the content and answers
`ConfigQueryResponse` with `resultCode = 500`, `errorCode = 304` and no content when they match; the
reply still carries `md5`, `contentType` and `lastModified` for that version. Servers before 3.3
ignore the field and send the content, which is why the reply — not the server version — decides what
happens.

The client only sends the hash when it can restore the content the hash belongs to: the local
snapshot must exist and hash to the value sent. Otherwise the read is unconditional, because a `304`
with nothing to return would be worse than a download. On `304` the restored result takes the
server's md5, content type and modification time and the local `encryptedDataKey`, matching the
split the Java client makes. It is not marked as served from the local cache: that flag means the
server was unreachable, and here the server answered. The listener path sends the held hash as well,
so a repeated notification can also be answered with `304`; that is treated as "unchanged" and
dropped rather than reported as a failed fetch.

### Configuration fuzzy watch

A fuzzy watch (`WatchAsync(dataIdPattern, groupPattern)`) follows a pattern rather than one
configuration, and the protocol is a small state machine over three request types:

| Message | Direction | Meaning |
| --- | --- | --- |
| `ConfigFuzzyWatchRequest` | client → server | `WATCH` or `CANCEL_WATCH` for `groupKeyPattern`, carrying the group keys the client already holds and an `initializing` flag. |
| `ConfigFuzzyWatchSyncRequest` | server → client | A diff: `FUZZY_WATCH_INIT_NOTIFY` batches during the initial catch-up, `FUZZY_WATCH_DIFF_SYNC_NOTIFY` for a later resync, and `FINISH_FUZZY_WATCH_INIT_NOTIFY` to end the initial catch-up. |
| `ConfigFuzzyWatchChangeNotifyRequest` | server → client | One key entering (`ADD_CONFIG`) or leaving (`DELETE_CONFIG`) the matched set, or reported as present (`CONFIG_CHANGED`, which the server also uses for a creation). |

- The pattern is `namespace>>groupPattern>>dataIdPattern`; the namespace is the client's configured
  one (blank → `public`), because one watch cannot span namespaces. A group key is
  `dataId+group[+tenant]`, with only `+` and `%` percent-escaped; any other escape is malformed and
  the key is logged and skipped rather than failing the batch.
- The initial catch-up ends at `FINISH_FUZZY_WATCH_INIT_NOTIFY`, which the server sends immediately when
  the diff is empty and after the last batch has been acknowledged otherwise. The client also ends it
  at the last `FUZZY_WATCH_INIT_NOTIFY` batch, so a consumer does not wait for that extra round trip and
  a lost marker cannot leave it hanging; batches are pushed in order, so the last one means the whole
  diff has been applied. A marker arriving afterwards finds every subscription already initialised.
- The matched set is the server's, keyed by group key. A push is applied only if the key is absent
  or present as the push claims, so a duplicate add or remove is not reported twice, and a
  subscriber that joins an initialised watch is seeded with the current set.
- **The server tracks membership, not content.** `ConfigFuzzyWatchChangeNotifier` derives
  `CONFIG_CHANGED` from "the configuration still exists" and `DELETE_CONFIG` from "it no longer
  does", and `syncGroupKeyContext` only notifies when the key is not already in the pattern's set.
  A content change to a configuration that already matches is therefore never pushed: `ListenAsync`
  is the API for content. A `CONFIG_CHANGED` for a key the watch does not hold is reported as
  `Added`, which is the only way a creation can be recognised at all — and is exactly what the
  official client does, since it maps both `ADD_CONFIG` and `CONFIG_CHANGED` to an addition. This
  client additionally reports `Modified` if a change push ever names a key the watch already holds;
  Java drops that push instead, and no current server sends it.
- The server limits patterns per connection (`50310`) and matched configurations per pattern
  (`50311`); a refusal is surfaced as `NacosServerException` carrying that code.
- A watch is re-sent after a reconnect with the keys the client still holds, and re-sent every
  `NacosConfigOptions.FuzzyWatchResyncInterval` (3 minutes by default) so a push lost on a
  connection that still looks healthy is repaired. The server answers a re-sent watch with a diff,
  which is what makes the repetition harmless. A registration that never finished its first catch-up
  is re-sent as an initialisation instead, because a diff sync carries no finish marker and the
  subscription would otherwise wait for one forever.
- `CANCEL_WATCH` is sent only for the last subscriber of a pattern, and only while connected: with
  no connection there is nothing to cancel, because the server dropped the watch with it.

## Naming specifics

- Registration, deregistration and queries use `InstanceRequest`/`PersistentInstanceRequest` and
  `ServiceQueryRequest`.
- **One published instance set per service per connection.** The server stores an instance
  registration in `AbstractClient.publishers`, keyed by service, so a new registration replaces the
  previous set rather than adding to it. `BatchInstanceRequest{type = "batchRegisterInstance",
  instances}` publishes the whole set in one request; a deregistration removes the set. This is why
  the client's replay state is keyed by service and why a batch is the only way one connection
  publishes several instances of the same service.
- Subscriptions use `SubscribeServiceRequest`; changes arrive as `NotifySubscriberRequest`, which
  carries the complete instance list, and are answered with `NotifySubscriberResponse`. The
  subscription's own response carries `serviceInfo`: the state of the service at the instant the
  subscription took effect, which is the one the seeding read cannot see. It is applied through the
  same path as a push — see *Reconnection and state replay*.
- A push identifies the service **inside** `serviceInfo` (`name`, `groupName`): the server builds it
  with `NotifySubscriberRequest.buildNotifySubscriberRequest(serviceInfo)`, so the request's own
  `namespace`/`serviceName`/`groupName` fields are absent on the wire even though the class declares
  them. Reading them is silently lossy — the push is discarded as unidentifiable. The Java client
  reads `serviceInfo` too (`NamingPushRequestHandler`).
- The pushed `serviceInfo.checksum` is **empty**, so a change is detected by comparing the instance
  lists (by address, with the instance description), not by a checksum:
  `ServiceInfoHolder.processServiceInfo` → `InstancesDiffer.doDiff` keys both lists by `ip:port`,
  compares the descriptions, and drops a push whose `lastRefTime` predates the applied state.
- `ServiceInfo.allIps` serialises as `allIPs` (capital `IP`) on the wire.
- **Selectors are client-side only.** `SubscribeServiceRequest` carries nothing but `subscribe` and
  `clusters`; there is no selector field on any message. The selector overload subscribes with an
  empty cluster filter — Java's `Constants.NULL` — and narrows each pushed list on the client, while
  the plain overload's `clusters` argument is the server-side narrowing. A consumer is notified only
  when its selected view changes, and the first element is filtered the same way, so a subscription
  never starts from a list it would not deliver later.
- **The service list is paged.** `ServiceListRequest{namespace, groupName, pageNo, pageSize}` is
  answered with `ServiceListResponse{count, serviceNames}`. `count` is the number of services in the
  group *before* paging, so a page beyond the last one returns no names and still reports the total.
  The server strips the group prefix from each returned key (`ServiceUtil.pageServiceName`), so the
  names are bare and the group is the one that was asked for. Group matching is exact, and the
  request's `selector` field is accepted but ignored by the 3.x handler (`TODO select service by
  selector`), which is why this client does not expose it. A non-positive page size would reach the
  server's `subList` and fail there, so it is rejected at the call site.

### Naming fuzzy watch

A naming fuzzy watch (`WatchAsync(serviceNamePattern, groupPattern)`) follows which services exist
rather than one service's instances. It reuses the configuration protocol's shape with service keys
and its own request names:

| Message | Direction | Meaning |
| --- | --- | --- |
| `NamingFuzzyWatchRequest` | client → server | `WATCH` or `CANCEL_WATCH` for `groupKeyPattern`, carrying the service keys the client already holds, an `initializing` flag and the client's namespace. |
| `NamingFuzzyWatchSyncRequest` | server → client | A diff: `FUZZY_WATCH_INIT_NOTIFY` batches during the initial catch-up, `FUZZY_WATCH_DIFF_SYNC_NOTIFY` for a later resync, and `FINISH_FUZZY_WATCH_INIT_NOTIFY` to end the initial catch-up. Answered with `NamingFuzzyWatchChangeNotifyResponse`. |
| `NamingFuzzyWatchChangeNotifyRequest` | server → client | One service entering (`ADD_SERVICE`) or leaving (`DELETE_SERVICE`) the matched set, `INSTANCE_CHANGED` for an instance list change, or `HEART_BEAT`. Answered with `NamingFuzzyWatchChangeNotifyResponse`. |

- The pattern is `namespace>>groupPattern>>serviceNamePattern`; the namespace is the client's
  configured one (blank → `public`), because one watch cannot span namespaces. A service key is
  `namespace@@group@@service` (`NamingUtils.getServiceKey`) and is **not** escaped: `@@` may not
  appear inside any of the three components. The server sends ten services per batch, where
  configuration sends twenty.
- The initial catch-up and resync follow the configuration rules: it ends at the finish marker or at
  the last initial batch, a late subscriber is seeded from the current set, a re-sent watch carries
  the known service keys and asks for a diff, and the last subscriber's departure sends
  `CANCEL_WATCH`. The resync interval is `NacosNamingOptions.FuzzyWatchResyncInterval` (3 minutes).
- **The server pushes only membership changes.** `syncServiceContext` returns `false` for
  `INSTANCE_CHANGED`, so a service whose instance list changes while it stays matched is not pushed;
  `ADD_SERVICE` and `DELETE_SERVICE` are sent when the first or last instance appears or disappears.
  `SubscribeAsync` remains the API for instances. If an `INSTANCE_CHANGED` ever arrives, a known
  service is reported as `InstancesChanged` and an unknown one as `Added`, so a push the server did
  send is never silently discarded; `HEART_BEAT` is ignored.
- `DELETE_SERVICE` removes the service from the client's set. The official Java client adds it back
  instead, which leaves its set wrong after a delete; this client follows the configuration watch.
- The same limits apply (`50310` patterns per connection, `50311` matches per pattern), surfaced as
  `NacosServerException` carrying the code.

## Distributed lock specifics

> **Which server this describes.** The lock service belongs to the **3.3** line; this section and the
> client follow it. The lock module a **3.2** server ships is an earlier contract: its
> `LockOperationEnum` knows only `ACQUIRE`, `RELEASE` and `EXPIRE`, its `LockInstance` has no
> `owner`, `waitTime` or `waiterRetry`, its response is the bare `result` boolean with no
> `LockResult`, and its service loader registers `NACOS_LOCK` as the only lock type. Against such a
> server this client acquires and releases fine, but `RENEW` and `CANCEL_WAIT` come back as
> `resultCode = 500` / `Nacos deserialize failed` — the operation name is not a value the older
> enum knows — and `REENTRANT` / `NON_REENTRANT` are refused with `lockType: <type> is not exist.`
> The handshake cannot tell the two apart: a 3.2 server advertises `lock = true` just as a 3.3 one
> does.

- One request type carries every operation. `LockOperationRequest` holds a `LockInstance` and an
  `lockOperationEnum` naming the operation: `ACQUIRE`, `RELEASE`, `RENEW` or `CANCEL_WAIT`. An enum
  value the server keeps for itself, `EXPIRE`, is never sent by a client.
- `expiredTime` is a **duration in milliseconds measured from when the server handles the request**,
  not an absolute timestamp. It is always sent because the server compares it to zero without a null
  check, so an omitted value fails the request. A negative value asks for the server's own lease,
  which is 30 seconds; the server also caps whatever it is given.
- `owner` is what reentrancy, release and renewal are all keyed by, and the server substitutes the
  connection id when it is absent. This client always sends one, because the wait queue is keyed by
  the owner and a notification that names the wrong owner would never release the waiter.
- The lock type (`NACOS_LOCK`, `REENTRANT`, `NON_REENTRANT`) is part of a lock's identity: the same
  key can be held independently under two types. `NACOS_LOCK` is the default and the one the server
  treats as an ordinary mutex.
- `waitTime` greater than zero asks the server to queue the request. The server then answers with
  `LockResult.waiting(position)` rather than failing, and pushes `LockNotificationRequest` when the
  lock becomes available or when it drops the waiter (`notificationType` is `AVAILABLE` or
  `TIMEOUT`). A retry that carries `waiterRetry = true` is only granted at the head of the queue,
  which is what makes the queue first-in-first-out. `-1` is the protocol's "do not queue" value.
- The response prefers the structured `lockResult` (`success`, `reentrantCount`, `waitPosition`,
  `errorMessage`) and falls back to the bare `result` boolean, which is what a renewal returns.
  Failing to acquire is an ordinary reply with a success result code, not a transport error;
  `resultCode = 500` is what the server uses for a request it refused outright.
- Synchronising on a lock is not enough on its own. The lock has no fencing token, so a holder whose
  lease expired can still act on what the lock guarded until it notices. Renewing well inside the
  lease, and treating an unsuccessful renewal as having lost the lock, is what makes the lease safe.
  Renewal is the client's job, and this client does it on the cadence Nacos' own client uses —
  `max(1s, min(lease / 3, 10s))` — with a lease left to the server assumed to be the server's 30
  seconds. See `NacosLockWatchdog`.

## AI registry specifics

> **Which server this describes.** The MCP registry is the Nacos **3.0.3** one or newer:
> `McpService` in the official API marks `getMcpServer` and `releaseMcpServer` `@Since("3.0.3")`, and
> a 3.0.2 server — the oldest release the rest of the suite runs against — answers both with
> `RequestHandler Not Found`, the error `GrpcRequestAcceptor` reports for a payload type it has no
> handler for. The handshake does not stand in for the request: the client refuses a registry call
> only when the server explicitly denies the `mcp` ability, so against a server that predates the
> registry the failure is the one the request itself reports.

### MCP servers

- The module label is `ai` and the connection carries an extra label of its own, `aiClientUuid`, a
  UUID minted once per client instance rather than per connection. Nacos records it with every MCP or
  agent endpoint a connection publishes, which is what lets an endpoint that outlived its publisher be
  attributed and cleaned up, so it has to survive a reconnect.
- A read is `QueryMcpServerRequest` (`namespaceId`, `mcpId`, `mcpName`, `version`) answered with
  `QueryMcpServerResponse` (`mcpServerDetailInfo`). A release is `ReleaseMcpServerRequest`
  (`namespaceId`, `mcpId`, `mcpName`, `serverSpecification`, `toolSpecification`,
  `resourceSpecification`, `endpointSpecification`, `createDraft`) answered with
  `ReleaseMcpServerResponse` (`mcpId`). `mcpName` is what the server routes on and is taken from the
  specification, so the two cannot disagree.
- The version is what a release creates and what a later read pins. Nacos moved it out of the plain
  `version` string into `versionDetail`, the official registry's object, whose members are snake case
  (`release_date`, `is_latest`) unlike every other field in the payload; the plain string is kept for
  compatibility. The client reads the structured form first and refuses a release that carries neither,
  because a server left to invent a version is one the caller cannot name again.
- The tool, resource and endpoint objects are the MCP protocol's rather than Nacos' own, and Nacos
  stores and serves them verbatim, so the client passes the caller's objects through instead of
  re-shaping them: `_meta` keeps its underscore, capabilities keep the enum's upper-case names
  (`TOOL`), and a tool's JSON Schema stays JSON.
- A server that does not exist is a business error rather than an empty answer. Nacos reports it with
  the generic not-found code (`404`) and a message naming the cause; a successful reply with no detail
  would therefore be a contract violation, and is reported as one rather than as a missing server.
- **A release is answered before it is readable.** The reply says the server accepted the version, not
  that the name can be read back with it: a read that arrives first gets the same not-found an unknown
  name gets, which is what the resource index catching up looks like from the client. Both forms of read
  are affected, the one that names a version and the one that names none, so a caller that has to see
  its own release repeats the read until it does.
- Two ability keys gate the registry: `mcp` and `mcpDraftRelease`. Only an explicit `false` is a
  rejection — the client refuses locally with `NacosServerException(501)` — because a key the server
  never mentioned says nothing, and a server that predates the key still serves the registry.
  `createDraft` is gated on its own key because a server that does not know the field would read it as
  unknown and publish the version directly, which is the opposite of what the caller asked for.
- **There is no change push.** Nacos pushes configuration and service changes, but it has no MCP
  change notification: the official client discovers a change by re-reading the server on an interval
  (`NacosMcpServerCacheHolder` plus a scheduled update task). A subscription here is therefore a
  cached copy refreshed every `NacosAIOptions.CacheRefreshInterval`, and it is the only operation in
  the registry that does not simply report what the server said. A read that fails leaves the cached
  copy in place and is retried, so a watcher outlives a control-plane outage; a `404` is not a failure
  either — a subscription to a name that has not been released yet stays open and reports it when it
  appears.
- **A subscription is keyed by version.** `{name}::{version}`, and `{name}::latest` for a subscription
  that names none. The version Nacos flags `is_latest` is also stored under the `latest` key, so a
  subscription without a version is answered from the cache rather than by another round trip, and a
  change is reported to both keys when it is the latest one.
- **An endpoint is a Naming instance, not an AI record.** `McpServerEndpointRequest` (`type` ∈
  `registerEndpoint`, `deregisterEndpoint`) is handled by `McpClientApplicationService.operateEndpoint`,
  which resolves the server's `REF` endpoint to a Naming service and registers the address as an
  instance of it, attributed to the publishing connection. So the endpoint is discovered, health
  checked and load balanced like any other instance, it disappears when the connection that published
  it closes, and the client replays it after a reconnect. The `version` field is what the server
  resolves the target service by.

### Where this client deviates from the official client (MCP servers)

Five places where `nacos-develop` does something this client does not, each recorded because a
deliberate difference that is not written down is indistinguishable from a bug. Everything else
follows the official client.

1. To subscribe to something already cached, the official client returns the cached detail and — because
   `addMcpServerUpdateTask` is only reached on the cache-miss branch — does not start a refresh loop for
   it. Here every subscription ensures its key has a loop, since a subscription that never refreshes is
   a subscription that never fires.
2. A change to the version Nacos flags `is_latest` is published to both `{name}::{version}` and
   `{name}::latest`. The official client keys the event by `latest` alone, so a consumer that named the
   exact version — which is the latest one at the time — is never told it changed.
3. Endpoint replay is keyed by `name+address+port`. The official client keys `McpServerEndpoint` by
   `mcpName` alone, so a server publishing several endpoints replays only the last one registered.
4. A registration the server refused is not kept. The official client records the intent before sending
   it and clears it only after a successful deregistration, so a reconnect retries a registration the
   caller was told had failed. Here the intent is dropped on failure, and a deregistration drops it
   before the request is sent, matching the naming client's redo contract.
5. A deregistration carries the version the endpoint was published for (the client recorded it). The
   official client sends no version and lets the server resolve the one it currently labels latest,
   which resolves to a different service whenever the endpoint was pinned to a version that has since
   been superseded — and then removes nothing.

### Prompts

> **Which server this describes.** The prompt registry is the Nacos **3.2.0** one or newer:
> `QueryPromptRequestHandler` is marked `@Since("3.2.0")`, as is the `GET /v3/client/ai/prompt`
> handler behind it. An older server answers the request the way §2 describes — `RequestHandler Not
> Found` — and nothing lets a client refuse earlier: the HTTP capability contract names a `prompt`
> capability, but the gRPC ability table has no prompt key at all, so there is no flag to read the
> server's answer from.

- A prompt is a template plus the placeholders it uses. A read is `QueryPromptRequest`
  (`namespaceId`, `promptKey`, `version`, `label`, `md5`) answered with `QueryPromptResponse`
  (`promptInfo`), and the answer is a *projection* of the stored version rather than the stored
  version: Nacos serves `promptKey`, `version`, `template`, `md5` and `variables`, and drops what the
  registry keeps only for management (`description`, `bizTags`, the author, the commit message). A
  caller that needs any of those needs the console rather than this client.
- A prompt is addressed by key and by *one* of two selectors. A version names a version; a label is a
  pointer the server moves between versions, which is what a caller asks for when it wants "whatever is
  currently stable" without knowing where that is. The server resolves the version first, then the
  label, then the version labelled `latest`, so a label is consulted only when no version is named —
  and an unknown label quietly resolves to latest instead of failing, which is the one selector typo
  that cannot be caught.
- The resolved version must be *online*, so a published draft is not readable and a version that
  exists but was never published is reported as missing. That is also why the client resolves nothing
  locally: the version plan is the server's, and a client that cached a label's target would answer
  from a plan that has since moved.
- A subscription is a refresh loop per selector, because Nacos does not push prompt changes. The read
  is conditional: it carries the hash of the copy that is cached, and the server answers `304` when
  that hash still applies, so an unchanged prompt costs a round trip rather than a body. A `304` is
  only reachable once a version has been resolved, so a selector that now points at a different
  version can never be answered with one — the version comparison happens first.
- **A withdrawal is reported**, unlike an MCP server's. A prompt that stops being served — deleted, or
  a version taken offline — is answered with the same `404` a key that was never published is, so
  there is no "not there yet" for the client to confuse a removal with. The client treats both the
  same way: `404` removes the cached copy and announces the removal as a change whose prompt is null.
  A subscriber that was never told would keep rendering a prompt that is gone.
- A failed refresh is neither a state change nor an outage: the last known prompt stays cached, the
  failure is logged, and the loop reads again on the next interval, so a watcher outlives a
  control-plane outage instead of failing with it.
- **The same read has an HTTP shape.** `GET /v3/client/ai/prompt` takes the same five parameters as
  the query string (`namespaceId`, `promptKey`, `version`, `label`, `md5`) and answers with the
  client OpenAPI's result envelope, the prompt in `data`. A missing prompt and an unchanged hash are
  HTTP `404` and `304`, and both are mapped to the same error codes the gRPC result codes carry, so
  the subscription cache and a caller treat the two transports identically. A `304` carries no body,
  which is why the status is what decides, not the body.
- **The transport is chosen per module.** `NacosAIOptions.TransportMode` selects `Auto`, `Grpc` or
  `Http`, and `PromptTransportMode` overrides it for prompts; the default is `Grpc`, which is the
  behaviour from before the option existed. `Http` never opens the AI gRPC connection. `Auto` tries
  gRPC first and, when the attempt fails with a connection or timeout error, retries the same read
  over HTTP and stays there for 30 seconds before trying gRPC again — a failed connect costs the
  connection timeout, so retrying it on every read would make `Auto` slower than pinning HTTP. A
  server error is not a transport failure and does not trigger the fallback.

### Where this client deviates from the official client (prompts)

Three places where `nacos-develop` does something this client does not.

1. A subscription whose selector is already cached is answered from the cache without a read. The
   official client always queries on subscribe (`subscribePrompt` reads the prompt before it schedules
   the update task). Here the read is skipped when a copy is held, so a second subscriber to a prompt
   someone is already watching costs no round trip.
2. The cached copy outlives its last subscriber. The official client removes the cache entry when the
   last listener unsubscribes, so the next subscriber reads the server again; here the entry stays
   until the server stops serving it. A caller that needs the server's word rather than a copy calls
   `GetPromptAsync`, which never consults the cache.
3. The refresh interval is the single `NacosAIOptions.CacheRefreshInterval` the MCP subscription also
   uses, where the official client reads a separate `nacosAiPromptCacheUpdateInterval`. One knob is a
   deliberate simplification: both subscriptions are the same kind of poll and the default is the same
   10 s either way.

### Skills

> **Which server this describes.** The skill registry is the Nacos **3.2.0** one or newer:
> `SkillClientController.get` is marked `@Since("3.2.0")`, and the same release introduced the admin
> API a skill is published through. An older server has no `/v3/client/ai/skills` endpoint at all.

A skill is one of the two AI resources that are not served over the AI connection. The server exposes
it as an ordinary OpenAPI endpoint, and the official client implements it only over HTTP — its gRPC
client answers the request with `SERVER_NOT_IMPLEMENTED`. Skills and agent specifications are the two
resources in that position; MCP and prompts have both paths.

```
GET {context}/v3/client/ai/skills?namespaceId={namespace}&name={skill}[&version=…][&label=…][&md5=…]
```

| Answer | What it means |
| --- | --- |
| `200` | The body is the skill's ZIP archive. |
| `304` | The `md5` that was sent is still the published one. No body. |
| `404` | No such skill, or no such version. The detail is in the result's `data`. |
| `403` | The token was rejected. |

The headers that describe the body:

| Header | Meaning |
| --- | --- |
| `X-Nacos-Skill-Md5` | The hash of the published content, unquoted. |
| `ETag` | The same hash, quoted, for a conditional `If-None-Match`. |
| `X-Nacos-Skill-Resolved-Version` | The version the selector resolved to. |
| `Content-Disposition` | `attachment;filename={skill}.zip`. |

- **The body is a directory, not a document.** The archive holds `{skill}/SKILL.md` and one entry per
  stored resource file under the same directory (`{skill}/reference/notes.txt`). Binary resources are
  decoded from the Base64 the registry stores them as, so the entries are the bytes a reader expects.
  The archive is handed to the caller as bytes because extracting it is the caller's business, but the
  client checks it first: a body that is not a ZIP at all, or one whose entries would extract outside
  the skill's directory, is refused rather than passed to an extractor.
- **A version must be indexed, and a label is not a fallback.** `SkillIndexManifestService.resolveVersion`
  returns the named version only when the manifest lists it; otherwise it looks up the label — or
  `latest` when no label is named — and returns null when that resolves to nothing. Unlike a prompt, an
  unknown label is *not* answered with the latest version: the version is reported as missing, so the
  selector cannot silently point somewhere else. That is why a read of a skill with a label that does
  not exist is a `404` and not the current content.
- **The hash is stored at publish time.** It is the content digest persisted on the version row, and a
  version published before the listener feature shipped has it back-filled on its first conditional
  read — which is why that one read answers `200` with a hash instead of `304`.
- **A withdrawal is a `404`.** Taking a version offline removes it from the index, so the read that
  resolved to it — including a read through `latest` — is answered the way a skill that was never
  published is. There is no "not there yet" to confuse it with, so the client reports the removal.
- **The reason is in `data`, not `message`.** A v3 error is
  `{ "code": 20004, "message": "resource not found", "data": "Skill not found: hello" }`: the
  `message` names the code and the `data` carries the detail. The client reports the detail, which is
  the same string the official client ends up surfacing from the raw body.
- **The token travels as `accessToken`**, the header the plugin filter reads on the client OpenAPI,
  and not as an `Authorization` bearer header. The admin API the fixture publishes through is the
  opposite: it is a Spring Security endpoint and takes the bearer header.

### Where this client deviates from the official client (skills)

Three places, matching the three the prompt registry has.

1. A subscription whose selector is already cached is answered from the cache without a read. The
   official client's `subscribeSkill` always queries before it schedules the update task, so a second
   subscriber to a skill someone is already watching costs a round trip there and nothing here.
2. The cached archive outlives its last subscriber. The official client removes the cache entry when
   the last listener unsubscribes; here the entry stays until the server stops serving it. A caller
   that needs the server's word rather than a copy calls `GetSkillAsync`, which never consults the
   cache.
3. The refresh interval is the single `NacosAIOptions.CacheRefreshInterval`, where the official client
   reads a separate `nacosAiSkillCacheUpdateInterval`. Both are the same kind of poll with the same
   10 s default.

One difference that is not a deviation but a consequence of the API shape: the official read returns
the ZIP bytes alone, while `GetSkillAsync` returns the archive together with the version that was
resolved and the hash the server reported. Those two values are in the response headers either way,
and a caller that wants to record what it received needs them.

### Agent specifications

> **Which server this describes.** The agent-specification registry is the Nacos **3.2.0** one or
> newer: `AgentSpecClientController.get` is marked `@Since("3.2.0")`, and the same release introduced
> the admin API a specification is published through. An older server has no
> `/v3/client/ai/agentspecs` endpoint at all.

An agent specification is the second AI resource served only over HTTP, next to a skill. No
`.proto` declares an agent-specification service, and the official gRPC client answers the call with
`SERVER_NOT_IMPLEMENTED` rather than forwarding it — so a read never opens the AI connection and only
needs a reachable HTTP endpoint.

```
GET {context}/v3/client/ai/agentspecs?namespaceId={namespace}&name={name}[&version=…][&label=…][&md5=…]
```

| Answer | What it means |
| --- | --- |
| `200` | The body is the v3 result envelope; `data` is the specification. |
| `304` | The `md5` that was sent is still the published one. No body. |
| `404` | No such specification, no such version, or the version is not online. The detail is in the result's `data`. |
| `403` | The token was rejected. |

The headers that describe the body:

| Header | Meaning |
| --- | --- |
| `X-Nacos-AgentSpec-Md5` | The hash of the published content. |
| `ETag` | The same hash, quoted on a `200` and unquoted on a `304`. |
| `X-Nacos-AgentSpec-Resolved-Version` | The version the selector resolved to. Sent on a `200` only. |

- **The body is an envelope, not the package.** Unlike a skill, whose body is the archive itself, a
  specification is answered inside the API's result: `{ "code": 0, "message": "success", "data": … }`.
  The specification in `data` carries `name`, `description`, `bizTags`, `content` and `resource`.
  `content` is the `manifest.json` as a JSON *string* — the registry keeps it opaque and hands it back
  unchanged — and `bizTags` is likewise the stored JSON array in a string. The client reports both as
  stored and never reinterprets them.
- **The resource map is keyed by an identifier, not by a path.** A file at `config/SOUL.md` is keyed
  `config_SOUL__md`, a name the registry derives from the resource's type and file name. The path is
  in `resourceIdentifier` (`config::SOUL.md`); the resource's own `name` is only the file name
  (`SOUL.md`) and its `type` is the directory (`config`). A caller that needs a path reads the
  identifier, and a caller that looks the file up by path finds nothing.
- **A label resolves before a version.** `AiResourceManager.resolveVersion` consults the labels
  first, then the named version, then `latest` — and an unknown label is not an error: it falls
  through to the version and then to `latest`. That is the opposite order to a prompt, whose
  documented priority is `version > label > latest`, and the opposite strictness to a skill, where a
  label that does not resolve makes the version missing. A read that names both therefore gets the
  label's version here and the named version for a prompt.
- **The version and the hash are headers.** The payload carries neither, which is why
  `NacosAgentSpec.Version` and `NacosAgentSpec.Md5` are populated from the response rather than from
  the body. The not-modified answer carries the hash alone — no resolved version — so a conditional
  read that changed nothing cannot restate which version the copy is of. A skill's not-modified
  answer does restate it; this one does not.
- **A withdrawal is a `404`, and a conditional read cannot see it.** Taking a version offline leaves
  the specification's labels untouched, so `latest` still resolves to that version; the server then
  reads the stored hash of the version the selector resolved to and answers `304` *before* it checks
  that the version is still online. A conditional read of a version that was taken offline is
  therefore answered "not modified" indefinitely. A skill escapes this because its selector resolves
  through an index manifest that an offline removes the version from, leaving no hash to compare.
  This client's subscription consequently reads in full every interval and detects the withdrawal
  through the `404` that a plain read gets; the hash is still reported on a `200`, for a caller that
  wants to record what it received.
- **The reason is in `data`, not `message`.** A v3 error is
  `{ "code": 20004, "message": "resource not found", "data": "AgentSpec not found: hello" }`, exactly
  as for a skill: `message` names the code and `data` carries the detail. A version that exists but is
  offline is reported the same way, with `AgentSpec version not online: hello`.
- **The token travels as `accessToken`** on the client OpenAPI, the header the plugin filter reads,
  and as a bearer header on the admin API — the same split as a skill's.

Publishing is an admin-API affair, and its shape explains why the fixture publishes the way it does:

- A **first version is uploaded as a package**, not created from a card. `POST /upload` takes a
  multipart ZIP whose `manifest.json` names the worker under `worker.suggested_name`, and the registry
  numbers the version itself — the upload endpoint takes no version, and the first one becomes
  `0.0.1`. The name cannot be passed separately: it comes from the manifest.
- A **later version is forked, edited and published**: `POST /draft` with `basedOnVersion` and
  `targetVersion`, `PUT /draft` with the edited card, then `POST /force-publish`. `publish` only
  accepts a version in `reviewing`, so an unpublished draft has to be forced through.
- `PUT /labels` **replaces the whole map**. Writing `{"stable":"0.0.1"}` alone drops the `latest`
  label the server maintains, after which a read that names no selector resolves to nothing and is
  answered `404`. A caller that binds one label has to write `latest` back in the same call.

### Where this client deviates from the official client (agent specifications)

The same three as the prompt and skill registries, for the same reasons.

1. A subscription whose selector is already cached is answered from the cache without a read.
2. The cached copy outlives its last subscriber; `GetAgentSpecAsync` never consults the cache, so it
   always reports the server's word.
3. The refresh interval is the single `NacosAIOptions.CacheRefreshInterval`, where the official client
   reads a per-resource interval of its own.

One more, which is not a simplification but a consequence of the `304` above: the refresh reads the
specification in full rather than conditionally, so it costs a body per interval instead of a `304`.
The alternative was a subscription that can never report a withdrawal.

### A2A agent cards

> **Which server this describes.** The agent card registry is the Nacos **3.1.0** one or newer:
> `QueryAgentCardRequestHandler`, `ReleaseAgentCardRequestHandler` and
> `AgentEndpointRequestHandler` are all marked `@Since("3.1.0")`, and the batch endpoint handler
> `@Since("3.1.1")`. A server older than 3.1 has no handler for any of them.

An agent card is the A2A protocol's own document, and Nacos serves it over the AI connection rather
than over HTTP. The client advertises the `agent` ability and refuses a call only when the server
states that it does not have it — a server that says nothing may still serve the request.

| Operation | Request | Answer |
| --- | --- | --- |
| Read a card | `QueryAgentCardRequest` (`namespaceId`, `agentName`, `version`, `registrationType`) | `QueryAgentCardResponse` (`agentCardDetailInfo`) |
| Publish a card | `ReleaseAgentCardRequest` (`agentCard`, `registrationType`, `setAsLatest`) | `ReleaseAgentCardResponse` |
| Register or remove an endpoint | `AgentEndpointRequest` (`endpoint`, `type`) | `AgentEndpointResponse` (`type`) |
| Replace an agent version's endpoints | `BatchAgentEndpointRequest` (`endpoints`) | `AgentEndpointResponse` (`type`) |

- **A card is validated before it is stored, and the validation decides its shape.** A card must name
  a name and a version, and it must be describable in one of two ways: either `supportedInterfaces`,
  where every entry names a URL, a protocol binding and a protocol version, or the older triple
  `protocolVersion` + `preferredTransport` + `url`. A card that satisfies neither is refused with
  `Required parameter 'agentCard.supportedInterfaces' not present, and old protocol fields … are
  incomplete`. That is why a card published for a service endpoint still declares an interface even
  though no runtime endpoint exists yet.
- **A `SERVICE` card's address is assembled at read time.** The card is stored as it was published,
  and the read then looks up the Naming service named after `{agentName}::{version}` in the agent
  endpoint group. When that service has instances, they replace the card's `supportedInterfaces`,
  `additionalInterfaces`, `url`, `preferredTransport` and `protocolVersion` — so the declared
  interface is a fallback that shows through while nothing is registered, not the address callers
  get once something is. A `URL` card skips this entirely: its own `url` is the address.
- **An endpoint must name the card version it serves.** The registration becomes an instance of a
  service named after the card's name and version, so the server refuses an endpoint without one
  (`Required parameter 'endpoint.version' can't be empty or null`) and the client refuses it before
  sending. The address and a port in the TCP range are required for the same reason. The other
  members carry defaults: `JSONRPC` for the transport and `HTTP` for the protocol, so an endpoint
  built from an address, a port and a version is already the usual A2A endpoint.
- **An endpoint lives and dies with the connection.** It is a Naming instance, not an AI record, so
  the server removes it when the connection that registered it closes, and the client replays the
  registrations it still owns after a reconnect. A registration the server refused is not replayed:
  the caller saw the failure.
- **A batch replaces the publisher's set for a version.** `BatchAgentEndpointRequest` is handled as
  `batchRegisterInstance`, not as a loop of `registerInstance`: every endpoint the caller published
  for that agent version and did not repeat is withdrawn. The request carries no version of its own,
  so the endpoints must all name the same one — the server refuses a mixed batch, and an empty one,
  because removing endpoints is the deregistration call. A single registration after a batch adds to
  the set rather than replacing it. A batch the server refuses changes nothing on its side, so the
  client drops it from the replay record and keeps the set it was replacing.
- **The reserved metadata keys are not the endpoint's.** An endpoint's `protocolVersion` and `tenant`
  are stored in the metadata under `__nacos.agent.endpoint.protocolVersion__` and
  `__nacos.agent.endpoint.tenant__`, which the server strips from the user metadata and reads as
  fields of their own. A caller that sets them through the model does not have to know that.
- **A missing agent is a `404`.** The read is answered with the not-found result code, which is what a
  subscription turns into the null element that announces a withdrawal.

### Where this client deviates from the official client (agent cards)

1. A subscription whose selector is already cached is answered from the cache without a read, and the
   cached card outlives its last subscriber; `GetAgentCardAsync` never consults the cache, so it
   always reports the server's word.
2. The refresh interval is the single `NacosAIOptions.CacheRefreshInterval`. A card carries no content
   hash, so every refresh is an unconditional read — there is no conditional form of the request to
   use instead.
3. The replay record has two kinds of entry: one per single registration — keyed by agent name,
   address, port and path — and one per agent version for a batch. A batch is replayed as one
   request, before the single registrations, because it replaces the publisher's set and a single
   registration made after it is an addition. The official client records one batch per agent,
   keyed by the agent alone, so a batch for one version can displace another; here the version is
   part of the key, so versions do not interfere.

### RAD agents

> **Which server this describes.** The RAD agent registry is the Nacos **3.3.0** one or newer:
> `AgentPublishRpcRequestHandler`, `AgentSearchRpcRequestHandler`, `AgentDiscoveryRpcRequestHandler`,
> `AgentEndpointRegisterRpcRequestHandler` and `AgentEndpointDeregisterRpcRequestHandler` are all
> marked `@Since("3.3.0")`. The watch that goes with it is gated on its own ability, `radWatchV1`,
> which the same `v3.3.0-RC` server states and serves (`AgentGrpcWatchService`,
> `AgentSubscribeRpcRequestHandler`); a server that does not state it is refused rather than watched
> without hints.

This is a second agent contract, not a newer version of the card one. A card is a document an agent
publishes about itself; here an agent is a versioned resource with metadata, versions, call
interfaces and runtime endpoints, managed the way a prompt or a skill is.

| Operation | Request | Answer |
| --- | --- | --- |
| Publish a version | `AgentPublishRpcRequest` (`namespaceId`, `publishRequest`) | `AgentPublishRpcResponse` (`versionDetail`) |
| Search | `AgentSearchRpcRequest` (`namespaceId`, `searchRequest`) | `AgentSearchResponse` (`page`) |
| Discover | `AgentDiscoveryRpcRequest` (`discoveryRequest`) | `AgentDiscoveryResponse` (`discoveryResult`) |
| Register endpoints | `AgentEndpointRegisterRpcRequest` (`namespaceId`, `registrationBatch`) | `AgentEndpointOperationResponse` |
| Remove endpoints | `AgentEndpointDeregisterRpcRequest` (`namespaceId`, `agentName`, `protocol`) | `AgentEndpointOperationResponse` |

- **The ability is negotiated strictly, unlike the others.** A server has to state `radV1` before any
  of this is sent. A server that states the older `agent` ability instead is a 3.1 or 3.2 one and is
  refused as not implementing RAD; a server that states neither is reported as one the client cannot
  classify, because the two contracts need different payloads for the same intent and guessing would
  send a payload the server may not route. The other AI resources treat an absent ability as "try it",
  which works because there is only one contract to try.
- **A publish must carry call interfaces or a base version, and never both.**
  `AgentValidationUtils.validateDraft` refuses `directContent == copiedContent`, so a fresh version
  names its interfaces and a derived version names `basedOnVersion`.
- **A call interface is validated as a descriptor.** It needs a `protocol`, a `descriptorMediaType`
  matching `<type>/<subtype>`, a non-null `nativeDescriptor`, and — on the publish side — an
  `endpointSourceOrder`. A discovery result is the mirror image: it must *not* carry
  `endpointSourceOrder`, because by then the order has been applied. One model therefore serves both,
  and which fields are meaningful depends on which direction it travels.
- **The first version of an agent goes online as it is published.** `AgentPublishApplicationService`
  submits the version when it is the first one, regardless of `autoSubmit`, and the default pipeline
  approves it — so a publish answers with `online` rather than `draft`, and a discovery resolves it
  immediately. A later version of an existing agent is only submitted when `autoSubmit` is set, so it
  stays a draft until something submits it. That is why the integration fixture needs no approval
  step, and why forcing a publish of the first version is a `400`: it is already online.
- **A search is answered from the shared AI resource index, not from the registry.** In `AUTO` mode,
  the server's default, `AgentSearchModeResolver` resolves search to `INDEX`: an agent is projected
  into the index only while its latest version is online, and the projection is applied by a
  background task polled every five seconds
  (`nacos.ai.resource.search.index.consumer.interval-seconds`) with a retry backoff of its own. A
  publish therefore answers before the agent is findable, while discovery is the immediate read.
  `SCAN`, which the server has to be configured into, walks the stored summaries instead. The client
  sends the same `AgentSearchRpcRequest` either way and cannot tell which path the server took, so a
  caller that publishes and searches in the same breath has to allow the index to catch up.
- **An endpoint batch is bound to a version range.** An endpoint that carries no `bindings` of its own
  inherits the batch's `runtimeVersion` and `versionRange`, and a batch that names neither is refused
  with `Invalid Agent version: null` — there is no version to bind the endpoints to. The range has to
  be canonical (`[1.0.0,2.0.0)`) and has to contain the runtime version, and a batch's bindings have to
  be strictly sorted. The registry replaces what the same publisher registered for the same agent and
  protocol, which is what makes a redeploy safe.
- **A removal is by protocol, not by endpoint.** The request carries the agent and the protocol alone,
  because the endpoints a process registered are only meaningful as a set.
- **The digest is the discovery fingerprint.** `contentDigest` is what tells a caller whether what it
  holds is what the registry would serve now, and it is the same value the watch's fingerprint is
  derived from.

### Where this client deviates from the official client (RAD agents)

1. The publish, search, discovery and endpoint operations are the whole surface: the official client
   also routes some of these through the A2A adapter when the server only speaks the older contract,
   which this client does not — it refuses instead, so a caller learns that the server is too old
   rather than silently getting a different shape back.
2. A publish is not retried. The official client sends it with `requestOnce` for the same reason: it
   creates a version, so a retry after a timeout could create two.
3. The client validates the endpoint batch's shape (agent, protocol, at least one endpoint) before
   sending, and leaves the version binding to the server, which is the only side that can canonicalise
   a range.

### RAD agent watch

> **Which server this describes.** The watch is Nacos **3.3.0** as well, negotiated on the
> `radWatchV1` ability: `AgentGrpcWatchService`, `AgentSubscribeRpcRequestHandler`,
> `AgentUnsubscribeRpcRequestHandler` and the `AgentDiscoveryNotifyRequest` push.

A watch is the push half of discovery. Instead of asking whether an agent's answer changed every few
seconds, a caller registers the question once and the server pushes a hint when the answer *may* have
changed; the caller then reads the state again through the ordinary discovery call.

| Operation | Request | Answer |
| --- | --- | --- |
| Install a watch | `AgentSubscribeRpcRequest` (`clientWatchId`, `discoveryRequest`, `materializedFingerprint`) | `AgentSubscribeRpcResponse` (`watchKey`, `observedFingerprint`, `refreshRequired`) |
| Remove a watch | `AgentUnsubscribeRpcRequest` (`watchKey`) | `AgentUnsubscribeRpcResponse` |
| Hint (server → client) | `AgentDiscoveryNotifyRequest` (`watchKey`, `eventType`, `observedFingerprint`, `errorCode`) | `AgentDiscoveryNotifyResponse` (`watchKey`, `accepted`) |

- **The hint is not the answer.** `INVALIDATE` says the projection moved and carries the fingerprint
  it moved to; `REVALIDATE` carries no fingerprint at all and says only that the server cannot tell
  whether the client's copy is current. Both mean "read again", and the state the client reports
  always comes from a discovery call, never from the hint. A hint whose fingerprint is the one the
  client already holds is not worth a read and is not acted on.
- **The fingerprint is only ever one the server stated.** `materializedFingerprint` tells the server
  what the client believes it holds, and `observedFingerprint` tells the client what the server
  believes the state is; `refreshRequired` says whether the two disagree. `AgentGrpcWatchService`
  validates the materialized fingerprint only when it is not null, and the watch is admitted on the
  strength of the discovery it is opened with, so a client that sends none is answered with the state
  it needs to start reading from.
- **A hint is acknowledged.** The server re-delivers a hint that is not answered, so a hint for a
  watch this client does not own — a removal and a push crossing on the wire — is declined rather than
  accepted. So is a hint that contradicts itself: no event type, an event type this client version
  does not define, or a termination with no reason.
- **A termination has two meanings.** With a not-found code the agent is gone, which is a state rather
  than the end of the interest in it: the watch is installed again and the caller is told the agent is
  not served in the meantime. With any other code the server has decided the watch is over, so it ends
  and the caller is given the error.
- **An agent that was never published is not a failure.** The discovery a watch is opened with is
  refused for an agent that does not exist, and a read of one that was deleted is refused too; both
  are reported to the caller as "not served" and the watch is offered again until the agent appears.
  The absence is reported once per absence rather than on every attempt.
- **The watch dies with the connection.** The server binds it to the connection that installed it, so
  a reconnect installs every watch again and reads the state again rather than assuming it is current.
- **The state is handed over only when it moved.** A hint says the answer may have changed; a read
  that finds it did not has nothing to report, and reporting it would tell a caller that acts on every
  element that something happened.

#### The HTTP batch long poll

A watch can also be served without the connection taking a push. `POST
/v3/client/ai/agents/watch` is a form POST (`generation`, `timeoutMillis`, `watches`) that carries
the client's complete set — each item is `{clientWatchId, discoveryRequest,
materializedFingerprint}` — and the server holds it until one of those fingerprints no longer matches
the projection or the timeout elapses, then answers `{generation, changed, changedClientWatchIds}`
inside the usual v3 `Result` envelope. `X-Nacos-Client-Id` names the client and `Request-Module: ai`
routes the request to the AI module.

- The batch is a comparison, so it needs a fingerprint: `materializedFingerprint` is
  `sha256-canonical-json-v1:` plus the SHA-256 of the server's canonical JSON of the result the
  caller holds. `AgentDiscoveryCanonicalizer` and `AgentEndpointCanonicalizer` define that form; this
  client ports both, and the port is pinned against the vector the Java implementation produces.
- A watch set change increments `generation` and cancels the in-flight request, and an answer that
  names another generation is discarded: its identifiers may describe a set the client no longer
  holds.
- A watch the server reported changed leaves the next batch until the state was read and its
  fingerprint handed back, so the same projection is not reported twice.
- The server accepts 1–1000 watches that share one namespace, with `timeoutMillis` between 1000 and
  60000; the read timeout is the long-poll timeout plus five seconds.
- Failures are mapped the way the official transport maps them. A capacity rejection (`50104`, sent
  as HTTP 503 with the detail in the result) rejects only the newest pending addition, which is
  reported unavailable. A migration (`50105`) ends every watch on the binding without ending the
  binding. A status the server would answer the same way for a batch — 404, -404, 501, 302, 403,
  400, -400, or capacity with nothing pending — makes the binding unavailable, and the manager then
  serves those watches by polling. An ordinary failure is retried after a second, and three
  consecutive ones make the binding unavailable too.

### Where this client deviates from the official client (RAD agent watch)

1. **The transport preference is a setting rather than a router.** The official client's
   `AgentWatchTransportRouter` picks the transport per watch, upgrades a downgraded watch back to gRPC
   when the wire returns, and decides fallback from the error code of each install. This client
   selects the transport when a watch is installed and again after a reconnect:
   `NacosAIOptions.AgentWatchTransport` is `Auto` by default, which prefers gRPC, falls back to the
   HTTP batch when the gRPC install failed for a reason another transport could fix, and falls back
   to polling when neither is usable; `Grpc`, `Http` and `Polling` pin one transport. A batch the
   server refuses for good moves its watches to polling for the rest of the connection's life rather
   than re-entering a binding the server already refused. A server that does not state `radWatchV1`
   is still refused with the not-implemented code when the transport is pinned to `Grpc`, because a
   watch whose hints are never delivered is worse than being told the server cannot serve it.
2. **A fingerprint is computed locally only for the HTTP batch.** Over gRPC this client still
   subscribes with `null`, adopts the server's `observedFingerprint` as the value it compares later
   hints against, and uses a local digest of the served document — never sent anywhere — only to
   decide whether a read should be handed to the caller. The cost is one extra discovery read per
   install, including after a reconnect. The batch has no such option: it compares fingerprints, so
   the result is canonicalised exactly as the server does before the watch is registered.
3. **The observable shape is an async stream.** The official client delivers a
   `NacosAgentDiscoveryEvent` to a listener; here the watch is an
   `IAsyncEnumerable<NacosAgentDiscoveryResult?>` whose first element is the current state and whose
   later elements are the state again on every change, with `null` reporting that the agent stopped
   being served. The official client's unavailable event becomes `null`; its terminal unavailability —
   which it reports as an event carrying an error code — becomes a `NacosServerException` that ends
   the enumeration, so a caller that awaits the next element cannot mistake "the watch is over" for
   "nothing changed yet".
4. **An error is classified by what asking again could change.** A server behind the release this
   client speaks to, a refusal of the credentials, a refusal to take the load, or a reply outside the
   contract ends the watch and is raised to the caller; a dropped connection, a server that is still
   starting, or a read that failed for a reason of its own is not, and is retried with bounded jittered
   backoff. The retry schedule is this client's (`NacosOptions.AI.CacheRefreshInterval` as the base,
   doubling to a minute, spread over four-fifths to six-fifths), not the official client's
   `AgentWatchRetryPolicy`.
5. **An absent agent is recognised under every spelling the server uses.** The gRPC error mapper
   answers with the v2 business code, so a discovery of an agent that does not exist arrives as
   `20004` (`RESOURCE_NOT_FOUND`) rather than `404`, while the projection behind a hint reports the
   status code. `404`, `-404`, `20004`, `50100` and `50101` are all read as "the agent is not
   published".

### The local failover cache

- Every AI resource a subscription holds can also be written to the snapshot directory
  (`NacosOptions.LocalCacheDirectory`) when `NacosAIOptions.EnableLocalCache` is on, and read back when
  the control plane cannot be reached. The official client keeps these caches in memory only
  (`NacosMcpServerCacheHolder` and the rest of `client/ai/cache`, plus the `watch` package), so this is
  an extension rather than a port; the multi-language contract lets a client document its own
  deviation, and the behaviour added is the one configuration and naming already have — a process that
  restarts into an outage starts from what it last read instead of from nothing.
- Only a subscription writes a copy, and only a read that could not reach the server reads one, so a
  reachable server is always asked first. The copy never outlives the answer it came from: a resource
  the server reports as no longer served has its file removed, so a withdrawal is not undone by an
  outage that follows it.
- The payload is the resource's JSON, serialized through the same source-generated contract the
  protocol uses. The file is written beside its target and moved into place, so a process killed
  mid-write leaves either the previous copy or the new one; a file that does not deserialize is treated
  as absent, which is why the format needs no version marker.
- A subscription whose first read cannot open the connection is answered from the copy instead: the
  connection is not needed to hand out what was already read. Without a copy the failure stays the
  caller's, exactly as it was.
- A subscriber is handed each state once. Storing a state, writing its snapshot and announcing it are
  a single step, and a subscription's first element is taken from the cache under the same gate: an
  opening read that overlaps a refresh sees the change either as its first element or as a later one,
  never as both. The snapshot is written before the announcement, so a subscriber that is handed a
  state can also rely on the failover copy being the one it holds.
- The RAD agent watch has no disk cache of its own. Its recovery contract — a snapshot plus a redo of
  the subscription — is the official client's in-memory one here, and
  `specs/en/client/client-local-cache-redo-spec.md` §8 marks the disk part of the RAD recovery as a
  target rather than a contract.

### Transport selection and HTTP fallback

The official client can serve an MCP, prompt, skill, agent-spec or agent call over either the AI gRPC
connection or the AI HTTP client API (`/v3/client/ai/*`), and routes each call by the transport mode
configured for that module: `AgentTransportMode` (`GRPC`, `HTTP`, `AUTO`) read from
`nacosAiTransportMode` and its five per-module keys, behind `McpTransportRouter`,
`PromptTransportRouter` and `AgentTransportRouter`, backed by `AiCapabilityCache` and
`AiHttpClientProxy`. Nacos 3.3 implements the HTTP side in seven controllers — `McpClientController`,
`PromptClientController`, `SkillClientController`, `AgentSpecClientController`,
`AgentClientController`, `AiCapabilityClientController` and `AiResourceSearchClientController` —
including the MCP and agent endpoint publication paths and their heartbeat.

This client does not route: discovery, MCP servers, prompts and agent cards travel over the AI
connection, and only the resources the server exposes as HTTP alone — a skill and an agent
specification — are read over HTTP. A server whose gRPC surface is unreachable is answered by the
request's own failure — `RequestHandler Not Found`, or an explicit `501` where the ability is
advertised as `false` — rather than by a fallback. The gap is tracked as P1-15 in
[java-parity.md](java-parity.md).

The watch is the one exception, because it has a second contract on the server:
`NacosAIOptions.AgentWatchTransport` selects gRPC, the HTTP batch long poll, periodic polling, or
`Auto`, which tries them in that order. The batch and the polling fallback are described in
[RAD agent watch](#rad-agent-watch).

## Authentication

- The client logs in over the HTTP OpenAPI and caches the access token, refreshing it before expiry.
- A rejected token is retried **once** after obtaining a fresh one. Two error codes mean the token
  was not accepted, because the gRPC and HTTP layers report the same condition differently:
  - `403` — `NacosException.NO_RIGHT` on the gRPC path.
  - `10001` — `ErrorCode.ACCESS_DENIED` in the OpenAPI envelope.
- A missing token and an expired token produce the same codes, so both are treated as "obtain a
  fresh token". The retry is bounded to one so a genuinely unauthorized identity fails fast instead
  of looping.

See `NacosGrpcConnection.IsTokenRejection` and `NacosTokenProvider`.

## Reconnection and state replay

A dropped stream is detected by the read loop and by idle health checks. Recovery uses exponential
backoff with jitter (`NacosOptions.Reconnect`) and is guarded so only one recovery runs at a time.

Two subtle rules are worth calling out:

- A read loop records the *epoch* of the connection it belongs to. A loop from a connection that has
  already been replaced must stay silent, or its stale failure would tear down the healthy
  connection that superseded it.
- After a reconnect, every configuration listener, service subscription and ephemeral instance
  registration is replayed, because the server discarded that connection-scoped state.
- A request that finds no connection waits for the recovery instead of being failed against a
  connection that is momentarily absent. The wait ends with the recovery's *first* attempt: a
  success sends the request (once — a failing request is still never replayed), a refusal hands the
  caller back to its own local copy, and the whole wait is bounded by `NacosOptions.RequestTimeout`,
  which is the same budget the request itself gets. A client that has never connected does not wait
  at all: that is a first connection rather than a reconnect, and blocking a caller on the
  background probe that is establishing it would only make a failed startup slow. The official
  client's `RpcClient.request` polls for the same property within its own budget; a signal is the
  same contract without the polling, and the attempt boundary is what keeps a long outage from
  costing every read the whole budget.
- The state the server reports when a registration is confirmed is applied rather than discarded.
  `SubscribeServiceResponse.ServiceInfo` carries the service state at the instant the subscription
  took effect — exactly the window the seeding read cannot see — and is applied through the same
  path as a push, both on the first subscription and at the reconnect replay. The server fills
  `lastRefTime` (`ServiceUtil.doSelectInstances` uses the wall clock), so the instance comparison and
  the staleness check that already guard pushes also suppress the echo of an unchanged state.
- `ConfigChangeBatchListenResponse.ChangedConfigs` is acted on at the reconnect replay: it names the
  configurations the server holds a different value for than the hash the client said it had, which
  is what changed while the listener was gone. Registration also records the hash of the value it
  just read, so the server answers "nothing changed for you" instead of reporting every listened
  configuration, and a configuration that never changed is not announced as changed on a reconnect.
  The first registration deliberately ignores the list, because the read that follows it covers the
  same window and acting on it would deliver the initial value twice.

See `NacosGrpcConnection.HandleConnectionLossAsync`, `ResolveChannelAsync`, `ReadLoopAsync` and
`RaiseReconnectedAsync`, and `NacosNamingClient.ApplyServiceStateAsync` /
`NacosConfigClient.ApplyMissedChangesAsync`.

## References

- [Verified gRPC specification](protocol-grpc-spec.md) — upstream files, commit hashes and
  serialization assertions.
- `src/Lolitacoding.Nacos/Protocol/Proto/nacos_grpc_service.proto` — a verbatim copy of the upstream
  proto (`api/src/main/proto/nacos_grpc_service.proto`), Apache-2.0.
