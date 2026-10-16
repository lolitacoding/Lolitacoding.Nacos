# Nacos Client gRPC Protocol — Verified Specification for a C# Re-Implementation

**Primary source verified against:** `github.com/alibaba/nacos`, branch `develop`, commit
`1511b5a85f744acb0ad54e8213b1f4d82674799c`, Maven revision **`3.3.0-RC`** (read from `pom.xml`
line 91). Cross-checked against tags **`2.5.1`** and **`2.4.3`**.

Every claim below was read from the actual source (a full shallow clone of `develop`, plus
`git show <tag>:<path>` for 2.x). Where a claim is derived rather than directly quoted, it is
marked **(DERIVED)**. Where I could not verify, it is marked **UNVERIFIED**.

Repo-root-relative paths are given for each file. The canonical URL form is
`https://github.com/alibaba/nacos/blob/develop/<path>`.

---

## 0. Corrections to premises in the request

Three items in the brief are wrong and matter for implementation:

| Premise in brief | Reality (verified) |
| --- | --- |
| `ConfigChangeBatchListenRequest` exists | **It does not exist.** The request class is **`ConfigBatchListenRequest`**; its *response* is `ConfigChangeBatchListenResponse`. See `META-INF/services/com.alibaba.nacos.api.remote.Payload`. |
| `@NacosRequestType` annotation and a `RequestType` enum select the handler | **Neither exists anywhere in the repo.** There is no `@NacosRequestType`, no `RequestType` enum, and no `ResponseType` annotation. Routing is done by **`Class.getSimpleName()`** as the map key. |
| `ConnectionSetupResponse` exists | **It does not exist.** The server's handshake acknowledgement is **`SetupAckRequest`** (a *request* type, pushed server→client). |

Four more high-value findings, **all confirmed against the Nacos repo's own serialization
assertions** (`api/src/test/java/...`), not from reasoning:

* **The body DOES include `"headers"`, `"requestId"` and `"module"`.** `GrpcUtils.convertRequestToByte`
  clears headers *then* serializes, and `Include.NON_NULL` does not drop empty collections — so the
  body carries `"headers":{}` even though the authoritative headers live in `metadata.headers`.
  `getModule()` and `getRequestId()` are ordinary read-only bean getters and **are** emitted.
  Proof: `EmptyContentRequestTest.java:31` asserts the exact string
  `{"headers":{"clientIp":"1.1.1.1"},"requestId":"1","module":"internal"}`.
* **Every response body includes `"success"`** (from `Response.isSuccess()`), e.g.
  `{"resultCode":200,"errorCode":0,"requestId":"1","success":true}`.
* **`ConfigQueryResponse.isBeta` serializes as JSON `"beta"`**, not `"isBeta"`.
  Proof: `ConfigQueryResponseTest.java:74` deserializes `...\"tag\":\"tag\",\"beta\":false,...`.
* **`ServiceInfo.allIps` serializes as `"allIPs"`** (capital `IP`, from `isAllIps()`).
  Proof: `QueryServiceResponseTest.java:66` and `NotifySubscriberRequestTest.java:66`.

⇒ **A C# implementation must therefore be liberal on input** (ignore `headers`/`requestId`/`module`
inside the body; they are redundant with `metadata`) **and should emit them on output** to stay
byte-compatible with what Nacos clients/servers assert and log.

---

## 1. How `Payload` is built and parsed

### 1.1 The proto envelope

`api/src/main/proto/nacos_grpc_service.proto` — field numbers confirmed **byte-identical across
2.4.3, 2.5.1 and develop** (`git diff 2.5.1 HEAD -- <proto>` produced no output).

```proto
syntax = "proto3";
import "google/protobuf/any.proto";
import "google/protobuf/timestamp.proto";
option java_multiple_files = true;
option java_package = "com.alibaba.nacos.api.grpc.auto";

message Metadata {
  string type = 3;
  string clientIp = 8;
  map<string, string> headers = 7;
}
message Payload {
  Metadata metadata = 2;
  google.protobuf.Any body = 3;
}
service Request { rpc request (Payload) returns (Payload) {} }
service BiRequestStream { rpc requestBiStream (stream Payload) returns (stream Payload) {} }
```

### 1.2 What goes into `google.protobuf.Any` — CONFIRMED

Source: `common/src/main/java/com/alibaba/nacos/common/remote/client/grpc/GrpcUtils.java`

The `Any` builder sets **only** `value`. `setTypeUrl(...)` is **never called**:

```java
// convert(Request)
byte[] jsonBytes = convertRequestToByte(request);
return payloadBuilder
    .setBody(Any.newBuilder().setValue(UnsafeByteOperations.unsafeWrap(jsonBytes))).build();

// convert(Response)
Metadata.Builder metaBuilder =
    Metadata.newBuilder().setType(response.getClass().getSimpleName());
return Payload.newBuilder()
    .setBody(Any.newBuilder().setValue(UnsafeByteOperations.unsafeWrap(jsonBytes)))
    .setMetadata(metaBuilder.build()).build();
```

* **`type_url` is left at the protobuf default, i.e. the empty string `""`.** ✅ confirmed.
  Exhaustive verification: `grep -rn "setTypeUrl|type_url|typeUrl"` across the whole repo returns
  hits **only** in the unrelated `istio/` xDS module (`CdsGenerator`, `EdsGenerator`,
  `LdsGenerator`, `RdsGenerator`, `NacosXdsService`, `ServiceEntryMcpGenerator`). **No hit in
  `GrpcUtils`, `GrpcRequestAcceptor`, `GrpcBiStreamRequestAcceptor`, `GrpcConnection`, or any
  remote/payload code.**
* **`value` is exactly the UTF-8 JSON bytes of the payload object.** ✅ confirmed — produced by
  `JsonUtils.toJsonBytes(obj)` and wrapped with `UnsafeByteOperations.unsafeWrap`. On parse the
  bytes are read back via `payload.getBody().getValue()` → `asReadOnlyByteBuffer()` →
  `JsonUtils.toObj(InputStream, classType)`.

### 1.3 `Metadata.type` is the simple class name — CONFIRMED

`type` is set from `request.getClass().getSimpleName()` / `response.getClass().getSimpleName()`
in all three `convert` overloads. This is the registry key and the handler-lookup key. It is
**not** a fully-qualified name — the official spec states this explicitly:

> "Do not use Java package names in `metadata.type`; use the simple class name."
> — `specs/en/grpc-api/api-spec.md` §2

### 1.4 `Metadata.headers` — CONFIRMED, with the exact real-world keys

`GrpcUtils.convert` does `metaBuilder.putAllHeaders(request.getHeaders())`, and `Request.headers`
is a **case-insensitive** `TreeMap<>(String.CASE_INSENSITIVE_ORDER)`
(`api/.../remote/request/Request.java` lines 30).

On receive, headers are restored onto the `Request` object:
`((Request) obj).putAllHeader(payload.getMetadata().getHeadersMap())` in `GrpcUtils.parse`.

**Headers are transported in `metadata.headers`, NOT inside the JSON body** (spec §2 rule). The
body's `headers` field is cleared before serialization precisely so it does not duplicate them.

Real header keys verified in client source:

| Header key (constant) | Literal value | Where set |
| --- | --- | --- |
| `Constants.CHARSET_KEY` | **`charset`** | `client/.../config/impl/ConfigTransportClient.java:130` |
| `Constants.CLIENT_APPNAME_HEADER` | `Client-AppName` | `ConfigTransportClient.java:126` |
| `Constants.CLIENT_REQUEST_TS_HEADER` | `Client-RequestTS` | `ConfigTransportClient.java:127` |
| `Constants.CLIENT_REQUEST_TOKEN_HEADER` | `Client-RequestToken` | `ConfigTransportClient.java:128` |
| `CONFIG_INFO_HEADER` | (config-info) | `ConfigTransportClient.java:129` |
| `Constants.Config.NOTIFY_HEADER` | **`notify`** | `client/.../ClientWorker.java:1532` |
| `Constants.ACCESS_TOKEN` | **`accessToken`** | injected via `SecurityProxy.getIdentityContext()` (`client/.../security/SecurityProxy.java:98-109`), consumed by `ClientWorker.java:1631` and `NamingGrpcClientProxy.java:515` |
| `Constants.APPNAME` | `AppName` | read server-side from `ConnectionSetupRequest.labels` |

`Constants.java` (`api/src/main/java/com/alibaba/nacos/api/common/Constants.java`) holds:
line 34 `APPNAME="AppName"`, line 36 `CLIENT_VERSION_KEY="ClientVersion"`, line 56
`NAMESPACE_ID="namespaceId"`, line 92 `ACCESS_TOKEN="accessToken"`, line 227
`CHARSET_KEY="charset"`, line 274 `NOTIFY_HEADER="notify"`, line 256 `APP_CONN_PREFIX="app_"`.

**`"exactMatch"` is UNVERIFIED.** I searched the entire repository (`grep "exactMatch"`); every
hit is the unrelated `AntPathMatcher` field / AI search boost — **no request header named
`exactMatch` exists in Nacos 3.3.0-RC.** It is not used in the gRPC path.

**`"namespace"` as a header is also not verified as a header.** Namespace travels as a *body
field* (`AbstractConfigRequest.tenant`, `AbstractNamingRequest.namespace`) and, for connection
scope, as `ConnectionSetupRequest.tenant`. `PropertyKeyConst.NAMESPACE = "namespace"` is the
*client property* key, not a wire header.

### 1.5 `clientIp`

Set by the sender: `.setClientIp(NetUtils.localIp())` in both request overloads. The **response**
payload (from the server) does **not** set `clientIp`. Note the official spec warns server-side
trust must come from connection metadata, not this field:

> "`metadata.clientIp` … Server-side request context uses connection metadata as the trusted
> source." — `specs/en/grpc-api/api-spec.md` §2

### 1.6 `PayloadRegistry`

Source: `common/src/main/java/com/alibaba/nacos/common/remote/PayloadRegistry.java`

It is a **`ServiceLoader<Payload>`** scan, keyed by `getClass().getSimpleName()`:

```java
private static synchronized void scan() {
    if (initialized) { return; }
    ServiceLoader<Payload> payloads = ServiceLoader.load(Payload.class);
    for (Payload payload : payloads) {
        register(payload.getClass().getSimpleName(), payload.getClass());
    }
    initialized = true;
}
static void register(String type, Class<?> clazz) {
    if (Modifier.isAbstract(clazz.getModifiers())) { return; }
    if (REGISTRY_REQUEST.containsKey(type)) {
        throw new RuntimeException(String.format("Fail to register, type: %s, clazz: %s", type, clazz.getName()));
    }
    REGISTRY_REQUEST.put(type, clazz);
}
public static Class<?> getClassByType(String type) { return REGISTRY_REQUEST.get(type); }
```

The SPI file is
`api/src/main/resources/META-INF/services/com.alibaba.nacos.api.remote.Payload`.
`GrpcUtils.parse` throws `RemoteException(NacosException.SERVER_ERROR, "Unknown payload type:" + type)`
when the type is not registered.

### 1.7 JSON engine and exact serialization settings

| | 2.x (verified on `2.5.1`) | develop = 3.3.0-RC |
| --- | --- | --- |
| Facade | `com.alibaba.nacos.common.utils.JacksonUtils` | `com.alibaba.nacos.api.utils.json.JsonUtils` |
| Engine | static `ObjectMapper` | pluggable `NacosJsonAdapter`, SPI-selected |

**2.5.1** — `common/.../utils/JacksonUtils.java`:
```java
static ObjectMapper mapper = new ObjectMapper();
static {
    mapper.disable(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES);
    mapper.setSerializationInclusion(Include.NON_NULL);
}
```

**develop** — `common/src/main/java/com/alibaba/nacos/common/json/Jackson2JsonAdapterDelegate.java`
lines 150-155 (and the Jackson 3 twin `Jackson3JsonAdapter.java`):
```java
private static ObjectMapper createObjectMapper() {
    ObjectMapper objectMapper = new ObjectMapper();
    objectMapper.disable(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES);
    objectMapper.setSerializationInclusion(Include.NON_NULL);
    return objectMapper;
}
```
Selection order (`api/.../utils/json/JsonAdapterSelector.java` lines 135-151): system property
`nacos.client.json.adapter` (`auto` | `jackson2` | `jackson3`); in `auto`, **Jackson 3 wins over
Jackson 2**, else the single available adapter.

**⇒ The effective JSON contract is unchanged: `Include.NON_NULL` (null fields omitted; empty
collections/`false`/`0` are emitted), `FAIL_ON_UNKNOWN_PROPERTIES` disabled, unknown fields
ignored. Output is property-name compatible between 2.x and 3.x.**

There are **no** `@JsonAnyGetter`, `@JsonProperty`, `@JsonIgnore` or custom serializers on any
`*Request`/`*Response` remote payload class. The only `@JsonAnyGetter` occurrences in `api/` are
in MCP registry models (`ServerResponse`, `McpRegistryServerDetail`) — unrelated. Payload classes
use plain bean serialization, except `Instance`/`ServiceInfo` which carry
`@JsonInclude(Include.NON_NULL)`.

---

## 2. Request type → response type inventory (CONFIG + NAMING)

Read verbatim from `META-INF/services/com.alibaba.nacos.api.remote.Payload` (develop) and
cross-confirmed against `RequestHandler<T, S>` generic signatures and `specs/en/grpc-api/api-spec.md` §7.

| Request `metadata.type` | Response `metadata.type` | Channel | Handler (`RequestHandler` class) |
| --- | --- | --- | --- |
| `ConfigQueryRequest` | `ConfigQueryResponse` | unary | `ConfigQueryRequestHandler` |
| `ConfigPublishRequest` | `ConfigPublishResponse` | unary | `ConfigPublishRequestHandler` |
| `ConfigRemoveRequest` | `ConfigRemoveResponse` | unary | `ConfigRemoveRequestHandler` |
| **`ConfigBatchListenRequest`** | **`ConfigChangeBatchListenResponse`** | unary | `ConfigChangeBatchListenRequestHandler` |
| `ConfigChangeNotifyRequest` | `ConfigChangeNotifyResponse` | **stream push (S→C)** | client-side only |
| `ClientConfigMetricRequest` | `ClientConfigMetricResponse` | unary | — |
| `ConfigFuzzyWatchRequest` | `ConfigFuzzyWatchResponse` | unary | `ConfigFuzzyWatchRequestHandler` (new in 3.x) |
| `ConfigFuzzyWatchChangeNotifyRequest` | `ConfigFuzzyWatchChangeNotifyResponse` | stream push | new in 3.x |
| `ConfigFuzzyWatchSyncRequest` | `ConfigFuzzyWatchSyncResponse` | stream push | new in 3.x |
| `InstanceRequest` | `InstanceResponse` | unary | `InstanceRequestHandler` |
| `PersistentInstanceRequest` | `InstanceResponse` | unary | `PersistentInstanceRequestHandler` |
| `BatchInstanceRequest` | `BatchInstanceResponse` | unary | `BatchInstanceRequestHandler` |
| `ServiceQueryRequest` | **`QueryServiceResponse`** | unary | `ServiceQueryRequestHandler` |
| `ServiceListRequest` | `ServiceListResponse` | unary | `ServiceListRequestHandler` |
| `SubscribeServiceRequest` | `SubscribeServiceResponse` | unary | `SubscribeServiceRequestHandler` |
| `NotifySubscriberRequest` | `NotifySubscriberResponse` | **stream push (S→C)** | client-side only |
| `NamingFuzzyWatchRequest` | `NamingFuzzyWatchResponse` | unary | `NamingFuzzyWatchRequestHandler` (new in 3.x) |
| `NamingFuzzyWatchChangeNotifyRequest` | `NamingFuzzyWatchChangeNotifyResponse` | stream push | new in 3.x |
| `NamingFuzzyWatchSyncRequest` | `NamingFuzzyWatchSyncResponse` | stream push | new in 3.x |
| `ConnectionSetupRequest` | **`SetupAckRequest`** (no `ConnectionSetupResponse`) | **stream** | `GrpcBiStreamRequestAcceptor` inline |
| `ServerCheckRequest` | `ServerCheckResponse` | **unary** | inline in `GrpcRequestAcceptor` |
| `HealthCheckRequest` | `HealthCheckResponse` | unary | `HealthCheckRequestHandler` |
| `ClientDetectionRequest` | `ClientDetectionResponse` | **stream push (S→C)** | client-side only |
| `ConnectResetRequest` | `ConnectResetResponse` | **stream push (S→C)** | client-side only |
| `PushAckRequest` | — | stream (C→S) | — |

Note the naming asymmetry: `ServiceQueryRequest` → **`QueryServiceResponse`** (not
`ServiceQueryResponse`).

**There is no `@NacosRequestType`, no `RequestType` enum, and no `ResponseType` annotation.**
Routing uses the simple name as a map key:

* Server: `GrpcRequestAcceptor.request()` calls
  `requestHandlerRegistry.getByRequestType(type)`, and `RequestHandlerRegistry` registers with
  `registryHandlers.putIfAbsent(tClass.getSimpleName(), requestHandler)` where `tClass` is the
  first actual type argument of `RequestHandler<T, S>`. Unknown type ⇒
  `ErrorResponse.build(NacosException.NO_HANDLER, "RequestHandler Not Found")`.

---

## 3. Exact JSON field names per message

**Universal notes** (each proven by an assertion in the repo's own test suite)

* All fields with `null` value are omitted (`Include.NON_NULL`). Empty collections, `false` and `0`
  are **emitted**.
* Every `Request` subclass also serializes:
  * `headers` — object. **Emptied before send but still emitted as `{}`** (proven:
    `EmptyContentRequestTest.java:31`, `ConnectResetRequestTest.java:46`, `SetupAckRequestTest.java:45`).
  * `requestId` — string (proven: `ConfigQueryRequestTest.java:85`).
  * `module` — string, from `getModule()`; `"config"` / `"naming"` / `"internal"`
    (proven: `ConfigQueryRequestTest.java:80`, `BasedNamingRequestTest.java:62`,
    `ConnectionSetupRequestTest.java:44`).
* Every `Response` subclass also serializes:
  * `resultCode` (int, default 200), `errorCode` (int), `message` (string), `requestId` (string)
  * **`success`** (boolean, from `isSuccess()`) — proven: `ConfigQueryResponseTest.java:46,65`,
    `ServerLoaderInfoResponseTest.java:51`, `EmptyContentResponseTest.java:34`.
* Canonical success/failure response bodies asserted in the repo:
  * `{"resultCode":200,"errorCode":0,"requestId":"1","success":true}`
  * `{"resultCode":500,"errorCode":0,"message":"Fail","success":false}`

### 3.0 Verbatim expected JSON (from the repo's own tests)

These are byte-exact strings asserted by Nacos' unit tests — the strongest available ground truth.

**`ConfigQueryRequest` body** (`ConfigQueryRequestTest.java:69-71`) — *deserialize* fixture:
```json
{"headers":{"notify":"true"},"dataId":"test_data","group":"group","tenant":"test_tenant","module":"config","tag":"tag","localMd5":"deserialized-md5"}
```
Note `"notify"` appears as a top-level field on the wire form used in the fixture (the `isNotify()`
getter is a bean property). Serialization asserts `module`, `dataId`, `group`, `tenant`, `tag`,
`requestId` and `localMd5`.

**`ConfigQueryResponse` body** (`ConfigQueryResponseTest.java:72-74`):
```json
{"resultCode":200,"errorCode":0,"requestId":"2239753e-e682-441c-83cf-fb8129ca68a4","content":"success","encryptedDataKey":"encryptedKey","contentType":"text","md5":"test_MD5","lastModified":1111111,"tag":"tag","beta":false,"success":true}
```

**`ConfigChangeBatchListenResponse` body** (`ConfigChangeBatchListenResponseTest.java:66-68`):
```json
{"resultCode":200,"errorCode":0,"requestId":"061e36b0-c7bd-4fd0-950c-73b13ca1cb2f","changedConfigs":[{"group":"group","dataId":"test_data","tenant":"test_tenant"}],"success":true}
```
Serialization also asserts exactly
`"changedConfigs":[{"dataId":"test_data","group":"group","tenant":"test_tenant"}]`.

**`ConfigBatchListenRequest` body** (`ConfigBatchListenRequestTest.java:36-40,46-48`):
`"listen":true`, `"module":"config"`, `"requestId":"..."`, and exactly
```json
{"headers":{"header1":"test_header1"},"listen":true,"configListenContexts":[{"group":"group","md5":"test_MD5","dataId":"test_data","tenant":"test_tenant"}],"module":"config"}
```

**`ConnectionSetupRequest` body** (`ConnectionSetupRequestTest.java:38-45,50-54`):
asserts `"clientVersion":"2.2.2"`, `"tenant":"testNamespaceId"`,
`"labels":{"labelKey":"labelValue"}`, `"abilityTable":{`, `"module":"internal"`, `"requestId":"1"`.
Deserialize fixture:
```json
{"headers":{},"requestId":"1","clientVersion":"2.2.2","abilities":{"remoteAbility":{"supportRemoteConnection":false},"configAbility":{"supportRemoteMetrics":false},"namingAbility":{"supportDeltaPush":false,"supportRemoteMetric":false}},"tenant":"testNamespaceId","labels":{"labelKey":"labelValue"},"module":"internal"}
```
⚠️ **`"abilities"` in that fixture is NOT a `ConnectionSetupRequest` field** — it is a *legacy 2.x*
shape still accepted because `FAIL_ON_UNKNOWN_PROPERTIES` is disabled (the test asserts only that
the known fields parse). **3.x carries `abilityTable`** (map<string,boolean>), not `abilities`.

⚠️ **The body `module` is not the connection `module` label.** The body keeps `"internal"` for a
transport message (`InternalRequest.getModule`, `api/.../remote/request/InternalRequest.java:30`) and
the server ignores it. Routing uses the **`labels.module`** entry instead
(`RemoteConstants.LABEL_MODULE`, `api/.../remote/RemoteConstants.java:36`), read in
`core/.../remote/grpc/GrpcBiStreamRequestAcceptor.java:160-183`. The naming, lock and AI managers
accept a connection **only** when that label matches theirs — `ConnectionBasedClientManager.java:60-62`
(naming), `LockConnectionEventListener.java:54-55` (lock),
`AiConnectionBasedClientManager.java:56-57` (ai) — so a client must open one connection per module
and label it accordingly.

**`InstanceRequest` body** (`InstanceRequestTest.java:41-46`):
```json
{"headers":{},"namespace":"namespace","serviceName":"service","groupName":"group","type":"deregisterInstance","instance":{"port":0,"weight":1.0,"healthy":true,"enabled":true,"ephemeral":true,"metadata":{},"instanceIdGenerator":"simple","instanceHeartBeatInterval":5000,"instanceHeartBeatTimeOut":15000,"ipDeleteTimeout":30000},"module":"naming"}
```
Note the four **derived** instance fields (`instanceIdGenerator`, `instanceHeartBeatInterval`,
`instanceHeartBeatTimeOut`, `ipDeleteTimeout`) come from `getX()` getters reading
`PreservedMetadataKeys` out of `metadata` — they are computed, not stored, and **are serialized**.

**`ServiceQueryRequest` body** (`ServiceQueryRequestTest.java:41-43`):
```json
{"headers":{},"namespace":"namespace","serviceName":"service","groupName":"group","cluster":"DEFAULT","healthyOnly":true,"udpPort":0,"module":"naming"}
```

**`ServiceListRequest` body** (`ServiceListRequestTest.java:43-45`):
```json
{"headers":{},"namespace":"namespace","serviceName":"","groupName":"group","pageNo":1,"pageSize":10,"selector":"label","module":"naming"}
```

**`SubscribeServiceRequest` body** (`SubscribeServiceRequestTest.java:40-42`):
```json
{"headers":{},"namespace":"namespace","serviceName":"service","groupName":"group","subscribe":false,"clusters":"aa,bb","module":"naming"}
```

**`NotifySubscriberRequest` body** (`NotifySubscriberRequestTest.java:63-67`, a hand-built request):
```json
{"headers":{},"namespace":"namespace","serviceName":"service","groupName":"group","serviceInfo":{"name":"service","groupName":"group","cacheMillis":1000,"hosts":[],"lastRefTime":0,"checksum":"","allIPs":false,"reachProtectionThreshold":false,"valid":true},"module":"naming"}
```
⚠️ That unit test fills the three identity fields in itself; the **server never does** — see §3.10. A
captured push from a real server carries `serviceInfo` (and `module`) only.

**`QueryServiceResponse` body** (`QueryServiceResponseTest.java:64-67`):
```json
{"resultCode":200,"errorCode":0,"serviceInfo":{"cacheMillis":1000,"hosts":[],"lastRefTime":0,"checksum":"","allIPs":false,"reachProtectionThreshold":false,"valid":true},"success":true}
```
Also asserts `"success":true`, `"resultCode":200`, `"errorCode":0`.

**`ConnectResetRequest`** (`ConnectResetRequestTest.java:46`):
```json
{"headers":{},"requestId":"1","serverIp":"127.0.0.1","serverPort":"8888","connectionId":"...","module":"internal"}
```

**`SetupAckRequest`** (`SetupAckRequestTest.java:45`):
```json
{"headers":{},"requestId":"1","abilityTable":{"fuzzyWatch":true},"module":"internal"}
```

**`ServerCheckResponse`** (`ServerCheckResponseTest.java:50`):
```json
{"resultCode":200,"errorCode":0,"connectionId":"35643245_1.1.1.1_3306","success":true,"supportAbilityNegotiation":...}
```

**`ClientDetectionRequest` / `EmptyContentRequest`** (`EmptyContentRequestTest.java:31`):
```json
{"headers":{"clientIp":"1.1.1.1"},"requestId":"1","module":"internal"}
```

⚠️ **Serialization order is irrelevant** — all tests use `json.contains(...)`, not equality, except
`BasedConfigRequestTest` which enables `SORT_PROPERTIES_ALPHABETICALLY` only in the *test* mapper.
Nacos' production mapper does **not** sort, so a C# implementation must not depend on field order.


### 3.1 ConfigQueryRequest  → `ConfigQueryResponse`
`api/src/main/java/com/alibaba/nacos/api/config/remote/request/ConfigQueryRequest.java`

| JSON field | Type | Meaning |
| --- | --- | --- |
| `dataId` | string | from `AbstractConfigRequest` |
| `group` | string | from `AbstractConfigRequest` |
| `tenant` | string | namespace id; from `AbstractConfigRequest` |
| `tag` | string | config tag |
| `localMd5` | string | **new in 3.3.0** — conditional GET |
| `headers` | object | emitted (usually `{"notify":"true"}` from the client) |
| `requestId` | string | emitted; also mirrored in `metadata.headers` |
| `module` | string | `"config"` |
| `notify` | boolean | from `isNotify()`, which reads the `notify` header (default `false`) |

`isNotify()` is a getter reading header `notify`, so `notify` **is** a serialized bean property.
Confirmed by the repo's own fixture `ConfigQueryRequestTest.java:92-93`, which round-trips
`"notify":true` and `"headers":{"notify":"true"}` in the same body — the value is duplicated.

`api/src/main/java/com/alibaba/nacos/api/config/remote/response/ConfigQueryResponse.java`

| JSON field | Type | Meaning |
| --- | --- | --- |
| `resultCode` | int | 200 ok, 500 fail |
| `errorCode` | int | e.g. 300 / 304 / 400 / 403 |
| `message` | string | |
| `requestId` | string | |
| `content` | string | config content (absent on 304) |
| `encryptedDataKey` | string | |
| `contentType` | string | e.g. `text` |
| `md5` | string | |
| `lastModified` | long | |
| **`beta`** | boolean | ⚠️ field is `isBeta`, getter `isBeta()` ⇒ JSON name **`beta`** |
| `tag` | string | |

Error constants: `CONFIG_NOT_FOUND = 300` (present in 2.x too); `CONFIG_NOT_MODIFIED = 304`
(**new in 3.3.0**); `CONFIG_QUERY_CONFLICT = 400`; `NO_RIGHT = 403`.

### 3.2 ConfigPublishRequest → `ConfigPublishResponse`
`api/.../config/remote/request/ConfigPublishRequest.java`

| JSON field | Type | Meaning |
| --- | --- | --- |
| `dataId` / `group` / `tenant` | string | inherited |
| `content` | string | config content |
| `casMd5` | string | compare-and-swap md5 |
| `additionMap` | object<string,string> | additional params, e.g. `type`, `src_user` |

`ConfigPublishResponse` adds **no** fields (only the 4 base fields + builder statics).

### 3.3 ConfigRemoveRequest → `ConfigRemoveResponse`
| JSON field | Type | Meaning |
| --- | --- | --- |
| `dataId` / `group` / `tenant` | string | inherited |
| `tag` | string | |

`ConfigRemoveResponse` adds **no** fields.

### 3.4 ConfigBatchListenRequest → `ConfigChangeBatchListenResponse`
`api/.../config/remote/request/ConfigBatchListenRequest.java` — **note the nested class**

| JSON field | Type | Meaning |
| --- | --- | --- |
| `dataId` / `group` / `tenant` | string | inherited |
| `listen` | boolean | `true` = add listener, `false` = remove (default `true`) |
| `configListenContexts` | array of object | see below |

`ConfigListenContext` (nested, package-private fields + public getters):

| JSON field | Type |
| --- | --- |
| `group` | string |
| `md5` | string |
| `dataId` | string |
| `tenant` | string |

`api/.../config/remote/response/ConfigChangeBatchListenResponse.java`

| JSON field | Type |
| --- | --- |
| `changedConfigs` | array of `ConfigContext` |
| (base fields) | |

`ConfigContext` (nested): `group`, `dataId`, `tenant`.

### 3.5 InstanceRequest → `InstanceResponse`
`api/.../naming/remote/request/InstanceRequest.java`

| JSON field | Type | Meaning |
| --- | --- | --- |
| `namespace` | string | from `AbstractNamingRequest` |
| `serviceName` | string | from `AbstractNamingRequest` |
| `groupName` | string | from `AbstractNamingRequest` |
| `type` | string | operation: `registerInstance` / `deregisterInstance` |
| `instance` | object | nested `Instance` |

`InstanceResponse` adds only **`type`** (string). `BatchInstanceResponse extends InstanceResponse`
and adds nothing.

`Instance` (`api/.../naming/pojo/Instance.java`, `@JsonInclude(NON_NULL)`) — nested JSON. Stored
fields: `instanceId`, `ip`, `port`(int), `weight`(double, default `1.0`), `healthy`(bool, default
true), `enabled`(bool, default true), `ephemeral`(bool, default true), `clusterName`,
`serviceName`, `metadata`(map<string,string>). **Plus four computed getters that ARE serialized**
(they read `PreservedMetadataKeys` out of `metadata`, with defaults):
`instanceIdGenerator` (default `"simple"`), `instanceHeartBeatInterval` (default 5000),
`instanceHeartBeatTimeOut` (default 15000), `ipDeleteTimeout` (default 30000).

⚠️ `Instance.toString()`/`equals()`/`hashCode()` are used as the comparison key, and Nacos' own
test compares `new Instance()` to the parsed object — so **defaults matter**: a C# client
registering an instance should send `weight:1.0`, `healthy:true`, `enabled:true`,
`ephemeral:true`, `metadata:{}`.

### 3.6 BatchInstanceRequest → `BatchInstanceResponse`
| JSON field | Type |
| --- | --- |
| `namespace` / `serviceName` / `groupName` | string |
| `type` | string |
| `instances` | array of `Instance` |

### 3.7 ServiceQueryRequest → `QueryServiceResponse`
| JSON field | Type | Meaning |
| --- | --- | --- |
| `namespace` / `serviceName` / `groupName` | string | inherited |
| `cluster` | string | cluster filter |
| `healthyOnly` | boolean | |
| `udpPort` | int | legacy udp push port |

`QueryServiceResponse` adds **`serviceInfo`** (object `ServiceInfo`) and `buildFailResponse`.

### 3.8 ServiceListRequest → `ServiceListResponse`
| JSON field | Type |
| --- | --- |
| `namespace` / `serviceName`(`""`) / `groupName` | string |
| `pageNo` | int |
| `pageSize` | int |
| `selector` | string |

`ServiceListResponse` adds **`count`** (int) and **`serviceNames`** (array of string).

### 3.9 SubscribeServiceRequest → `SubscribeServiceResponse`
| JSON field | Type | Meaning |
| --- | --- | --- |
| `namespace` / `serviceName` / `groupName` | string | inherited |
| `subscribe` | boolean | `true` subscribe, `false` unsubscribe |
| `clusters` | string | comma-separated cluster filter |

`SubscribeServiceResponse` adds **`serviceInfo`** (object `ServiceInfo`): the state the server holds at
the moment the subscription became effective. That is where a change that landed between the
client's own read of the service and this response would otherwise be lost; this client serves the
subscription's first element from a read issued *before* the request and does not use the response's
copy, so such a change is not delivered until the next one (see PLAN.md).

### 3.10 NotifySubscriberRequest → `NotifySubscriberResponse`
`NotifySubscriberRequest extends ServerRequest` — **server push, not a subclass of
`AbstractNamingRequest`**, so it declares its own fields:

| JSON field | Type |
| --- | --- |
| `namespace` | string |
| `serviceName` | string |
| `groupName` | string |
| `serviceInfo` | object `ServiceInfo` |

**Only `serviceInfo` is ever populated.** The server builds every push with
`NotifySubscriberRequest.buildNotifySubscriberRequest(serviceInfo)`, which sets nothing else, so the
identity of the pushed service has to be read back out of `serviceInfo.name` and
`serviceInfo.groupName`; the request's own three fields are always absent on the wire. A push
captured from `nacos-server:3.0.2` is:

```json
{"headers":{},"requestId":"1","serviceInfo":{"name":"<service>","groupName":"DEFAULT_GROUP","cacheMillis":10000,"hosts":[{"instanceId":"10.20.30.41#8081#DEFAULT#<service>","ip":"10.20.30.41","port":8081,"weight":1.0,"healthy":true,"enabled":true,"ephemeral":true,"clusterName":"DEFAULT","serviceName":"DEFAULT_GROUP@@<service>","metadata":{},"instanceHeartBeatInterval":5000,"instanceHeartBeatTimeOut":15000,"instanceIdGenerator":"simple","ipDeleteTimeout":30000}],"lastRefTime":1792173476703,"checksum":"","allIPs":false,"reachProtectionThreshold":false,"valid":true},"module":"naming"}
```

The Java client reads it the same way: `NamingPushRequestHandler.requestReply` passes
`notifyRequest.getServiceInfo()` to the `ServiceInfoHolder` and never looks at the request's own
fields.

**The pushed `checksum` is empty**, so a change cannot be detected by comparing checksums. Java's
`ServiceInfoHolder.processServiceInfo` diffs the pushed state against the last one with
`InstancesDiffer.doDiff`, which keys both instance lists by `ip:port` and compares the instance
descriptions, and it drops a push whose `lastRefTime` is older than the state already applied. This
client does the same.

`NotifySubscriberResponse` adds **no** fields (base `Response` fields + `success` only).

`ServiceInfo` (`api/.../naming/pojo/ServiceInfo.java`, `@JsonInclude(NON_NULL)`) — nested JSON
(sent inside `QueryServiceResponse`, `SubscribeServiceResponse`, `NotifySubscriberRequest`):

| JSON field | Type | Note |
| --- | --- | --- |
| `name` | string | service name |
| `groupName` | string | |
| `clusters` | string | |
| `cacheMillis` | long | default 1000 |
| `hosts` | array of `Instance` | default `[]` |
| `lastRefTime` | long | default 0 |
| `checksum` | string | default `""` |
| **`allIPs`** | boolean | ⚠️ capital `IP`, from `isAllIps()` |
| `reachProtectionThreshold` | boolean | |
| `valid` | boolean | computed (`isValid()`) |

`@JsonIgnore` on `jsonFromServer`, `getKey()`, `getKeyWithoutClusters()`, `getKeyEncoded()` — these
are **not** serialized.

**`ConnectResetRequest`** `serverIp`, `serverPort` are **strings** (not ints) — confirmed by
`ConnectResetRequestTest.java:46` (`"serverPort":"8888"`).

### 3.11 ConnectionSetupRequest → `SetupAckRequest`
`api/.../remote/request/ConnectionSetupRequest.java` (extends `InternalRequest`):

| JSON field | Type | Meaning |
| --- | --- | --- |
| `clientVersion` | string | e.g. the client's full version |
| `tenant` | string | namespace id |
| `labels` | object<string,string> | includes `AppName`; `app_`-prefixed keys become app labels |
| `abilityTable` | object<string,boolean> | ability negotiation; **`null` on the wire when the client is not negotiating** |

Server reply `SetupAckRequest` (`api/.../remote/request/SetupAckRequest.java`, extends `ServerRequest`):
single field **`abilityTable`** (object<string,boolean>) — the **server's** abilities.

### 3.12 Other core/internal payloads

| Type | JSON fields |
| --- | --- |
| `ServerCheckRequest` | (none; base only) |
| `ServerCheckResponse` | `connectionId` (string), `supportAbilityNegotiation` (bool) |
| `HealthCheckRequest` | (none) |
| `HealthCheckResponse` | (none) |
| `ClientDetectionRequest` | (none) |
| `ClientDetectionResponse` | (none) |
| `ConnectResetRequest` | `serverIp`, `serverPort`, `connectionId` (all string) |
| `ConnectResetResponse` | (none) |
| `PushAckRequest` | `requestId` (string), `success` (bool), `exception` (object) |
| `ConfigChangeNotifyRequest` | `dataId`, `group`, `tenant` |
| `ConfigChangeNotifyResponse` | (none) |
| `ServerReloadRequest` | `reloadCount` (int), `reloadServer` (string) |
| `ServerLoaderInfoResponse` | `address` (string), `loaderMetrics` (object<string,string>) |
| `ErrorResponse` | (base fields only) |

### 3.13 Distributed-lock payloads

`lock/.../LockOperationServiceImpl` and `api/.../lock/**` define the lock module. All four payloads
are keyed by their simple class names, as every other payload is.

This is the **3.3** lock service. The module a 3.2 server runs is an earlier contract — `ACQUIRE`,
`RELEASE` and `EXPIRE` only, a `LockInstance` without `owner`, `waitTime` or `waiterRetry`, a bare
boolean `result` and no `lockResult`, and `NACOS_LOCK` as its only lock type. What that means for a
caller is recorded in the [protocol notes](protocol-notes.md#distributed-lock-specifics).

`LockOperationRequest` (client → server, `AbstractLockRequest.getModule()` = `"lock"`):

| Field | Type | Notes |
| --- | --- | --- |
| `lockInstance` | object | The `LockInstance` below. |
| `lockOperationEnum` | string | `ACQUIRE`, `RELEASE`, `RENEW` or `CANCEL_WAIT`. `EXPIRE` is server-side only. |

`LockInstance`:

| Field | Type | Notes |
| --- | --- | --- |
| `key` | string | The lock identifier. |
| `expiredTime` | number | A **duration in milliseconds**, not a timestamp. Negative asks for the server's 30-second default; the server caps the value it accepts. Required non-zero for `ACQUIRE` and `RENEW`. |
| `params` | object<string,> | Opaque; the server records only the entry count. |
| `lockType` | string | `NACOS_LOCK` (default), `REENTRANT`, `NON_REENTRANT`. |
| `owner` | string | The holder. The server defaults it to the connection id when absent. |
| `waitTime` | number | Milliseconds; `-1` means "do not queue", `> 0` queues the request. |
| `waiterRetry` | bool | A retry from the wait queue. The server grants it only at the head of the queue. |

`LockOperationResponse` (server → client): `result` (bool, the bare outcome a renewal returns) and
`lockResult` (`LockResult`: `success`, `reentrantCount`, `waitPosition` — `-1` when not queued —
and `errorMessage`). A refused acquisition is `resultCode = 200` with `lockResult.success = false`;
`resultCode = 500` is a request the server rejected outright.

`LockNotificationRequest` (server push, `extends ServerRequest`): `lockKey`, `lockType`, `owner` and
`notificationType` (`AVAILABLE` or `TIMEOUT`). The client answers `LockNotificationResponse`. The
server pushes it with `RpcPushService.pushWithoutAck`, so the reply is what stops it retrying an
unacknowledged notification.

`LockResult` also serialises a computed `waiting` flag. It is exactly `waitPosition >= 0`, and a
read-only property would be dropped on the way back in, so the client derives it instead.

---

## 4. The gRPC port

**Rule: SDK gRPC port = main HTTP port + 1000. Cluster gRPC port = main HTTP port + 1001.**

*Client side* — `common/.../remote/client/grpc/GrpcSdkClient.java:76-81`:
```java
@Override
public int rpcPortOffset() {
    Integer configuredPortOffset = configuredRpcPortOffset();
    return configuredPortOffset != null ? configuredPortOffset
        : Integer.parseInt(System.getProperty(GrpcConstants.NACOS_SERVER_GRPC_PORT_OFFSET_KEY,
            String.valueOf(Constants.SDK_GRPC_PORT_DEFAULT_OFFSET)));
}
```
`common/.../remote/client/grpc/GrpcClient.java:372`:
```java
int port = serverInfo.getServerPort() + rpcPortOffset();
```

*Constants* — `api/src/main/java/com/alibaba/nacos/api/common/Constants.java`:
```java
public static final Integer SDK_GRPC_PORT_DEFAULT_OFFSET = 1000;      // line 102
public static final Integer CLUSTER_GRPC_PORT_DEFAULT_OFFSET = 1001;  // line 104
```

*Server side* — `core/.../remote/BaseRpcServer.java:107-108`:
```java
public int getServicePort() {
    return EnvUtil.getPort() + rpcPortOffset();
}
```
`core/.../remote/grpc/GrpcSdkServer.java:49-50` returns `Constants.SDK_GRPC_PORT_DEFAULT_OFFSET`;
`GrpcClusterServer.java:49-50` returns `Constants.CLUSTER_GRPC_PORT_DEFAULT_OFFSET`.

*Override property* — `common/.../remote/client/grpc/GrpcConstants.java:36`:
```java
public static final String NACOS_SERVER_GRPC_PORT_OFFSET_KEY = "nacos.server.grpc.port.offset";
```
It is read from a **system property** (client side) and from client properties
(`DefaultGrpcClientConfig.java:345-347`). Note the **server** offsets are compiled-in constants;
the spec says "Server-side SDK and cluster servers use their own default offsets."

Confirmed by the official spec, `specs/en/grpc-api/api-spec.md` §3:

| Server | Default port | Source label | Caller |
| --- | --- | --- | --- |
| SDK gRPC server | `${server.port} + 1000` | `sdk` | SDK clients |
| Cluster gRPC server | `${server.port} + 1001` | `cluster` | Nacos server nodes |

**Version check:** identical `1000`/`1001` in `2.4.3`, `2.5.1` and `develop`. No change.

---

## 5. Connection setup handshake

### 5.1 Client sequence
`common/.../remote/client/grpc/GrpcClient.java:364-438` (`connectToServer`):

1. Compute `port = serverInfo.getServerPort() + rpcPortOffset()`.
2. **Unary `ServerCheckRequest`** on the `Request` service → receive `ServerCheckResponse`,
   read `connectionId` and `supportAbilityNegotiation`.
3. If `supportAbilityNegotiation`, reset the ability context and set the connection ability table
   to `null` (meaning "not yet known").
4. Open the **bi-directional stream** `BiRequestStream.requestBiStream(...)` and bind it.
5. Send **`ConnectionSetupRequest`**:
```java
ConnectionSetupRequest conSetupRequest = new ConnectionSetupRequest();
conSetupRequest.setClientVersion(getClientVersion());
conSetupRequest.setLabels(super.getLabels());
conSetupRequest.setAbilityTable(
    NacosAbilityManagerHolder.getInstance().getCurrentNodeAbilities(abilityMode()));
conSetupRequest.setTenant(super.getTenant());
grpcConn.sendRequest(conSetupRequest);
```
6. If negotiation is required, block up to
   `clientConfig.capabilityNegotiationTimeout()` for the server's abilities; otherwise
   **`Thread.sleep(100L)`** to let the setup land (legacy compat path).

### 5.2 Server handling
`core/.../remote/grpc/GrpcBiStreamRequestAcceptor.java:160-216`:

* If `parseObj instanceof ConnectionSetupRequest`:
  * `appName` = `labels.get("AppName")`, default `"-"`.
  * Build `ConnectionMeta(connectionId, clientIp, remoteIp, remotePort, localPort, GRPC, clientVersion, appName, labels)`.
  * `metaInfo.setNamespaceId(setUpRequest.getTenant())`.
  * Read the TLS flag from the channel attribute `"TLS_PROTECTED"`.
  * Create the connection via `ConnectionGeneratorServiceDelegate`.
  * **If `setUpRequest.getAbilityTable() != null`**, store it on the connection.
  * Reject and `connection.close()` if the server is starting (`isSdkSource() && !isStarted()`) or
    if `connectionManager.register(...)` fails (over limit).
  * **On success, if `abilityTable != null`:**
    ```java
    connection.sendRequestNoAck(new SetupAckRequest(
        NacosAbilityManagerHolder.getInstance().getCurrentNodeAbilities(AbilityMode.SERVER)));
    ```
    i.e. the **server pushes `SetupAckRequest` (with the server's ability table) over the stream**,
    without expecting an ack.

* Else if `parseObj instanceof Response` → `RpcAckCallbackSynchronizer.ackNotify(connectionId, response)`.
  This is how responses to server-initiated pushes are matched.

### 5.3 Client handling of the ack
`GrpcClient.java:281-285`: on receiving `SetupAckRequest`, it is routed to
`setupRequestHandler.requestReply(request, null)` and **no response is returned**. It is matched
against the pending ability-negotiation context. `GrpcClient.java:623-626` reads
`setupAckRequest.getAbilityTable()`.

### 5.4 Unregister / connection reset
* The server **does not** push a message named "ConnectionUnregister". **`ConnectionUnregister`
  does not exist** as a payload type (grep-verified). Connections are removed server-side by
  `ConnectionManager.unregister(...)`, driven by `NacosRuntimeConnectionEjector`
  (`core/.../remote/NacosRuntimeConnectionEjector.java`): when
  `now - lastActiveTime >= KEEP_ALIVE_TIME`, the server sends **`ClientDetectionRequest`** with a
  5000 ms timeout; if the client does not answer successfully, the connection is unregistered.
* To *move* a client, the server sends **`ConnectResetRequest`**
  (`core/.../remote/ConnectionManager.java:293-319`), populated with `serverIp`, `serverPort`,
  `connectionId` when a redirect address is known, via `connection.request(connectResetRequest, 3000L)`.
* Client side (`common/.../remote/client/RpcClient.java:470-501`): `ConnectResetRequestHandler`
  replies with `ConnectResetResponse` and calls `switchServerAsync(...)` to the given server, or
  the next server if no address was provided, then `afterReset(request)`.

Note the spec (`api-spec.md` §4) says the first stream payload "should be" `ConnectionSetupRequest`
and that unary requests are accepted only after the connection is registered.

---

## 6. Unary `Request` vs bi-directional `BiRequestStream`

**Routing rule (verified): it is determined by which RPC method the sender invokes — NOT by
inspecting the payload type.** The same `Payload` envelope and the same JSON body are used on both.

### 6.1 Client → server

| Path | Mechanism | Verified at |
| --- | --- | --- |
| **Unary** | `grpcFutureServiceStub.request(grpcRequest)` on `RequestGrpc.RequestFutureStub`, then `requestFuture.get(timeouts)` | `common/.../grpc/GrpcConnection.java:70-85` (`request`), `:88-119` (`requestFuture`), `:132-178` (`asyncRequest`) |
| **Stream** | `payloadStreamObserver.onNext(convert)` on the `BiRequestStream` stub | `GrpcConnection.java:126-129` (`sendRequest`), `:121-124` (`sendResponse`) |

`GrpcConnection` holds **both** stubs simultaneously: `grpcFutureServiceStub` (unary) and
`payloadStreamObserver` (stream).

### 6.2 Server → client
Verified in `core/.../remote/grpc/GrpcConnection.java`:
* `sendRequestNoAck(Request)` → `streamObserver.onNext(payload)` (line 71-79; comment notes
  `onNext()` is not thread-safe, hence `synchronized`).
* `request(request, timeoutMills)` (line 169) and `asyncRequest(...)` (line 187) — pushes routed
  through the stream; the client's reply comes back as a `Response` payload on the same stream and
  is matched by `RpcAckCallbackSynchronizer.ackNotify`.

### 6.3 Server receive dispatch

| | `GrpcRequestAcceptor` (unary `Request.request`) | `GrpcBiStreamRequestAcceptor` (`BiRequestStream.requestBiStream`) |
| --- | --- | --- |
| Special-case | `ServerCheckRequest` answered inline with `ServerCheckResponse` | `ConnectionSetupRequest` → register connection, push `SetupAckRequest` |
| Business requests | resolved via `RequestHandlerRegistry.getByRequestType(type)` and `requestHandler.handleRequest(...)` | **not** handled as business requests |
| `Response` payloads | n/a | → `RpcAckCallbackSynchronizer.ackNotify(...)` |
| Anything else | error responses | logged as `"unknown payload receive"` and dropped |

So:
* **Unary `Request`:** `ServerCheckRequest`, and all business requests
  (`ConfigQueryRequest`, `ConfigPublishRequest`, `ConfigRemoveRequest`,
  `ConfigBatchListenRequest`, `InstanceRequest`, `BatchInstanceRequest`,
  `ServiceQueryRequest`, `ServiceListRequest`, `SubscribeServiceRequest`,
  `PersistentInstanceRequest`, `HealthCheckRequest`, fuzzy-watch requests, …).
* **Stream:** client→server `ConnectionSetupRequest` and all push acks/responses;
  server→client `ConfigChangeNotifyRequest`, `NotifySubscriberRequest`, `ClientDetectionRequest`,
  `ConnectResetRequest`, `SetupAckRequest`, fuzzy-watch notifications, and any pushed `Response`.

Specifically for the messages in the brief:
* `ConfigChangeBatchListenRequest` → **it is `ConfigBatchListenRequest`, and it travels on the
  UNARY `Request` RPC** (it has a registered `RequestHandler`). It is a request/response, not a
  stream push.
* `ConfigChangeNotifyRequest`, `NotifySubscriberRequest`, `ClientDetectionRequest` → **stream
  push, server→client**.
* `ConnectResetRequest` → **stream push, server→client**.

### 6.4 Client-side stream receive
`GrpcClient.bindRequestStream` (`GrpcClient.java:266-352`): parses each incoming `Payload`; if
`SetupAckRequest`, handle specially and return; otherwise
`handleServerRequest(request)`; if a `Response` comes back, set its `requestId` from the request
and `sendResponse(response)` back over the stream.

### 6.5 Ping/pong details
* **`ClientDetectionRequest` / `ClientDetectionResponse`** — server-initiated liveness probe.
  Server: `NacosRuntimeConnectionEjector.java:89-116` sends it with a 5000 ms timeout to
  connections whose `lastActiveTime` is stale. Client: `RpcClient.java:460-466` registers a
  handler that returns `new ClientDetectionResponse()`. Failure to answer ⇒
  `connectionManager.unregister(outDateConnectionId)`.
* **`HealthCheckRequest` / `HealthCheckResponse`** — a registered unary pair with
  `HealthCheckRequestHandler`; both carry no fields.
* **`ConnectResetRequest`** — server→client "reconnect elsewhere"; client replies
  `ConnectResetResponse` and switches server.

---

## 7. Result/error codes

`api/src/main/java/com/alibaba/nacos/api/remote/response/ResponseCode.java`:

```java
public enum ResponseCode {
    SUCCESS(200, "Response ok"),
    FAIL(500, "Response fail");
}
```
Only two values. `Response.java`: `int resultCode = ResponseCode.SUCCESS.getCode();` (default 200),
plus `int errorCode`, `String message`, `String requestId`. `isSuccess()` ⇔ `resultCode == 200`.

`setErrorInfo(int errorCode, String errorMsg)` sets `resultCode = 500`, `errorCode`, `message`.

**How errors are surfaced** (official spec §5 + code):
* The transport-level error type is **`ErrorResponse`**, serialized with `metadata.type =
  "ErrorResponse"`. Built via `ErrorResponse.build(int errorCode, String msg)` or
  `ErrorResponse.build(Throwable)`.
* `ErrorResponse.build(Throwable)` copies `errorCode` from `NacosException`/`NacosRuntimeException`,
  else falls back to `ResponseCode.FAIL.getCode()` = 500, sets `message = exception.getMessage()`,
  and sets `resultCode = errorCode` (**note: for the Throwable overload `resultCode` is set to the
  error code, not to 500**).
* Error path in `GrpcRequestAcceptor.request(...)`:
  * server not started → `ErrorResponse.build(NacosException.INVALID_SERVER_STATUS, "Server is starting,please try later.")`
  * no handler → `ErrorResponse.build(NacosException.NO_HANDLER, "RequestHandler Not Found")`
  * invalid/unregistered connection → `ErrorResponse.build(NacosException.UN_REGISTER, "Connection is unregistered.")`
  * parse failure / not a `Request` → `ErrorResponse.build(NacosException.BAD_GATEWAY, ...)`
  * handler threw → `ErrorResponse.build(e)`
* Client surfaces it by throwing: `GrpcConnection.requestFuture(...)` / `asyncRequest(...)` convert
  an `ErrorResponse` into `new NacosException(response.getErrorCode(), response.getMessage())`.
* `ConfigQueryResponse`-specific codes ride on `errorCode` while `resultCode` is 500:
  `300` not found, `304` not modified (3.3+), `400` query conflict, `403` no right.
* Higher-level callers check `ResponseCode.SUCCESS.getCode() != response.getResultCode()` and throw
  `NacosException(response.getErrorCode(), response.getMessage())`; `403`/`NO_RIGHT` additionally
  triggers client re-login (`NamingGrpcClientProxy.java:529-534`).

---

## 8. Nacos 2.x vs 3.x differences

Method: `git show <tag>:<path>` / `git diff 2.4.3 2.5.1` / `git diff 2.5.1 HEAD` on the official
repo. Tags fetched: **2.4.3**, **2.5.1** (2.2.3/2.0.4 fetches failed on network timeout and are
**UNVERIFIED**).

| Area | Verdict |
| --- | --- |
| **proto file** | **IDENTICAL.** `git diff 2.5.1 HEAD -- api/src/main/proto/nacos_grpc_service.proto` = empty. Field numbers type=3, clientIp=8, headers=7, metadata=2, body=3; both services unchanged. |
| **Port offsets** | **IDENTICAL**: `SDK_GRPC_PORT_DEFAULT_OFFSET = 1000`, `CLUSTER_GRPC_PORT_DEFAULT_OFFSET = 1001` in 2.4.3, 2.5.1, develop. |
| **`Any` construction** | **Unchanged**: `Any.newBuilder().setValue(...)`, no `type_url`, in 2.x and 3.x. Only the *name* of the JSON helper changed (`JacksonUtils` → `JsonUtils`). |
| **JSON engine** | **Refactored, byte-compatible.** 2.x: static `JacksonUtils` `ObjectMapper` with `FAIL_ON_UNKNOWN_PROPERTIES` disabled + `Include.NON_NULL`. 3.3: pluggable `NacosJsonAdapter` SPI (Jackson2/Jackson3), with the **same two settings**. Property names and inclusion behavior unchanged. |
| **`Response` fields** | **Unchanged.** Only a `@SuppressWarnings` removal and reflow; `resultCode`/`errorCode`/`message`/`requestId` identical. |
| **`ConnectionSetupRequest`** | **IDENTICAL** (`git diff` empty). Fields `clientVersion`, `tenant`, `labels`, `abilityTable` unchanged. |
| **`InstanceResponse`** | **IDENTICAL.** |
| **`ConfigBatchListenRequest`** | Only whitespace/formatting; fields unchanged. |
| **`PayloadRegistry`** | Only a message-format change; `ServiceLoader` + simple-name key unchanged. |
| **`ResponseCode`** | **IDENTICAL** (200/500). |
| **`Instance.java`** | Fields unchanged; 3.x adds `implements NacosForm` + `validate()`/`fillDefaultValue()` (server-side validation only — no wire-shape change). |
| **`ConfigQueryRequest`** | **+ `localMd5`** (new in 3.3.0), for 304 conditional GET. Additive; older clients that omit it get full content. |
| **`ConfigQueryResponse`** | **+ `CONFIG_NOT_MODIFIED = 304`** constant (new in 3.3.0). Additive. |
| **Payload registry size** | 2.4.3 = 44 entries, 2.5.1 = 44, develop = 92. **Nothing was removed** (`git diff`/`Compare-Object` showed zero removals). The 48 additions are all new AI, fuzzy-watch, and lock payloads — **no config/naming class was renamed, removed, or reshaped.** |

**⇒ No breaking wire-protocol change between 2.x and 3.x for the config/naming gRPC surface.** A
client implementing the 2.5.1 contract will interoperate with a 3.x server and vice versa, with
these additive 3.3 features to be aware of:

* `localMd5` on `ConfigQueryRequest` ⇒ server may answer `errorCode = 304` with **no `content`**
  but with `md5`, `contentType`, `lastModified`; the client must restore content from local cache.
  (`config/.../ConfigQueryRequestHandler.java:121-125, 234`.)
* New fuzzy-watch payload types and new AI/lock payloads exist in 3.x; a 2.x client simply never
  sends them.
* Ability negotiation (`abilityTable` on both sides) lets each end discover the other's support.

**UNVERIFIED:** behaviour of the earliest 2.x lines (2.0.x/2.2.x) — those tag fetches failed on
network timeout. `abilityTable` on `ConnectionSetupRequest` is present in 2.4.3 and 2.5.1, but
whether it existed in 2.0.x could not be confirmed.

---

## 9. Practical implementation notes for C#

1. **Encode an envelope:** `Metadata{ type = SimpleName, clientIp = <your IP>, headers = {...} }`,
   `Payload{ metadata, body = Any{ value = UTF8(json), type_url = "" } }`. Leave `type_url` unset
   (or explicitly empty) — the Java side never reads it.
2. **JSON:** use property names exactly as tabled; omit nulls; do **not** fail on unknown fields.
   Expect `headers`, `requestId`, `module` inside inbound bodies and **emit them** on outbound
   bodies to match Nacos byte-for-byte (Nacos' own tests assert these). Order is irrelevant.
3. **Watch the two naming traps:** `ConfigQueryResponse` → `"beta"` (not `isBeta`);
   `ServiceInfo` → `"allIPs"` (capital IP).
4. **Two RPCs, one envelope:** `Request/request` for business calls and `ServerCheckRequest`;
   `BiRequestStream/requestBiStream` for setup and all pushes.
5. **Handshake order is mandatory:** unary `ServerCheckRequest` → open bi-stream → send
   `ConnectionSetupRequest` as the first stream frame → await `SetupAckRequest` (only if you sent a
   non-null `abilityTable`). If you send `abilityTable = null`, the server will **not** send
   `SetupAckRequest`, and you must pace yourself before issuing business calls.
6. **Answer server pushes** on the stream with the matching response type and echo `requestId`.
7. **Correlate responses by `metadata.type`** (the response class name), not by `type_url`.
8. **`ConfigBatchListenRequest` (not `ConfigChangeBatchListenRequest`)** goes over the **unary**
   RPC and is answered by `ConfigChangeBatchListenResponse`.
9. **`ConnectResetRequest.serverPort` is a string**, not an int.
10. **Do not rely on `RequestType`/`@NacosRequestType`** — neither exists. Route by simple name.
