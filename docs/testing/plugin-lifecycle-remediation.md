# 插件生命周期、计量与旧端兼容验证

文档 ID：TST-012\
状态：Active\
版本：v1.1.0\
更新日期：2026-10-31

本记录覆盖[改进方案](../development/plugin-system-improvement-plan.md) B3、B6 的生命周期、应用计量和真实旧端回归，对应 W04、W05、W08（CR-01、CR-05、CR-10、CR-13、CR-15，IR-04、IR-06、IR-07、IR-08）。HTTP 请求边界和 SSH 动作门槛分别见 [TST-010](plugin-http-remediation.md)、[TST-011](plugin-ssh-remediation.md)。协议决定见 [ADR-0030](../adr/0030-inspection-lifecycle-protocol.md)。

## 实际变更

- Server 和 Agent 共用待建立、active、终止三个资源阶段。TLS/证明、首头、前门认证和目标连接各有期限；审批使用独立期限。首次 active 后取消待建立总预算，只保留会话撤销、业务空闲、阻塞写入和半关闭期限。
- 调用方输入就绪仍允许发送 HTTP 请求头或 SSH 请求，保持 pending。目标 TCP 就绪先允许目标腿传输上游认证；应用认证成功后才发送 v7 `InspectionActive`，释放首次 pending。两类就绪不能合并，否则会产生等待死锁。
- 目标 Agent 在收到放行后重新计算目标连接期限。`InspectionTargetReady` 和 `OpenFailed` 经当前目标会话和连接归属校验路由到 peer；原跳板保留 `TargetReady` / `Start`。
- 保留 BindAccepted / Start 之前的持久化审计。可审计连接的既有 opened 起始事件使用 `prepared` / `inspection_preparing`；业务开通另写 `inspection_session_opened`。目标失败不增加业务 opened/active。
- 首次审批继续占用连接 pending。active 会话的后续审批另租用有界 pending 名额，并与 HTTP/SSH 共用的全局审批容量联合限制。当前没有批准 API，超时结果仍是拒绝。
- 后续审批租约暂缓 Server 业务空闲，结束后恢复计时，保持 active 状态。Agent 无法解析腿部加密字节的业务含义，因此不重复判定插件业务 idle；仍保留待建立、阻塞写、半关闭和会话撤销。
- 故障撤销共享令牌并关闭两侧；正常 EOF 有界排空。Stream 写入独立限时，`idle=0` 不会取消阻塞写期限。peer 终止先取消、等待所有方向结束，再释放容量、封存字节并写终止审计，避免最后一次成功写回调落在审计快照之后。
- 新增 `applicationToTarget` / `applicationToCaller`。HTTP/HTTPS 计上游应用流的 HTTP 头和正文；SSH 计 channel payload。它们不混入 `peerCiphertext*`；旧透传口径保持。分钟历史表新增列默认零，旧行原值不变。
- v7 为修复后插件能力最低版本。v3–v6 保留原有 `none` 通道；快照过滤插件通道，v6 序列化不携带新等待预算，不把忽略未知字段当作兼容保证。

## 反例与正式回归

独立审查时原实现使用 open=20 秒、目标每秒发送 1 字节、共 30 字节，实际约 20.06 秒只收到 20 字节。该反例及原因保留在[独立审查报告](plugin-system-independent-review-2026-10-31.md)，原探针位于本机忽略目录 `work/plugin-review-transport`。本次将完整长流、审批和阻塞清理断言纳入正式测试，不依赖手工探针作为最终回归门槛。

测试代码：[生命周期进程测试](../../tests/RelayLink.IntegrationTests/PluginLifecycleTests.cs)、[SSH 进程测试](../../tests/RelayLink.IntegrationTests/SshInspectionProcessTests.cs)、[生命周期单测](../../tests/RelayLink.UnitTests/InspectionLifetimeTests.cs)、[协议兼容单测](../../tests/RelayLink.UnitTests/InspectionProtocolTests.cs)、[peer 终止竞态](../../tests/RelayLink.UnitTests/PeerRelayReliabilityTests.cs)、[历史迁移](../../tests/RelayLink.UnitTests/TrafficHistoryTests.cs)。

| 场景 | 结果断言 |
|---|---|
| 原 30 字节 / 30 秒 HTTP 反例 | open=20 秒，30 字节全部收到，持续时间超过 open |
| HTTP/HTTPS × server-peer/server-jump | open=2 秒、idle=2 秒，40 字节每 100 ms 发送，完整收到；HTTPS 两端应用 TLS 均真实握手 |
| SSH × server-peer/server-jump | 真实 Server、Agent、SSH 调用方和目标；open=2 秒、idle=2 秒，4 秒业务流完整；应用双向计数恰为 5 / 40 字节 |
| HTTP/HTTPS 计量 | API 应用计数等于目标实际收到的 HTTP 请求及响应头和正文，peer 密文字节为零 |
| 审批 × 两种模式 | open=2 秒、approval=5 秒、idle=1 秒，等待不被建立或业务空闲提前截断；最终拒绝，目标零连接 |
| 审批中管理员断开/配置撤销 × 两种模式 | 等待进入审计明确记录 waiting 后撤销，5 秒内 pending/active 均归零，连接表清空且目标零连接 |
| 首次 pending 上限 1 | 第二连接在短期限内拒绝；首个审批超时后连接表清空 |
| 活动连接后续审批 | 两模式先完成允许请求，再审批第二请求；idle=1 秒不截断 5 秒审批，后续请求未到目标 |
| 阶段空闲与阻塞写 | pending 不运行 active idle；active 真正静默会取消；idle 关闭时阻塞 Stream 写仍有界 |
| 单向故障 | abort 恰调用一次，另一方向取消且任务被观察 |
| 取消与写完成竞态 | 模拟已交给传输层的写在取消后成功完成；其返回之前不释放总容量、不记 closed，之后审计与最终计数都为 3 字节 |
| 撤销后的迟到目标就绪 | 有界终止归属缓存只接受同会话已结束连接的迟到结果；实际控制读取循环继续处理 Pong，错误归属和未知连接仍拒绝 |
| 协议与历史 | v3/v4/v5/v6 快照过滤新能力；v6 无新等待字段；旧 SQLite 历史列值保持，新应用字节只计一次 |

## 执行记录

环境：Windows、本机 .NET SDK 10.0.114。进程测试只监听 `127.0.0.1`，每个 Agent 独立目录。证书、口令和数据均为测试临时材料。

```powershell
dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release -p:SkipAdminWebBuild=true --artifacts-path work/plugin-transport-unit --filter 'FullyQualifiedName~InspectionLifetimeTests|FullyQualifiedName~InspectionProtocolTests|FullyQualifiedName~TrafficHistoryTests|FullyQualifiedName~PeerRelayReliabilityTests|FullyQualifiedName~PeerSetup|FullyQualifiedName~FrameTests|FullyQualifiedName~PendingReadinessTests' --logger 'trx;LogFileName=plugin-lifecycle-unit-final.trx' --results-directory work/plugin-lifecycle-results

dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true --artifacts-path work/plugin-transport-build --filter 'FullyQualifiedName~PluginLifecycleTests|FullyQualifiedName~SshInspectionProcessTests' --logger 'trx;LogFileName=plugin-lifecycle-final.trx' --results-directory work/plugin-lifecycle-results

dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true --artifacts-path work/plugin-transport-build --filter 'FullyQualifiedName~Approval_on_active_connection' --logger 'trx;LogFileName=plugin-active-approval.trx' --results-directory work/plugin-lifecycle-results
```

- 单元专项：**44 通过、0 失败、0 跳过**，含终止计量竞态、八项迟到就绪、后续审批暂停 idle，以及目标已就绪后迟到失败不覆盖目标结果的回归。
- 生命周期进程专项：**9 通过、0 失败、0 跳过**，约 80 秒。之后新增两种模式的活动连接后续审批测试，定点执行 **2 通过、0 失败、0 跳过**，约 13 秒；之后补审批中管理员断开和有效配置变更撤销四项，定点执行 4/4 通过；当前两测试类共十五项，完整重跑结果以总验证记录为准。
- 扩展进程回归：此前执行 `PeerConnectionTests|ServerTunnelTests|PluginLifecycleTests`，当时包含四个初始生命周期案例，**56 通过、0 失败**。新增 HTTPS/SSH/30 秒案例以最后一次九项专项为准；整个修复集的最终全套结果见[总验证记录](plugin-system-remediation-verification.md)。

HTTPS 测试扩展的初轮失败来自 Windows Schannel 不支持测试生成的临时私钥句柄，随后又暴露测试证书缺少匹配的密钥用途。测试材料改为 P-256、serverAuth/数字签名用途及 PFX `UserKeySet` 重新加载后，两模式定点测试和最终九项专项均通过。没有放宽证书链或名称验证来绕过失败。

追加撤销测试首次尝试停用仍被有效映射引用的目标，被既有配置引用校验返回 400。改为受支持的目标连接期限修改后验证“配置变更撤销”，四项全部通过；未放松原有引用校验。证据为 `work/plugin-remediation-final/results/revocation-diagnostic.trx`、`revocation-passed.trx`。

## 真实旧版 Agent 二进制

使用本机已有、忽略目录中的 rc.8（线协议 v3）和 rc.9（线协议 v4）发布包。执行前核对 SHA256SUMS，并逐项核对每个压缩包的 29 个文件与解压目录完全一致，没有重新编译旧 Agent 或下载替代物。

| 发布包 | SHA-256 |
|---|---|
| v0.1.0-rc.8 Windows x64 | `30e6be9a58fb47385e7e17bf121ed210de14e35ff7362653aeb5bf0ec6d717f9` |
| v0.1.0-rc.9 Windows x64 | `394c2c8ce9bc8468b671dda053f62eb9079743bd77b6ae80047eb814604fa72e` |

```powershell
$env:RELAYLINK_LEGACY_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v3-compatibility/agent/RelayLink.Agent.dll').Path
$env:RELAYLINK_LEGACY_V4_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v4-compatibility/agent/RelayLink.Agent.dll').Path
dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:EnablePeerTlsTests=true -p:EnableLegacyAgentTests=true -p:EnableLegacyV4AgentTests=true -p:SkipAdminWebBuild=true --artifacts-path work/plugin-legacy-build --filter 'FullyQualifiedName~Legacy_v3|FullyQualifiedName~Udp_peer_request_to_real_v3|FullyQualifiedName~HttpProxyLegacyAgentTests' --logger 'trx;LogFileName=legacy-v3-v4.trx' --results-directory work/plugin-lifecycle-results
```

结果：**14 通过、0 失败、0 跳过**，约 19 秒。覆盖 v3 旧/新调用方与目标的明文、加密 TCP 组合，v3 UDP 拒绝且控制会话继续在线，v4 raw peer TLS/半关闭，以及 v4 HTTP 能力过滤/拒绝后 raw 映射仍在线。

另补真实 v5：使用本机已有 `.local/public-release-assets/v0.1.0-rc.12/RelayLink-Agent-win-x64-framework-dependent-0.1.0-rc.12.zip`，SHA-256 为 `5a913cb3b4a811652b4785337a2358c4a15d536f10b3a427c6edd91356f1f0f1`，与同目录 `SHA256SUMS` 一致。解压后逐项核对全部 31 个文件与压缩包内容完全一致；旧包 `ProtocolConstants.Version` 为 5。旧 Agent DLL 的 SHA-256 为 `fb57467925e42b7d6fe8d55d599b1a212900bb2bc965df2bcb714c1e0a09d0c3`。没有重新编译旧 Agent、下载替代物或修改远程环境。

```powershell
$env:RELAYLINK_LEGACY_V5_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v5-compatibility/agent/RelayLink.Agent.dll').Path
dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:EnablePeerTlsTests=true -p:EnableLegacyV5AgentTests=true -p:SkipAdminWebBuild=true --artifacts-path artifacts/plugin-v5 --filter 'FullyQualifiedName~LegacyV5AgentTests' --logger 'trx;LogFileName=legacy-v5.trx' --results-directory work/plugin-lifecycle-results
```

结果：**8 通过、0 失败、0 跳过**，约 17 秒。覆盖 `none + applicationProtocol=http` 的旧调用方、新调用方与旧目标组合、双旧端 peer，以及旧目标 public HTTP；验证实际 POST、查询串和目标 Host 改写。另覆盖 server-peer 的旧调用方过滤、旧目标拒绝、双旧端过滤，以及 server-jump 旧目标拒绝，目标零连接，前后 `none` raw 映射及半关闭均正常，控制会话保持在线。测试由 `EnableLegacyV5AgentTests` 显式启用，默认 CI 不依赖本机发布资产。

v5 首轮八项在辅助 raw 断言中失败：测试在 `Shutdown(Send)` 后再次调用 `TcpClient.GetStream()`，被 .NET 判定为非连接 socket；四项 HTTP 主断言当时已通过。修正为半关闭前保存 Stream 后重跑八项全过，产品代码未改动。首轮及最终 TRX 分别保留为 `work/plugin-lifecycle-results/legacy-v5-initial.trx`、`legacy-v5.trx`。v6 仍仅有序列化和快照回归，不能称为真实发布二进制覆盖。

## 验证边界

本机检查到 Docker CLI，但 Docker Desktop Linux daemon 不可用；两套 WSL Ubuntu 均无 .NET SDK。本轮未安装工具链或改动远程环境，因此没有执行 Linux/macOS 原生测试，也未执行容量压测。未完成的管理员审批闭环、RDP 解析、SSH forwarding 不因生命周期修复而变为已支持。

TRX 和生成材料位于忽略目录，正文仅保留可复跑命令、断言和实际结果。最终提交前的全套 .NET、前端和文档检查由[总验证记录](plugin-system-remediation-verification.md)统一记录。

2026-11-04 补充回归：`PluginLifecycleTests` 增至 **23/23 通过**（重跑一致），新增客户端半关闭后反向响应完整排空（HTTP/HTTPS × 两种模式）、目标 RST 后 10 秒内结束且容量归零并写 aborted 审计、活跃连接后续审批期间 active=1/pending=0、管理 API 读回 decision/response 审计且无伪造批准事件。全量集成 156/156 及收口说明见 [TST-009 §7](plugin-system-remediation-verification.md#7-2026-11-04-复核收口)。
