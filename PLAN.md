# Lolitacoding.Nacos 开发计划书 (v0.1)

> 本文档面向 AI 编码助手（Claude Code / Copilot Agent / Cursor 等）。请先通读"全局约定"，再按"里程碑"顺序逐任务执行。每个任务必须满足其"验收标准"后才能进入下一个。

## 1. 项目目标

为 Nacos 提供现代化的 .NET 客户端 SDK：

- 仅面向 **.NET 10**（`net10.0`），充分使用新语言特性（C# 14、`System.Text.Json` 源生成、`Span`、`Channel<T>`、`TimeProvider`、`IAsyncEnumerable` 等）。
- 仅支持 **Nacos 3.x** 服务端（不再支持 2.x；见第 6 节"待核对事项"）。
- 覆盖核心能力：**配置中心（Config）**、**服务注册发现（Naming）**、**分布式锁（Lock）** 与 **AI 注册中心（MCP / Prompt / Skill / AgentSpec / Agent）**。
- 提供与 .NET 生态的一等集成：`IConfiguration`、`Microsoft.Extensions.DependencyInjection`、`IHostedService`、`HttpClientFactory`、`Microsoft.Extensions.ServiceDiscovery`（可选依赖）。
- 支持 **TLS / mTLS**：以 `https://` 地址启用，可配置私有 CA、跳过校验（仅开发）、客户端证书与协议/密码套件。
- 对 **Native AOT / 裁剪友好**：不使用反射式 JSON 序列化，不使用运行时代码生成。

非目标：Nacos 服务端管理（admin/console）API、.NET 8 及以下的多目标。

## 2. 命名与仓库结构

仓库名：`Lolitacoding.Nacos`（一个服务一个仓库）。NuGet 前缀 `Lolitacoding.*`。

```
Lolitacoding.Nacos/
├─ Directory.Build.props
├─ Directory.Packages.props          # 中央包版本管理
├─ global.json                       # 固定 .NET 10 SDK
├─ .editorconfig
├─ Lolitacoding.Nacos.slnx
├─ src/
│  ├─ Lolitacoding.Nacos/                                      # 核心：协议、传输、Config/Naming 客户端
│  ├─ Lolitacoding.Nacos.Extensions.DependencyInjection/       # DI 注册、Options
│  ├─ Lolitacoding.Nacos.Extensions.Configuration/             # IConfigurationProvider（配置热更新）
│  ├─ Lolitacoding.Nacos.Extensions.ServiceDiscovery/          # Microsoft.Extensions.ServiceDiscovery 集成
│  ├─ Lolitacoding.Nacos.AspNetCore/                           # 服务自动注册/注销 HostedService
│  ├─ Lolitacoding.Aspire.Hosting.Nacos/                       # Aspire AppHost 编排集成（AddNacos 容器资源）
│  └─ Lolitacoding.Aspire.Nacos/                               # Aspire 客户端集成（AddNacosClient 连接字符串）
├─ tests/
│  ├─ Lolitacoding.Nacos.Tests/                # 单元测试
│  └─ Lolitacoding.Nacos.IntegrationTests/     # 基于 Testcontainers 的集成测试
├─ samples/
│  ├─ Sample.ConfigConsole/
│  ├─ Sample.AspNetCore/
│  └─ Sample.Aspire.AppHost/                   # Aspire 编排示例（Nacos 容器 + Sample.AspNetCore）
└─ docs/
```

依赖方向：`Core` ← `Extensions.DependencyInjection` ← (`Extensions.Configuration`, `Extensions.ServiceDiscovery`, `AspNetCore`, `Aspire.Nacos`)。`Aspire.Hosting.Nacos` 只依赖 `Aspire.Hosting`，不依赖核心库。核心库**不得**依赖 ASP.NET Core 或 DI 抽象以外的框架包。

## 3. 全局约定（AI 必须遵守）

**构建与质量**
- `TargetFramework=net10.0`，`LangVersion=latest`，`Nullable=enable`，`ImplicitUsings=enable`，`TreatWarningsAsErrors=true`。
- 启用 `IsAotCompatible=true`（核心库），所有 trim/AOT 警告视为错误。
- 使用 `Directory.Packages.props` 中央管理版本；`.csproj` 中不写包版本号。
- 版本号使用 MinVer（基于 git tag）；开启 SourceLink、符号包（`snupkg`）、确定性构建。
- 所有公共 API 必须有 XML 文档注释；公共 API 变更需记录在 `PublicAPI.Shipped/Unshipped.txt`（Microsoft.CodeAnalysis.PublicApiAnalyzers）。

**编码风格**
- 文件范围命名空间；`sealed` 优先；`record`/`record struct` 用于不可变 DTO。
- 异步方法一律带 `CancellationToken`（末位参数，可选默认值 `default`），库内部 `ConfigureAwait(false)`。
- 时间相关逻辑一律注入 `TimeProvider`，禁止直接调用 `DateTime.Now`/`Task.Delay`（用 `TimeProvider.Delay` 的等价方式便于测试）。
- 日志使用 `[LoggerMessage]` 源生成；禁止字符串插值日志。
- JSON 使用 `System.Text.Json` 源生成（`JsonSerializerContext`）；禁止 `Newtonsoft.Json`。
- 禁止静态单例与全局可变状态；所有可配置项走 `IOptions<NacosOptions>` 风格的 Options 类并带校验（`IValidateOptions<T>`）。
- 抛出异常使用自定义异常层次：`NacosException`（基类）→ `NacosAuthException`、`NacosTimeoutException`、`NacosServerException` 等，保留服务端错误码。

**测试**
- 单元测试：xUnit + 断言库（任选其一并全仓统一）；网络层通过接口抽象替身，不得真实联网。
- 集成测试：Testcontainers 启动 `nacos/nacos-server` 3.x 镜像，标记 `[Trait("Category","Integration")]`。
- 每个任务的新增代码行为覆盖率目标 ≥ 80%。

**提交规范**
- Conventional Commits（`feat:`、`fix:`、`test:`、`docs:`、`chore:`）。
- 每个任务一个分支/PR，PR 描述需列出"验收标准"逐条自检结果。

## 4. 架构设计

```
INacosClientFactory / NacosClient
 ├─ INacosConfigClient    ── 配置：获取 / 发布 / 删除 / 监听
 └─ INacosNamingClient    ── 服务：注册 / 注销 / 查询 / 订阅 / 心跳

内部层次：
  Transport（传输层）  : gRPC 双向流连接管理、请求/响应、重连、健康检查
  Protocol（协议层）   : 请求/响应消息模型、序列化、错误码映射
  Auth（认证层）       : 用户名密码登录、token 缓存与刷新；预留 AccessKey/SecretKey（阿里云 MSE）扩展点
  Cache（本地缓存层）  : 配置快照 / 服务列表 failover 文件，服务端不可用时降级读取
  Services（业务层）   : ConfigService、NamingService
```

关键设计决策：

1. **传输以 gRPC 为主**：Nacos 2.x+ 客户端通过 gRPC 长连接获得配置变更与服务变更推送。使用 `Grpc.Net.Client`（`GrpcChannel`），连接断开后以指数退避 + 抖动重连，并在重连后自动恢复所有监听与已注册实例。
2. **HTTP OpenAPI 作为降级/辅助通道**（用于登录获取 token、服务端探测等），通过 `IHttpClientFactory` + 弹性策略（`Microsoft.Extensions.Http.Resilience`）。
3. **监听模型**：对外暴露 `IAsyncEnumerable<ConfigChange>` 与回调委托两种方式；内部使用 `Channel<T>` 做背压。
4. **多服务器地址**：支持配置多个 `ServerAddresses`，故障自动切换；预留地址服务器（endpoint）发现的扩展点，v1 可不实现。
5. **线程安全**：所有客户端对象注册为 Singleton，可并发使用。

## 5. 里程碑与任务

### M0 · 工程脚手架
- [ ] **T0.1** 创建解决方案与上述项目骨架，配置 `Directory.Build.props`、`Directory.Packages.props`、`global.json`、`.editorconfig`、分析器、MinVer、SourceLink。
  - 验收：`dotnet build -c Release` 零警告零错误；`dotnet pack` 能产出 nupkg/snupkg。
- [ ] **T0.2** 配置 GitHub Actions：build + test（单元）+ 集成测试（Docker）+ tag 触发发布到 NuGet。
  - 验收：PR 触发 CI 全绿；发布流程在 dry-run 模式通过。
- [ ] **T0.3** 编写 `README.md`、`CONTRIBUTING.md`、`LICENSE`（建议 MIT 或 Apache-2.0）。

### M1 · 协议与传输层
- [ ] **T1.1** 获取并固化 Nacos gRPC 协议定义（见第 6 节），生成 C# 代码（`Grpc.Tools`）放入 `Protocol/` 内部命名空间，不对外暴露。
  - 验收：能序列化/反序列化 `Payload` 与 `Metadata`；有基于抓包样例或 Java SDK 输出的单元测试。
- [ ] **T1.2** 实现请求/响应消息模型（Config、Naming 所需的最小集合）与 `RequestTypeRegistry`（类型名 → 消息类型，使用源生成 JSON，AOT 安全）。
- [ ] **T1.3** 实现 `NacosGrpcConnection`：建立连接、握手（ConnectionSetup）、请求-响应、服务端主动推送处理、心跳（健康检查）、断线重连（指数退避 + 抖动）、重连事件通知。
  - 验收：集成测试中杀掉并重启容器后连接自动恢复；重连次数/间隔可由 `TimeProvider` 驱动在单元测试中验证。
- [ ] **T1.4** 实现认证：用户名/密码登录获取 accessToken，缓存并在到期前刷新；token 过期时自动重试一次。
  - 验收：开启鉴权的 Nacos 集成测试通过；错误密码抛出 `NacosAuthException`。

### M2 · 配置中心（Config）
- [ ] **T2.1** `INacosConfigClient`：`GetConfigAsync`、`PublishConfigAsync`、`RemoveConfigAsync`（dataId / group / namespace，支持 content-type 与 MD5 校验）。
- [ ] **T2.2** 配置监听：`ListenAsync(dataId, group)` 返回 `IAsyncEnumerable<ConfigChange>`，另提供回调式 `AddListener`；变更推送后拉取最新内容并比对 MD5，避免重复通知。
- [ ] **T2.3** 本地快照与 failover：成功获取的配置写入本地缓存目录；服务端不可用时回退读取；缓存目录可配置，可关闭。
- [ ] **T2.4** 命名空间、分组、`Tenant` 等基础概念的 Options 与校验。
  - M2 验收：集成测试覆盖 发布→获取→变更推送→删除 全流程；断线重连后监听仍然有效。

### M3 · 服务注册发现（Naming）
- [ ] **T3.1** `INacosNamingClient`：`RegisterInstanceAsync`、`DeregisterInstanceAsync`、`GetInstancesAsync`（支持集群、健康过滤）。
- [ ] **T3.2** 临时实例的心跳 / 持久实例支持；重连后自动重新注册全部已注册实例。
- [ ] **T3.3** 服务订阅：`SubscribeAsync(serviceName)` 返回 `IAsyncEnumerable<ServiceChange>`；本地服务列表缓存与 failover。
- [ ] **T3.4** 负载均衡辅助（轮询、加权随机）作为可选工具类，不强耦合到客户端。
  - M3 验收：多个实例注册后订阅方能收到上下线推送；进程退出时自动注销。

### M4 · .NET 生态集成
- [ ] **T4.1** `Extensions.DependencyInjection`：`services.AddNacos(options => ...)` / 绑定 `IConfiguration` 节；Options 校验；`IHostedService` 管理客户端生命周期（启动连接、优雅关闭）。
- [ ] **T4.2** `Extensions.Configuration`：`builder.Configuration.AddNacosConfiguration(...)`，支持 JSON/YAML/Properties 解析（YAML 可选依赖单独包）、多 dataId 叠加、变更触发 `IChangeToken`（支持 `IOptionsMonitor` 热更新）。
- [ ] **T4.3** `AspNetCore`：`app.UseNacosRegistration()` 或 `AddNacosServiceRegistration()`，启动后自动注册（地址从 `IServer` 绑定地址推断，可手动覆盖），关闭时注销；健康检查元数据。
- [ ] **T4.4** `Extensions.ServiceDiscovery`：`services.AddNacosServiceDiscovery()` 注册 `IServiceEndpointProvider`，使 `Microsoft.Extensions.ServiceDiscovery` 的 `ServiceEndpointResolver` / `HttpClient` 能解析 Nacos 服务名（`nacos://service` 或裸名）；订阅服务变更触发端点重新解析。
  - 验收：`AddNacosServiceDiscovery` 后 `http://nacos://service/` 可解析为 Nacos 实例；服务上下线推送能在无需轮询下刷新端点。
- [ ] **T4.5** `Aspire.Hosting.Nacos` + `Aspire.Nacos`：`builder.AddNacos("nacos")` 在 Aspire AppHost 中编排 Nacos 容器（standalone、可选鉴权、数据卷、就绪健康检查，固定发布 8848/9848/9849 并保持客户端端口偏移）；`AddNacosClient()` 从注入的连接字符串配置客户端。
  - 验收：`samples/Sample.Aspire.AppHost` 生成的 manifest 含 `nacos` 容器资源与 `sample-api` 的 `ConnectionStrings__nacos`；`WaitFor(nacos)` 在就绪健康检查通过后再启动依赖服务。
  - M4 验收：`samples/Sample.AspNetCore` 可运行，修改 Nacos 配置后应用内 `IOptionsMonitor` 值实时变化。

### M5 · 发布前打磨
- [x] **T5.1** Native AOT 发布 sample 验证；确认无 trim/AOT 警告。（`samples/Sample.Aot`；CI 的 `aot` job 在带 MSVC 工具链的 Windows runner 上发布，IL2xxx/IL3xxx 按错误处理）
- [x] **T5.2** 可观测性：`ActivitySource` + `Meter`（连接状态、请求耗时、重连次数、推送数量）。（`NacosDiagnostics` / `NacosMetrics`）
- [x] **T5.3** 文档：快速开始、配置项说明、从 `nacos-sdk-csharp` 迁移指南、FAQ。（`docs/quickstart.md`、`docs/configuration.md`、`docs/migration-from-nacos-sdk-csharp.md`、`docs/faq.md`）
- [ ] **T5.4** 发布 `0.1.0-preview` 到 NuGet，申请保留前缀 `Lolitacoding.*`。

### 后续（v1 之后）
- 阿里云 MSE AccessKey 认证；地址服务器发现。
- 以同样的结构扩展其他国内生态组件（独立仓库）。

## 6. 待核对事项（AI 开始 M1 前必须先确认，不得凭记忆编写）

1. **协议定义**：从官方 `alibaba/nacos` 仓库对应版本的 `api` 模块（`nacos_grpc_service.proto`）与 Java 客户端实现（`nacos-client`）核对 Payload/Metadata 结构、请求类型名称、握手流程、端口偏移（gRPC 通常为主端口 +1000）。
2. **Nacos 3.x 契约**：以 3.x（`nacos-develop`）为准核对 HTTP OpenAPI v3、登录接口（`/v3/auth/user/login`）、连接 `module` 标签、能力协商，以及 AI/Lock 的 gRPC 与 HTTP 契约。以官方源码与 `specs/` 为准，如与本文档冲突，以官方为准并更新本文档。
3. **许可证**：参考官方 Java SDK 行为时，只实现协议兼容，不拷贝其源码；protobuf 定义的使用需遵守其 Apache-2.0 许可并保留声明。
4. **已有社区实现**：参考 `nacos-sdk-csharp` 的 API 习惯以降低迁移成本，但不复制代码。

## 7. 公共 API 草案（供 AI 对齐，可在实现中微调）

```csharp
public interface INacosConfigClient
{
    ValueTask<string?> GetConfigAsync(string dataId, string group = "DEFAULT_GROUP", CancellationToken ct = default);
    ValueTask<bool> PublishConfigAsync(string dataId, string content, string group = "DEFAULT_GROUP", string? type = null, CancellationToken ct = default);
    ValueTask<bool> RemoveConfigAsync(string dataId, string group = "DEFAULT_GROUP", CancellationToken ct = default);
    IAsyncEnumerable<ConfigChange> ListenAsync(string dataId, string group = "DEFAULT_GROUP", CancellationToken ct = default);
}

public interface INacosNamingClient
{
    ValueTask RegisterInstanceAsync(NacosInstance instance, CancellationToken ct = default);
    ValueTask DeregisterInstanceAsync(NacosInstance instance, CancellationToken ct = default);
    ValueTask<IReadOnlyList<NacosInstance>> GetInstancesAsync(string serviceName, string group = "DEFAULT_GROUP", bool healthyOnly = true, CancellationToken ct = default);
    IAsyncEnumerable<ServiceChange> SubscribeAsync(string serviceName, string group = "DEFAULT_GROUP", CancellationToken ct = default);
}

public sealed class NacosOptions
{
    public IList<string> ServerAddresses { get; set; } = [];
    public string Namespace { get; set; } = "public";
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public string? LocalCacheDirectory { get; set; }
}
```

## 8. AI 执行流程建议

1. 每次只领取**一个任务**，先复述任务目标与验收标准，再写代码。
2. 先写接口与测试，再写实现；新增公共 API 同步更新 `PublicAPI.Unshipped.txt`。
3. 遇到第 6 节的未确认事项，先查官方资料并在 PR 中附上依据，不要猜测协议细节。
4. 完成后运行：`dotnet build -c Release`、`dotnet test`（单元）、必要时集成测试，并在 PR 描述中粘贴结果。
5. 发现本计划与实际不符时，提出修改建议并更新本文档，而不是静默偏离。

## 9. v0.2 新增范围与执行阶段

v0.1 已完成 Config / Naming / DI / Configuration / ServiceDiscovery / Aspire。v0.2 在此基础上扩展，按下列阶段推进，每个阶段独立提交并满足"验收标准"后再进入下一个。

### 阶段 1 · Nacos 3.x only + TLS + gRPC 端口修正（✅ 已完成）

- 移除对 2.x 的登录回退（`/v1/auth/users/login`），只使用 `/v3/auth/user/login`。
- 修正 gRPC 端口推导：SDK gRPC 端口恒为 `main + 1000`；TLS 与明文在同一端口协商，`main + 1001` 是集群端口而非 TLS 端口。
- 新增 `NacosTlsOptions`（挂在 `NacosOptions.Tls`）：`AllowUntrustedCertificate`、私有 CA（内联/文件）、mTLS 客户端证书（内联/文件 + 口令）、`EnabledSslProtocols`、`CipherSuites`；同时作用于 gRPC 通道与 HTTP 登录客户端。
- 集成测试、CI、文档与包描述全部收敛到 3.x。
- 验收：Release 构建零警告零错误；单元测试全绿；`https://` 地址推导出的 gRPC 端口为 `main + 1000`。

### 阶段 2 · 传输基础：按模块独立连接与 `module` 标签（✅ 已完成）

- Nacos 服务端按连接的 `module` 标签工作（`config` / `naming` / `lock` / `ai`）。当前实现未发送该标签，导致临时实例注册与清理、锁清理、AI 端点注册都无法正确工作。
- 改为每个模块一条 gRPC 连接（各自携带 `module` 标签、能力表与重连生命周期），由 `NacosClient` 汇总对外暴露。
- 验收：命名临时实例注册/注销端到端通过；各连接的 `module` 标签与能力表正确。

### 阶段 3 · 分布式锁（对齐 3.3 语义）

拆成两个提交：B1 传输与低层客户端，B2 高层可重入锁与 watchdog。

#### B1 · 锁消息与低层 `INacosLockClient`（✅ 已完成）

- `Protocol/Messages/LockMessages.cs`：`LockOperationRequest` / `LockOperationResponse`、`LockNotificationRequest` / `LockNotificationResponse`，以及嵌套的 `LockInstance` / `LockResult`；全部注册进 `NacosJsonContext` 与 `PayloadTypeRegistry`（顶层类名与 Java 一致）。
- 公共 API：`INacosLockClient`（`AcquireAsync` / `ReleaseAsync` / `RenewAsync` / `CancelWaitAsync`）、`NacosLockKey`、`NacosLockTypes`、`NacosLockResult`、`NacosLockAcquireOptions`；`NacosClient.Lock` 暴露，`module=lock`，能力位 `lock = true`。
- 连接按需惰性建立（`NacosLazyModuleConnection`，后续 AI 模块复用）。
- 等待队列：owner 恒显式发送；`waitTime > 0` 时先注册等待者再发首个请求，按 `AVAILABLE` / `TIMEOUT` 推送唤醒，并以轮询窗口兜底；放弃时向服务端补发 `CANCEL_WAIT`。
- 修正传输层缺陷：`DispatchServerRequestAsync` 现在会把 handler 返回的首个非空响应写回（对齐 `GrpcClient.onNext` / `RpcClient.handleServerRequest`），配置/命名推送确认与锁通知确认都依赖它。
- 验收：Release 构建零警告零错误；341 个单元测试全绿（新增获取/释放/续租/等待通知/超时/能力协商/惰性连接/推送应答用例）。

#### B2 · 高层 `NacosLock`（可重入 + watchdog 续租）（✅ 已完成）

- 公共 API：`INacosLockClient.GetLock(key, options)` 返回 `NacosLock`；`AcquireAsync`（无限等待）与 `TryAcquireAsync(wait, ct)`（有限等待，`TimeSpan.Zero` 表示只问一次、不入队）；`NacosLockOptions` 只有 `LeaseDuration` 与 `Parameters`（owner 与 waitTime 由锁自身管理）。
- `AcquireAsync` 返回 `NacosLockLease`（`Key` / `Owner` / `ReentrancyCount` / `IsHeld` / `Ended`，`DisposeAsync` 释放一层）。释放最后一层时才向服务端发 `RELEASE`；重复释放只生效一次；被拒绝或发不出去的释放只记日志、不抛异常，由租约到期兜底。
- 可重入按“持有者”而非“嵌套锁对象”实现：owner 每次获取新生成（`{clientId}:{n}`），同一异步流内再次获取只增加本地计数、不产生服务端往返，服务端看到的计数恒为 1。这样既避免服务端把重入请求排到持有者自己后面，也避免“释放还在途中、紧接着又获取”被服务端误判为重入。
- 非可重入类型（`NACOS_LOCK` / `NON_REENTRANT`）在同一流内重入时抛 `LockRecursionException`（对应 Java 的 `IllegalMonitorStateException` 保护），而不是让调用方与自己在服务端队列里互相等待。
- 持有关系随 `AsyncLocal` 沿异步流传递：嵌套调用、以及从持锁流中启动的任务，都视为同一持有者（重入）；需要真正互斥的并发工作要用低层客户端显式 owner，或以不继承流的方式启动。流的记录必须在公共同步入口创建——`async` 方法内写入的 `AsyncLocal` 对调用方不可见——该约束已由单元测试固化。
- 等待语义：`AcquireAsync` 每次向服务端排队 5 分钟（Java `DEFAULT_SERVER_WAIT_TIME_MS`）并在被丢弃后重排，直到取消；`TryAcquireAsync(wait)` 以本地截止时间为准，到点返回 `null` 并把队列项撤回。
- `NacosLockWatchdog`：每个持有者一条续租循环，间隔为 `max(1s, min(lease/3, 10s))`（未指定租约时按服务端默认 30s 计算）；续租被拒或异常即结束持有、停止续租，并记录 `LeaseRenewFailed`（7003）。`Ended` 覆盖“释放”与“失去”两种结束，`IsHeld` 区分二者。
- 新增日志 7003（续租失败）/ 7004（释放失败）；客户端 `DisposeAsync` 先结束全部持有、停止续租，再关闭连接。
- 验收：Release 构建零警告零错误；359 个单元测试全绿（新增重入、跨流独立 owner、非可重入拒绝、有限/无限等待、队列重排、续租间隔与字段、失去锁、释放幂等与被拒、客户端释放等用例）。

#### B3 · 真实服务器端到端验收（✅ 已完成）

- 集成测试 `tests/Lolitacoding.Nacos.IntegrationTests/LockingIntegrationTests.cs` 新增 4 个用例，每个自起一个容器：获取 → 他人被拒 → 释放 → 他人接管；可重入类型下服务端计数随每次获取递增、逐层释放；等待队列在持有者释放时由服务端推送交接；高层租约在 6 秒租约下连续续租 15 秒不掉锁。
- 关键发现：锁模块的完整契约属于 **3.3**。当时最新稳定版 `nacos-server:3.2.4` 的 `nacos-lock` 仍是旧协议——`LockOperationEnum` 只有 `ACQUIRE` / `RELEASE` / `EXPIRE`，`LockInstance` 无 `owner` / `waitTime` / `waiterRetry`，响应只有裸 `result`（无 `LockResult`），SPI 只注册 `SimpleLockFactory`（仅 `NACOS_LOCK`）。于是在 3.2 上 `RENEW` / `CANCEL_WAIT` 被拒（`resultCode=500`、`Nacos deserialize failed`），`REENTRANT` / `NON_REENTRANT` 被拒（`lockType: ... is not exist.`）。`nacos-server:v3.3.0-RC` 三件套齐全（三个 factory、`RENEW` / `CANCEL_WAIT`、`waitTime` / `waiterRetry`、`LockResult`），4 个用例全绿（1m05s）。
- 能力位不足以区分版本：3.2 服务器握手时同样声明 `lock = true`，客户端无法据此判断协议，只能文档化。锁测试改用独立镜像（`NACOS_LOCK_TEST_IMAGE`，默认 `v3.3.0-RC`），其余套件保持 `v3.0.2`（可被 `NACOS_TEST_IMAGE` 覆盖），即受支持的最老 3.x 版本。
- 顺带修掉两个 Aspire 缺陷：`NacosBuilderExtensions.ReadinessPath` 由 v1 console health 改为 `/nacos/v3/admin/core/state/readiness`（v1 路径在 3.2+ 返回 404，`WaitFor` 永远不会就绪）；`AddNacos` 恒定写入 `NACOS_AUTH_TOKEN` / `NACOS_AUTH_IDENTITY_KEY` / `NACOS_AUTH_IDENTITY_VALUE` 开发默认值（3.2+ 镜像即使关闭鉴权也强制要求，否则容器退出 255）。
- 套件修复（与 B3 一并完成，否则集成套件无法为后续阶段背书）。修复前 21 个用例只有 1 个通过：多数失败源于测试与容器夹具，而命名订阅的推送用例最终暴露出**客户端的两个真实缺陷**（见下）。
  - 就绪必须看响应体：`/nacos/v3/admin/core/state/readiness` 在某个模块尚未就绪时返回 200 且 `{"code":30000,"message":"naming not in readiness"}`，只有 `{"code":0,…,"data":"ok"}` 才算就绪。两个启动路径（Testcontainers 等待谓词与重启后的等待）都改为校验响应体。
  - 端口分配不能依赖 bind 探测：被 Docker 分配给别的容器的端口在本机仍可 bind，`IsFree` 说空闲而 docker 报 `port is already allocated`。改为串行启动（静态 `SemaphoreSlim`）+ 单调端口游标（步长 1001）+ 冲突重试。
  - 3.x 不预置任何账号：`nacos/nacos` 并不存在，未建管理员前登录一律被拒（3.0.2 回 `403 Access Denied`，3.2.4 回 `User not found!`）。夹具在就绪后调用 `POST /nacos/v3/auth/user/admin?password=…` 建首个管理员（已存在时返回 409，予以接受）。
  - 写入配置的 HTTP 助手只能走 v3 admin API（`/nacos/v3/admin/cs/config`，表单参数是 `groupName`）：v3 客户端接口只读，v1 写接口在 3.x 已下线；该 admin 接口即使关闭鉴权也要求令牌，而在关闭鉴权的服务器上没有任何账号可登录。因此传输层用例改为直接用 gRPC 发布配置（顺带更贴合该用例的定位）。
  - 配置与命名集成用例从未调用 `ConnectAsync`，而客户端不会隐式连接（宿主服务与单元测试都会显式连接）：补上。同时修正 `NacosClient` 中“首个请求会隐式连接”的错误注释。
  - 写入是最终一致的：配置发布返回后约 234ms 才可读，实例注册约 18ms 可读，注销约 656ms 才消失。读回断言改为轮询（`NacosEventually.BecomesAsync`）。
  - `ListenAsync` / `SubscribeAsync` 先产出当前快照、再向服务器注册监听，落在这个窗口里的变更不会被推送（`SubscribeServiceResponse` 携带订阅生效瞬间的服务状态，Java 客户端即以此为准，本客户端当时尚未使用它；该窗口已在 G2 关闭）。推送类断言改为重复施加变更直到收到推送（`NacosEventually.PushedAsync`）。
- 命名推送的两个真实缺陷（由该用例暴露，已修，随阶段 3 一并提交）：
  - **推送身份读错了位置**：服务器用 `NotifySubscriberRequest.buildNotifySubscriberRequest(serviceInfo)` 构造推送，**只填 `serviceInfo`**，请求自身的 `namespace` / `serviceName` / `groupName` 在线上恒为缺失（即便 Java 类声明了这三个字段），而旧实现在这三个字段为空时直接返回，于是每一条推送都被静默丢弃。改为像 Java 的 `NamingPushRequestHandler` 一样，从 `serviceInfo.name` / `serviceInfo.groupName` 读取服务身份，`namespace` 缺省时回落到客户端配置的命名空间；抓包实证记入 `docs/protocol-grpc-spec.md` §3.10 与 `docs/protocol-notes.md`。
  - **变更判定依赖了服务器不发的字段**：推送里的 `serviceInfo.checksum` 恒为空串，旧实现“checksum 相同即忽略”，会把**每一条**推送当作重复丢掉。改为复刻 Java `ServiceInfoHolder.processServiceInfo` → `InstancesDiffer.doDiff`：按地址（`ip:port`）比较两边实例表并逐项比较实例描述（权重 / 集群 / 健康 / 可用 / 临时 / 元数据 / `instanceId`），顺序无关；另按 `lastRefTime` 丢弃早于已应用状态的推送（每条推送各自在独立任务上落地，旧状态可能后完成），新增日志事件 5012。
  - 单元测试此前用“顶栏也带身份字段”的脚本推送，正好掩盖了这两个缺陷；已改为服务器实际发送的形状（只带 `serviceInfo`），见 `NacosNamingClientTests.Push` 的说明。
- 两个协议缺口（当时记为“留待阶段 4”）：连接尚未建立时请求直接抛 `NacosConnectionException`，重连期间的请求不排队等待；监听/订阅的快照与注册之间非原子。两者已由 G1 / G2 关闭（见下）。
- 文档：`docs/faq.md`、`docs/protocol-notes.md`、`docs/protocol-grpc-spec.md` §3.13、`README.md`、`docs/aspire.md` 与 `NacosLockTypes` 注释写明 3.3 要求及 3.2 的降级行为。

验收（阶段级）：单元测试 **360 个全绿**（覆盖获取/释放/续租/超时/重入/等待通知）；与真实服务器集成 **21 个用例全绿（3m54s）**：锁的 4 个用例跑在 `nacos/nacos-server:v3.3.0-RC`，其余（配置 / 命名 / 传输 / 鉴权）跑在受支持的最老 3.x 版本 `nacos/nacos-server:v3.0.2`。

### 阶段 4 · AI 注册中心（完整对齐 3.x）

- 资源族：MCP Server、Prompt、Skill、AgentSpec、Agent（含 A2A 与 Agent watch / RAD 推送）。
- 传输：gRPC 为主、HTTP v3 客户端接口回退；能力位 `mcp` / `agent` / `radWatchV1` 参与协商。
- 模型：对齐 3.x 全部字段；未知字段以 `JsonExtensionData` 保留。
- 推送约定（阶段 3 的教训）：服务器推送常把身份放在负载体里（命名推送只填 `serviceInfo`，请求自带的 `namespace` / `serviceName` / `groupName` 恒空），checksum 也可能是空串。AI 的推送 / watch 一律按 `nacos-develop` 发送端的构造方式读取身份、以状态比较判定变更，并各抓一次真实推送核对字段。
拆成若干提交：C1 AI 基础设施 + MCP 查询/发布，C2 MCP 订阅与本地缓存 + 端点注册/注销，C3 Prompt 查询与订阅，C4 Skill 查询与订阅，C5 AgentSpec 查询与订阅，C6a A2A AgentCard，C6b RAD v1 Agent 注册，C7 RAD agent watch，C8 宿主/示例与版本矩阵；gRPC↔HTTP 回退仍为独立切片。

#### C1 · AI 基础设施 + MCP 查询/发布（✅ 已完成）

- `src/Lolitacoding.Nacos/Ai/`：`INacosAiClient` / `INacosMcpRegistry`、`NacosAiClient`（持有 AI 连接，复用 `NacosLazyModuleConnection` 惰性创建）、`NacosMcpClient`、能力位常量（`NacosAiServerAbility`）与 MCP 模型。
- 线路契约：`QueryMcpServerRequest` / `QueryMcpServerResponse`（`mcpServerDetailInfo`）与 `ReleaseMcpServerRequest` / `ReleaseMcpServerResponse`（`mcpId`），`type` 为 Java 简单类名，模块 `"ai"`；连接标签为 `source` / `module` / `AppName` 加上每客户端实例生成一次的 `aiClientUuid`（对应 `AiGrpcClient`），随客户端而非连接存活。
- 模型直接作为线路负载（与 Java 用 `McpServerBasicInfo` 一致），不做近似映射；MCP 自身的 `_meta`、官方注册表的 `release_date` / `is_latest`、大写能力值（`TOOL` / `PROMPT` / `RESOURCE`）、`isRequired` / `isSecret` 等非常规字段名逐一按上游保留。
- 能力位门禁：`mcp` 与 `mcpDraftRelease`，只有服务器**显式**声明 `false` 才本地抛 `NacosServerException(501)`；未声明即未知，照常发请求。`createDraft` 单独门禁，因为不识别该字段的服务器会把它当未知字段丢掉而直接发布，恰与调用方意图相反。
- 版本解析：优先 `versionDetail.version`，回退旧 `version` 字符串；两者皆无则拒绝发布——服务端自行编造的版本调用方无法再次指名。
- 失败语义：不存在的 MCP 服务器是业务错误（服务器回 `404` + `MCP server not found`，注册表自身的 `MCP_SERVER_NOT_FOUND` 为 `50000`）；因此“成功但无 detail”按契约破坏（`NacosServerException`）处理，而不是当成空结果。
- 关键发现：MCP 客户端契约属于 **3.0.3**（`McpService` 标注 `@Since("3.0.3")`），而套件固化的最老 3.x（`v3.0.2`）对查询与发布都回 `RequestHandler Not Found`。AI 集成用例改用独立镜像（`NACOS_AI_TEST_IMAGE`，默认 `v3.2.4`——已验证可服务注册表的最老版本），其余套件仍为 `v3.0.2`。
- 选项 `NacosAiOptions`（`EnableLocalCache` / `CacheRefreshInterval`）随本阶段引入；`CacheRefreshInterval` 在 C2 的订阅中生效，`EnableLocalCache`（落盘 failover）当时尚未实现（内存中的订阅缓存始终开启），已在 D1 补齐。
- 这一阶段暂不做 gRPC↔HTTP 自动回退：服务器缺失能力位时按上述 501 语义报错即可；回退推迟到 C8 之后的独立切片（C8 说明了为何仍不能真机验证）。
- 验收：Release 构建零警告零错误；单元测试 **386 个全绿**（新增 26 个：查询/发布的字段映射与参数校验、能力位门禁与“未声明即放行”、服务器错误码上抛、`createDraft` 省略、模型 JSON 契约与整条详情往返）；集成测试新增 2 个用例，在 `nacos/nacos-server:v3.2.4` 上全绿（发布 → 查询（含 `allVersions` 与工具清单）→ 按版本再查；未发布的名字回业务错误且先发布一次以证明注册表确实在服务）。

#### C2 · MCP 订阅与本地缓存 + 端点注册/注销（✅ 已完成）

- `Ai/NacosMcpServerCache.cs`：订阅的本地缓存与后台刷新。Nacos 对 MCP **没有服务端推送**（官方客户端的 `subscribeMcpServer` 也只是本地按间隔重读），因此每个被订阅的版本由一个自调度轮询任务（`Task.Delay(interval, _timeProvider, token)`，与 `NacosLockWatchdog` 同一形状）驱动：重读 → 与缓存副本比较 → 变化时投递。读失败不清缓存、下一轮重试，订阅因此能跨控制面故障；`404`（尚未发布）同样不是失败，订阅保持打开。
- 缓存键：`{name}::{version}`，未指定版本为 `{name}::latest`；`is_latest` 为真时同一份 detail 也存入 `latest` 键，使不带版本的订阅命中缓存而不必再发一次请求。
- 变更判定：比较整份 detail 的序列化结果（两侧都来自服务器，表示稳定），因此嵌套的 `toolSpec` / `resourceSpec` 里任何一处改动都能被发现；非最新的版本不再写 `latest` 键，避免把旧版本误标为最新。
- 订阅形状：`IAsyncEnumerable<NacosMcpServerDetail> SubscribeServerAsync(name, version = null, ct)`——先产出当前快照，再产出变化；结束枚举即退订，最后一个订阅者离开时停止轮询任务。启动连接与能力位检查发生在第一次 `MoveNextAsync`（async iterator 语义），与“首次使用才开连接”一致。
- 线路：`McpServerEndpointRequest` / `McpServerEndpointResponse`（`type` ∈ `registerEndpoint` / `deregisterEndpoint`），`BatchRegisterEndpoint` 未暴露（官方 `McpService` 亦未暴露）。
- 端点的本质是**命名实例**：服务端 `McpClientApplicationService.operateEndpoint` 把 MCP 服务器的 `REF` 端点解析成命名服务，再以「发布者连接 id」注册实例，因此端点会随注册它的连接消亡，重连后必须重放。`NacosAiClient` 在惰性连接工厂里挂 `NacosGrpcConnection.Reconnected`（与 `NacosLockClient` 挂 `ServerRequestReceived` 的同一位置），`NacosMcpEndpointPublication` 记录已发布端点并在重连后重发 `registerEndpoint`。服务器 `v3.2.4` 实测：无端点规格地发布一个 `mcp-sse` 服务器会自动生成 `REF` 服务 `{name}::{version}`，注册的端点随后出现在查询结果的 `backendEndpoints`（地址 + 服务器的 `exportPath`）。
- 与上游 `nacos-develop` 的**有意差异**（逐条记录，便于以后核对；其余一律跟随官方客户端）：
  1. 每次订阅都会确保该键有轮询任务；上游只在缓存未命中分支调用 `addMcpServerUpdateTask`，重复订阅一个已缓存的键不会重启轮询。
  2. `is_latest` 的变化同时投递到 `{name}::{version}` 与 `{name}::latest`；上游只按 `latest` 投递，于是**指名最新版本号**的订阅者收不到变化。
  3. 端点重放的键是 `name+address+port`；上游 `McpServerEndpoint` 只按 `mcpName` 记录，同一服务器的多个端点重连后只剩最后一个被重放。
  4. 服务器拒绝的注册不会被记下稍后补发，注销则先移除本地意图再发送；上游先缓存意图、注销成功后才清除，于是重连会重试一个调用方已被告知失败的注册。与命名客户端的 redo 语义一致。
  5. 注销时带上该端点发布时的版本（客户端自己记录）：服务端按版本解析要移除实例的目标服务（`operateEndpoint` → `buildServiceRef`），上游不发版本、由服务端取“当前最新”，端点固定在已被取代的版本上时就会作用在错的服务上、实例残留。
- 日志事件：8003 端点已注册、8004 端点已注销、8005 重放失败（Warning）、8006 重放数量、8007 订阅资源变化（Debug）、8008 刷新失败（Warning）。
- 验收：Release 构建零警告零错误；单元测试 **412 个全绿**（新增 26 个：订阅首值/版本与 latest 键/变化投递/404 容忍/刷新失败容忍/缓存命中/第二个订阅者/退订停止轮询/释放时结束/参数与能力位门禁；端点的请求字段/端口校验/错误上抛/重连重放/拒绝不重放/注销停止重放/多端点重放/释放后拒绝/惰性连接）；集成测试新增 3 个用例，在 `nacos/nacos-server:v3.2.4` 上全绿（订阅收到真实服务器发布的新版本且 `is_latest` 随之为真；远端服务器发布 → 注册端点 → 轮询到实例出现在 `backendEndpoints`（地址与 `exportPath`）→ 注销后消失 → 不带版本再注册并同样可见；未知名字注册端点回 `404`）。

#### C3 · Prompt 查询与订阅（✅ 已完成）

- `Ai/NacosPrompt.cs`：`NacosPrompt`（`promptKey` / `version` / `template` / `md5` / `variables`）与 `NacosPromptVariable`（`name` / `defaultValue` / `description`）。这是**服务器投影后的客户端形态**而非存储形态：`PromptConvertUtils.toClientPrompt` 从 `PromptVersionInfo` 里只取这五个字段，`description` / `bizTags` / 作者 / commit 信息一律不下发，因此客户端也无从请求——投影清单本身就是契约。
- 线路：`QueryPromptRequest`（`namespaceId` / `promptKey` / `version` / `label` / `md5`）与 `QueryPromptResponse`（`promptInfo`），模块 `"ai"`，与 MCP 共用同一条 AI 连接（Nacos 按模块路由连接，不按资源类型）。契约属于 **3.2.0**（`QueryPromptRequestHandler` 标注 `@Since("3.2.0")`），比 MCP 的 3.0.3 晚，且 gRPC 能力表里**没有 prompt 能力位**，因此客户端无法提前拒绝更老的服务器，只能等服务器回 `RequestHandler Not Found`。
- 选择器语义全部交给服务器（`resolveClientVersion`）：指名版本最优先，其次标签，标签解析不到时**静默回落到 `latest`**（唯一无法在客户端捕获的笔误），两者都不给即 `latest`；解析出的版本还必须处于 ONLINE，因此已发布的草稿不可读、存在但从未发布的版本按“不存在”报错。客户端不做任何本地解析——版本计划属于服务器，缓存标签指向会答出一个已经移动过的计划。
- 读取 `GetPromptAsync(promptKey, version = null, label = null, ct)` 是无条件读（`md5` 传 `null`）：不存在、版本未发布、版本已下线、内容缺失都回 `404`（`NacosServerException`），所以“回 `200` 但没有 `promptInfo`”按契约破坏处理并记 8011，而不是当成空 Prompt。
- 订阅 `IAsyncEnumerable<NacosPrompt?> SubscribePromptAsync(promptKey, version = null, label = null, ct)`：与 MCP 一样是每个选择器一个本地轮询任务（Nacos 同样没有 Prompt 推送），差别在于**元素可为 `null`**——Prompt 的 `404` 与“尚未发布”无法区分（服务器对两者回同一错误码），因此 `404` 一律视为撤回：清掉缓存副本并投递 `null`，避免订阅者继续渲染一个已经不存在的模板；首个元素是当前状态（未发布即 `null`），结束枚举即退订，最后一个订阅者离开时停止轮询。
- 条件读：种子读不带 `md5`；刷新读带上缓存副本的 `md5`，服务器回 `304`（`NOT_MODIFIED`）即静默跳过、不产生变更通知。`304` 只在版本解析成功之后产生，所以选择器已经指向别的版本时不可能拿到 `304`。
- 缓存键沿用官方 `CacheKeyUtils.buildPromptKey` 的形状，但给两段选择器各加前缀消歧：有标签优先 → `{key}::label:{label}`，否则 `{key}::version:{version}`，否则 `{key}::latest`。版本串与标签串可以合法地相同，不加前缀会共用同一个缓存项。与 MCP 不同，**不做 `latest` 别名**——官方 Prompt 缓存也只按选择器键存放，`is_latest` 别名是 MCP 事件模型特有的。
- 变更判定：比较整份 Prompt 的序列化结果（两侧都来自服务器，表示稳定），因此模板、哈希、变量元数据任一处变化都能被发现；“一直是缺失”不算变化，撤回只通知一次。
- 日志事件：8009 订阅资源变化（Debug）、8010 已撤回（Debug）、8011 契约破坏（Warning）、8012 刷新失败（Warning）。
- 与上游 `nacos-develop` 的**有意差异**（3 条，其余跟随官方客户端；与 C2 的 5 条并列记录）：
  1. 选择器已有缓存副本时，订阅**不回源读取**；上游 `subscribePrompt` 每次都读一遍再挂更新任务。收益是第二个订阅同一 Prompt 的调用方零往返。
  2. 缓存副本在最后一个订阅者离开后**保留**；上游在最后一个 listener 退订时 `promptCache.remove(cacheKey)`。需要“服务器当下的答案”而非副本时调用 `GetPromptAsync`——它从不查缓存（与上游一致）。
  3. 刷新间隔复用 `NacosAiOptions.CacheRefreshInterval`；上游按资源类型读 `nacosAiPromptCacheUpdateInterval`。两者都是同一种轮询且默认同为 10 秒，故合并为一个开关。
- 验收：Release 构建零警告零错误；单元测试 **443 个全绿**（新增 31 个：模型 JSON 契约与类型路由（含选择器省略、变量元数据、整条往返）；请求字段与命名空间、无条件读、`404` / `304` 错误码上抛、契约破坏、参数校验、释放后拒绝；订阅的首值/选择器透传/变化投递/条件读并核对 `md5`/撤回投递 `null`/先 `null` 后出现/撤回只报一次/刷新失败容忍/缓存命中零往返/版本与标签同串不同键/退订停止轮询/释放时结束/参数校验）。集成测试新增 5 个用例、整套 **31 个用例全绿（5m34s）**，Prompt 的 5 个跑在 `nacos/nacos-server:v3.2.4` 上（HTTP 管理接口草稿 + 发布两版 → 读 latest / 指定版本 / 绑定标签后按标签读，并核对变量元数据；未发布键与未发布版本回 `404`；订阅收到新版；订阅收到标签重绑后的版本；`DELETE` 后订阅收到 `null`）。
- harness：`NacosContainer.EnsureInitialAdminAsync` 改为公开并新增 `CreateAccessTokenAsync`——`NACOS_AUTH_ENABLE=false` 的服务器同样对 `/v3/admin` 要求登录，因此用 HTTP 播种资源必须先建首个管理员（幂等，409 视为成功）。`NacosHttp` 新增 `PublishPromptAsync`（draft + force-publish）/ `BindPromptLabelsAsync` / `DeletePromptAsync`；`NacosEventually.ReportsAsync` 为“已经产生且无法重复施加的变更”提供有界等待（一个 Prompt 版本只能发布一次，删除也不可重复）。
- 顺带记录一条实测事实：Prompt 的 HTTP 客户端接口与 gRPC 接口走同一套服务，错误消息完全一致，因此集成用例可以直接照抄 HTTP 探测到的消息文本。

#### C4 · Skill 查询与订阅（✅ 已完成）

- `Ai/NacosSkill.cs`：`NacosSkill`（`name` / `version` / `md5` / `archive`）。Skill 不是文档而是**目录**（`SKILL.md` + 被引用的资源文件），服务器以 ZIP 归档下发，因此模型直接携带归档字节（`ReadOnlyMemory<byte>`）以及响应头里的“解析出的版本”和“已发布内容哈希”。官方 `downloadSkillZip` 只回 `byte[]`；这里一并返回版本与哈希，因为调用方需要记录“实际拿到了什么”，而这两个值本来就在响应头里。
- 传输：**仅 HTTP**。官方客户端同样只在 HTTP 上实现（`AiGrpcClient.querySkill` 直接抛 `SERVER_NOT_IMPLEMENTED`），服务端是普通 OpenAPI：`GET {context}/v3/client/ai/skills?namespaceId=&name=&version=&label=&md5=`。因此读 Skill 完全不打开 AI gRPC 连接，只需要一个可达的 HTTP 端点；token 走 `accessToken` 头（客户端 OpenAPI 的过滤器按这个名字取值），而不是 `Authorization: Bearer`（管理 API 才用它）。
- 新增 `Internal/NacosAiHttpClient`：AI 模块的 HTTP 通路。按配置顺序轮流选起始服务器，**只在连接级失败时切换下一个**（404/500 是集群事实，重试只是加延迟）；`403` 失效 token 后**只重试一次**；超时与连接失败映射为 `NacosConnectionException`；错误体按 v3 结果解析，**优先取 `data`**——`message` 只是错误码名（`resource not found`），真正的细节（`Skill not found: x`）在 `data` 里。
- 选择器：版本必须存在于索引清单，否则按标签查（无标签则 `latest`），解析不到就是 `404`。与 Prompt 不同，**未知标签不会回落到 latest**：`SkillIndexManifestService.resolveVersion` 返回 null，于是版本按“不存在”报错，笔误不会被静默替换成别的内容。
- 归档校验：先查 ZIP magic 与最小长度，再逐条目拒绝 `..`、NUL、绝对路径与 Windows 盘符前缀（与官方 `validateZipBytes` / `validateZipEntryPaths` 同规则）。归档以字节交给调用方、解压由调用方负责，但不安全的条目不能被递到解压器手上。
- 读取 `GetSkillAsync(skillName, version = null, label = null, ct)` 是无条件读（`md5` 传 `null`）；`304` / `404` / `403` 分别映射为 `NacosServerException(304)` / `NacosServerException(404)` / `NacosAuthException(403)`；释放后调用抛 `ObjectDisposedException`。
- 订阅 `IAsyncEnumerable<NacosSkill?> SubscribeSkillAsync(...)`：与 Prompt 同一套机制，元素可为 `null`（下线/删除 → `404` → 清缓存并投递 `null`）。**变更判定按归档字节逐字节比较**，不按响应头里的哈希：服务器算不出哈希时仍会下发内容，两个都没有哈希的不同归档若按 `null == null` 比较就永远不会被通知。
- 复用：把 C3 的 `NacosPromptCache` 抽成泛型 `NacosAiResourceCache<T>`（查询、比较、哈希提取、日志回调全部注入），Prompt 与 Skill 共用“每选择器一个刷新循环 + 每消费者一个队列 + 最后一个订阅者停止”的实现，`NacosPromptCache` 随之删除；C5 的 AgentSpec 可直接复用。
- 日志事件：8013 Skill 变化（Debug）、8014 已撤回（Debug）、8015 刷新失败（Warning）。
- 与上游 `nacos-develop` 的**有意差异**（3 条，与 Prompt 的三条同构）：
  1. 选择器已有缓存副本时订阅不回源读取（上游 `subscribeSkill` 每次都读一遍再挂更新任务）。
  2. 缓存副本在最后一个订阅者离开后保留（上游 `unsubscribeSkill` 会 `skillMd5Cache.remove`）。需要“服务器当下的答案”时调用 `GetSkillAsync`——它从不查缓存。
  3. 刷新间隔复用 `NacosAiOptions.CacheRefreshInterval`（上游按资源类型读 `nacosAiSkillCacheUpdateInterval`）。
- 验收：Release 构建零警告零错误；单元测试 **484 个全绿**（新增 41 个：HTTP 通路的 token 头/匿名/403 单次重试/重试有上限/服务器切换/全失败/超时/查询串顺序与空值省略/304 空体/错误体 `data` 优先/非结果体回退 reason phrase；Skill 读取的版本与哈希/选择器透传/默认不传选择器/参数校验/未知资源与未知版本/403/异常状态/非 ZIP/损坏 ZIP/路径逃逸/绝对路径/不打开 AI 连接/释放后拒绝；订阅的首值/选择器/变化/条件读并核对 `md5`/撤回投递 `null`/先 `null` 后出现/只报一次/失败容忍/缓存命中/版本与标签同串不同键/退订停止轮询/释放时结束/参数校验；`ContextPath` 为空的 URI 拼接与 `ParseAll` 的默认上下文路径）。集成测试新增 5 个用例（整套 **36 个**），跑在 `nacos/nacos-server:v3.2.4` 上：草稿 + 强制发布 → 读 latest 并逐条核对归档（`{name}/SKILL.md` 与 `{name}/reference/notes.txt`）；发布两版 + 绑定标签 → latest / 指定版本 / 标签三种读法各回对应版本；未知名字与未知版本回 `404`；订阅收到新版；`POST /offline` 后订阅收到 `null`。
- harness：`NacosHttp` 新增 `SkillMarkdown` / `PublishSkillAsync`（新建走 `POST /draft`；新版本先按 `basedOnVersion` fork，再 `PUT /draft` 改内容，最后 `POST /force-publish`）/ `BindSkillLabelsAsync` / `OfflineSkillAsync`。三条实测记录：①标签接口是**整表覆盖**，只写 `{"stable":...}` 会把服务端维护的 `latest` 一起丢掉，之后不带选择器的读变成 `404`，集成用例因此显式写回 `latest`；②`publish` 只接受 `reviewing` 状态的版本，草稿必须走 `force-publish`；③错误体形如 `{"code":20004,"message":"resource not found","data":"Skill not found: x"}`。
- 顺带修正一处 C3 遗留的测试竞态：两个订阅同时启动时，请求到达顺序不确定，断言 `[0]`/`[1]` 会偶发失败（Prompt 与 Skill 的“版本与标签同串”用例都改为断言选择器集合）。
- 顺带修正一处更早的缺陷：`NacosOptions.ContextPath` 此前只是被校验、并未参与地址解析（`ParseAll` 没把默认上下文路径传下去），因此自定义上下文路径对 HTTP 登录与数据读取都不生效。现在工厂把 `options.ContextPath` 传给 `ParseAll`，`BuildHttpUri` 也会去掉基地址的尾斜杠，根路径部署不再拼出 `//v3/...`。

#### C5 · AgentSpec 查询与订阅（✅ 已完成）

- `Ai/NacosAgentSpec.cs`：`NacosAgentSpec`（`namespaceId` / `name` / `description` / `bizTags` / `content` / `resource` / `version` / `md5`）与 `NacosAgentSpecResource`（`name` / `type` / `content` / `metadata` / `resourceIdentifier`）。与 Skill 不同，服务器下发的不是归档而是 **v3 结果信封**（`{code,message,data}`），`data` 才是规格本体；`content` 是 `manifest.json` 的原文（字符串），`bizTags` 同样是存储形态的 JSON 数组字符串——两者都按“服务器怎么存就怎么给”透传，客户端不重新解释。
- 传输：**仅 HTTP**，`GET {context}/v3/client/ai/agentspecs?namespaceId=&name=&version=&label=&md5=`。没有任何 `.proto` 声明 AgentSpec 服务，官方 `AiGrpcClient.queryAgentSpec` 直接抛 `SERVER_NOT_IMPLEMENTED`，因此读规格不打开 AI gRPC 连接。token 走 `accessToken` 头；管理 API 用 bearer 头。
- 版本与哈希在**响应头**里（`X-Nacos-AgentSpec-Resolved-Version` / `X-Nacos-AgentSpec-Md5`），payload 不带，因此模型上的 `Version` / `Md5` 由响应头填充。**304 只回 md5、不回解析版本**——与 Skill 相反：`SkillRequestUtil.buildSkillNotModifiedResponse` 会把 resolvedVersion 带上，`AgentSpecRequestUtil.buildAgentSpecNotModifiedResponse` 传的是 `null`。
- 选择器：**标签优先于版本**。`AiResourceManager.resolveVersion` 的顺序是 label → version → `latest`，未知标签**不报错**而是逐级回落；Prompt 的文档优先级是 `version > label > latest`，Skill 的未知标签直接判版本缺失。三种资源三种规则，集成用例显式断言了这一顺序。
- 资源 map 的 key 是注册中心派生的标识符（`config_SOUL__md`），路径在 `resourceIdentifier`（`config::SOUL.md`）；`name` 只是文件名（`SOUL.md`），`type` 是目录（`config`）。按路径查 map 是查不到的。
- **下线会被条件读掩盖**（本次发现并修掉的真实缺陷）：`queryAgentSpecForClient` 先解析选择器、再比对版本行里存的 md5，命中就直接回 304，**之后才**检查版本是否 ONLINE；而 `/offline` 不改动标签，`latest` 仍指向该版本，于是“带 md5 的读”对一个已下线的版本永远得到 304，订阅永远收不到撤回。Skill 不受影响：它的选择器走索引清单，下线即从清单移除，解析不到版本，也就没有 hash 可比。修复是把订阅的 `Hash` 提取器置空（`static _ => null`），每次刷新做**无条件读**，用 `IsSame`（整文档序列化比较）判定变化；代价是每周期传一次 body，换来“撤回能被看见”。
- 读取 `GetAgentSpecAsync(name, version = null, label = null, ct)` 无条件读（`md5` 传 `null`）；`304` / `404` / `403` 分别映射为 `NacosServerException(304)` / `NacosServerException(404)` / `NacosAuthException(403)`；释放后调用抛 `ObjectDisposedException`。
- 订阅 `IAsyncEnumerable<NacosAgentSpec?> SubscribeAgentSpecAsync(...)`：复用 C4 泛型化的 `NacosAiResourceCache<T>`，元素可为 `null`（下线 → `404` → 清缓存并投递 `null`）。
- 日志事件：8016 变化（Debug）、8017 已撤回（Debug）、8018 刷新失败（Warning）、8019 结果缺失（Warning）。
- 发布侧（仅集成夹具使用）：首版 `POST /upload`（multipart ZIP，名字取自 `manifest.json` 的 `worker.suggested_name`，版本由服务端定为 `0.0.1`）→ `force-publish`；后续版本 `POST /draft`（fork）→ `PUT /draft`（改内容）→ `force-publish`。三条实测：①`PUT /labels` 是**整表覆盖**，只写 `{"stable":…}` 会把服务端维护的 `latest` 一起丢掉，之后不带选择器的读变成 `404`；②`publish` 只接受 `reviewing` 状态的版本，草稿必须 `force-publish`；③`v3.2.4` 镜像自带 187 个内置规格，用例必须用唯一名字，否则撞名。
- 验收：Release 构建零警告零错误；单元测试 **517 个全绿**（新增 33 个：模型与 JSON 形状/选择器透传/404 与 403 映射/非结果体/订阅首值/变化/撤回/缓存命中/退订停止轮询/无条件读不带 `md5`/304 视为未变化）；集成测试新增 5 个用例（整套 **41 个**），跑在 `nacos/nacos-server:v3.2.4` 上：上传 + 发布 → 读 latest 并核对 manifest 与资源标识符；发布两版 + 绑定标签 → latest / 指定版本 / 标签 / 标签+版本 四种读法；未知名字与未知版本回 `404`；订阅收到新版；`POST /offline` 后订阅收到 `null`。

#### C6a · A2A AgentCard 查询、发布与端点注册（✅ 已完成）

- `Ai/NacosAgentCard.cs`：`NacosAgentCard`（A2A 文档本体：`protocolVersion` / `name` / `description` / `version` / `iconUrl` / `capabilities` / `skills` / `url` / `preferredTransport` / `additionalInterfaces` / `supportedInterfaces` / `provider` / `documentationUrl` / `securitySchemes` / `security` / `defaultInputModes` / `defaultOutputModes` / `supportsAuthenticatedExtendedCard` / `signatures`）、`NacosAgentCardDetail`（+ `registrationType` / `latestVersion`）、`NacosAgentInterface` / `NacosAgentSkill` / `NacosAgentCapabilities` / `NacosAgentExtension` / `NacosAgentProvider`，以及公开常量 `NacosAgentRegistrationType.Url` / `.Service`。安全方案、签名、扩展参数这三处形状由协议开放，按 `JsonElement` 原样保留——投影成模型只会丢字段。
- 传输：**走 AI gRPC 连接**（与 Skill/AgentSpec 相反）。线上类型名 `QueryAgentCardRequest` / `ReleaseAgentCardRequest` / `AgentEndpointRequest`；能力位 `agent`，且按“只有服务端明确说 false 才拒绝”的宽松规则判定——没声明不等于不支持。
- 版本：`@Since("3.1.0")`（`QueryAgentCardRequestHandler` / `ReleaseAgentCardRequestHandler` / `AgentEndpointRequestHandler`），批量端点 `@Since("3.1.1")`。因此在 `v3.2.4` 上可真机验证。
- **卡片先校验后存储，校验决定形状**：必须有 name + version，且满足两种描述之一——`supportedInterfaces`（每项都要有 url + protocolBinding + protocolVersion），或旧三元组 `protocolVersion` + `preferredTransport` + `url`。两者都不满足即 `Required parameter 'agentCard.supportedInterfaces' not present, and old protocol fields … are incomplete`。所以**服务型卡也必须声明一个 interface**，哪怕还没有运行时端点。
- **`SERVICE` 卡的地址是读时合成的**：读卡时按 `{agentName}::{version}` 去 `AGENT_ENDPOINT_GROUP` 下找 Naming 服务，有实例就用它们覆盖 `supportedInterfaces` / `additionalInterfaces` / `url` / `preferredTransport` / `protocolVersion`；没有实例则声明的 interface 透出。`URL` 卡跳过这一步，自己的 `url` 就是地址。
- **端点必须绑定卡片版本**：注册会变成名为 `{name}::{version}` 的服务实例，服务端拒绝空版本（`Required parameter 'endpoint.version' can't be empty or null`），客户端也前置校验（版本 / 地址 / 端口范围），与官方客户端一致。其余字段带默认值：transport `JSONRPC`、protocol `HTTP`。
- 端点即 Naming 实例，随注册它的连接消亡，因此 `NacosAgentEndpointPublication` 记录并按 `agent::address:port/path` 逐条重放；**被服务端拒绝的注册不重放**（调用方已经看到失败）。`protocolVersion` / `tenant` 存于保留元数据键 `__nacos.agent.endpoint.protocolVersion__` / `__nacos.agent.endpoint.tenant__`。
- 读取 `GetAgentCardAsync(agentName, version = null, registrationType = null, ct)`；订阅 `IAsyncEnumerable<NacosAgentCardDetail?>`，复用 `NacosAiResourceCache<T>`（卡片无哈希 → 每次无条件读，用整文档序列化比较判定变化；缓存的第三个选择器槽位承载 `registrationType`）。
- 日志事件：8020 已发布（Info）、8021 结果缺失（Warning）、8022 变化（Debug）、8023 已撤回（Debug）、8024 刷新失败（Warning）、8025/8026 端点注册与注销（Info）、8027 重放（Info）、8028 重放失败（Warning）。
- 验收：Release 构建零警告零错误；单元测试 **538 个全绿**（新增 21 个：请求字段与线上类型名 / 选择器透传 / 结果缺失 / 能力位拒绝与“未声明则照常尝试” / 发布默认 `SERVICE` 且不动 latest / 注册类型与 latest 透传 / 端点字段与默认值 / 端点前置校验 / 重放 / 拒绝不重放 / 注销后不重放 / 订阅首值·选择器·变化·撤回·缓存命中）；集成测试新增 5 个用例（整套 **46 个**），跑在 `nacos/nacos-server:v3.2.4` 上：发布 + 读回并核对 skills/capabilities、未知 agent 回 404、注册端点后读回的 `url` 与 `supportedInterfaces` 来自该端点、注销后回落到声明的 interface、订阅收到新版本。

#### C6b · RAD v1 Agent 注册中心（✅ 已完成）

- `Ai/NacosAgentRegistry.cs`：`NacosAgentPublishRequest`（metadata + `version` / `callInterfaces` / `author` / `changeDescription` / `basedOnVersion` / `autoSubmit`）、`NacosAgentVersionDetail`（+ `namespaceId` / `agentName` / `callInterfaces`）、`NacosAgentVersionSummary` / `NacosAgentVersionInfo` / `NacosAgentSummary`、`NacosAgentCallInterface` / `NacosAgentEndpointSet` / `NacosAgentRuntimeEndpoint` / `NacosAgentVersionBinding`、`NacosAgentSearchRequest` / `NacosAgentPage<T>`、`NacosAgentReference` / `NacosAgentDiscoveryRequest` / `NacosAgentDiscoveryFilter` / `NacosAgentDiscoveryResult`、`NacosAgentEndpointBatch`、`NacosAgentRegistryProvider`。`nativeDescriptor` / `extensions` 按 `JsonElement` 保留。
- 传输：AI gRPC 连接，线上类型名 `AgentPublishRpcRequest` / `AgentSearchRpcRequest` / `AgentDiscoveryRpcRequest` / `AgentEndpointRegisterRpcRequest` / `AgentEndpointDeregisterRpcRequest`（及各自的 Response）。版本 `@Since("3.3.0")`，因此真机用 `v3.3.0-RC`（新增 `NACOS_AGENT_TEST_IMAGE`）。
- **能力位是严格协商**（本模块唯一一处）：必须服务端明确 `radV1=true` 才发请求；只声明 `agent`（3.1/3.2 的 A2A 契约）→ 501 并说明需要 3.3；两者都没声明 → 500「无法判断」。其它 AI 资源是“未声明就照常尝试”，因为它们只有一个契约可试。
- **publish 必须给 callInterfaces 或 basedOnVersion 之一，不能都给**（`validateDraft` 里 `directContent == copiedContent` 即拒）。callInterface 按描述符校验：`protocol`、`descriptorMediaType`（`<type>/<subtype>`）、非空 `nativeDescriptor`、发布侧还必须有 `endpointSourceOrder`；而**发现结果反过来不允许**带 `endpointSourceOrder`（此时顺序已生效）。
- **首版发布即上线**：`AgentPublishApplicationService` 对首版无视 `autoSubmit` 直接 submit，默认流水线直接通过，所以 publish 返回 `online` 而不是 `draft`，发现立刻可解析；后续版本只有 `autoSubmit` 才提交，否则保持草稿。这也意味着对首版再 `force-publish` 是 400（已在线）。
- **端点批次必须绑定版本区间**：端点自身没有 `bindings` 时继承批次的 `runtimeVersion` / `versionRange`，两者都不给即 `Invalid Agent version: null`。区间必须规范形式（`[1.0.0,2.0.0)`）且包含 runtimeVersion，批次内 bindings 必须严格有序。批次是“替换”单位——同一发布者同一协议再注册会覆盖上一批，这正是滚动发布安全的原因。
- **移除按协议**而非按端点：请求只带 agentName + protocol，因为一个进程注册的端点只有作为集合才有意义。
- 读取：`PublishAgentAsync` / `SearchAgentsAsync` / `DiscoverAgentAsync` / `RegisterEndpointsAsync` / `DeregisterEndpointsAsync`；客户端前置校验（publish 的 name+version、批次的 agent/protocol/非空端点），版本区间留给服务端规范化。日志事件：8029 已发布（Info）、8030 发布缺详情（Warning）、8031 发现缺结果（Warning）、8032/8033 端点批次注册与移除（Info）。
- 验收：Release 构建零警告零错误；单元测试 **552 个全绿**（新增 14 个：发布字段与线上类型名 / 必填校验 / 缺详情 / 搜索与分页 / 发现与过滤器 / 缺结果 / 端点批次 / 空批次拒绝 / 按协议移除 / 能力位四态）；集成测试新增 5 个用例（整套 **51 个**），跑在 `nacos/nacos-server:v3.3.0-RC` 上：发布→搜索→发现闭环、搜索命中、未知 agent 报错、注册端点后成为发现结果里的 RUNTIME 端点、按协议移除后消失。

#### C7 · RAD agent watch（✅ 已完成）

- `Ai/NacosAgentWatchManager.cs`：watch 的全部状态机。每个 intent（一个「什么 + 过滤器」的规范化身份）持有稳定的 `ClientWatchId`（连接内去重键）、服务端下发的 `WatchKey`、intent 自己的 `Fingerprint` / `PendingFingerprint`、当前已投递状态与它的本地摘要，以及一个 `Wake` 信号。按 intent 懒启动一个循环任务，最后一个调用方退出时退订并停掉循环。
- 线上类型（`Protocol/Messages/AiMessages.cs`）：`AgentSubscribeRpcRequest`（`clientWatchId` / `discoveryRequest` / `materializedFingerprint`）↔ `AgentSubscribeRpcResponse`（`watchKey` / `observedFingerprint` / `refreshRequired`）；`AgentUnsubscribeRpcRequest`（`watchKey`）↔ `AgentUnsubscribeRpcResponse`；服务端推送 `AgentDiscoveryNotifyRequest`（`watchKey` / `eventType` / `observedFingerprint` / `errorCode`）→ `AgentDiscoveryNotifyResponse`（`watchKey` / `accepted`）。已登记进 `NacosJsonContext` 与 `PayloadTypeRegistry`；`EventType` ∈ `INVALIDATE` / `REVALIDATE` / `TERMINATED`。
- **客户端能力位新增 `radWatchV1`**（`NacosGrpcConnection.BuildAbilityTable()`），服务端能力位同名；与 RAD 注册中心一样是**严格协商**：能力位缺失即视为不实现，服务端明确 `false` 也拒绝，两种情况都抛 `NacosServerException(501)`，而不是静默降级成「永远不再推送」的 watch。
- **提示不等于答案**：`INVALIDATE` 带新指纹、`REVALIDATE` 不带指纹，两者都只表示「可能变了」；对外投递的状态永远来自一次 `AgentDiscoveryRpcRequest` 读取，从不来自提示本身。提示指纹等于手上指纹时不做任何读。
- **指纹只认服务端说的那一份**：`materializedFingerprint` 告诉服务端客户端手上是什么，`observedFingerprint` + `refreshRequired` 是回答。服务端只在非 null 时校验该字段，所以本客户端**订阅时发 null**，随后采用服务端的 `observedFingerprint` 作为后续提示的比对基准。代价是每次装表（含重连后）多一次投影读；换来的是不需要第二份指纹规范化实现，两侧也就不会对「同一个状态的指纹」产生分歧。
- **提示必须应答**：未被应答的提示服务端会重投，所以不属于本客户端的 `watchKey`（例如退订与推送在线上交错）一律回 `accepted:false`；自相矛盾的提示（没有事件类型、不是本版本定义的事件类型、`TERMINATED` 却不带错误码）同样拒收。
- **同一个状态不重复投递**：提示只说「可能变了」，读完发现没变就不投递——否则「每来一个元素就动作」的调用方会被同名消息淹没。判等用**本地摘要**（对已服务文档序列化，只在进程内比较，从不上线）；`REVALIDATE` 因此安全。
- **`TERMINATED` 有两种含义**：带 not-found 码表示 agent 不在了——这是**一种状态**而不是「不再关心」，于是投递一次「未服务」、清掉 `WatchKey` / 指纹，然后用同一个 `ClientWatchId` 重新装表（服务端 `findByClientWatchId` 命中仍注册着的旧 watch，因此重新订阅是重新绑定而非新开）；带其它码表示服务端已经裁决，watch 结束并把错误抛给调用方。
- **「没发布过」不是失败**：装表时发现目标不存在、以及读取时发现已下线，都投给调用方「未服务」并继续重试装表，等 agent 出现；每个「缺失期」只报一次。这里踩到一个真实的不对称：gRPC 错误映射器报的是 v2 业务码，未知 agent 的发现/装表失败实际到达是 **`20004`（`RESOURCE_NOT_FOUND`）**，而 watch 背后的投影报的是状态码 `404`，旧 A2A 路径还有 `50100` / `50101`——四种拼写都按「未发布」处理（先按 50100 写、真机探测纠正为 20004）。
- **watch 随连接消亡**：重连后每个 intent 重新装表并**重新读取**（清掉指纹，不假设状态没变）；重连期间到达的提示因 `watchKey` 不在册而被拒收，不会误判。
- 错误分类按「再问一次能不能改变结果」：服务端明确不实现（501）、拒绝凭据、拒绝负载、回复不合契约 → 结束并把错误交给调用方；连接断开、服务端尚在启动、读取自身失败 → 不结束，按有界抖动退避重试（基准 `NacosOptions.Ai.CacheRefreshInterval`，翻倍至 60 秒上限，±20% 抖动，空闲 5 分钟）。**把 500 归入终止**是本实现有意的偏差：官方客户端的 `activateSynchronously` 会把首次失败抛给调用方，而本客户端的循环模型没有这个「激活屏障」。
- 公共面：`INacosAgentRegistry.WatchAgentAsync(reference, filter, ct)` 返回 `IAsyncEnumerable<NacosAgentDiscoveryResult?>`（首元素是当前状态，`null` = 当前未服务；服务端终止 watch 时枚举结束）。`NacosAgentRegistryClient` 新增 `Watch`、`WatchAgentAsync`、`DisposeAsync`（`IAsyncDisposable`）；`NacosAiClient` 把推送接进 manager（`ServerRequestReceived`）、在 `Reconnected` 上 `RestoreAsync`、释放时先于 `_connection` 关掉 watch。日志事件 8034–8040（启动/停止/刷新失败/被终止/提示未命中/恢复失败/退订失败）。
- **偏差**（记入 `docs/protocol-notes.md`「Where this client deviates from the official client (RAD agent watch)」）：①只实现 gRPC 传输——官方有 `AgentWatchTransportRouter`（gRPC → 批量 HTTP 长轮询 → 有界轮询，且线上恢复后升级回 gRPC），本客户端对不声明 `radWatchV1` 的服务器直接 501；②不本地计算指纹（见上）；③可观测形态是异步流（官方是 listener + 事件），官方「不可用」事件对应 `null`、「终止性不可用」对应结束枚举的 `NacosServerException`；④重试节奏是本客户端自己的，不是官方 `AgentWatchRetryPolicy`。
- **顺带修的集成夹具缺陷**（真机跑出来的，与 C7 无关但会拖垮验证）：①RAD 发布是「写后自己回读」的写操作，回读窗口偶尔读不到刚创建的版本，服务端回内部错误（`AgentPersistenceService` 的 `AgentVersion cannot be read after creation`）。发布对同一请求幂等（已存在的版本原样返回），因此新增 `NacosEventually.WritesAsync` 只用在「发布是前置条件」的用例上，主题是发布的用例仍直连调用以便暴露真实缺陷。②多处 `BecomesAsync` 的等待条件只检查版本可解析，而断言读的是同一回复里的嵌套集合/压缩包（`Skills` / `SupportedInterfaces` / `Variables` / `CallInterfaces` / `ToolSpec.Tools` / `Resource` / `Archive`），服务端把版本行与派生内容分两步落地，于是「等到了版本、内容还空」会随机判负；等待条件改为覆盖断言真正触碰的部分。
- 验收：Release 构建零警告零错误；单元测试 **573 个全绿**（新增 21 个：首元素/装表报文不含指纹/提示驱动变化/同指纹提示不读/未发布投 null/ACK 已受理/拒收未知名单/`TERMINATED` 带原因 50105 抛错/`TERMINATED` 50100 投 null 并重装/装表被拒 50100 → null/被拒 403 一次即终止/`REVALIDATE` 后状态未变不重复投递/`TERMINATED` 无原因拒收/两调用方共享同一 watch/最后一个停止时退订/能力位缺失 → 501/能力位为 false → 501/重连后重装/`radWatchV1` 已声明/真机同款码 20004 → null/缺失不重复投递）；集成测试新增 `AgentWatchIntegrationTests` 两个用例（发布后 watch 报当前状态、随后一次发布由服务端推送过来；未发布的 agent 报「未服务」），整套 **53 个全绿**，基座套件在 `v3.0.2` / `v3.2.4` / `v3.3.0-RC` 三个版本上分别通过。

#### C8 · DI 收口、示例与版本矩阵（✅ 已完成；gRPC↔HTTP 回退仍推迟）

- **DI 接线**：`AddNacos` 原本只注册 `NacosClient` / `INacosConfigClient` / `INacosNamingClient`，锁与 AI 两个后加的模块要调用方自己 `GetRequiredService<NacosClient>().Lock` / `.Ai` 才能拿到。现按同样方式注册，且都解析为共享客户端持有的那个实例，不会开第二条连接。
- **同步释放缺陷（接线后暴露）**：DI 容器默认同步释放单例，遇到只实现 `IAsyncDisposable` 的实例会抛 `InvalidOperationException: '...' type only implements IAsyncDisposable`。`NacosClient` / `NacosConfigClient` / `NacosNamingClient` 都实现了 `IDisposable` 以避免这一点，但 `NacosLockClient` 与 `NacosAiClient` 没有——此前没有任何容器持有它们，所以从未暴露。两者现按同一模式补上 `IDisposable`（阻塞等待 `DisposeAsync`，只关进程内资源），`using IHost` 的正常关停重新可用。**并非只有 AI 不能同步释放：锁客户端同样不能**；早先“只有 AI”的判断来自只接线了一个模块。
- 注册测试改为解析四个特性客户端并断言各自就是共享客户端的实例；它以**同步**方式释放 provider，因此任何一个特性客户端一旦不再可同步释放，测试即失败。
- 新增可运行示例 `samples/Sample.AiRegistry`：从容器解析 `INacosAiClient`，读取 MCP / Prompt / Skill / AgentSpec / AgentCard 五种资源并订阅一个 MCP server，按常规方式释放 host。
- **gRPC↔HTTP 自动回退仍不做**：官方客户端有 `McpTransportRouter` / `PromptTransportRouter` / `AgentTransportRouter`（配 `AiCapabilityCache` 与约 1165 行的 `AiHttpClientProxy`），按能力缓存把调用分流到 gRPC 或 HTTP；本客户端仍只走 gRPC（技能与 agent spec 本来就是 HTTP）。差别只在“gRPC 能力缺失但 HTTP 面存在”的服务器上体现，而现有镜像没有这种服务器可验证，故按“宁可不做也不发未验证代码”的原则推迟为独立切片，当前行为记录在 `docs/protocol-notes.md` 的「Transport selection and HTTP fallback」。
- 版本矩阵与四个集成镜像的对应关系补入 `docs/faq.md`。CI 的集成任务改为矩阵：基座套件在 `v3.0.2` / `v3.2.4` / `v3.3.0-RC` 三个版本上各跑一遍（本地分别 51/51 验证），模块镜像保持固定。
- 修复 `ConfigIntegrationTests.Listen_SurvivesAServerRestart` 的固有竞态：`ListenAsync` 的推送注册只在枚举恢复到下一个元素时才发生，会与容器重启后客户端的重连赛跑——连接尚未建立时注册被拒，异常从 `MoveNextAsync` 抛出并直接判负。测试现在先等一次读成功（`tolerateFailures`）再恢复枚举，竞态消除；当时“重连期间请求不排队”这一产品缺口另有文档记录，已由 G1 关闭（见下）。
- 验收：Release 构建零警告零错误；单元测试 **552 个全绿**；集成测试 **51 个全绿**，基座套件在 `v3.0.2` / `v3.2.4` / `v3.3.0-RC` 三个版本上分别通过（四个镜像）。

- 验收：查询/发布/订阅端到端通过；AI 推送与 watch 可用（阶段级，随各子阶段推进）。

#### D1 · AI 本地缓存落盘 failover（✅ 已完成）

- `Internal/NacosAiSnapshotStore.cs`：AI 资源的落盘 failover 存储，形状对齐 `NacosConfigSnapshotStore`（`<LocalCacheDirectory>/ai` 目录、临时文件 + 原子改名、路径安全校验、读不回来即视为不存在）。载荷是资源的 JSON，走协议同一套 source-generated 契约（`NacosJsonContext` 新增 4 个 root），因此格式不需要版本标记：读不回来与从未写过对调用方是同一件事。文件名是 `kind` + 选择器键的 SHA-256 十六进制，`kind` 参与身份（Prompt 与 Skill 可以合法同名）；用 SHA-256 而不是协议的 MD5，是因为这个摘要只做本地文件名、不与任何对端共享。
- 落盘策略 `NacosAiSnapshot<T>`（`Kind` + `JsonTypeInfo<T>` + 存储）挂在两个缓存上：泛型 `NacosAiResourceCache<T>`（Prompt / Skill / AgentSpec / AgentCard，`NacosAiResourceCacheOptions<T>.Snapshot`）与 `NacosMcpServerCache`（构造参数）。客户端没拿到存储（`EnableLocalCache = false`）时策略为 `null`，缓存退回纯内存行为，与今天完全一致。
- 写入时机：`Process` / `CompareAndStore` 在**副本发生变化**时写入（首次读、内容更新），在资源被服务端判定为**已撤回**时删除文件（`Process(key, null)`）。删除**不**依赖「是否算变化」——否则上一进程留下的文件会在下一次故障中被复活，这是 failover 唯一不能失败的方向。
- 读取时机：**只有**读不到服务器时（`NacosConnectionException` / `NacosTimeoutException`）才读文件，服务端可达时永远先问服务端。两条路径都覆盖：
  - 订阅的首读（`TryQueryAsync`）读不到服务器 → 文件有副本就作为首值投递，没有则原样抛出该异常。
  - 订阅**装表前**的连接预检失败 → 用 `RestoreAsync` 把副本 seed 进缓存后继续订阅：副本已在缓存里，首读不再需要连接，调用方拿到的是上一次读到的内容，刷新循环照常运行并在服务器回来时发现变化；没有副本时仍然是原来的异常。
  - 这条预检是本切片的关键发现：Prompt 与 MCP 的 `Subscribe*Async` 会先 `GetConnectionAsync`（MCP 还要查能力位），**「重启碰上故障」会在到达缓存之前就失败**，而能力位在连接不可达时也无从判定；因此只能在「已有副本」的前提下放行预检。没有预检的 Skill / AgentSpec / AgentCard 直接走缓存回调。
- 接线：`NacosClientFactory` 在 `EnableLocalCache` 为真时创建存储并传给 `NacosAiClient`，后者传给 MCP / Prompt / Skill / AgentSpec / AgentCard 五个客户端；单元测试夹具 `AiTestServer.CreateClient` 增加可选存储参数，不传时行为不变（既有 AI 用例因此零改动）。
- 与上游的**有意差异**（记入 `docs/protocol-notes.md`「The local failover cache」）：官方 Java 客户端这些缓存**只在内存**（`client/ai/cache` 与 `client/ai/watch` 均为内存实现），落盘是本客户端的扩展，多语言契约允许客户端记录自身偏差。RAD agent watch **不做**磁盘缓存：`specs/en/client/client-local-cache-redo-spec.md` §8 把 RAD 恢复的磁盘部分标为 target 而非契约，其重连恢复仍是内存语义。
- 日志事件：8041 从本地缓存应答（Info）、8042 落盘失败（Warning；读已经成功，缓存写不进去不判调用方失败）。
- 文档：`NacosOptions.LocalCacheDirectory` 与 `NacosAiOptions.EnableLocalCache` 的注释改写（原先写「尚未实现」），`docs/configuration.md` 的 `NacosAiOptions` 表、`docs/faq.md` 新增「Does the AI registry cache survive a restart?」、`docs/protocol-notes.md` 新增「The local failover cache」，README 的 AI 段落补一句。
- 验收：Release 构建零警告零错误；单元测试 **594 个全绿**（新增 21 个：存储的往返（含 Skill 归档字节）/ 分类隔离 / 覆盖写 / 删除 / 读不回来视为不存在 / 空目录校验；缓存的写入时机 / 撤回删文件 / 读不到服务器时投递副本 / 无副本仍抛原异常 / `RestoreAsync` seed 后零读 / 无存储时 `RestoreAsync` 为假；MCP 同样的四条；客户端接线后 Prompt 与 MCP 的落盘；以及一次真正的「重启」——第一个进程读到副本，第二个进程用同目录、指向不可达地址（`Optional = false`，端口 1）仍拿到首值）。集成测试新增 `AiLocalCacheIntegrationTests` 一个用例，整套 **54 个全绿**（10m10s，基座 `v3.0.2`）：真机发布并订阅 → 停掉容器 → 新客户端用同目录订阅同一 prompt 仍是上次读到的模板，而从未服务过的 key 仍然是原来的连接异常。

#### G1 / G2 · 关闭两个已记录的协议缺口（✅ 已完成）

- **G1 · 重连期间到达的请求等待连接，而不是直接失败。** `Transport/NacosGrpcConnection.cs` 新增 `_everConnected`、`_reconnectProbe`、`_reconnectAttemptFailed` 与 `ResolveChannelAsync`：`SendAsync` 取通道时不再「没有就抛」，而是先看有没有正在恢复的连接可等。等待由「恢复的**第一次**尝试的结果」结束（成功即发送；被拒即回到调用方自己的本地 failover），整段等待再由 `NacosOptions.RequestTimeout`（默认 5 秒，即该请求自身的预算）封顶；没有新增公共选项。
  - 为什么等第一次尝试就够：Java 的 `RpcClient.request(request, timeoutMills, maxRetries)` 是在预算内轮询重试，而 `MaxAttempts = 0`（无限重试）下「只按预算等待」会让持续故障期间的每一次读都耗满预算——那正是本地副本存在的意义所要避免的延迟。以尝试为界，抖动（一次被拒）立刻交还给本地副本，真正短暂的断链则被吸收。
  - 从未连上过的客户端立刻失败，不等后台探测：那不是在恢复连接，而是在尝试建立**第一个**连接，调用方等它没有意义（D1 与配置/命名的本地 failover 延迟因此不变）。客户端已释放时同理。
  - 实现细节：`HandleConnectionLossAsync` 在 `TearDown()` 之后建立探测，`finally` 里无条件完成它（成功、放弃、取消、关停四条退出路径都释放等待者，等待者重新读取通道后要么拿到连接要么失败）；被拒的尝试置 `_reconnectAttemptFailed` 并完成探测。日志新增 3006（等待被放弃，Info）。
- **G2 · 用服务器在注册时给出的状态，而不是丢掉它。** 命名侧把推送处理器里的「落地一份服务状态」抽成 `ApplyServiceStateAsync`，`SubscribeAsync` 与 `OnReconnectedAsync` 都拿 `SubscribeServiceResponse.ServiceInfo` 走同一条路径：订阅在服务端生效的瞬间客户端看不见，落在读快照与订阅生效之间的变更不会推给任何人，而服务端的应答正好是那一刻的状态——重连时它还是「断链期间错过了什么」的唯一报告。幂等由阶段 3 已有的机制保证（逐实例比较 + `lastRefTime` 丢弃更早状态）：服务器确实会填 `lastRefTime`（`ServiceUtil.doSelectInstances` 取当前钟），所以应答与推送之间可以比较；既有夹具的应答与读回同一份状态，因此对既有用例是空操作。
  - 配置侧 `SendListenRequestAsync` 改为返回 `ConfigChangeBatchListenResponse`：注册后把**首次读到的 md5** 记进监听注册（这正是让服务端回答「你手上没变」的字段，也避免重连时把从未变过的配置报成变化），重连重放时把 `ChangedConfigs` 里的每一项当作一次推送处理（`OnConfigChangedAsync`，同样是「重读 + 比较 + 通知」）。**首次注册刻意不处理该列表**：紧随其后的读覆盖同一窗口，处理它会把首值投递两次。日志新增 4011（断链期间有配置变化，Info）。
- 单元测试新增 9 个（传输 4：等待重连后被服务、首次尝试被拒即刻失败、从未连上即刻失败、等待被 `RequestTimeout` 封顶；命名 3：订阅应答的状态被应用、应答回显同一状态不重复投递、重连后投递断链期间的变化；配置 2：重放请求携带客户端手上的 md5、断链期间的配置变化被投递给监听）。每个新行为都做过**反证**：把对应实现改回原样后，其用例确实失败（命名与配置的等待改为有界读取，失败是断言信息而不是挂住整套）。
- 集成测试新增 `ConfigIntegrationTests.Reconnect_ServesTheRequestWaitingOnItAndTheChangesItMissed`：客户端首次重连推迟到 90 秒后（真实服务器回到就绪只要十几秒），于是「服务器已回、客户端尚未重连」这个窗口是确定的——期间用一个 HTTP 客户端改掉配置（此时没有任何监听注册在服务端，不可能有推送），随后断言两件事：断链期间发出的那次读**不会被拒绝**而是等连接回来并读到新值（G1），以及重连重放把这次无人可推的变更报给监听（G2）。
- 文档：`docs/protocol-notes.md` 记录等待语义与「注册时状态」的用法，`docs/faq.md` 增加「重连期间的请求会怎样」与「订阅期间的变化会不会漏掉」两问。
- 验收：Release 构建零警告零错误；单元测试 **603 个全绿**（新增 9 个）；集成测试新增 1 个用例，整套 **55 个全绿**（基座 `v3.0.2`，1m44s 为新增用例单独耗时）。

### 阶段 5 · 与 Java SDK 对齐（逐片推进）

- **差距清单**：`docs/java-parity.md`。逐模块（`api` / `client` / `client-basic` / `maintainer-client` / `specs/en`）对照后，把缺口按「.NET 应用实际受多大影响」排成 P0–P3，并写明每一项的 Java 入口、当前状态与理由；同一份文档也列出已经对齐的部分与常设非目标（管理面、控制台、Spring/JVM 方言、AI 自动 HTTP 回退、2.x 兼容）。
- **排序**：P0 = 配置与命名的模糊监听（唯一被 `fuzzyWatch=false` 显式声明的缺口）；P1 = 命名客户端侧 selector、配置过滤器链与 `EncryptedDataKey`、服务名分页；P2 = 批量注册、AI agent 批量 watch（HTTP 长轮询）、本地快照加密、地址服务器、服务端状态；P3 = `NamingMaintainService` 与 AI 管理面（非目标）。
- 每一片仍按既有验收流程：Release 零警告零错误 → 全部单元测试 → 容器内集成测试 → 独立叙述式提交 → 分离 worktree 复验 → 清理。

#### P0-1 · 配置模糊监听（✅ 已完成）

- **缺口**：`fuzzyWatch` 是本客户端唯一对服务端显式声明为 `false` 的协议能力（`AbilityKey.SERVER_FUZZY_WATCH`）。官方入口是 `ConfigService.fuzzyWatch(dataIdPattern, groupPattern, watcher)` / `fuzzyWatchWithGroupKeys(...)`；它解决的是「按模式发现不知道名字的配置」——精确监听必须先知道 dataId，模糊监听才知道有哪些。
- **公共面**：`INacosConfigClient.WatchAsync(dataIdPattern, groupPattern, ct)` 返回 `IAsyncEnumerable<NacosConfigWatchEvent>`，形状与 `ListenAsync` 一致；重载 `WatchAsync(groupPattern, ct)` 用 `*` 作为 dataId 模式。`NacosConfigWatchKind` 为 `Added` / `Modified` / `Removed` / `InitialSyncCompleted`，事件携带 `GroupKeyPattern` 与 `Key`（完成标记的 `Key` 为 null）。`NacosConfigOptions.FuzzyWatchResyncInterval` 默认 3 分钟、校验必须大于 0。重载的 `ct` 是**必填**的：两个重载若都带可选 `ct`，只传一个字符串的调用会有歧义（RS0026 直接报错），因此文档要求不需要取消时显式传 `CancellationToken.None`。
- **协议**：`ConfigFuzzyWatchRequest{groupKeyPattern, receivedGroupKeys, watchType, initializing}`、`ConfigFuzzyWatchSyncRequest{groupKeyPattern, contexts[{groupKey, changedType}], syncType, totalBatch, currentBatch}`、`ConfigFuzzyWatchChangeNotifyRequest{groupKey, changeType}`。模式是 `namespace>>groupPattern>>dataIdPattern`，命名空间取客户端配置（空则 `public`），一个 watch 不能跨命名空间；group key 是 `dataId+group[+tenant]`，只有 `+`→`%2B`、`%`→`%25` 两种转义，其余百分号转义或第四段一律视为非法 key——记日志后跳过该项，不拖垮整批。模式匹配复刻 `FuzzyGroupKeyPattern`：无 `*` 即相等、`*` 全匹配、`*x*` 包含、`*x` 结尾、`x*` 开头，段中 `*`（`a*b`）什么都不匹配。
- **能力位**：只有服务端**明确**声明 `fuzzyWatch=false` 才拒绝并抛 `NacosServerException(501)`；能力表里没有这个键表示「服务端没表态」（未协商的握手就是这样），照常尝试。能力表随握手到达，所以 `WatchAsync` 先建立连接再检查——否则从未连接过的客户端会拿空表跳过检查，把 watch 请求发给一个已声明不支持的服务器。
- **状态机**：每个 pattern 一个共享注册（`_fuzzyWatches`），多个订阅者共用一次服务端 watch 与一条 diff 流；创建/增删订阅/取消共用一把 `SemaphoreSlim` 串行化，且这把锁**跨越 WATCH 往返**——否则「最后一个订阅者退出」与「同时新增订阅」交错会留下一个无人能取消的服务端 watch。订阅者各自维护已知集合以抑制重复事件；晚到的订阅者先用当前集合 seed（逐个 `Added`），已初始化则立即补一个 `InitialSyncCompleted`。推送处理放在 `NacosConfigClient` 内部（`OnFuzzyWatchSync` / `OnFuzzyWatchChange`），工厂只做路由与应答，因此脚本化的单元测试能直接驱动推送。
- **初始同步的收尾**：服务端用 `FINISH_FUZZY_WATCH_INIT_NOTIFY` 结束初始阶段——初始 diff 为空时立即发；非空时在最后一批被 ack 之后发（`FuzzyWatchSyncNotifyCallback.onSuccess` 在 `batchCompleted()` 且 `syncType == FUZZY_WATCH_INIT_NOTIFY` 时补发）。本客户端同时在收到最后一批 `FUZZY_WATCH_INIT_NOTIFY` 时结束，省掉那次额外的往返，也让「结束标记丢失」不至于让调用方一直等下去；批次按序推送，收到最后一批即意味着整份 diff 已应用，而随后才到的结束标记只会看到订阅已初始化。每个订阅恰好收到一次 `InitialSyncCompleted`。
- **服务端语义的实测发现（关键）**：`ConfigFuzzyWatchChangeNotifier` 用「配置是否还存在」决定发 `CONFIG_CHANGED` 还是 `DELETE_CONFIG`，而 `syncGroupKeyContext` 只在 key **不在**该 pattern 的匹配集合里时才返回需要推送。也就是说模糊监听跟踪的是**成员集合，不是内容**：已匹配配置的内容变更根本不会推送（内容仍归 `ListenAsync`）。因此 `CONFIG_CHANGED` 落到一个本地未知的 key 上时必须报成 `Added`——否则该 key 永远进不了集合，后续删除也会因为「删一个没见过的 key」而被抑制；这正是 Java 的做法（`handlerFuzzyWatchChangeNotifyRequest` 把 `ADD_CONFIG` 与 `CONFIG_CHANGED` 一律当新增）。`Modified` 是本客户端额外的防御性映射：Java 对「已知 key 的变更推送」直接丢弃，而当前服务端根本不会发这种推送；文档两处都写明了这一点。集成测试据此按「创建 → 删除 → 再创建」验收，而不是「创建 → 修改」。
- **重连与周期重同步**：重连后按客户端手上的集合重发 watch；`initializing` 取 `!registration.Initialized`——已初始化的重发是 diff 重同步，而尚未完成初始同步的重发会重新走一次初始化，从而拿到结束标记（否则订阅可能永远等不到 `InitialSyncCompleted`）。另有定时器按 `FuzzyWatchResyncInterval` 重发全部 watch，让「连接看起来健康但推送丢了」也能被修复——服务端对重发只回 diff，所以重复是幂等的。定时器随第一个注册启动、最后一个注销时停止。最后一个订阅者退出时，仅在连接可用时发 `CANCEL_WATCH`；断链时服务端已随连接丢弃该 watch，没有可取消的对象。
- 日志 4012–4018（初始同步完成/重同步失败/非法 group key/重连后重注册/事件/超限拒绝/未知变更类型）；`docs/protocol-notes.md` 新增「Configuration fuzzy watch」并补两个推送的应答行，`docs/faq.md` 新增「`WatchAsync` 与 `ListenAsync` 的区别」，`docs/java-parity.md` 的 P0-1 标记完成并记录上述语义。
- 单元测试新增 16 个（`Config/NacosConfigWatchTests.cs`，用假通道驱动真实客户端）：注册报文（模式/`watchType`/`initializing`/`receivedGroupKeys`）、初始 diff 投递、只有结束标记时收尾、多批次以最后一批收尾、最后一批之后到达的结束标记不重复产生 `InitialSyncCompleted`、增/改/删与重复抑制、未知 key 的 `CONFIG_CHANGED` 推出 `Added`、晚到订阅者的 seed 与共享注册（只发一次 watch）、重连后带已知集合重发且订阅不中断、未完成初始同步的注册在重连时重新初始化（`initializing=true`、空集合）、最后一个订阅者退出发 `CANCEL_WATCH`、能力位拒绝（501 且不发请求）、从未连接时先握手再按能力位拒绝、非法 key 不拖垮整批、定时重同步、只传 group 的重载、以及重同步间隔必须为正的选项校验。**反证**：假夹具最初没有把推送路由给客户端（生产里由工厂负责），用例不是断言失败而是挂住——补上路由并给所有等待加了 10 秒上限后，才成为真正的断言。另有 `Internal/NacosFuzzyPatternTests.cs` 的 49 个用例直接钉住匹配器与 group key 编解码（字面量期望值而非用实现反算）：五种模式形态、段中 `*` 不匹配、命名空间空值按 `public`、非三段模式匹配「无」、两种转义、编码/解码往返、以及九种非法 key。
- 验收：Release 构建零警告零错误；单元测试 **668 个全绿**（新增 65 个：16 个客户端状态机用例 + 49 个匹配器/编解码用例）；集成测试新增 `ConfigIntegrationTests.Watch_ReportsTheMatchedSetAsConfigurationsAppearAndDisappear` 一个用例（真实容器 `v3.0.2`：空初始集合 → 创建得 `Added` → 删除得 `Removed` → 再创建再得 `Added`），整套 **56 个全绿**。

#### P0-2 · 命名模糊监听（✅ 已完成）

- **缺口**：命名侧与配置侧共用同一个 `fuzzyWatch` 能力位，Java 入口是 `NamingService.fuzzyWatch(...)` / `fuzzyWatchWithGroupKeys(...)` / `cancelFuzzyWatch(...)`。它解决的是「按模式发现不知道名字的服务」：精确订阅必须先知道服务名，模糊监听才知道有哪些服务。
- **公共面**：`INacosNamingClient.WatchAsync(serviceNamePattern, groupPattern, ct)` 返回 `IAsyncEnumerable<NacosServiceWatchEvent>`；重载 `WatchAsync(groupPattern, ct)` 用 `*` 作为服务名模式，且 `ct` **必填**（与配置重载相同的 RS0026 歧义原因）。`NacosServiceWatchKind` 为 `Added` / `InstancesChanged` / `Removed` / `InitialSyncCompleted`，事件携带 `GroupKeyPattern` 与 `Service`（完成标记的 `Service` 为 null）。`NacosNamingOptions.FuzzyWatchResyncInterval` 默认 3 分钟、校验必须大于 0。
- **协议**：`NamingFuzzyWatchRequest{namespace, groupKeyPattern, receivedGroupKeys, watchType, initializing}`、`NamingFuzzyWatchSyncRequest{groupKeyPattern, contexts[{serviceKey, changedType}], syncType, totalBatch, currentBatch}`、`NamingFuzzyWatchChangeNotifyRequest{serviceKey, changedType, syncType}`；两种服务端推送都用 `NamingFuzzyWatchChangeNotifyResponse` 应答（Java 也是如此，命名侧没有单独的 sync response）。模式是 `namespace>>groupPattern>>serviceNamePattern`；服务键是 `namespace@@group@@service`（空命名空间按 `public`），**不做转义**，因为 `@@` 不允许出现在三段中的任何一段。
- **状态机**：完全复用配置侧的形状——每个 pattern 一个共享注册、多订阅者共用一次服务端 watch、`SemaphoreSlim` 串行化且跨越 WATCH 往返、晚到订阅者用当前集合 seed、最后订阅者退出时仅在连接可用时发 `CANCEL_WATCH`。推送处理在 `NacosNamingClient` 内部（`OnFuzzyWatchSync` / `OnFuzzyWatchChange`），工厂只做路由与应答。
- **服务端语义**：`syncServiceContext` 对 `INSTANCE_CHANGED` 返回 `false`，所以服务端只在**成员集合**变化时推送 `ADD_SERVICE` / `DELETE_SERVICE`（第一个/最后一个实例出现或消失）；已匹配服务的实例变化不推送，实例列表仍归 `SubscribeAsync`。若真收到 `INSTANCE_CHANGED`：已知服务报 `InstancesChanged`（Java 会把它折成新增，丢失区别），未知服务报 `Added`；`HEART_BEAT` 直接忽略。
- **与 Java 的刻意差异**：Java 的 sync 处理器对每种 context 都调用 `addReceivedServiceKey`，删除后集合仍是错的；本客户端对 `DELETE_SERVICE` 从集合中移除，与配置侧一致。集成测试据此按「注册 → 注销 → 再注册」验收。
- **收尾与重同步**：初始 diff 在 `FINISH_FUZZY_WATCH_INIT_NOTIFY` 或最后一批 `FUZZY_WATCH_INIT_NOTIFY` 时结束（每订阅恰好一次 `InitialSyncCompleted`）；重连按已知集合重发（`initializing = !registration.Initialized`），并按 `FuzzyWatchResyncInterval` 周期重发。命名侧每批 10 个服务（配置侧 20）。
- **日志** 5013–5019（初始同步完成/重同步失败/非法服务键/重连后重注册/事件/超限拒绝/未知变更类型）；`docs/protocol-notes.md` 新增「Naming fuzzy watch」，`docs/faq.md` 补充命名侧 `WatchAsync` 与 `SubscribeAsync` 的区别，`docs/configuration.md` 增加命名选项，`docs/java-parity.md` 的 P0-2 标记完成。
- 单元测试新增 19 个（`Naming/NacosServiceWatchTests.cs`）：注册报文（`namespace`/模式/`watchType`/`initializing`/`receivedGroupKeys`）、初始 diff、只有结束标记时收尾、多批次只在最后一批收尾、增/删与重复抑制、已知服务的 `INSTANCE_CHANGED` 报 `InstancesChanged`、未知服务的 `INSTANCE_CHANGED` 报 `Added`、`HEART_BEAT` 忽略、晚到订阅者 seed 与共享注册、重连带已知集合重发、结束标记不重复、最后订阅者退出发 `CANCEL_WATCH`、能力位拒绝（501 且不发请求）、从未连接时先握手再拒绝、非法服务键不拖垮整批、模式外的推送不投递、定时重同步、只传 group 的重载、释放时结束所有枚举。另有 `Internal/NacosFuzzyPatternTests.cs` 新增 17 个服务键用例（编码/解码/往返/八种非法键，字面量期望值）。
- 验收：Release 构建零警告零错误；单元测试 **704 个全绿**（新增 36 个：19 个客户端状态机用例 + 17 个服务键编解码用例）；集成测试新增 `NamingIntegrationTests.Watch_ReportsServicesEnteringAndLeavingTheMatchedSet`（真实容器 `v3.0.2`：空初始集合 → 注册得 `Added` → 注销得 `Removed` → 再注册再得 `Added`），整套 **57 个全绿**。

#### P1-3 · 命名客户端侧 selector（✅ 已完成）

- **缺口（按 3.x 校正）**：Java 3.x 的 selector 只存在于 `subscribe` / `unsubscribe`。最初对照表里还列了 `getAllInstances(..., Selector)`、`selectOneHealthyInstance(..., Selector)` 与 `client.naming.selector` 包，但 3.x `develop` 已把这些重载和 CMDB 标签/表达式 selector 一起删掉，因此**不复刻**；需要复刻的只剩 `com.alibaba.nacos.client.naming.selector.NamingSelectorFactory`。它解决的是「同一个服务只想跟一部分实例」：按机房、按标签或按地址段收窄，而不必让服务端返回一个更小的列表。
- **公共面**：`INacosNamingClient.SubscribeAsync(NacosServiceKey, INacosInstanceSelector)`，配套 `NacosInstanceSelectorContext`（`Service` / `Clusters` / `Instances`）与工厂 `NacosInstanceSelectors`：`All`（原样返回，不复制列表）、`Healthy`（只看 `Healthy` 标志，等价 Java `Instance::isHealthy`，与 `IsSelectable` 不同）、`Clusters(params string[])`（空集合保留全部）、`IpAddress(regex)`（构造时编译，按 `Pattern.matches` 的整串语义匹配）、`Metadata(map, matchAny = false)`（默认全部命中，空 map 保留全部）、`From(Func<...>)`（自定义实现）。`Clusters` / `Metadata` 复制入参，selector 的生命周期长于创建它的调用。
- **协议**：selector **纯客户端**，任何报文都没有 selector 字段（`SubscribeServiceRequest` 只有 `subscribe` 与 `clusters`）。selector 重载以 `clusters: null` 订阅（Java 的 `Constants.NULL`），让服务端发全量再由客户端收窄；只按集群收窄时仍应使用原重载，让服务端做过滤。
- **状态机**：服务端订阅与重放按服务共享，视图按消费者隔离——每个 `SubscriptionConsumer` 各自保存上次投递的**选中视图**，只有选中视图变化才投递，因此被过滤掉的实例变化不会唤醒消费者；`Subscription.LastRefTime` 只在至少一个消费者被通知时推进。首个元素同样经过 selector，消费者从它初始化时拿到的形状与后续推送一致。
- **错误处理**：selector 在**首个元素**上抛异常直接抛给调用方（bug 属于调用点）；在**推送路径**上按消费者捕获并记日志（事件 5020 `SelectorFailed`），订阅保留上次投递的视图，不让异常逃进共享读循环拖垮同一服务的其他消费者。
- **为何重载没有 `ct`**：RS0026 禁止两个都带可选参数的订阅重载（只写 service 的调用点无法区分），RS0027 又要求带可选参数的那个必须是参数最多的重载，而集群重载已是 3 个参数，selector 重载若也带 `ct` 就打平而报错。于是 `SubscribeAsync(service, selector)` 只收两个参数，令牌在枚举时经 `WithCancellation` 传入（AI 模块测试已有此用法），接口 XML 文档、FAQ 与 README 都写明；离开枚举（含 `break`）同样退订。
- **集成测试的实测发现（关键）**：服务端 `AbstractClient.publishers` 是「每个连接、每个服务只保留一个实例」，同一连接上第二次注册会**替换**第一次（Java SDK 同样如此）。因此一个服务的多个实例必须由多个连接分别注册——这也正是真实部署（多个进程）的形状；集成用例据此用三个连接：观察者 + 两个实例所有者。
- **日志** 5020；`docs/java-parity.md` 的 P1-3 标记完成并按 3.x 现实重写（selector 对照表、按消费者收窄、无令牌理由），`docs/protocol-notes.md` 写明 selector 不上线，`docs/faq.md` 新增「How do I follow only some instances of a service?」，`README.md` 补 selector 示例。
- 单元测试新增 23 个：`Naming/NacosInstanceSelectorTests.cs` 15 个（工厂语义与 Java 边界：空集合保留全部、整串地址匹配、空 map 保留全部、按序号比较、复制入参、构造期拒绝非法正则、`From` 传入 service 与集群过滤、返回 null 视为错误、null 上下文拒绝）；`Naming/NacosNamingClientTests.cs` 8 个订阅用例（首个元素即收窄、以全集群订阅、被过滤实例的推送不唤醒、选中视图变化才投递、实例进入选中集、只影响自己的消费者、selector 抛异常后保留上次视图且不卡死、`WithCancellation` 能结束枚举）。
- 验收：Release 构建零警告零错误；单元测试 **727 个全绿**（新增 23 个）；集成测试新增 `NamingIntegrationTests.Subscribe_WithSelector_FiltersTheInstancesARealServerReports`（真实容器 `v3.0.2`，三个连接：服务端确有两条实例 → 只投递 gold → silver 改标签后进入选中集 → gold 注销后选中集只剩 silver），整套 **58 个全绿**。

#### P1-4 · 配置过滤器链与加密数据密钥（✅ 已完成）

- **缺口**：Java 每次发布与读取都经过 `IConfigFilter` 链（`ConfigFilterChainManager`），内置的 `ConfigEncryptionFilter` 负责发布时加密、读取时解密，并把数据密钥存到本地快照旁。3.x 的 `ConfigQueryResponse` 已带 `encryptedDataKey`，但本客户端此前丢弃它，导致加密配置读回来只有密文、没有密钥。
- **公共面**：`INacosConfigFilter`（`Name` / `Order` / `InvokeAsync`）、`NacosConfigFilterContext`（`Key` / `Stage` / 可变的 `Content`、`ContentType`、`EncryptedDataKey` / 只读的 `Md5`、`FromLocalCache`）、`NacosConfigFilterStage{Publish, Read}`、`INacosConfigClient.AddConfigFilter`、`INacosConfigEncryptionProvider`（对应 Java `EncryptionPluginService` 的 `GenerateSecretKey` / `Encrypt` / `Decrypt` / `EncryptSecretKey` / `DecryptSecretKey` / `AlgorithmName`），以及内置的 `NacosConfigEncryptionFilter`。`NacosConfigResult.EncryptedDataKey` 暴露服务端存储的（已包装）密钥。
- **链的语义**：按 `Order` 升序执行，同序保持插入顺序；每个过滤器看到前一个的修改。没有 Java 的 `next` 委托——要中止就抛异常。同名过滤器只保留第一次注册（与 Java 的 `ConfigFilterChainManager.addFilter` 一致）。`AddConfigFilter` 以整体替换数组的方式实现，读/发布在途时添加过滤器不会看到半成品链。
- **加密规则（逐条复刻 Java）**：dataId 以 `cipher-` 开头且不恰好等于 `cipher-` 才启用；算法取第一个与第二个 `-` 之间的段（`cipher-AES-app.json` → `AES`），按序数比较（大小写敏感）；找不到对应 provider 时原样放行并记警告（事件 4019），与 Java 一样不让缺插件变成发布失败。
- **发布**：过滤器先跑，请求发送 `context.Content`、`additionMap["type"]` 与 `additionMap["encryptedDataKey"]`（未加密时为 `""`）。密钥不是顶层字段：服务端用 `getAdditionParam("encryptedDataKey")` 读取，与 `type` 同一张自由映射表。过滤器把内容置空时抛 `InvalidOperationException`（事件 4022），避免把配置误清空。快照保存过滤后的（密文）内容与包装密钥，因为那才是服务端存储的一对。
- **读取**：先校验原始 MD5，再把原始内容与密钥写入快照，最后跑过滤器；返回的 `Md5` 始终是存储值的哈希，变更检测不受影响。本地快照回退同样跑链，否则断链时会把密文交给调用方。内容为空时不跑过滤器。
- **推送**：先比较原始 MD5 并更新快照，再跑链投递；过滤器抛异常时记日志（事件 4020）并跳过本次投递，订阅保留。显式读取/发布上的过滤器异常则直接抛给调用方。
- **与 Java 的刻意差异**：Java 在读取后把 `encryptedDataKey` 换成**明文**密钥；本客户端保留服务端存储的包装密钥，否则新公开的 `NacosConfigResult.EncryptedDataKey` 会泄露密钥。
- **快照格式**：元数据加版本标记 `v2`，内容类型与密钥各自 base64 编码；旧文件（无 `v2` 标记）仍按原格式读取、密钥为 null；元数据损坏视为无快照。
- **配置入口**：.NET 没有 Java `ServiceLoader` 的等价物，因此由 `NacosConfigOptions.EncryptionProviders`（`IList<INacosConfigEncryptionProvider>`，默认空）显式登记；非空时客户端自动加入 `NacosConfigEncryptionFilter`（Order 0）。`AddConfigFilter` 仍是自定义变换与显式装配链的入口。
- **日志** 4019–4022（缺 provider / 推送过滤器失败 / 同名过滤器忽略 / 过滤器清空发布内容）；`docs/java-parity.md` 的 P1-4 标记完成并记录链与加密语义，`docs/protocol-notes.md` 写明密钥的线上位置与链的运行时机，`docs/configuration.md` 增加 `EncryptionProviders`，`docs/faq.md` 新增「How do I encrypt configuration values?」，`README.md` 补一段说明。
- 单元测试新增 39 个：`Config/NacosConfigFilterTests.cs` 19 个（加密/解密、非 cipher、恰好 `cipher-`、未知算法、大小写、算法分段四种形态、首个匹配 provider、空内容、取消、构造校验、`Name`/`Order`、选项校验）；`Config/NacosConfigClientTests.cs` 16 个（顺序、同名忽略、发布密文与密钥、未加密空密钥、清空内容抛错、发布快照存密文对、读取过滤但保留原始 MD5、cipher 读取解密、缺失内容不跑链、读取异常上抛、快照回退解密、推送解密、推送失败隔离且订阅存活、`AddConfigFilter` 三个参数/生命周期用例）；`Internal/NacosConfigSnapshotStoreTests.cs` 新增 4 个（密钥往返、含分隔符的密钥、旧格式兼容、损坏元数据）。
- 验收：Release 构建零警告零错误；单元测试 **766 个全绿**（新增 39 个）；集成测试新增 `ConfigIntegrationTests.Encryption_RoundTripsThroughARealServer`（真实容器 `v3.0.2`：加密客户端发布 → 无 provider 的客户端读到密文与包装密钥 → 加密客户端读到明文 → 监听推送同样收到明文），整套 **59 个全绿**。

#### P1-5 · 服务列表分页（✅ 已完成）

- **缺口**：Java 的 `NamingService.getServicesOfServer(pageNo, pageSize, groupName, selector)` 按页枚举一个 group 下的服务名；本客户端的线上消息（`ServiceListRequest`/`ServiceListResponse`）早已存在，但没有公开方法，只能靠模糊监听跟随集合、无法一次性列出已有服务。
- **公共面**：`INacosNamingClient.GetServicesAsync(int pageNo = 1, int pageSize = 10, string? groupName = null, CancellationToken ct = default)` 返回 `NacosServiceListPage`（`Count` 与 `Services`）。参数顺序与 Java 对齐；`groupName` 省略时用 `NacosConstants.DefaultGroup`，与 Java 的两参重载一致。
- **服务端语义（读 `ServiceListRequestHandler` 与 `ServiceUtil.pageServiceName` 确认）**：`count` 是分页**前**该 group 的服务总数，因此第 `ceil(Count / pageSize)` 页之后仍返回空列表并保留总数；服务端按 `group@@service` 分页后再剥掉 group 前缀，所以 `Services` 里是裸服务名；group 为精确匹配，没有通配符。
- **参数校验**：`pageNo`、`pageSize` 小于 1 直接抛 `ArgumentOutOfRangeException`。Java 原样发送：页码 ≤ 0 被服务端 `start < 0 → 0` 夹到第一页，页大小 ≤ 0 则会在 `subList` 里报一个与调用者无关的错。提前校验把两种错误都变成调用点上的明确异常。
- **不做本地缓存回退**：与 `GetInstancesAsync` 不同，服务列表描述的是命名空间本身；断链时用旧列表会静默隐藏期间新建的服务，因此直接抛出连接异常。
- **selector 不公开**：3.x 的 `ServiceListRequestHandler` 对 `selector` 字段只留了 `// TODO select service by selector`，而 CMDB label/表达式 selector 已从 3.x 客户端移除。暴露一个不起作用的参数比不暴露更糟；等服务端实现过滤后可以在不改动现有方法的前提下增加重载。
- **日志**：无需新增事件，查询失败沿用传输层异常与既有日志。
- **文档**：`docs/java-parity.md` 的 P1-5 标记完成并写出与 Java 的对照表、分页/计数/裸名/校验/无缓存/无 selector 的理由；`docs/protocol-notes.md` 增加 `ServiceListRequest`/`ServiceListResponse` 的字段与分页语义；`docs/faq.md` 新增「How do I list every service in a group?」（含翻页循环示例）；`README.md` 命名示例补一段。
- 单元测试新增 10 个：请求字段与结果、默认 group 与默认分页、越界页保留总数、缺失名称列表视为空、页码 < 1 两种、页大小 < 1 两种、断链不回退缓存、释放后抛 `ObjectDisposedException`。
- 验收：Release 构建零警告零错误；单元测试 **776 个全绿**（新增 10 个）；集成测试新增 `NamingIntegrationTests.GetServices_PagesAGroupThroughARealServer`（真实容器 `v3.0.2`：同 group 注册两个服务 → 第一页 1 条且总数为 2 → 第二页另一条 → 第三页空但总数仍为 2 → 另一个 group 为空），整套 **60 个全绿**。

#### P2-6 · 批量实例注册与批量注销（✅ 已完成）

- **缺口**：Java 的 `NamingService.batchRegisterInstance(serviceName, groupName, instances)` 用一条 `BatchInstanceRequest`（`type = "batchRegisterInstance"`）发布一个服务的多个实例；本客户端的消息类早已存在，但没有公开方法。原对照表把 `batchDeregisterInstance` 记成「Java 侧的循环」，这是错的：3.x 的 gRPC 实现取本客户端上次注册的批次、按 IP+端口扣掉指定实例，再把剩余部分作为新批次重新发布。
- **公共面**：`INacosNamingClient.RegisterInstancesAsync(NacosServiceKey, IReadOnlyList<NacosInstance>, CancellationToken)` 与 `DeregisterInstancesAsync(...)`；日志新增 5021（发布批次，Debug）。
- **服务端模型（读 `AbstractClient.addServiceInstance` 与 `EphemeralClientOperationServiceImpl.batchRegisterInstance` 确认）**：服务端按「连接 + 服务」只保存一份已发布实例集合（`publishers` 以 service 为键），因此批次是一次发布整份集合，之后的单条注册、另一个批次都会**替换**它，注销则删除整份集合。部署若要在一个连接上发布同一服务的多个实例，只能用批次，或每个实例一个连接。
- **客户端重放随之改为按服务记账**：原实现按「服务 + 端点」保存注册，重连时会把已被替换的旧注册也重放一遍；现在每个服务只保留最后一次注册（单条或批次），与 Java 的 redo 表键一致。这同时修掉了一个既有缺陷：同一连接先后注册两个实例后重连，旧实现可能把两个都重放，最终哪个生效取决于字典枚举顺序。发送失败的注册仍然不重放，但它替换掉的上一份注册会恢复记账——服务端此时保留的正是那一份，忘掉它会让重连丢掉一个仍在发布的实例。
- **校验**：空列表、包含 `null`、含持久化实例都在发送前抛 `ArgumentException`。空批次在服务端不是空操作，而是把已发布集合替换为空——等于用注册的语法做注销；持久化实例则没有批量通道（`PersistentClientOperationServiceImpl.batchRegisterInstance` 仍是 `TODO`），发出去会被静默丢弃。
- **批量注销**：按 IP+端口匹配，扣掉后把剩余实例作为新批次发布；服务不是由 `RegisterInstancesAsync` 发布的批次时抛 `InvalidOperationException`（Java 抛 `INVALID_PARAM`）。剩余为空时改发一条普通注销，而不是 Java 的空批次：服务端状态相同，但不会留下一个空批次在重连时重放。
- **文档**：`docs/java-parity.md` 的 P2-6 标记完成并新增对照表与上述取舍（同时修正原文「注销是循环」的说法）；`docs/protocol-notes.md` 记录「每个连接每个服务一份已发布集合」与 `BatchInstanceRequest`；`docs/faq.md` 新增「How do I register several instances of one service from one client?」；`README.md` 命名示例补一段。
- 单元测试新增 14 个：批次请求字段与默认集群、空批次、持久化实例、`null` 元素、服务端拒绝后不再重放、替换失败时恢复上一份注册、重连重放整个批次、单条注册替换批次后的重放、批次替换单条后的重放、批量注销发布剩余部分并重放、全部移除时发普通注销且不重放、未批量注册时拒绝、空列表拒绝、释放后抛 `ObjectDisposedException`。
- 验收：Release 构建零警告零错误；单元测试 **790 个全绿**（新增 14 个）；集成测试新增 `NamingIntegrationTests.RegisterInstances_PublishesABatchAndReplacesItThroughARealServer`（真实容器 `v3.0.2`：一条请求发布两个实例 → 查询两条 → 批量注销一个 → 只剩另一个 → 单条注册替换整个批次 → 只剩新实例 → 注销后为空），整套 **61 个全绿**。

#### P2-7 · AI agent 批量 watch（HTTP 长轮询，✅ 已完成）

- **缺口**：RAD watch 此前只走 AI 模块的 gRPC 推送（`AgentSubscribeRpcRequest` + `radWatchV1`）。3.3 的 Java 客户端还有一条 HTTP 批量长轮询：一次请求携带本客户端**当前全部** watch 的 `{clientWatchId, discoveryRequest, materializedFingerprint}`，服务端挂起至多 `timeoutMillis`，任一 watch 的投影变化就返回变化 id；客户端再逐个重新 Discover。它对只开放 HTTP 的部署、以及 gRPC watch 能力未协商成功的服务端是唯一可用的推送通道。
- **公共面**：`NacosAiOptions.AgentWatchTransport`，类型 `NacosAgentWatchTransport { Auto, Grpc, Http, Polling }`，默认 `Auto`。`Auto` 优先 gRPC watch（服务端声明 `radWatchV1`），否则 HTTP 批量长轮询，再否则轮询 Discover；`Grpc` 只接受 gRPC（服务端未声明时按 501 拒绝）；`Http` 跳过 gRPC；`Polling` 按 `Ai.CacheRefreshInterval` 重新读取。`Polling` 是本客户端对 Java 内部兼容回退的显式暴露，Java 公开的 `AgentTransportMode` 只有 `GRPC/HTTP/AUTO`。
- **HTTP 协议（读 `AgentClientController.watch`、`AgentWatchBatchForm`、`AiHttpClientProxy.watchAgents` 确认）**：`POST /v3/client/ai/agents/watch`，`application/x-www-form-urlencoded`，字段 `generation`、`timeoutMillis`、`watches`（JSON 数组）；请求头 `X-Nacos-Client-Id`（本进程的 AI 发布者标识）与 `Request-Module: ai`；响应是 v3 `Result<AgentWatchBatchResponse>{generation, changed, changedClientWatchIds}`。服务端要求 `materializedFingerprint` 匹配 `sha256-canonical-json-v1:[0-9a-f]{64}`、`clientWatchId` 匹配 `[A-Za-z0-9._:-]{1,128}`、一批 1–1000 项、同一批只能有一个有效命名空间，且 `timeoutMillis` 在 1000–60000。
- **指纹（逐行移植 `AgentDiscoveryCanonicalizer` 与 `EndpointCanonicalizer`）**：`NacosAgentDiscoveryCanonicalizer.Fingerprint(result)` 先把结果规范化成 Java 的 frame，再以 `appendJson` 的规则（对象键按 UTF-16 序、字符串转义、数字按 `BigDecimal.stripTrailingZeros().toPlainString()`、孤立代理项转 `\uXXXX`）写出 canonical JSON，最后取 SHA-256 小写十六进制。frame 只含 `agentName`、`callInterfaces`、`contentDigest`、可选 `description`、可选 `tags`（排序后）、`namespaceId`（空则 `public`）、`version`；接口 frame 含 `descriptorMediaType`、`endpointSets`、`nativeDescriptor`、`protocol`、`protocolVersion`；端点 frame 含 `bindings`、`healthy`、`metadata`、`priority`、`transport`、`uri`、`weight`。`enabled`、`endpointSets[].lastUpdatedTime`、`endpointSourceOrder` 不参与指纹；数组顺序（接口、端点集、端点、绑定、tags 之外的列表）保留，因为服务端把它当作契约顺序。
- **URI 规范化**：小写 scheme；主机做 IDN/小写（IPv4 去前导零、IPv6 压缩并加方括号）；有效端口必须显式（`http`/`ws` 缺省 80，`https`/`wss` 缺省 443，其它 scheme 必须写端口）；原始 path/query 原样保留（不能交给 `System.Uri`，它会把非 ASCII 路径百分号编码）；拒绝 fragment、userinfo、opaque URI、空主机、越界端口与超过 2048 个码点的 URI。已知夹具 `{"agentName":"AgentA","callInterfaces":[],"contentDigest":"sha256:aaaa…","namespaceId":"public","version":"1.0.0"}` → `sha256-canonical-json-v1:3362e4414540623a15d86c0c0b99a0b76f82a203c3f26eb905b9db43444436d5`，与 Java 测试一致。
- **HTTP 协调器（移植 `HttpAgentWatchTransport`）**：一个命名空间一个绑定；每次 watch 集合变化递增 generation 并取消在途请求；`awaitingRefresh` 中的 watch 不进入下一批，直到客户端用新指纹 `Update`；成功响应清空 `pendingAdditions`；容量错误 `50104`（HTTP 503 + detail 50104）只拒绝**最新加入**的 watch 并标记终止，迁移中 `50105` 使整批终止；`404/-404/501/302/403/400/-400/容量` 立即把绑定标记为不可用，其它错误连续 3 次后同样降级；绑定不可用后不再使用 HTTP。请求读超时为长轮询超时 + 5 秒，失败重试间隔 1 秒。
- **管理器整合**：每个 intent 记录当前 `WireKind`（`None/Grpc/Http/Polling`）。HTTP 路径先 Discover 得到指纹再注册（服务端要求指纹非空），gRPC 路径保持原来的“先订阅再读取”；每次读取成功后把新指纹 `Update` 回协调器；HTTP 失效回调只置 dirty 并唤醒 intent，真正的重新读取仍在 intent 自己的循环里；HTTP 绑定整体不可用时，非终止错误把该 intent 切到轮询（`TransportOverride = Polling`），`503/-503/400/-400/409` 终止该 watch；gRPC 重连时 `RestoreAsync` 只把 `Auto`/`Grpc` 下的非 gRPC intent 重置为重新选择，`Http`/`Polling` 的注册不受影响。HTTP 绑定返回 501 时按 Java 路由器的规则回退到轮询，而不是让 watch 失败。
- **日志**：新增 8043–8049（传输选择、HTTP 注册/停止/失效、HTTP 失败与重试、回退），HTTP 成功的长轮询结果只记 Debug。
- **文档**：`docs/java-parity.md` 的 P2-7 标记完成并给出与 Java 的对照表与取舍；`docs/protocol-notes.md` 记录 watch 端点、表单字段、指纹算法与容量/迁移错误；`docs/configuration.md` 记录 `AgentWatchTransport` 的四种取值与默认；`docs/faq.md` 新增「Why does an agent watch need HTTP long polling?」。
- 单元测试新增：指纹已知夹具、tags/description/namespace、排除字段、map 顺序与数组顺序、URI（默认端口/IDN/IPv4/IPv6/Unicode/非法）、descriptor 数字与转义；HTTP 请求形状与头、变化回调、awaitingRefresh、generation 取消、容量拒绝、迁移终止、回退集合、三次失败降级；管理器 Auto→HTTP、Auto→Polling、Grpc 501 拒绝、Http 跳过 gRPC。
- 验收：Release 构建零警告零错误；单元测试 **852 个全绿**（新增 62 个：指纹/URI 规范化 31 个、HTTP 绑定 9 个、协调器 17 个、管理器传输选择 5 个）；集成测试新增 `AgentWatchIntegrationTests.WatchAgentAsync_ReportsAChangeOverTheHttpBatch`（真实容器 `v3.3.0-RC`，`Http` 模式：发布 agent → watch 收到初始状态 → 重新发布 → watch 收到新版本，且证明两侧算出的指纹一致），整套 **62 个全绿**。

> 阶段 2–4 的类名与 DTO 字段以 `nacos-develop`（`specs/en/ai/*`、`specs/en/lock/lock-spec.md`、`api/src/main/java`、`client/src/main/java`）为准。所有新增公共 API 同步更新 `PublicAPI.Unshipped.txt`，并补充对应文档与测试。
