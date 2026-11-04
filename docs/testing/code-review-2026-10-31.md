# RelayLink 代码审查记录（2026-10-31）

文档 ID：TST-007\
状态：Active\
版本：v1.0.0\
更新日期：2026-10-31

本文记录对 `feat/plugin-system` 分支（`main...1df5281`，9 个提交、51 个文件）的一次静态代码审查。审查对象为插件系统与可审计通道的首期实现。本文只提出问题与修改建议，未修改任何产品代码、配置示例或测试，也不构成需求验收或发布证据。

## 1. 范围与方法

- 阅读 `AGENTS.md`、`README.md`、`docs/README.md`、[插件系统需求](../requirements/2026-10-28-plugin-system.md)、[插件系统设计](../design/plugin-system.md)、[ADR-0028](../adr/0028-server-inspection-channels.md) 与 [ADR-0029](../adr/0029-application-plugins-and-bridge.md)。
- 静态审查 `main...HEAD` 的 51 个文件：协议（`InspectionProof`、消息与序列化）、Server 配置与运行时（`ChannelApplication`、`InspectionPolicy`、`CredentialStore`、`HttpInspection`、`SshInspection`、`PeerRelayRegistry`、`ProxyListenerService`、`Program`）、Agent（`ControlSessionWorker`、`PeerSessionCoordinator`）、AdminWeb 与 AgentWeb，以及相关测试。
- 重点检查：腿部 TLS 与两段证明、插件决策顺序与失败关闭、资源释放与半关闭、超时与取消、秘密材料、审计与日志、配置校验与持久化、协议版本兼容。
- 执行单元测试与检查通道专项集成测试；未执行完整集成矩阵、前端检查、网站检查与部署验收。

## 2. 结论摘要

| 级别 | 数量 | 说明 |
|---|---|---|
| 高 | 3 | 可视互访会话超时、映射可用性误判、SSH 策略不可配置 |
| 中 | 8 | RDP 死通道、审批超时、SSH 请求重放、HTTPS 不可创建、路径持久化、凭据审计顺序、HTTP abort、凭据库启动失败 |
| 低 | 8 | 主机密钥、空闲超时、审计完整性、指标口径、异常处理、tag 大小写、秘密残留、文档一致性 |

未发现新的认证绕过或明文秘密写入通道 JSON、快照和日志。双侧腿部 TLS、域分隔的两段证明、凭据库加密且管理接口不可读回、v5 快照过滤与策略不进 Agent 快照均有实现和测试，整体失败关闭路径完整。

## 3. 重要发现

### CR-01（高）server-peer 会话在 openTimeoutSeconds 后被强制中断

**位置**

- `src/RelayLink.Server/Runtime/PeerRelayRegistry.cs:222`：`inspectionTimeout` 使用 `relay.OpenTimeout`。
- `src/RelayLink.Server/Runtime/PeerRelayRegistry.cs:224`：该 token 传给 `InspectAsync`。
- `src/RelayLink.Server/Runtime/PeerRelayRegistry.cs:294`、`:342`：`InspectAsync` 在其上等待完整的 `SshInspection.RelayAsync` 与 `HttpInspection.PumpAsync`。

**现象**

`OpenTimeoutSeconds` 来自 `limits.openTimeoutSeconds`（示例配置 20 秒）。检查阶段新建的取消源会在 20 秒后取消，而该取消令牌贯穿整个会话中继，不是只覆盖握手、证明和首次决策。任何持续超过 20 秒的服务端可视互访 SSH、HTTP 或 HTTPS 会话都会以 `cancelled` 结束。

**影响**

可视互访的正常业务会话被周期性中断；SSH 交互、长连接 HTTP、慢响应都会受影响。现有集成测试均在 20 秒内完成，因此 CI 不暴露。

**建议**

仅在腿部 TLS、两段证明、插件决策和目标就绪期间使用打开超时；决策完成后改用 `relay.LifetimeToken` 或会话生命周期令牌，并按设计补上空闲超时。为两类应用各增加一个超过打开超时的长会话测试。

### CR-02（高）server-peer 映射恒显示不可用

**位置**

- `src/RelayLink.Server/Runtime/ChannelAvailability.cs:24`：`IsOutboundMappingAvailable` 新增 `(target?.InspectionMode ?? "none") == "none"`。
- `src/RelayLink.Server/Program.cs:475`、`:550`：映射状态与仪表盘快照使用该结果。
- `src/RelayLink.AdminWeb/src/main.tsx`：映射表据此显示在线/离线并隐藏本机地址。

**现象**

server-peer 正是通过访问方 Agent 的 loopback 映射接入，`PeerSessionCoordinator` 会为该映射创建监听；但可用性判断把目标通道不是 `none` 的映射一律排除。管理端因此显示“离线”，并隐藏 `127.0.0.1:<port>`。

**影响**

新功能的主入口在管理端看起来不可用，操作员无法复制地址。Agent 本机页按 `localAddress` 判断，不受影响，但两端状态不一致。

**建议**

允许 `none` 与 `server-peer`；对 server-peer 额外要求调用方与目标会话均为协议 v6，与 `PeerRelayRegistry.RequestAsync` 的实际拒绝条件保持一致。

### CR-03（高）SSH 检查策略永远不可配置

**位置**

- `src/RelayLink.Server/Configuration/ChannelApplication.cs:30`：非 HTTP/HTTPS 应用携带 `Inspection` 直接拒绝。
- `src/RelayLink.Server/Configuration/InspectionPolicy.cs:43`：同样的限制。
- `src/RelayLink.Server/Runtime/SshInspection.cs:19`：读取 `channel.Inspection?.Ssh`。
- `src/RelayLink.Server/Configuration/InspectionPolicy.cs:106`：`SshInspectionPolicy` 定义。

**现象**

只要应用是 `ssh`，写入 `inspection.ssh` 会让整个客户端配置加载失败；不写则 `SshInspectionPolicy` 只能取默认值。结果是：

- 无法开启 `RequireApproval`，与 FR-29“打开会话前可审批”不符；
- 无法按 `DeniedCommandPrefixes` 拒绝命令，也无法配置 shell、exec、subsystem；
- `AllowTcpForwarding` 即使打开，也只授权了全局请求，并未把转发桥接到目标。

**影响**

文档描述为“可配置”的 SSH 审计与阻断能力实际不可用，且配置错误要等到加载阶段才以通用错误暴露。默认值仍然拒绝端口转发、X11 与 agent forwarding，因此不构成直接放行。

**建议**

让 SSH 应用的 `Inspection` 通过校验并校验 SSH 子结构；在实现转发桥接之前，明确拒绝或忽略会误导的转发开关。补充配置文件加载与策略生效测试。

## 4. 次要发现

### CR-04（中）RDP 检查通道可保存，但每条连接必然拒绝

- 位置：`src/RelayLink.Server/Configuration/ChannelApplication.cs:36`（允许 rdp）、`src/RelayLink.Server/Runtime/ProxyListenerService.cs:389`（跳板只放行空应用）、`src/RelayLink.Server/Runtime/PeerRelayRegistry.cs:309`（可视互访只处理 ssh/http/https），以及 AdminWeb 应用下拉。
- 现象：保存成功，但目标连接前即被拒。需求 FR-29 要求 RDP 首期做连接级允许、拒绝、审批与元数据审计，设计 §2.3 写“原样对接”，设计状态段又写“RDP 关闭”，三处不一致。
- 建议：实现连接级门控与原始字节桥接，或在保存时明确拒绝 rdp 并同步文档。

### CR-05（中）审批等待被打开超时截断

- 位置：`src/RelayLink.Server/Runtime/ProxyListenerService.cs:372`、`src/RelayLink.Server/Runtime/PeerRelayRegistry.cs:224`。
- 现象：`approvalTimeoutSeconds` 允许 1–300（默认 60），但等待使用打开超时（20 秒）；结果记为 `cancelled` 而非 `approval_timeout`。
- 建议：拆分请求头决策与审批等待，审批使用连接生命周期令牌，符合设计 §4。

### CR-06（中）SSH 多通道请求被跨会话重放

- 位置：`src/RelayLink.Server/Runtime/SshInspection.cs:30`、`:57`、`:153`。
- 现象：`forwarded` 为共享 `List`，打开新目标通道时重放全部历史 `pty-req`、`env`、`window-change`、`signal`；同一连接开第二个 session 会继承错误终端信息，列表还会被并发修改。`signal` 在通道打开时重放也不正确。
- 建议：按 caller channel 保存并转发请求，或实时转发；对共享状态加同步。

### CR-07（中）HTTPS 应用可在 UI 选择，但无法创建

- 位置：`src/RelayLink.AdminWeb/src/main.tsx:2145`、`src/RelayLink.Server/Configuration/InspectionPolicy.cs:39`；`ChannelConfigurationEditor` 的创建与更新均不接受 `Inspection`。
- 现象：选择 HTTPS 保存必然失败，证书只能通过配置文件写入。
- 建议：策略编辑器落地前隐藏 HTTPS 选项，或补充证书与策略 API。

### CR-08（中）相对证书路径被改写为绝对路径并持久化

- 位置：`src/RelayLink.Server/Configuration/ConfigurationLoader.cs:72`、`:77`、`src/RelayLink.Server/Runtime/ChannelConfigurationEditor.cs:116`。
- 现象：加载时解析路径，任何通道编辑都会把整个 client 重新序列化；相对路径因此变成机器绝对路径。
- 建议：持久化时保留原始相对路径，仅在运行时使用解析结果；配置目录迁移或备份恢复后再验证。

### CR-09（中）凭据写入与删除先落盘后审计，审计失败返回 503

- 位置：`src/RelayLink.Server/Program.cs:400`、`:420`。
- 现象：秘密已保存或已删除，客户端却看到失败；删除重试得到 404。
- 建议：先审计后写入，或返回成功并附诊断；为两条路径补失败注入测试。

### CR-10（中）server-peer HTTP 的 abort 是空操作

- 位置：`src/RelayLink.Server/Runtime/PeerRelayRegistry.cs:342`。
- 现象：任一方向异常时不会取消另一方向；当前靠 CR-01 的超时兜底，修复后会挂起直到对端关闭。
- 建议：传入取消令牌并释放两侧 TLS 与流。

### CR-11（中）凭据库损坏会让整个服务端启动失败

- 位置：`src/RelayLink.Server/Program.cs:77`、`src/RelayLink.Server/Runtime/CredentialStore.cs:124`。
- 现象：key/store 不匹配或无法解密直接抛异常，所有通道都不能启动；设计 §10 只要求“整条可审计通道失败关闭”。
- 建议：至少在启动错误中明确原因；若要坚持整体失败，在部署文档中写明凭据目录与服务器配置必须同时备份。

## 5. 低优先级发现

- **CR-12（低）上游 SSH 主机密钥未校验**：`src/RelayLink.Server/Runtime/SshInspection.cs:142` 接受任意 `ServerPublicKey`，没有 known_hosts 或指纹固定。目标腿位于 Agent 腿部 TLS 内，风险有限，建议记录或提供通道级指纹。
- **CR-13（低）可审计会话缺少空闲超时**：server-peer 不使用 `RelayIdleWatch`，SSH 也没有 idle timeout；死连接可能长期占用配额。
- **CR-14（低）审计事件不完整**：设计 §8 的 `inspection_approval_granted/denied/expired`、`inspection_session_closed`、`credential_released` 均未实现；目前只有 `inspection_decision` 与 `inspection_response`。
- **CR-15（低）决策前记录 opened**：`src/RelayLink.Server/Runtime/PeerRelayRegistry.cs:202` 在插件决策前写 `peer_connection_opened`、释放待建立配额并计 `PeerOpened`，被拒绝的连接会显示为已建立。
- **CR-16（低）fire-and-forget 审计重抛**：`src/RelayLink.Server/Runtime/SshInspection.cs:223` 在未等待的调用中重抛 `AuditUnavailableException`，产生未观察任务异常。
- **CR-17（低）`app:` tag 大小写不一致**：`ChannelApplication.FromTags` 用 ordinal 判断，编辑器与前端用忽略大小写；配置文件手写 `App:ssh` 会被前端当成应用 tag、运行时当成普通 tag。
- **CR-18（低）删除通道不清理凭据与主机密钥**：同一 `channelId` 重建会复用旧秘密。
- **CR-19（低）文档状态不一致**：需求文档 §6 仍写“仅完成调研和文档”（`docs/requirements/2026-10-28-plugin-system.md:139`）；设计 §2.3 的 RDP 行与状态段冲突；ADR-0029 仍为 Proposed。

## 6. 已确认的设计边界（不作为缺陷）

- HTTP 插件只处理 HTTP/1.1，拒绝 HTTP/1.0，符合设计 §4.3。
- HTTPS 不为每个站点动态签发证书，也不自动安装根证书；站点证书固定导致失败是预期行为。
- 未指定应用只允许出现在跳板代理的明文路径；server-peer 必须选择四种应用之一。
- RDP 首期不做图形、剪贴板和 CredSSP 解析，是设计中的能力边界；配置接受方式的问题见 CR-04。
- 管理页尚未提供检查策略与凭据编辑，属于文档已写明的阶段状态；HTTPS 选项的可用性问题见 CR-07。

## 7. 实际执行的检查

环境：Windows 11，.NET SDK 10.0.114。

| 检查 | 命令 | 结果 |
|---|---|---|
| 单元测试 | `dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release -p:SkipAdminWebBuild=true` | 通过，228/228，5 秒 |
| 检查通道集成测试 | `dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true --filter "FullyQualifiedName~inspection\|FullyQualifiedName~Server_peer\|FullyQualifiedName~Server_jump"` | 通过，5/5，13 秒 |

未执行：完整集成测试矩阵、AdminWeb 的 typecheck、test 与 format、AgentWeb 构建、网站构建与浏览器验收、Docker 与安装包、真实跨主机部署。上述项目仍以[验证状态](verification-status.md)和各验证记录为准。

## 8. 建议处理顺序

1. CR-01：修正 server-peer 会话令牌，并补超过打开超时的长会话测试。
2. CR-02：修正映射可用性判断，与 v6 条件对齐。
3. CR-05：拆分审批等待的连接令牌。
4. CR-03：允许并校验 SSH 策略；再决定端口转发是否真正桥接。
5. CR-04：先做产品决定，实现 RDP 连接级门控或拒绝 rdp 配置。
6. CR-06、CR-07、CR-08、CR-09、CR-10、CR-11 按模块处理。
7. 低优先级项可随下一次可观测性与配置改动分批完成。

## 9. 声明

本次审查未修改源代码、测试、配置示例或部署资源。严重级别基于当前代码与文档中的部署假设；实际风险取决于管理面暴露范围、安全组配置和是否启用新通道。修复时请按仓库规则同步更新受影响的文档、示例、测试和验证记录。
