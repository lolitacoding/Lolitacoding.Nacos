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
connection is opened on first use instead: an application that never takes a lock would otherwise
carry a handshake, a heartbeat and one socket per cluster node for a module it never calls.

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
- `ServiceInfo.allIps` serialises as `allIPs` (capital `IP`) on the wire.

## Distributed lock specifics

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
