# 插件系统改进验证

文档 ID：TST-009\
状态：Active\
版本：v1.1.0\
更新日期：2026-11-04

本记录追踪 [DEV-006](../development/plugin-system-improvement-plan.md) 的 B0–B6、W01–W17。审查基线为 `f19fe8f`，方案及相关文档提交为 `fe61f22`。原两份审查保持历史原文，不改写为修复通过证明。

## 1. 结果与范围

本轮已有 HTTP/HTTPS、SSH、生命周期、统计、配置和凭据管理的改进已实施。最终产品代码为 `ebe9bd8`，补充验收提交为 `09284d3`；2026-11-04 的复核收口见 §7，对应提交以本文件 Git 历史为准。本机完整单元 **358/358**、启用全部旧端开关的完整集成 **156/156** 通过，均无失败或跳过。156 项包含 134 项当前能力集成和 22 项真实旧版 Agent 二进制案例（v3/v4 为 14 项、v5 为 8 项）。

RDP 数据面、管理员批准 API/页面、凭据发放、桥应用及证书/策略编辑表单保持方案明确的未来范围。隐藏或拒绝不可用入口不等于实现这些能力，原 REQ-001O/DES-009 的 Draft 状态不变。

平台结论限于 Windows 本机；未推送、发布或部署，Linux/macOS 原生与远程 CI 未执行。Docker daemon 不可用，两套 WSL 均无 .NET SDK，未改远程环境。跨平台 CI 和容量压测属于后续验证，不能把本轮写成全部平台验收完成。

## 2. 分批提交

| 提交 | 对应批次 | 内容 |
|---|---|---|
| `fe61f22` | 方案提交 | 合并审查与改进方案 |
| `d1651f9` | B0 | 明确实施范围，采纳 ADR-0030–0033 的协议、信任、HTTP 与凭据恢复契约 |
| `fcfdcd8` | B5 基础 | 原子凭据回执、结果审计恢复、删除标记、不可用状态与故障测试 |
| `fce86ba` | B1–B3、B4 投影基础 | HTTP/SSH 安全门槛、v7 生命周期、配额、计量及路径模型，共同集成共享接口 |
| `ebe9bd8` | B4/B5 集成 | 管理 API、页面能力状态、相对路径/策略保存、删除重建和回执过期审计 |
| `09284d3` | B6 追加验收 | 审批中撤销及真实 v5 二进制兼容测试 |

B1–B3 在独立模块并行开发后共同提交，避免拆出缺少另一端协议或接口的不可交付版本。2026-11-04 的补充修复、文档同步与回归测试同属收口批次，内容见 §7；后续测试与最终文档提交以本文件 Git 历史为准；不会在文件中写入自引用提交 SHA。

## 3. 工作项关闭追踪

| 工作项 | 处置与验收证据 |
|---|---|
| W01 | HTTP/HTTPS 每个请求解析、策略和审计后才转发；pipeline、正文、歧义、升级负例，[TST-010](plugin-http-remediation.md) |
| W02 | SSH 目标公钥指纹先于目标凭据认证；未知/不匹配键零密码发送，[TST-011](plugin-ssh-remediation.md) |
| W03 | 目标连接和每个 SSH 动作前允许审计；后续动作及拒绝审计任务被观察，TST-011 |
| W04 | 分阶段期限、独立阻塞写、故障双向 abort 与正常半关闭；30 字节/30 秒反例、两模式 HTTP/HTTPS/SSH 长流通过；半关闭排空与目标故障双侧释放有真实进程回归（§7），[TST-012](plugin-lifecycle-remediation.md) |
| W05 | 首次等待持有 pending，业务就绪才转 active；后续审批有界租约且暂停业务 idle，不重复计算连接；审批等待期间 active=1/pending=0 有快照断言（§7），TST-012 |
| W06 | SSH schema 经真实文件入口生效、其他字段编辑保持；非法 forwarding 明确拒绝；跨应用字段、前缀条数与长度、审批期限边界经 Loader 拒绝（§7），TST-011/[TST-013](plugin-configuration-remediation.md) |
| W07 | 每 channel 独立状态、有限队列/任务/窗口、正确响应后传输和收尾；env 隔离、signal 归属、并发上限与并发关闭有回归（§7），TST-011 |
| W08 | HTTP 首批请求头及 SSH/peer 应用数据计入一次，历史追加列不改旧值；POST 正文只计一次与跨 read 流水线有回归（§7），TST-010/TST-012 |
| W09 | 两侧 v7/readiness、目标启用、地址和信任材料决定可用性；不可用入口返回结构化原因并显示具体文案，HTTP 入口显示与复制同一地址（§7），TST-013 |
| W10 | inspection 配置要求控制 TLS；文件/API 保存校验和原 none 路径回归；编辑器在 TLS 关闭时拒绝保存 jump/peer（§7），TST-013 |
| W11 | RDP 审计新建/重新启用拒绝，存量可停用/删除；新建 HTTPS 无表单材料时明确禁用，现有文件配置保持可编辑。启用中 RDP 停用与重新启用拒绝有编辑器回归（§7）。能力补齐仍延期，TST-013 |
| W12 | 原始相对路径保留并可搬迁；保留标签忽略大小写识别但只接受规范值；混合大小写与重复保留值经真实文件加载拒绝（§7），TST-013 |
| W13 | 持久化意图、秘密与回执原子替换、结果审计对账；操作 ID 回显、store 不可用 503、清理中 409 与后台对账有回归（§7），TST-013、[ADR-0031](../adr/0031-credential-mutation-and-deletion-recovery.md) |
| W14 | key/store 缺失、错误或损坏不会自动变空库；管理与 none TCP 仍运行，inspection 失败关闭；权限拒绝与坏 store 下 jump 拒绝有回归（§7），TST-013 |
| W15 | 同一配置写锁下处理凭据与删除，持久删除标记阻止复用，清理旧凭据/前门主机键；标记阻止同 ID 写入有回归（§7）。并发竞态由配置写锁结构保证，未单列并发用例 |
| W16 | 进入策略处理的 HTTP 请求有显式成功/拒绝/失败及可经管理 API 读回的 decision↔response 关联；SSH channel 退出/字节、连接关闭最后写入计量一致。Kestrel 消息框架直接拒绝的畸形请求不产生应用审计行（失败关闭）；未实现批准/发放事件不伪造，TST-010–012、§7 |
| W17 | 需求当前状态、设计能力边界、协议 v7、配置示例、运维恢复及索引同步；2026-11-04 补齐需求索引摘要与协议参考表 v7 消息（§7）；原报告与实际验证分开维护 |

## 4. 检查命令与结果

环境：Windows、.NET SDK 10.0.114。网络测试只监听 `127.0.0.1`，每个 Agent 独立目录。临时证书、口令、回执与 TRX 位于忽略目录，不进入提交。

| 检查 | 实际结果 |
|---|---|
| `dotnet restore RelayLink.slnx --configfile NuGet.Config` | 通过 |
| .NET 单元全量 | 358 通过，0 失败/跳过，约 6 秒 |
| .NET 集成全量（peer TLS + 真实 v3/v4/v5） | **156 通过，0 失败/跳过，约 5 分 1 秒**，包含原多服务端映射案例 |
| 2026-11-04 补充回归单元专项 | 27 项新增用例通过，全量 358/358；含 SSH/配置 18、HTTP 4、凭据 3、可用性原因 2 |
| 2026-11-04 补充回归集成专项 | 18 项新增用例通过，全量 156/156；HTTP/生命周期 10、凭据与失败关闭 8 |
| 追加验收前完整集成 | 112 通过，0 失败/跳过，约 4 分钟；原始 TRX 保留 |
| 审批中管理员断开/配置撤销新增专项 | 4 通过，0 失败/跳过；两种模式目标零连接、容量回收 |
| v3/v4 真实发布二进制专项 | 14 通过，未重编译旧 Agent |
| v5 rc.12 真实发布二进制专项 | 8 通过，包与全部 31 个文件校验，HTTP/none 成功及 inspection 拒绝 |
| AdminWeb typecheck / test / format | 全部通过；12 项页面测试 |
| AgentWeb build / format | 全部通过 |
| 网站构建与测试 | 构建 70 个静态页面；5 项测试通过 |
| 文档与仓库检查 | UTF-8 无 BOM/LF、相对文件链接、JSON 示例、`git diff --check` 通过 |

全量命令保留独立产物目录，避免并行批次争用输出：

```powershell
dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release -p:SkipAdminWebBuild=true --artifacts-path work/plugin-final-build --logger 'trx;LogFileName=unit-final.trx' --results-directory work/plugin-remediation-final/results
$env:RELAYLINK_LEGACY_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v3-compatibility/agent/RelayLink.Agent.dll').Path
$env:RELAYLINK_LEGACY_V4_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v4-compatibility/agent/RelayLink.Agent.dll').Path
$env:RELAYLINK_LEGACY_V5_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v5-compatibility/agent/RelayLink.Agent.dll').Path
dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:EnablePeerTlsTests=true -p:EnableLegacyAgentTests=true -p:EnableLegacyV4AgentTests=true -p:EnableLegacyV5AgentTests=true -p:SkipAdminWebBuild=true --artifacts-path work/plugin-final-all --logger 'trx;LogFileName=integration-all-final.trx' --results-directory work/plugin-remediation-final/results
pnpm --dir src/RelayLink.AdminWeb typecheck
pnpm --dir src/RelayLink.AdminWeb test
pnpm --dir src/RelayLink.AdminWeb format:check
pnpm --dir src/RelayLink.AgentWeb build
pnpm --dir src/RelayLink.AgentWeb format:check
node scripts/build-website.mjs
npm --prefix website test
```

真实旧端的启用开关、包哈希、命令与断言见 TST-012。v6 只执行协议/快照兼容回归，不称为真实发布二进制验证。TRX 是本机执行证据；正式测试源已提交，CI 不依赖 work 目录。

## 5. 失败与复核记录

保留旧实现的 HTTP 2 项失败、SSH 初始 7 项失败、配置 4 失败/1 通过，以及补充 HTTPS 证书用途反例 2 通过/1 失败，修复后均符合预期。它们与最终全量结果不混算。

原审查集成首次 89 通过、1 项多服务端映射超时，单独重跑通过；历史记录保留在 TST-008。本轮最终完整集成中的该案例通过，没有删除、跳过或放宽它。

实施过程中，首轮全量单元 294 通过/2 失败，原因分别是旧 jump 测试没配置控制 TLS、新 Windows 原子替换故障测试只接受 IOException 而系统实际返回 UnauthorizedAccessException。补齐正确前提和异常类别后通过。凭据 API 初轮 4 通过/2 失败来自新增测试误用 CSRF 头，修正为现有 `X-RelayLink-CSRF` 后通过。

独立复核追加修正了秘密库丢失后重启误当空库、过期回执同 ID 吞结果审计、HTTPS 自定义 CA 用途、HTTP 失败结果精度、迟到 ready 误断控制会话、peer 关闭最后字节计量，以及后续审批被 active idle 截断。各项均有正式回归；没有靠放宽放行门槛让测试通过。

撤销测试首次尝试停用仍被有效映射引用的目标，被原有引用校验按设计返回 400；改用受支持的配置修改触发撤销，四项通过。没有修改引用校验。SSH 响应与数据先后顺序、Windows HTTPS 临时证书材料和 v5 测试半关闭辅助代码的迭代失败，分别记录于专项文档。网站第一次构建曾在专项文档尚未落盘时因索引链接不存在而失败，文件齐备后构建和测试通过。

## 6. 保留边界

- 完整管理员审批、RDP 数据面、凭据发放、Bridge/intent/本机唤起和证书/策略表单未纳入本轮；不能将“等待后拒绝”写成审批闭环完成。
- SSH 命令前缀不是 shell 沙箱；HTTP WebSocket 只检查升级请求；跳板未指定应用仍保留既有原始转发，不能据此关闭原需求 A97 的未选应用条款。
- 缺少 SSH 目标指纹的旧配置仍可加载，但该通道不可用。旧版 key-only 秘密库须按 ADR-0031 人工确认处理，不能自动重置丢失数据。
- Linux/macOS 原生、远程 CI、容量压测和跨主机灾难恢复未执行；本机故障注入验证不等同于物理断电或生产容量演练。

## 7. 2026-11-04 复核收口

独立复核逐项对照代码、已提交测试与 TRX 后确认：W01–W16 的代码处置真实存在，无"声称已修但代码未改"的项；但发现 W17 仍有两处文档未同步、若干"已验收"声明缺少已提交测试，以及管理页对非材料类不可用原因显示笼统。同日完成修复与补充回归。

### 7.1 修复

- 文档：`docs/requirements/README.md` 的 REQ-001O 摘要由"仅有调研和设计"改为已实现范围与延期边界；`docs/design/technical-design.md` §7 标题改为当前版本，并补 v7 的 25 `InspectionActive`、26 `InspectionTargetReady`（与代码 `FrameType.cs`、DES-009、ADR-0030 一致）。
- 管理页：服务端为公共通道和互访入口返回结构化 `unavailableReason`（`ChannelAvailability.PublicChannelUnavailableReason` 与 `OutboundMappingUnavailableReason`），页面覆盖离线、未就绪、旧协议、控制 TLS 关闭、目标通道停用、地址未上报等原因；HTTP 入口显示与复制使用同一 `accessUrl`。
- 凭据 API：秘密库不可用时 PUT/DELETE 返回 503 而非 400；仅"清理中"冲突返回 409；GET 凭据在清理中同样返回 409，库不可用时保持 503。

### 7.2 新增回归（27 项单元 + 18 项集成）

| 工作项 | 新增证据 |
|---|---|
| W03/W07 | env 按 channel 隔离且启动后不重放；signal 只转发到所属 channel；每 channel 65 并发触发上限中止；双 channel 转发中取消后 relay 无残留任务 |
| W04 | HTTP/HTTPS × 两种模式客户端半关闭后反向响应完整排空并正常结束；目标 RST 后 10 秒内结束、容量归零、有 aborted 终止审计 |
| W05 | 活跃连接后续审批等待期间管理快照 active=1、pending=0 |
| W06/W10/W11/W12 | 策略跨应用字段、前缀条数与长度、控制字符、审批期限 0/301 经 Loader 拒绝，边界值接受；编辑器在控制 TLS 关闭时拒绝保存 jump/peer 且文件不变；启用中 RDP 可停用、重新启用拒绝；混合大小写与重复保留 `app:` 值经真实文件加载拒绝 |
| W08 | POST（Content-Length/chunked）头+正文精确计一次；pipeline 第二请求跨 read 分片仍逐请求检查 |
| W09 | 不可用映射/通道显示具体原因（停用、Agent 离线等），HTTP 入口显示并复制完整地址；AdminWeb 12 项通过 |
| W13/W15 | 操作 ID 回显与缺省生成；5 类坏 store 下 PUT/DELETE/GET 503；清理标记阻止写入且 GET 409；后台对账补记恰好一条、重试不重复 |
| W14 | 权限拒绝 fail closed（Windows ACL / Unix 权限）；坏 store 下 server-jump 真实连接被关闭、目标零连接、审计记录拒绝 |
| W16 | 管理 API 读回 decision/response 行、`request:N` 关联、拒绝 403，且不存在伪造的 `inspection_approval_granted` |

### 7.3 复跑命令与结果

```powershell
dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release -p:SkipAdminWebBuild=true --artifacts-path work/fix/final-unit --logger 'trx;LogFileName=unit-final-fix.trx' --results-directory work/fix/final-results
$env:RELAYLINK_LEGACY_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v3-compatibility/agent/RelayLink.Agent.dll').Path
$env:RELAYLINK_LEGACY_V4_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v4-compatibility/agent/RelayLink.Agent.dll').Path
$env:RELAYLINK_LEGACY_V5_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v5-compatibility/agent/RelayLink.Agent.dll').Path
dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:EnablePeerTlsTests=true -p:EnableLegacyAgentTests=true -p:EnableLegacyV4AgentTests=true -p:EnableLegacyV5AgentTests=true -p:SkipAdminWebBuild=true --artifacts-path work/fix/final-int --logger 'trx;LogFileName=integration-final-fix.trx' --results-directory work/fix/final-results
pnpm --dir src/RelayLink.AdminWeb typecheck
pnpm --dir src/RelayLink.AdminWeb test
pnpm --dir src/RelayLink.AdminWeb format:check
```

- 单元 **358/358**（0 失败/跳过，约 6 秒）；集成 **156/156**（0 失败/跳过，5 分 1 秒），其中 22 项真实旧端为 v3/v4 14、v5 8，故 134 项当前能力。新增用例首次执行即通过；开发期间出现的 CS0579 重复程序集特性由并发构建争用造成，串行复跑通过，未改产品代码规避。
- AdminWeb typecheck/12 项测试/format、AgentWeb build/format、网站构建 70 页与 5 项测试、`git diff --check` 均通过。
- TRX 保留在忽略目录 `work/fix/final-results/`；DOTNET 生成物不进入提交。

### 7.4 保留边界

- W15 的"检查后删除"并发竞态由同一配置写锁与删除标记结构保证，本轮补了标记阻止写入的回归，未构造真实并发竞态用例。
- 关闭审计与分钟历史仍共用同一应用字节计数来源，未新增跨层串联断言；两者一致性依赖同一计量回调。
- Kestrel 在消息框架层直接拒绝的畸形请求（重复 Host/Content-Length、超大头）不进入策略处理，因此没有 `inspection_decision`/`inspection_response` 行；行为仍失败关闭，W16 的措辞已限定为"进入策略处理的请求"。
- 拒绝审计失败时会话以中止方式拒绝，客户端不保证收到显式 SSH 失败响应；目标不会放行动作，审计任务由会话所有者观察。
- RDP 数据面、批准 API/页面、凭据发放、桥应用、证书/策略表单与跨平台验证的边界同 §1、§6，未因本轮补充而改变。
