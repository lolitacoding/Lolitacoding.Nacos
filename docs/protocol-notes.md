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
- `NotifySubscriberRequest` → `NotifySubscriberResponse`, and the payload already carries the
  instance list.
- `LockNotificationRequest` → `LockNotificationResponse`, and the waiter the notification names is
  released.

The first non-null response any handler produces is the one written back, exactly as the official
client does, and the remaining handlers still run. A handler that returns `null` — one that only
observes a push, like a configuration listener — produces no reply.

Handlers run on the read loop and are isolated: one that throws is logged without stopping the loop
or the other features sharing the connection. See `NacosGrpcConnection.DispatchServerRequestAsync`.

## Configuration specifics

- **Publish** is a `ConfigPublishRequest`. The content type is not a field of its own: the server
  reads it from the free-form `additionMap` under the key `"type"`.
- **MD5 verification** compares the content hash against the value the server reports. A mismatch is
  an error, not a warning, because applying corrupted content would silently misconfigure the
  application. Controlled by `NacosConfigOptions.VerifyMd5`.
- **Listeners** use `ConfigBatchListenRequest` (note: the request class is `ConfigBatchListenRequest`;
  its *response* is `ConfigChangeBatchListenResponse`). The hash the client already holds is sent so
  the server can skip notifying about a change the client has already applied, and a change whose
  MD5 matches the last one delivered is suppressed.

## Naming specifics

- Registration, deregistration and queries use `InstanceRequest`/`PersistentInstanceRequest` and
  `ServiceQueryRequest`.
- Subscriptions use `SubscribeServiceRequest`; changes arrive as `NotifySubscriberRequest`, which
  carries the complete instance list, and are answered with `NotifySubscriberResponse`.
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
  cached copy refreshed every `NacosAiOptions.CacheRefreshInterval`, and it is the only operation in
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
3. The refresh interval is the single `NacosAiOptions.CacheRefreshInterval` the MCP subscription also
   uses, where the official client reads a separate `nacosAiPromptCacheUpdateInterval`. One knob is a
   deliberate simplification: both subscriptions are the same kind of poll and the default is the same
   10 s either way.

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

See `NacosGrpcConnection.HandleConnectionLossAsync`, `ReadLoopAsync` and `RaiseReconnectedAsync`.

## References

- [Verified gRPC specification](protocol-grpc-spec.md) — upstream files, commit hashes and
  serialization assertions.
- `src/Lolitacoding.Nacos/Protocol/Proto/nacos_grpc_service.proto` — a verbatim copy of the upstream
  proto (`api/src/main/proto/nacos_grpc_service.proto`), Apache-2.0.
