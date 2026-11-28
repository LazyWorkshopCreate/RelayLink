# RelayLink 代码审查记录（2026-11-25）

文档 ID：TST-015\
状态：Active\
版本：v1.1.0\
更新日期：2026-11-25

本文记录在同步至 `origin/main`（`61ad904` / `v0.1.0-rc.14`）后对仓库开展的全量静态代码审查与基线检验结果，以及全部 38 项发现的修复落地与闭环验证结果。审查遵循 [代码审查（2026-10-23）](code-review-2026-10-23.md) 之后的演进基线，不重复已关闭项，并严格尊重既有架构边界。

## 1. 范围与方法

- 静态审查范围覆盖：
  - 服务端、协议、传输与日志：`src/RelayLink.Server/`、`src/RelayLink.Protocol/`、`src/RelayLink.Transport/`、`src/RelayLink.Logging/`
  - AI 自动化管理面、CLI、Skill、契约与六语言 SDK：`src/RelayLink.Server/Automation/`、`src/RelayLink.Server/AutomationEndpoints.cs`、`src/RelayLink.AutomationClient/`、`src/RelayLink.Cli/`、`src/RelayLink.Sdk/`、`sdk/`、`skills/relaylink-admin/`、`contract/`
  - Agent、HTTP 反向代理与配置迁移器：`src/RelayLink.Agent/`、`src/RelayLink.Agent.ConfigMigrator/`
  - 双端前端与产品网站：`src/RelayLink.AdminWeb/`、`src/RelayLink.AgentWeb/`、`website/`、`scripts/build-website.mjs`、`scripts/preview-website.mjs`
  - CI/CD、打包部署脚本、示例配置与仓库文档：`.github/`、`deploy/`、`scripts/`、`config/examples/`、`README.md`、`docs/`
- 基线实测执行仓库构建、前后端、网站、Skill 与六语言 SDK 检查，核实声明与实际行为一致性。

## 2. 结论摘要

| 级别 | 数量 | 编号范围 | 重点领域 |
|---|---|---|---|
| 严重（Critical） | 4 | CR26-01 – CR26-04 | 自动化端口/TLS 未隔离与 fail-closed 失效、CLI SPKI 固定退化、变更申请跨主体越权、CI 工作流归属条件错配 |
| 高（High） | 15 | CR26-05 – CR26-19 | 控制口握手无超时 DoS、拒绝流写爆审计/永久错误日志、控制流半帧/跨会话中断、Windows UDP 退出、配对爆破、回环绕过、CLI 凭据与路径穿越、变更状态机死锁、配对容量锁死、自动化存储 DoS、HTTP EOF/FIN 缺失、Agent 控制超时与中间 CA、SDK 核心缺陷、前端审批缩权与 URL 校验、零单测/部署/公网 IP 泄露 |
| 中（Medium） | 14 | CR26-20 – CR26-33（含 CR26-34） | 共享配额与僵尸定时器、握手期配置竞态、FrameReader 帧头后挂起与 CR-09、history 写锁与无上限、null 线字段/ZeroMemory 竞态、可用性统计与正则 `\z`、六语言 SDK 一致性、OpenAPI 契约漂移、自动化审计与启动对账、Skill/CLI 契约、VirtualHttp 竞态与非 WS Upgrade、Agent 本机写接口与端口分配器、AdminWeb/AgentWeb 状态竞态、网站与文档/运维脚本漂移 |
| 低（Low） | 4 | CR26-35 – CR26-38 | 审计恢复 SQL、非阻塞 IdleWatch、配置校验细节与死代码、HttpProxyPolicy 规范化与 AgentIdentity ACL、网站 Sanitizer 与包管理、Docker/Release/CLI 健壮性打磨 |

## 3. 已确认的设计边界（不作为缺陷）

- 普通代理数据通道绑定后为明文原始 TCP（见 [ADR-0008](../adr/0008-separated-control-and-raw-data.md)）；
- 服务端管理页面支持匿名只读总览、通道、映射与分钟流量（仅提示部署暴露约束，不强制改为登录才能只读）；
- 互访业务流支持管理员按通道关闭内层 TLS（见 [ADR-0016](../adr/0016-optional-peer-traffic-encryption.md)）；
- Agent 本机页面按 [ADR-0034](../adr/0034-agent-local-write-session.md) 移除 `local-management.token` 与登录密码，单用户/管理员操作前提下依赖 loopback + Host/Origin + HttpOnly `SameSite=Strict` 会话 + CSRF；
- Agent 本机页按 [ADR-0042](../adr/0042-agent-native-tool-links.md) 提供的固定 `127.0.0.1` 地址 `.rdp` 与 `ssh://`/`ssh -p` 构造已确认不存在属性注入或命令注入。

## 4. 严重发现（Critical）

### CR26-01（严重）自动化 API 未做端口/TLS 隔离，且证书加载失败未真正 fail-closed

- **位置**：`src/RelayLink.Server/Program.cs:58-91`、`:139-154`、`src/RelayLink.Server/AutomationEndpoints.cs:18-53`
- **现象**：`Dashboard`（明文 HTTP，可监听非 loopback）与 `Automation`（`127.0.0.1:18082` HTTPS）共用同一个 `WebApplication`，管道未校验 `LocalPort` 或 `Request.IsHttps`；第 139 行仅凭 `automation is { Enabled: true }` 注册路由，即便证书加载失败把 `automationCertificate` 置 `null` 并打印禁用警告，`/api/v1/automation/*` 仍可在明文端口访问（同时漏捕 `UnauthorizedAccessException` 且未检证书有效期 `NotBefore`/`NotAfter`）。
- **影响**：(1) 仅可达自动化端口的主机可直接请求全部匿名仪表盘接口与管理台，绕过自动化配对与审计；(2) `/api/v1/automation/*` 经明文 Dashboard 端口暴露，证书失效时亦不阻断。
- **建议**：维护 `automationListenerActive` 门禁非管理员自动化路由与证书有效期/权限异常；添加中间件强制 `/api/v1/automation/*` 仅允许 HTTPS 且命中 `automation.Port`，并在 `automation.Port` 拒绝其他所有路径。
- **处置（2026-11-25）**：**已修复**。`Program.cs` 新增 `automationListenerActive` 门禁及 `NotBefore`/`NotAfter`/`UnauthorizedAccessException` 检查；`app.Use` 中间件强制 `/api/v1/automation/*` 仅可在 HTTPS + `automation.Port` 访问且在该端口拒绝其他路径；`AutomationEndpointTests` 已验证。

### CR26-02（严重）CLI TLS 证书固定永久退化为 `acceptAny = true` 且比对字节错误

- **位置**：`src/RelayLink.Cli/CliApp.cs:65`、`:119`、`:134-135`、`:184-189`、`:314-356`、`src/RelayLink.AutomationClient/AutomationClient.cs:123-149`
- **现象**：首次 `pair` 未显式传入 `--spki` 时从未采集并持久化服务端 SPKI，导致 `profile.SpkiSha256` 永久为 `null`，后续命令均以 `acceptAny: profile.SpkiSha256 is null` 绕过证书校验；`AutomationClient` 实际计算 `certificate.GetPublicKey()` 原文而非 `SHA256(SubjectPublicKeyInfo)`，且未限制 `https` scheme；`auth trust --reset` 未实现。
- **影响**：任意同机进程或中间人可伪造自动化 TLS 服务端；手动配置的规范 SPKI SHA-256 指纹校验失败。
- **建议**：改为计算 `SHA256(ExportSubjectPublicKeyInfo())` 的 Base64Url；`pair` 阶段采集并持久化 SPKI；非首次配对严禁 `acceptAny`；校验 `https` scheme 并实现 `auth trust --reset`。
- **处置（2026-11-25）**：**已修复**。`AutomationClient` 改为计算 `SHA256(ExportSubjectPublicKeyInfo())`（Base64Url）、强制 `https` 且非首次配对禁用 `acceptAny`；`CliApp` 在 `pair` 阶段采集并写入 `SpkiSha256` 并实现 `auth trust --reset`；`CliProcessTests` 与 `AutomationTests` 已验证。

### CR26-03（严重）变更申请跨主体越权读写与无审计撤销

- **位置**：`src/RelayLink.Server/AutomationEndpoints.cs:540-595`、`src/RelayLink.Server/Automation/AutomationChangeService.cs:167-200`
- **现象**：`GET /change-requests`（`ListAsync(null, ...)`）、`GET /change-requests/{id}` 与 `DELETE /change-requests/{id}` 均未校验申请与当前设备/主体的归属；`DELETE` 也缺失审计门禁与审计日志。
- **影响**：持有 `changes.read` 的设备可枚举他人变更申请；持有 `changes.write` 的设备可无痕撤销他人待批申请。
- **建议**：持久化申请方 `device_thumbprint`，按归属过滤列表与拦截非本人读取/撤销，`DELETE` 强制走审计门禁并落审计。
- **处置（2026-11-25）**：**已修复**。`AutomationStore` 持久化 `device_thumbprint`；`AutomationEndpoints` 与 `AutomationChangeService` 按设备指纹过滤 `GET /change-requests` 并校验 `GET`/`DELETE` 归属，`DELETE` 强制审计门禁与审计记录；`AutomationEndpointTests` 已验证。

### CR26-04（严重）CI / Release / Pages 工作流门禁错配导致公开仓库及 Fork 零检查

- **位置**：来源仓库当时的 CI、Release 和 Pages 工作流入口条件。
- **现象**：来源仓库的标准 CI 和发布工作流使用固定的仓库身份条件，部分运行场景跳过了应执行的检查。
- **建议**：使公开仓库与公开 Fork 的推送和 Pull Request 能执行标准 CI。
- **处置（2026-11-25）**：**已修复**。公开仓库通过 GitHub 托管 runner 执行 `ci.yml` 的三类校验作业。

## 5. 高危发现（High）

### CR26-05（高）控制口未认证 TLS 握手无超时且无 KeepAlive

- **位置**：`src/RelayLink.Server/Runtime/TunnelAcceptorService.cs:64-80`
- **现象**：`unauthenticatedLimit` 在 `CreateTransportStreamAsync` 之前占用，但 `handshakeTimeout` 在握手完成后才创建并启动，且未开启 `TcpKeepAlive`。
- **影响**：64 个发送 TCP SYN 后不推 TLS 握手的空连接即可永久锁死控制端口所有 Agent 接入。
- **建议**：握手前即创建并传递 `handshakeTimeout.Token`，对已接受连接开启 KeepAlive。
- **处置（2026-11-25）**：**已修复**。`TunnelAcceptorService` 在 `CreateTransportStreamAsync` 前创建并传入 `handshakeTimeout` 与 KeepAlive，确保未完成握手的空连接超时释放。

### CR26-06（高）前置拒绝与登录锁定无限写入共享审计队列引发全站 DoS

- **位置**：`src/RelayLink.Server/Runtime/ProxyListenerService.cs:262-393`、`src/RelayLink.Server/Program.cs:208-214`、`src/RelayLink.Server/Runtime/AuditService.cs:41-164`、`src/RelayLink.Logging/FileLogging.cs:34-39`
- **现象**：安全组拒绝、Agent 离线、登录锁定等高频拒绝与关键放行事件共用 `AuditService.capacity(1024)` 且未限流/合并；队列满时抛出的异常触发永久保留 `server-error-*.jsonl` 刷盘。
- **影响**：单来源拒绝泛洪可耗尽审计队列使全站放行与登录全部 fail-closed 瘫痪，并撑爆永久错误日志。
- **建议**：将高频拒绝审计与关键放行审计配额隔离并按窗口去重合并，对未通过准入的并发任务设限并限制错误日志打印频率。
- **处置（2026-11-25）**：**已修复**。`AuditService` 隔离拒绝审计独立有界通道并支持窗口去重聚合；`ProxyListenerService` 限制准入并发并限频记录错误日志。

### CR26-07（高）控制流半帧损坏与跨会话取消/击穿（CR-05 遗留缺口）

- **位置**：`src/RelayLink.Transport/FrameWriter.cs:24-45`、`src/RelayLink.Server/Runtime/SessionRegistry.cs:162-163`、`src/RelayLink.Server/Runtime/PeerRelayRegistry.cs:100-110`、`src/RelayLink.Server/Runtime/ProxyListenerService.cs:311-323`
- **现象**：`FrameWriter` 写失败后不锁死 `faulted`；`Session.SendAsync` 捕获外不关会话；`PeerRelayRegistry.RequestAsync` 用调用方 token 给目标会话发 `PeerOpen` 并将目标异常抛回调用方循环。
- **建议**：`FrameWriter` 写半帧失败即永久 `faulted`；`Session.SendAsync` 遇 `FrameWriteException` 立即关闭会话；`PeerRelayRegistry` 隔离目标发送令牌与异常处理。
- **处置（2026-11-25）**：**已修复**。`FrameWriter` 新增不可逆 `faulted` 状态与 `FrameWriteException`；`Session.SendAsync` 与 `PeerRelayRegistry.RequestAsync` 隔离异常并即时关闭损坏控制会话。

### CR26-08（高）Windows UDP 代理监听因 `WSAECONNRESET` 永久退出（CR-13 遗留缺口）

- **位置**：`src/RelayLink.Transport/DatagramRelay.cs:16-25,189`、`src/RelayLink.Server/Runtime/ProxyListenerService.cs:164-173`、`src/RelayLink.Server/Program.cs:325,531`
- **现象**：Windows 无连接 UDP listener 未关闭 `SIO_UDP_CONNRESET`，任一调用方关闭端口触发 ICMP 不可达即抛出 `WSAECONNRESET` 终止 `DatagramListener.RunAsync`，无重试且面板仍显示 `listening`。
- **建议**：在 `DatagramListener` 中禁掉 Windows `SIO_UDP_CONNRESET` 并恢复可重试 `SocketException`，面板反映 `"failed"`。
- **处置（2026-11-25）**：**已修复**。`DatagramRelay` 在 Windows 禁用 `SIO_UDP_CONNRESET` 并容错可恢复 `SocketException`；`ProxyListenerService` 在真实失败时将状态标为 `"failed"`。

### CR26-09（高）配对端点缺失证明字段校验且 `/status` 可无限在线猜解用户码

- **位置**：`src/RelayLink.Server/AutomationEndpoints.cs:142-249`、`src/RelayLink.Server/Automation/AutomationPairingService.cs:134-207`、`src/RelayLink.AutomationClient/AutomationClient.cs:227-267`
- **现象**：`/status`、`/token`、`DELETE` 漏检 `requestId`/`instanceId`/`htm`/`htu`（`DELETE` 还不消费 `jti`）；`/status` 验错 `userCode` 不计数不锁定。
- **建议**：统一强制完整证明绑定与 `AcceptJti`；`/status` 接入错码计数与 5 次锁定。
- **处置（2026-11-25）**：**已修复**。`AutomationEndpoints` 与 `AutomationPairingService` 统一验证全字段证明与 `AcceptJti`，`/status` 接入错码计数和 5 次锁定；`AutomationEndpointTests` 已验证。

### CR26-10（高）回环自动化端口防暴露校验可绕过且全局冲突漏检 `automation.port`

- **位置**：`src/RelayLink.Server/Configuration/ConfigurationLoader.cs:258-292,396-401`、`src/RelayLink.Server/AutomationEndpoints.cs:779-786`、`src/RelayLink.Server/Automation/AutomationChangeService.cs:84-118`
- **现象**：`IsLoopbackHost` 对 `localhost` 区分大小写且放行 `localhost.`、`*.localhost`、`0.0.0.0`、`::`；全局端口冲突校验及端口建议未检查 `automation.Port`。
- **建议**：集中实现规范化 `IsLoopbackOrUnspecifiedHost`，并将 `automation.Port` 纳入冲突与端口建议排除集。
- **处置（2026-11-25）**：**已修复**。`ConfigurationLoader.IsLoopbackOrUnspecifiedHost` 与 `ChannelPortSuggestion`、`AutomationEndpoints`、`AutomationChangeService` 统一规范化判断与全局端口占用检查。

### CR26-11（高）CLI 明文凭据无 Windows ACL / Unix TOCTOU，且 `auth logout` 路径穿越删任意 `.json`

- **位置**：`src/RelayLink.Cli/CliStorage.cs:85-147`、`src/RelayLink.Cli/CliApp.cs:328-334`
- **现象**：Windows 不设 ACL 且未用 DPAPI；Unix 先 0644 落盘再 chmod；`auth logout` 拼接未消毒 `profile.Name + ".json"`。
- **建议**：Windows 使用 DPAPI（`CurrentUser`）+ 仅当前用户 ACL，Unix 使用 `0600` 原子创建写入；`profile.Name` 严格字符集校验并用统一删除方法。
- **处置（2026-11-25）**：**已修复**。`CliStorage` 在 Windows 使用 DPAPI + 当前用户 ACL，Unix 以 `0600` 原子写入，严格校验 profile 名称并统一安全删除路径。

### CR26-12（高）变更申请状态机死锁、`target`/`payload` 混淆、永不过期与 Tag 丢失

- **位置**：`src/RelayLink.Server/Automation/AutomationChangeService.cs:53-299`、`src/RelayLink.Server/Runtime/ClientConfigurationEditor.cs:163`
- **现象**：`ExecuteAsync` 未捕获 `ClientUpdateException` 致永久卡死 `Applying`；`target` 与 `payload` ID 可不一致（混淆代理）；24 小时过期未校验；`SubmitAsync` 幂等未校验载荷与主体；`channel.security` 清空 `Tags`；`client.create` 缺省写入空 `agentServerHost`。
- **建议**：补齐异常捕获、事务内幂等/判重、`target`/`payload` 一致校验、过期检查与 `Tags`/默认主机保留。
- **处置（2026-11-25）**：**已修复**。`AutomationChangeService` 与 `ClientConfigurationEditor` 已补齐异常回滚、事务内幂等校验、目标/载荷一致性、24 小时过期拦截及 `Tags`/默认主机保留。

### CR26-13（高）配对容量永久锁死、`maxAuthorizations` 未生效与 `sessionLifetimeMinutes` 被忽略

- **位置**：`src/RelayLink.Server/Automation/AutomationStore.cs:206-234`、`src/RelayLink.Server/Automation/AutomationPairingService.cs:115-119`、`src/RelayLink.Server/Automation/AutomationAdminService.cs:30-38`、`src/RelayLink.Server/Automation/AutomationScopes.cs:47-88`
- **现象**：`CountPairingsAsync` 未排除已过期行导致 8 条过期后永久拒绝配对；`maxAuthorizations` 从未校验；`ApproveAsync` 忽略 `SessionLifetimeMinutes`；`AutomationScopes.Normalize` 静默吞掉未知 scope 且 `IsSubsetOf` 阻止 `*.write` 缩权至 `*.read`。
- **建议**：原子统计未过期活跃配对与授权数上限；尊重配置的会话生命周期上限；拒绝未知 scope 并允许基于 `Satisfies` 的缩权。
- **处置（2026-11-25）**：**已修复**。`AutomationStore`、`AutomationPairingService`、`AutomationAdminService` 与 `AutomationScopes` 已修复过期过滤、授权容量上限、配置有效期及 `Satisfies` 缩权校验。

### CR26-14（高）自动化存储与防重放内存 DoS、写事务放大及目录 ACL 顺序缺陷

- **位置**：`src/RelayLink.Server/Automation/AutomationPairingService.cs:25-94`、`src/RelayLink.Server/AutomationEndpoints.cs:111-116,482-488`、`src/RelayLink.Server/Automation/AutomationStore.cs:51-127`、`src/RelayLink.Server/Automation/DeviceProof.cs:38-59`
- **现象**：`replayCache`/`IdempotencyCache` 无上限且 O(N) 扫描；缺令牌级限流；`DeviceProof` 非法 JSON 抛匿名 500；`AutomationStore` 先 `Directory.CreateDirectory` 绕过 `PrepareDatabase` 私有 ACL；缺 `token_hash` 索引、缺清理任务、每次读鉴权写 `automation_instance`。
- **建议**：分桶限制 `replayCache`/幂等表容量、加令牌级限流、先校验入参、修正 `PrepareDatabase` 调用顺序、缓存 `instanceId` 并补齐索引与清理。
- **处置（2026-11-25）**：**已修复**。已为 `replayCache`/`IdempotencyCache` 施加容量上界，添加令牌级限流与入参格式校验，修正 `PrepareDatabase` ACL 创建顺序，添加 `token_hash` 索引与周期清理并缓存 `instanceId`。

### CR26-15（高）互访 HTTP 目标端正常关闭不发应用 EOF 帧 `[0,0,0,0]` 与 `FrameType.Fin`

- **位置**：`src/RelayLink.Agent/PeerSessionCoordinator.cs:305-312`、`src/RelayLink.Transport/PeerApplicationStream.cs:37-51`、`src/RelayLink.Agent/HttpProxyRuntime.cs:518-523`
- **现象**：Kestrel 结束虚拟连接后 `PeerApplicationStream` 直接关闭 `tls`，未发送 4 字节 `[0,0,0,0]` 应用 EOF 与 `FrameType.Fin`，触发服务端 `transport_error` 与访问端 `RST`。
- **建议**：在 `PeerApplicationStream` 实现幂等 `CompleteWritesAsync` 写入零长 EOF，并在目标端优雅完成时调用 `Framed.CompleteWritesAsync`。
- **处置（2026-11-25）**：**已修复**。`PeerApplicationStream` 实现幂等 `CompleteWritesAsync`；`HttpProxyRuntime.VirtualHttpConnection.DisposeAsync` 与 `PeerSessionCoordinator` 在优雅结束时发送 `[0,0,0,0]` 与 `FrameType.Fin`；`HttpProxyHostTests` 已验证。

### CR26-16（高）Agent 控制会话建连/握手/注册无超时且不支持中间 CA 链

- **位置**：`src/RelayLink.Agent/ControlSessionWorker.cs:69-88,439-456`
- **现象**：建连、TLS 握手与等待 `RegisterAccepted`/`Ready` 均无超时且未开 `TcpKeepAlive`；错误启用 `X509RevocationMode.Online` 且自定义链校验丢弃了握手中的中间 CA 证书。
- **建议**：为连接至 `Ready` 全过程增加超时与 `TcpKeepAlive`，改为 `SslClientAuthenticationOptions.CertificateChainPolicy` + `CustomRootTrust`。
- **处置（2026-11-25）**：**已修复**。`ControlSessionWorker` 为建连到 `Ready` 全程增加 30 秒超时与 `TcpKeepAlive`，TLS 验证切换为 `CertificateChainPolicy`（`CustomRootTrust` / `NoCheck`）并由 `PeerConnectionTests` 中间 CA 链用例验证。

### CR26-17（高）`.NET` / `TypeScript` / `Rust` SDK 核心运行时与多目标缺陷

- **位置**：`src/RelayLink.Sdk/RelayLink.Sdk.csproj:7`、`Directory.Build.props:3`、`src/RelayLink.Sdk/DataPlane.cs:64-76,108-132`、`src/RelayLink.Sdk/EntryDiscovery.cs:20-66`、`sdk/typescript/src/dataPlane.ts:64-81,110-129`、`sdk/rust/src/lib.rs:24,103-113`
- **现象**：`.NET` 因 `Directory.Build.props` 未清空 `TargetFramework` 导致仅编出 `net10.0`（掩盖了 `netstandard2.0` 缺 `System.Text.Json`），且 `TcpEntry.ConnectAsync`/`EntryDiscovery`/`UdpEntry` 均忽略超时与 `CancellationToken`；`TypeScript` `bindUdp` 异步 connect 未捕获异常可致进程崩溃且 `connectTcp` 泄漏 `error` 监听；`Rust` 将全部超时错误映射为 `ENTRY_UNAVAILABLE` 使 `TIMEOUT` 失效且缺失 `discover_mapping`。
- **建议**：修复 `.NET` 多目标编译与取消传播；`TypeScript` `bindUdp` 异步化并清理监听器；`Rust` 恢复 `TIMEOUT` 映射并实现零依赖入口发现。
- **处置（2026-11-25）**：**已修复**。`RelayLink.Sdk` 清空 `<TargetFramework />` 并引入内部无依赖 `MiniJson` 完成 `netstandard2.0`/`net8.0`/`net10.0` 全目标编译及取消超时传播；TypeScript、Rust 对应修复并通过一致性套件。

### CR26-18（高）AgentWeb `mappingTools()` 放行控制字符/非回环 URL；AdminWeb 审批无法缩权且默认 404

- **位置**：`src/RelayLink.AgentWeb/src/mapping-tools.ts:36-56`、`src/RelayLink.Agent/PeerSessionCoordinator.cs:70-74`、`src/RelayLink.Agent/AgentLocalDashboard.cs:32`、`src/RelayLink.AdminWeb/src/main.tsx:583-620,999-1003,1714-1764`
- **现象**：Agent 未校验 `mapping.Http` 且 `mappingTools()` 依赖 `new URL()` 静默剥离 `\r\n\t`（返回原始带换行 URL）；AdminWeb 审批始终提交 `{}` 无法按 FR-35 勾选缩小 scope/有效期，且 `automation.enabled=false` 时仍展示按钮触发 3 个 404。
- **建议**：前后端同步按 `HttpProxyPolicy` 校验 `mapping.Http`、拒控制字符与非 `127.0.0.1` IP 并返回规范化 `url.href`；`GET /api/v1/admin/session` 暴露 `automationEnabled` 并在 AdminWeb 补齐缩权控件。
- **处置（2026-11-25）**：**已修复**。Agent 后端与 `mappingTools()` 同步拦截控制字符与非 `127.0.0.1` IP 并返回规范 `url.href`；服务端 `/api/v1/admin/session` 暴露 `automationEnabled` / `automationSessionLifetimeMinutes`，AdminWeb 按标志显隐入口并在 `AutomationModal` 实现 scope/有效期缩权控件与组件测试。

### CR26-19（高）`RelayLink.Sdk` 零单测且新组件缺 CI、部署与运维脚本隐患及文档泄露真实公网 IP

- **位置**：`.github/workflows/ci.yml`、`tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj`、`sdk/README.md:26-33`、`deploy/docker/Dockerfile:28-35`、`deploy/docker/compose.yaml:4-28`、`scripts/start-server-compose.ps1:25-43`、`deploy/windows/install-server-service.ps1:1-22`、`deploy/windows/install-agent-service.ps1:152-171`、`deploy/windows/relaylink-agent.iss:171-219`、`scripts/start-local-acceptance.ps1` 等 4 个验收脚本、`docs/testing/verification-status.md:78-151`
- **现象**：`RelayLink.Sdk` 实为 0 单测、`sdk/*`/`skills/*`/`AdminWeb build`/多目标均缺 CI；Docker 以 `root` + `network_mode: host` 运行且生成开放权限 `server.json`；Windows Server 服务以 `LocalSystem` 运行且缺回滚与 `failureflag`；Windows Agent `Reconfigure` 遇 `http-resources/` 中途崩溃且无服务残留目录导致安装全模式死锁；4 个本地验收脚本均未适配多服务端配置；`docs/testing/verification-status.md` 提交了真实公网 IP 与真实客户端 ID。
- **建议**：逐项脱敏、补齐单测与 CI、Docker 降权、Windows 脚本与安装向导原子化修复并更新本地验收脚本。
- **处置（2026-11-25）**：**已修复**。完成 `verification-status.md` 脱敏、补齐 `SdkDataPlaneTests` 与 6 语言 SDK/Skill/`AdminWeb build` CI 检查、Docker 切非 root `10001:10001` 与权限收紧、Windows Server/Agent 服务脚本及 `.iss` 安装向导修复，并将 4 个本地验收脚本升级至多服务端配置。

## 6. 中危发现（Medium）

- **CR26-20（中）[已修复] PeerRelay 共享配额、僵尸定时器、`Reset` 排空与 TOCTOU**：已在 `PeerRelayRegistry` 接入 `ClientPendingLimits`、移除客户端连接双重计数、正常结束释放 300s `Task.Delay`、收到 `Reset` 立即中止排空、并在 `TryAdd` 后复查启用与映射一致。
- **CR26-21（中）[已修复] Agent 注册握手期并发编辑竞态**：`Session` 新增 `HasCompletedHandshake`；`ChannelConfigurationEditor` 与 `ClientConfigurationEditor` 对未就绪会话跳过发送 `ConfigUpdate` 并返回准确状态，`TunnelAcceptorService` 从锁内读取最新快照。
- **CR26-22（中）[已修复] `FrameReader` 帧头后零字节挂起与 CR-09 未修**：帧头读取后使用进展超时逐次递减校验与首字节载荷限时读取，`FrameTests` 已覆盖半帧/卡住回归测试。
- **CR26-23（中）[已修复] `/api/v1/history` 写锁放大、无上限与缺错误处理**：`TrafficHistoryService` 仅启动时启用 WAL 且只检查一次 ACL、查询加 `LIMIT`、对齐请求桶、`SampleIntervalSeconds <= 5` 拒绝非法配置，`Program.cs` 为 `/api/v1/history` 捕获 503。
- **CR26-24（中）[已修复] 线协议 `null` 异常、`ZeroMemory` 竞态与 `BindAccepted` 时序**：`PendingConnectionRegistry` 与 `PeerRelayRegistry` 对字段判空并使用安全拷贝加锁比对，`DataAcceptorService` 调整 `BindAccepted` 发送与链接销毁后的 CTS 生命周期。
- **CR26-25（中）[已修复] 可用性统计、任务清理、socket 泄漏、秘密检查与正则 `$`**：`ChannelAvailability` 校验目标客户端启用/在线，`TunnelAcceptorService` `finally` 等待读任务并区分类别日志，`ProxyListenerService` 取消释放 `caller`，`SecretFilePermissions` 涵盖自动化密钥与 Unix `GroupWrite`，`HttpProxyOptions`/`ConfigurationLoader` 改用 `\A...\z`。
- **CR26-26（中）[已修复] 六语言 SDK 跨语言一致性缺陷**：C#/.NET、TypeScript、Python、Go、Rust、Java 均已支持 UDP 发现、`profileId` 消歧、IPv6、禁止代理/重定向、跨平台拒绝码识别与完备包装/一致性测试套件。
- **CR26-27（中）[已修复] OpenAPI / 错误契约与实现漂移**：`contract/automation-api.openapi.json`、`contract/errors.json`、`AutomationEndpoints.cs` 与 `CliApp.cs` 完成 200 envelope、`security` AND、`/health`、HTTP 状态码及错误细节契约对齐。
- **CR26-28（中）[已修复] 自动化审计时机错位、缺事件与启动对账未等待**：调整读写审计与去重合并，`Program.cs` 启动期串行执行 `ReconcileAsync`，`AutomationChangeService` 对父客户端已删除的 `channel.delete` 正确判定为已达成期望。
- **CR26-29（中）[已修复] Skill 包装脚本环境变量透传与 CLI 选项/JSON 输出缺陷**：`relaylink.mjs` 使用环境变量白名单与 `RELAYLINK_BIN` 绝对路径校验，`CliApp`/`CliOptions`/`EntryDiscovery` 修复 `--json` 混合输出、`channels set`、`--api-version`、真复制 `skill install` 与入口发现过滤，同步 `SKILL.md` 及 `reference/change-requests.md`。
- **CR26-30（中）[已修复] `VirtualHttpConnection` 所有权竞态、非 WS `Upgrade` 绕过与权限异常**：以原子所有权状态机隔离 `ServeAsync` 与 Kestrel 对 `closed` CTS 的释放，`HttpProxyRuntime.ForwardAsync` 拒绝非 WebSocket `Upgrade` 并捕获 `PrepareAsync` 的 `UnauthorizedAccessException`。
- **CR26-31（中）[已修复] Agent 本机写会话、配置加载、端口分配与迁移器缺陷**：`AgentLocalDashboard` 校验 `Sec-Fetch-Site`/`Sec-Fetch-Dest`，`AgentLocalWriteSession` 增加滑动 30 分钟续期与 `expiresAtUtc`；`AgentProcessConfiguration` 防护 `null` 密钥，`AgentPortAllocator` 坏损恢复与写失败回滚，`AgentProcessRuntime`/`AgentConfigurationMigrator` 加 Windows 替换重试、旧 worker 异常容错与配额/空白 CA 保留。
- **CR26-32（中）[已修复] `AdminWeb` 自动化弹窗、异步竞态、错误处理与模块耦合**：拆分 `types.ts`/`api.ts`/`components/{Modal,AutomationModal,AuditModal}.tsx`，将 `automationOpen` 纳入 `editing`、禁用并发按钮、`execute` 刷新主表、登出清空状态并展示原因/过期时间、单调序列号防并发覆盖、`api()` 提取 `detail`、改用 Fetch 下载配置并补齐自动化审计标签与测试。
- **CR26-33（中）[已修复] `AgentWeb` 写会话续期、导入配额与错误恢复缺陷**：`AgentConfigurationFactory` 下发 `maxConnections`/`maxPendingConnections`；`AgentWeb` 自动续期并在 401/403 时续签重试一次、失败不清空已选文件、轮询成功清除轮询报错、支持 `Escape` 关闭弹窗。
- **CR26-34（中）[已修复] 网站陈旧、CI 重试掩盖、运维模板不对称与仓库文档漂移**：同步网站至 HTTP/自动化/六语言 SDK；CI 单元测试移除重试掩盖并消除 `pages.yml` 冗余触发与宽权限；补充 systemd `relaylink-server.service` 沙箱、PowerShell CIM 命令、BOM 修复、`server.windows.example.json` 及仓库文档一致更新。

## 7. 低危发现（Low）

- **CR26-35（低）[已修复] 服务端与传输健壮性及死代码清理**：收窄 `AuditService` 恢复 SQL 条件、消除 `RelayIdleWatch.Dispose` 阻塞、`ProxyListenerService.StopAsync` 持锁遍历、`ConfigurationLoader` 补充 IPv6/上限/异常校验、`SecurityGroupEditor` 读取 `current.Clients` 并清理 `AdminLoginGuard` 死代码。
- **CR26-36（低）[已修复] HTTP 规范化、Agent 方法检查与身份文件 ACL**：`HttpProxyPolicy` 拒绝非规范主机/端口并透传规范化 `Host`；`AgentLocalDashboard` 不区分大小写检查 HTTP 方法并兼容 80 端口；`AgentIdentity` 在 Windows 继承受保护目录 ACL 并执行 `Flush(true)`。
- **CR26-37（低）[已修复] 网站构建加固、退出登出 CSRF 与前端工程一致性**：`build-website.mjs` 关闭 `allowProtocolRelative`、强制 `_blank` `noopener noreferrer` 与替换回调；`DELETE /api/v1/admin/session` 校验 CSRF；整理 `website/package.json` `devDependencies`/`preview` 与锁定文件并新增 `src/RelayLink.AgentWeb/.prettierrc.json`。
- **CR26-38（低）[已修复] 容器缓存、Release 压缩、CLI 异常处理与证书到期告警**：`Dockerfile` 精确拷贝工程目录并加 `/health/ready` `HEALTHCHECK`；`release.yml` 增加 `Compress-Archive -Force`；`AutomationClient` 捕获 `JsonException`；`CliApp` 完善资源释放与严格 profile 命名；服务端对 30 天内过期自动化证书输出 `SECURITY` 告警。

## 8. 初始基线验证记录（同步至 `61ad904`）

| 检查项目 | 结果 | 说明 |
|---|---|---|
| `dotnet build RelayLink.slnx -c Release --no-restore -p:SkipAdminWebBuild=true` | 通过 | 0 警告 0 错误（`RelayLink.Sdk` 实仅编出 `net10.0`） |
| `dotnet build src/RelayLink.Sdk/RelayLink.Sdk.csproj -c Release --no-restore` | 异常 | 仅编出 `net10.0`，`netstandard2.0` 与 `net8.0` 未构建 |
| `dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj -c Release --no-build -p:SkipAdminWebBuild=true` | 256/256 通过 | `RelayLink.Sdk` 单测为 0 项 |
| `dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj -c Release --no-restore -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true` | 104/104 通过 | 若未重编 `PEER_TLS_TESTS` 则仅 88 通过 / 2 跳过 |
| `AdminWeb`（typecheck / test / build / format:check） | 通过 | 组件测试 10/10 |
| `AgentWeb`（build / test / format:check） | 通过 | `mapping-tools` 测试 4/4 |
| `website`（build-website.mjs / npm test） | 通过 | 双语 74 页，站点测试 5/5 |
| `skills` / `sdk/typescript` / `sdk/python` / `sdk/go` / `sdk/rust` | 语法/类型/测试通过 | `Go` 3/3、`Rust` conformance 5/5；TS 与 Python 无测试套件 |
| `sdk/java` | 需 UTF-8 编码 | 默认 GBK 报 97 错；加 `-encoding UTF-8` 主代码编译通过，单测依赖 JUnit 且无构建文件 |

## 9. 修复后闭环验证（2026-11-26 本机全栈复测）

| 检查项目 | 命令 / 范围 | 结果 |
|---|---|---|
| 解决方案 Release 构建 | `dotnet build RelayLink.slnx --configuration Release --no-restore -p:SkipAdminWebBuild=true` | **通过**（0 警告 0 错误） |
| `.NET` SDK 多目标编译 | `dotnet build src/RelayLink.Sdk/RelayLink.Sdk.csproj --configuration Release --no-restore` | **通过**（`netstandard2.0`、`net8.0`、`net10.0` 全目标编出，0 警告 0 错误） |
| `.NET` 单元测试 | `dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release --no-restore -p:SkipAdminWebBuild=true` | **299/299 通过**（0 失败 0 跳过；含 `SdkDataPlaneTests` 3 项） |
| `.NET` 集成测试 | `dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release --no-restore -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true` | **116/116 通过**（0 失败 0 跳过；含 HTTP EOF、中间 CA 链、CLI 进程与自动化端点全套回归） |
| `AdminWeb` 前端 | `pnpm --dir src/RelayLink.AdminWeb`（`typecheck` / `test` / `build` / `format:check`） | **全部通过**（组件测试 **12/12**，含 `automationEnabled` 门禁、缩权提交、Problem `detail` 与 CSRF 退出） |
| `AgentWeb` 前端 | `pnpm --dir src/RelayLink.AgentWeb`（`build` / `test` / `format:check`） | **全部通过**（`mapping-tools` 测试 **4/4**，涵盖控制字符、非回环 IP 与规范化 URL） |
| 产品静态网站 | `node scripts/build-website.mjs && npm --prefix website test` | **82 页双语生成，6/6 测试通过**（14 个发行版本/语言，覆盖 HTTP、自动化、SDK 页面与 `_blank` `noopener noreferrer`） |
| `Skill` 包装脚本 | `node --check skills/relaylink-admin/scripts/relaylink.mjs` | **通过**（语法零错误） |
| `TypeScript` SDK | `npm --prefix sdk/typescript test` | **3/3 通过**（`tsc` 编译 + 一致性/UDP/IPv6/发现测试） |
| `Python` SDK | `python -m unittest discover -s sdk/python/tests -v` | **3/3 通过** |
| `Go` SDK | `go vet ./... && go test -v ./...` | **6/6 通过**（Windows 宿主下均已复跑通过） |
| `Rust` SDK | `cargo test --manifest-path sdk/rust/Cargo.toml` | **4/4 通过** |
| `Java` SDK | `javac -encoding UTF-8` + `java ... com.relaylink.sdk.ConformanceRunner` | **全部一致性用例通过** |
| PowerShell 语法、换行与 Git 检查 | 全量 `.ps1` AST 解析、`BOM`/`CRLF` 核验与 `git diff --check` | **通过**（`PS_AST_TOTAL_ERRORS=0`，`.sh` 与 `TST-015` 无 BOM/CRLF，`git diff --check` 零告警） |

### 9.1 本机环境边界说明（未在当前 Windows 宿主直接执行）

- Linux `systemd` 服务安装与 Docker 非 `root` 容器实机运行、macOS Intel/ARM64 `launchd` 脚本以及 Inno Setup Windows 安装向导 GUI 交互流程未在当前 Windows 对话宿主中实机拉起（已通过静态代码检查、`Dockerfile`/脚本重构及全量 PowerShell 语法解析核验，待在目标 runner / Sandbox 按对应指南执行）。
