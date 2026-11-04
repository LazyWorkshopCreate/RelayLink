# 插件系统实现独立审查

文档 ID：TST-008\
状态：Active\
版本：v1.0.0\
更新日期：2026-10-31\
审查基线：`1df52815734ef20165f0be9e2b6b7332bf262971`

## 1. 结论

本次确认 **10 项问题：4 项 P1、6 项 P2**。HTTP/HTTPS 的逐请求策略、SSH 的目标身份校验和审计放行顺序存在安全缺陷；服务端可视互访的已建立连接还会被建立阶段的超时强制关闭。建议先处理 P1，再将当前实现用于需要依赖审计和阻断的环境。

单元和前端测试通过不能证明上述边界正确。本次通过独立探针复现了规则绕过、未经验证的 SSH 目标接收口令、审计失败后仍下发命令，以及持续传输在 20 秒处中断；复现使用本机测试目标和临时数据，未接触部署凭据。

本次改动仅新增独立审查报告并追加索引，未修复产品代码，未推送远程。开始时工作区已有 `docs/README.md` 修改和 `docs/testing/code-review-2026-10-31.md`；本次未阅读或采用那份报告的结论，也未修改其内容，另存本文件并追加独立索引。

## 2. 范围与方法

检查范围为 `930eb77` 至审查基线之间的插件相关实现及其依赖调用链：配置与管理 API、v6 快照与版本过滤、`server-peer` / `server-jump`、腿部 TLS 与访问证明、HTTP/HTTPS、SSH、秘密库、管理端及 Agent 页面、资源释放和审计统计。

审查分别覆盖应用协议、传输生命周期、配置与页面，再对发现进行去重和交叉核对。证据分为静态调用链、独立函数探针、真实 Server 与双 Agent 进程复现、已有回归测试。没有修改产品源文件以制造复现条件。

对照资料：

- [插件系统需求 REQ-001O](../requirements/2026-10-28-plugin-system.md)
- [插件系统设计 DES-009](../design/plugin-system.md)
- [ADR-0028](../adr/0028-server-inspection-channels.md)、[ADR-0029](../adr/0029-application-plugins-and-bridge.md)
- [审计放行门槛 ADR-0013](../adr/0013-sqlite-audit-gate.md)
- [仓库目录规则](../development/repository-layout.md)、[文档规则](../development/documentation-policy.md)

需求和设计仍为 Draft，相关 ADR 仍为 Proposed。本报告不将其改写成已批准事实；引用 FR/A 编号用于定位预期能力。已明确尚未实现的 RDP、完整审批和桥应用单列在第 5 节，不混入运行缺陷计数。

严重级别：P1 为应优先修复的安全边界失效或主要功能故障；P2 为应修复的配置、配额、统计和可用性错误。行号均对应上述基线。

## 3. 发现的问题

### IR-01 · P1：HTTP/HTTPS 只检查首个请求，后续请求绕过规则和审计

**位置：** [HttpInspection.cs](../../src/RelayLink.Server/Runtime/HttpInspection.cs)，57–60、116–122 行。

`AcceptAsync` 只解析一次请求头并调用策略。允许后，`PumpAsync` 将已读到的整个 `prefix` 写给目标，再直接双向复制剩余流，没有继续识别 HTTP 请求边界。`prefix` 本身还可能包含流水线中的第二个请求。

**触发与影响：** 同一 HTTP/1.1 连接先发送允许的 `GET /health`，随后发送应拒绝的 `POST /secret`。无论复用连接还是流水线发送，后续请求都不再匹配 `deny` / `approve`，也没有对应的请求审计。HTTPS 解密后共用此路径，两种审计模式均受影响。

**验证：** 独立探针中第二请求单独匹配结果是 `deny`；将两请求合并后，`exchange_allowed=True`、摘要只有 `GET example.test /health`，且 `disallowed_request_reached_upstream=True`。

**建议与回归：** 使用正确识别 HTTP/1.1 消息边界的转发实现，逐请求决策和审计，覆盖 Content-Length、chunked、keep-alive 与 pipeline。若阶段性只支持一个请求，应完整处理该请求后关闭，不能把多余字节转发。补充“首请求允许、第二请求拒绝/审批”的明文与 TLS 测试。关联 FR-29、A97、A100。

### IR-02 · P1：SSH 无条件接受目标主机键并向其发送目标口令

**位置：** [SshInspection.cs](../../src/RelayLink.Server/Runtime/SshInspection.cs)，142–149 行。

目标侧的 `ServerPublicKey` 回调无条件生成认证成功的 `ClaimsPrincipal`，未核对目标主机键、指纹或受信 CA。紧接着从秘密库取得认证材料并执行目标认证。

**触发与影响：** 目标地址指向冒名 SSH 服务，或 Agent 后方目标网络的 DNS、路由被替换时，服务端会将目标口令交给该主机。使用私钥认证时不会直接发送私钥，但仍会接受未验证的目标身份。RelayLink 到目标 Agent 的 TLS 不能证明 Agent 后方 SSH 服务的主机身份。

**验证：** 本机目标每次生成全新、从未登记的主机键，仍成功收到探针口令，输出 `ssh_arbitrary_target_host_received_password=True`；报告不包含口令或私钥。

**建议与回归：** 为每通道保存预期目标主机键或受信 CA，未知键、变更键失败关闭，明确首次登记方式。测试可信键成功、未知键拒绝和键变化拒绝，并断言拒绝时未发送目标密码。

### IR-03 · P1：SSH 在允许审计落库之前已经向目标发送命令

**位置：** [SshInspection.cs](../../src/RelayLink.Server/Runtime/SshInspection.cs)，118–123、139–155 行。

`DecideAsync` 先调用 `OpenTargetChannelAsync`，该方法已连接并认证目标、打开 SSH channel、发送 shell/exec/subsystem 请求；之后才写成功的 `inspection_decision`。

**触发与影响：** 前门认证成功后，审计队列耗尽、磁盘故障或数据库不可写。目标已收到并可能执行命令，后续审计异常不能撤销执行，不满足“审计失败则不放行”。

**验证：** 探针在完成前门认证后耗尽 `AuditService.capacity`，再发送无害 exec。输出 `target_exec_received_when_audit_capacity_zero=True`、`ssh_exec_audit_rows=0`、`total_audit_rows=1`；唯一记录是前门认证。

**建议与回归：** 将允许决定持久化放到连接目标、打开对应会话和发送命令之前，执行结果另记。故障注入应断言审计失败时目标连接或执行次数为零，并检查两侧关闭。关联 FR-32、ADR-0013。

### IR-04 · P1：可视互访的建立超时会终止正常长连接

**位置：** [PeerRelayRegistry.cs](../../src/RelayLink.Server/Runtime/PeerRelayRegistry.cs)，220–224、294–298、342 行。

`RunAsync` 建立 `relay.OpenTimeout` 时长的取消源，然后把该令牌传给整个 `InspectAsync`。后者使用同一个令牌执行 SSH 会话及 HTTP 全程转发，未在建立完成后切换为连接生命周期和空闲超时。

**触发与影响：** `server-peer` 的 SSH 登录、HTTP 大响应或持续输出超过 `OpenTimeoutSeconds`，即使一直有数据仍被关闭。这不是空闲回收。

**验证：** 真实 Server 与两个 Agent，`OpenTimeoutSeconds=20`，HTTP 目标声明 30 字节正文、每秒输出 1 字节。客户端只收到 20 字节，约 20.06 秒即结束，预期应至少收到全部 30 字节。

**建议与回归：** 分离 TLS/证明/策略建立阶段和已建立会话的令牌；后者使用生命周期、撤销、空闲及阻塞写入超时。添加跨越建立超时的持续 HTTP 和 SSH 会话测试。关联 A95、DES-009 第 7 节。

### IR-05 · P2：SSH 策略无法通过实际配置校验

**位置：** [ChannelApplication.cs](../../src/RelayLink.Server/Configuration/ChannelApplication.cs)，30–31 行；[InspectionPolicy.cs](../../src/RelayLink.Server/Configuration/InspectionPolicy.cs)，43–44 行。

两处校验都将非空 `Inspection` 限定为 HTTP/HTTPS；但 SSH 运行时从 `channel.Inspection?.Ssh` 读取策略。

**触发与影响：** 为 SSH 通道设置 `allowExec=false`、`requireApproval=true` 或命令前缀阻断时，正常配置加载直接报错。默认无策略配置可以运行，自定义阻断与审批却无法配置。这里是拒绝加载，不是静默忽略已保存策略；手工写入该配置也可能导致启动失败。

**验证：** 同一 SSH 通道，无 `Inspection` 时成功；添加 SSH 策略后报 `Inspection policy is only valid on HTTP or HTTPS inspection channels`。

**建议与回归：** 按应用分别校验策略及其字段，允许合法 `Inspection.Ssh`，拒绝跨应用配置。测试必须经过配置加载路径，不能仅直接构造 DTO 调用 SSH 函数。关联 FR-29、A98。

### IR-06 · P2：可视互访在插件审批前提前释放待建立配额

**位置：** [PeerRelayRegistry.cs](../../src/RelayLink.Server/Runtime/PeerRelayRegistry.cs)，213、217–224、318 行；[HttpInspection.cs](../../src/RelayLink.Server/Runtime/HttpInspection.cs)，140–156 行。

两侧数据隧道绑定后立即释放 `pendingLimit` 并执行 `MarkOpened` / `PeerOpened`，之后才进行腿部 TLS、访问证明、插件判断及审批等待。

**触发与影响：** 多个 `server-peer` 连接进入已实现的 `approve` 等待分支时，它们不再占用配置的 `MaxPendingConnections`，且被统计为 active。总连接配额仍然存在，但待建立配额不能限制这些尚未放行的连接。HTTP 独立的 256 条审批上限也不能替代管理员配置的较小待建立上限。

**验证：** 静态追踪完整的配额获取、提前释放、审批等待和 `Cleanup` 路径；未另做并发配额动态试验。

**建议与回归：** 将待建立配额释放和已建立状态转换延后到证明、插件决定和目标就绪完成，拒绝与取消统一释放一次。以待建立上限 1 测试两条审批等待连接，验证第二条被拒绝及取消后的恢复。关联 FR-29、A102。

### IR-07 · P2：可视互访和 SSH 转发未更新业务字节统计

**位置：** [PeerRelayRegistry.cs](../../src/RelayLink.Server/Runtime/PeerRelayRegistry.cs)，294–298、342、463–468 行；[SshInspection.cs](../../src/RelayLink.Server/Runtime/SshInspection.cs)，122 行；[ProxyListenerService.cs](../../src/RelayLink.Server/Runtime/ProxyListenerService.cs)，343–355、504–509 行。

可视互访 HTTP 转发把两个字节回调都传为 `null`，也不更新 `PeerRelay` 的字节计数。SSH 使用 `PipeAsync`，既没有计数回调，也没有把字节归入连接和通道计数器；跳板 SSH 路径同样如此。

**触发与影响：** 业务已正常传输，关闭审计仍记录双向 0 字节，分钟流量也无法从现有计数器得出真实业务流量。`TrafficHistoryService` 30–47 行只采集这些既有计数器，没有另一路应用字节来源。

**验证：** 静态追踪转发、连接快照、关闭审计和分钟采样；未对该项进行独立计量负载测试。

**建议与回归：** 在应用边界统计业务字节，同时更新连接审计和分钟历史；保持现有 `peerCiphertext*` 的旧模式语义。对 HTTP/SSH 两种模式做已知字节量的端到端断言，验证非零且不重复计算 TLS 开销。关联 FR-29、FR-32、DES-009 第 8 节。

### IR-08 · P2：HTTP 首批已读请求字节完全漏计

**位置：** [HttpInspection.cs](../../src/RelayLink.Server/Runtime/HttpInspection.cs)，116–121 行。

`PumpAsync` 写入 `prefix` 后未调用 `fromCaller`，只有后续 `CopyStreamToStreamAsync` 更新计数。`prefix` 包含完整请求头，也可能包含已一起读入的正文。

**触发与影响：** 跳板 HTTP/HTTPS 即使传入有效计数回调，普通单次读取即可完成的 GET 请求仍记录为向目标发送 0 字节；POST 也会少计预读正文。此项与 IR-07 的空回调独立，补上调用方回调仍不能自动修复。

**验证：** 独立探针实际向目标写入 108 字节，`fromCaller` 累计值为 0。

**建议与回归：** 成功写完 prefix 后统一计入相同口径，覆盖只有头、头与部分正文一次读入及正文分段三种情况。关联 A100。

### IR-09 · P2：可用的服务端可视互访被管理页面显示为离线

**位置：** [ChannelAvailability.cs](../../src/RelayLink.Server/Runtime/ChannelAvailability.cs)，24–29 行；[管理页面](../../src/RelayLink.AdminWeb/src/main.tsx)，1498–1512、1526–1540 行。

`IsOutboundMappingAvailable` 硬编码目标审计模式必须为 `none`。管理 API 把该结果交给页面；页面只有在 `available=true` 时才显示入口地址和复制按钮。

**触发与影响：** 两侧为 v6、映射启用并已上报有效本机地址的 `server-peer` 入口，仍永久显示离线、地址为 `—`，无法使用复制入口功能，即使实际业务转发可用。

**验证：** 同一映射、地址和两个 v6 会话，`server-peer` 返回 `False`；仅将模式改为 `none` 返回 `True`。

**建议与回归：** 为 `server-peer` 根据两侧版本和实际就绪状态判定可用性；补充服务端投影和管理页面测试。关联 A94、A95、A105。

### IR-10 · P2：关闭控制 TLS 时仍可保存永远不可用的跳板通道

**位置：** [ConfigurationLoader.cs](../../src/RelayLink.Server/Configuration/ConfigurationLoader.cs)，273、308–311 行；[ProxyListenerService.cs](../../src/RelayLink.Server/Runtime/ProxyListenerService.cs)，306–307 行。

配置层要求现有 HTTP 代理和授权互访使用控制 TLS，却遗漏 `server-jump`。运行时又明确对 `server-jump && !Tunnel.TlsEnabled` 拒绝全部连接。

**触发与影响：** 在允许明文控制连接的既有部署上创建 raw TCP 跳板通道。保存校验通过，针对 v6 Agent 的公共通道可用性检查也返回真，但所有业务连接立即以 `inspection_unavailable` 结束。此项是配置和可用性不一致，没有观察到降级为明文。

**验证：** `ConfigurationLoader.ValidateClientUpdate` 接受 TLS 关闭的跳板配置，`IsPublicChannelAvailable` 返回 `True`；拒绝路径由运行时代码确认。

**建议与回归：** 在文件加载和 API 保存前统一检查可审计模式的 TLS 前提，给出明确错误；可用性投影遵循同一条件。关联 FR-27、A96。

## 4. 验证记录

环境：Windows，本机 .NET SDK `10.0.114`；进程测试仅监听 loopback，每个 Agent 使用独立测试目录。没有验证 Linux/macOS 运行结果、跨公网性能或真实业务目标。

| 检查 | 实际结果 |
|---|---|
| .NET Release 单元测试，`SkipAdminWebBuild=true` | 228 通过，0 失败，0 跳过 |
| .NET Release 集成测试，启用 `EnablePeerTlsTests=true` | 首轮 89 通过、1 失败、0 跳过；失败项单独重跑 1 通过 |
| AdminWeb `typecheck` | 通过 |
| AdminWeb `test` | 9 通过 |
| AdminWeb `format:check` | 通过 |
| AgentWeb `build` | 通过 |
| AgentWeb `format:check` | 通过 |
| HTTP/SSH 独立探针 | 复现 IR-01、IR-02、IR-03、IR-08 |
| Server 与双 Agent 持续 HTTP 进程探针 | 复现 IR-04 |
| 配置与可用性独立探针 | 复现 IR-05、IR-09、IR-10 |

首轮集成测试失败项为 `MultiServerAgentTests.Same_mapping_id_on_two_servers_rotates_conflicting_loopback_port_and_relays_to_own_target`，在 `MultiServerAgentTests.cs:235` 等待两个连接项在线并报告本机地址时达到 45 秒取消期限。同一二进制单独重跑约 2 秒通过。该次超时原因未定位，不能将首轮写成全绿，也不能据此认定为插件回归或已解决的问题。

已有 HTTP 集成测试的目标在首个请求后返回 `Connection: close`，见 `PeerConnectionTests.cs` 206–331 行。因此单请求允许/拒绝通过，不能覆盖 IR-01 的复用连接绕过，也不能覆盖 IR-04 的长响应中断。当前 SSH 单元测试直接构造运行配置，不能替代 IR-05 所需的配置加载校验。

测试命令：

```powershell
dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release -p:SkipAdminWebBuild=true --logger "trx;LogFileName=plugin-independent-unit.trx" --results-directory work/plugin-independent-review/test-results
dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true --logger "trx;LogFileName=plugin-independent-integration.trx" --results-directory work/plugin-independent-review/test-results
dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release --no-build -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true --filter "FullyQualifiedName=RelayLink.IntegrationTests.MultiServerAgentTests.Same_mapping_id_on_two_servers_rotates_conflicting_loopback_port_and_relays_to_own_target" --logger "trx;LogFileName=plugin-independent-integration-retry.trx" --results-directory work/plugin-independent-review/test-results
pnpm --dir src/RelayLink.AdminWeb typecheck
pnpm --dir src/RelayLink.AdminWeb test
pnpm --dir src/RelayLink.AdminWeb format:check
pnpm --dir src/RelayLink.AgentWeb build
pnpm --dir src/RelayLink.AgentWeb format:check
```

独立探针留在被忽略的本机 `work/` 目录，未作为产品测试提交。它们引用本次构建的 Release 程序集；SSH/配置探针需要输出目录中的 Windows SQLite 原生依赖。复现方式和关键输出已记录在各问题中，本机可再次执行：

```powershell
dotnet run --project work/independent-plugin-http-review/Review.csproj --configuration Release
dotnet run --project work/plugin-review-transport/Repro.csproj --configuration Release
dotnet work/review-config-ui/bin/Release/net10.0/Review.dll
```

上述探针不是新增的正式回归套件，后续修复应把相应断言纳入 `tests/`。未执行完整旧协议二进制版本矩阵、并发容量压测或浏览器交互验收；不据此宣称 A92–A107 全部通过。

## 5. 已声明的范围缺口和文档偏差

| 内容 | 当前状态与结论 |
|---|---|
| RDP 门控、自动登录 | 设计文首明确尚未实现；现有 RDP 审计模式失败关闭，不算回退到原始代理 |
| 完整审批 | HTTP/SSH 有等待后拒绝的部分逻辑，没有管理员批准 API/页面；不能称 A102 完成 |
| 桥应用、一次性 intent、凭据发放 | 尚无完整实现，不能称 A103 的发放部分、A104、A105 唤起部分完成 |
| 管理页插件策略、HTTPS 材料和凭据表单 | 尚未提供；HTTPS 应用虽在下拉中，创建请求不能携带必需的 `Inspection` 材料，当前需用配置文件准备 |
| SSH 审计完整性 | 当前主要写认证和动作决定；设计列出的 subsystem 名称、退出状态等尚缺完整证据，不能称 A98 全量完成 |
| 插件主机抽象 | 当前主要按应用分支调用静态实现，尚非设计所述四实现注册表；不把缺少抽象本身认定为功能缺陷 |
| 需求及索引的实现状态 | 需求末尾与索引仍称没有实现，设计文首已描述部分实现；应同步事实，不自行把 Draft/Proposed 改为 Accepted |

## 6. 建议修复顺序

1. 先修 IR-01 至 IR-03 的协议安全与审计门槛，并加入失败关闭测试。
2. 分离建立和会话生命周期，修 IR-04、IR-06，验证长连接、审批取消和资源释放。
3. 修 SSH 配置入口及业务计数，再修页面可用性和 TLS 保存校验。
4. 明确未完成能力的实现计划，更新实现状态和验收证据；不因已有单项测试通过而标记整套插件验收完成。
