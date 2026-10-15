# Lolitacoding.Nacos 开发计划书 (v0.1)

> 本文档面向 AI 编码助手（Claude Code / Copilot Agent / Cursor 等）。请先通读"全局约定"，再按"里程碑"顺序逐任务执行。每个任务必须满足其"验收标准"后才能进入下一个。

## 1. 项目目标

为 Nacos 提供现代化的 .NET 客户端 SDK：

- 仅面向 **.NET 10**（`net10.0`），充分使用新语言特性（C# 14、`System.Text.Json` 源生成、`Span`、`Channel<T>`、`TimeProvider`、`IAsyncEnumerable` 等）。
- 同时支持 **Nacos 2.x 与 3.x** 服务端（见第 6 节"待核对事项"）。
- 覆盖两大核心能力：**配置中心（Config）** 与 **服务注册发现（Naming）**。
- 提供与 .NET 生态的一等集成：`IConfiguration`、`Microsoft.Extensions.DependencyInjection`、`IHostedService`、`HttpClientFactory`、`Microsoft.Extensions.ServiceDiscovery`（可选依赖）。
- 对 **Native AOT / 裁剪友好**：不使用反射式 JSON 序列化，不使用运行时代码生成。

非目标（v1 不做）：Nacos 服务端管理 API、AI/MCP 注册中心相关功能、.NET 8 及以下的多目标。

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
- 集成测试：Testcontainers 启动 `nacos/nacos-server` 镜像（2.x 与 3.x 各一套，通过测试矩阵参数化），标记 `[Trait("Category","Integration")]`。
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
- [ ] **T5.1** Native AOT 发布 sample 验证；确认无 trim/AOT 警告。
- [ ] **T5.2** 可观测性：`ActivitySource` + `Meter`（连接状态、请求耗时、重连次数、推送数量）。
- [ ] **T5.3** 文档：快速开始、配置项说明、从 `nacos-sdk-csharp` 迁移指南、FAQ。
- [ ] **T5.4** 发布 `0.1.0-preview` 到 NuGet，申请保留前缀 `Lolitacoding.*`。

### 后续（v1 之后）
- 阿里云 MSE AccessKey 认证；地址服务器发现；TLS / mTLS。
- 以同样的结构扩展其他国内生态组件（独立仓库）。

## 6. 待核对事项（AI 开始 M1 前必须先确认，不得凭记忆编写）

1. **协议定义**：从官方 `alibaba/nacos` 仓库对应版本的 `api` 模块（`nacos_grpc_service.proto`）与 Java 客户端实现（`nacos-client`）核对 Payload/Metadata 结构、请求类型名称、握手流程、端口偏移（gRPC 通常为主端口 +1000）。
2. **2.x 与 3.x 差异**：核对 3.x 对 HTTP OpenAPI（v1/v2/v3）的变更、登录接口路径、默认鉴权行为，以及客户端 gRPC 协议是否有不兼容改动。以官方文档与源码为准，如与本文档冲突，以官方为准并更新本文档。
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
