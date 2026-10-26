# RelayLink 代码审查记录（2026-10-23）

文档 ID：TST-006\
状态：Active\
版本：v1.1.0\
更新日期：2026-10-23

本文记录对 `main` 分支提交 `29877a6` 的一次静态代码审查与基线检查结果。审查只提出问题与修改建议，**未修改任何产品代码、配置示例或测试**；本记录也不构成需求验收或发布证据。

## 1. 范围与方法

- 阅读 `README.md`、`docs/README.md`、需求与设计索引、`AGENTS.md`、`CONTRIBUTING.md`、文档规则和目录规划。
- 静态审查 `src/` 下全部 C# 项目（Server、Agent、Protocol、Transport、Logging）、两端 React 前端、`tests/`、`deploy/`、`scripts/` 与 `.github/workflows/`。
- 重点检查：认证与授权、TCP/UDP/HTTP 数据面、资源释放与背压、并发与取消、配置持久化顺序、秘密材料处理、日志与审计、前后端输入校验。
- 运行仓库文档中列出的本机构建与测试命令，记录实际结果；未部署运行实例，未执行 Windows/macOS 安装包与目标业务验收。

## 2. 结论摘要

| 级别 | 数量 | 说明 |
|---|---|---|
| 高 | 1 | 本机 Agent 管理面可被本机普通用户用于把服务账号重定向到任意服务端 |
| 中 | 5 | 测试环境可复现性、互访超时一致性、登录锁定可用性、配置下发错误语义、映射配置校验顺序 |
| 低 | 12 | 协议边界、可观测性、并发细节、文档与部署约束 |

未发现可直接远程利用的认证绕过、SQL 注入、路径穿越或明文秘密写入仓库。整体实现的安全基线较好：服务端按客户端独立密钥认证、数据隧道一次性令牌、互访 HMAC 证明与证书固定、HTTP 入口 Host/Origin 策略、管理面 CSRF、SQLite 参数化查询、秘密文件权限收紧等均有对应实现与测试。

## 3. 重要发现

### CR-01（高）本机 Agent 写接口无身份认证，服务账号可被本机普通用户重定向

**位置**

- `src/RelayLink.Agent/AgentLocalDashboard.cs:149`：`GET /api/v2/admin/session` 对任意能访问 loopback 的调用方无条件签发 HttpOnly 会话 Cookie 与 CSRF 令牌。
- `src/RelayLink.Agent/AgentLocalDashboard.cs:173`、`:218`、`:241`：新增、修改、停用/启用配置项只校验会话、CSRF、Host 与 Origin，不校验调用方身份。
- `src/RelayLink.Agent/AgentProcessConfiguration.cs:78`：导入校验允许 `useTls=false`，且 `serverHost` 可为任意主机。
- `docs/design/multi-server-agent.md` 第 6 节明确“无需管理密码或登录”，并说明“loopback 可被本机其他进程访问”。

**现象**

任意本机用户或进程都可以：

1. 读取 `/api/v2/admin/session` 取得会话 Cookie 和 CSRF 令牌；
2. 以 `POST /api/v2/admin/servers` 导入一个由攻击者构造的服务端配置（可关闭 TLS，或自带攻击者控制的 CA）；
3. 让以 `LocalService`、`LocalSystem` 或 root 运行的 Agent 主动连接攻击者的服务端；
4. 由攻击者下发任意 `ChannelSnapshot`，使 Agent 以服务账号身份连接 `127.0.0.1` 或内网目标并双向转发字节。

**影响**

该路径不依赖配置文件权限：即使 Agent 配置目录仅服务账号可读，普通用户仍可通过 loopback HTTP 修改运行时配置。后果是从“本机普通用户”提升到“服务账号可访问的网络位置和内网服务”，在多用户 Windows、启用交互登录的服务器或共享 Linux 主机上尤其明显。`useTls=false` 使攻击者无需提供可信证书。

**建议**

- 把“本机页面等同于本机管理员”的假设写成明确的威胁模型和部署前提（仅单用户、可信主机），并在安装指南中标注；当前文档只描述了 loopback 可被访问，没有分析服务权限后果。
- 或者为写接口增加真正的本机身份边界：Windows 命名管道 ACL、Unix domain socket `0600`，或一次性本地管理令牌（安装时生成、仅管理员可读）；HTTP 仅作为只读状态页。
- 在导入时限制 `serverHost` 与 `useTls` 的组合，至少对“无 TLS + 任意主机”给出显式确认，并把本机配置变更写入不可静默关闭的审计记录。
- 无论采用哪种方案，都应避免以高权限账户运行 Agent；安装文档应给出最小权限服务账号建议。

**处置（2026-10-23）**：已修复。未增加密码登录。写接口要求配置目录中的 `local-management.token`（Unix `0600`，Windows 为当前身份、SYSTEM 与 Administrators），请求头为 `X-RelayLink-Local-Token`。只读状态接口仍然匿名。`useTls=false` 必须带上 `confirmInsecureTransport=CONNECT_WITHOUT_TLS`。成功的本机配置变更写入 Agent 文件日志，不记录密钥或令牌。安装指南写明单用户或管理员操作的前提，并建议不要用 LocalSystem 或 root。见 [ADR-0027](../adr/0027-agent-local-management-token.md)。

### CR-02（中）Windows 非标准 ACL 环境下 39 项测试失败，验证命令不可复现

**位置**

- `src/RelayLink.Server/Runtime/SecretFilePermissions.cs:191`：`EnsureDatabaseDirectory` 在目录已存在时调用 `IsUnsafeDatabaseDirectory`，Windows 下任一非服务/管理员 SID 对文件夹或子文件拥有读或写权限即拒绝打开数据库。
- `tests/RelayLink.UnitTests/TrafficHistoryTests.cs:15`、`:71`；`tests/RelayLink.IntegrationTests/PeerConnectionTests.cs:779`；`tests/RelayLink.IntegrationTests/MultiServerAgentTests.cs` 等夹具使用 `Directory.CreateDirectory(Path.GetTempPath()/...)`，未收紧目录 ACL。

**现象**

在当前 Windows 审查环境执行文档中的命令：

- `dotnet test tests/RelayLink.UnitTests/...`：**179 通过、1 失败、共 180**；失败项 `TrafficHistoryTests.Legacy_jsonl_is_imported_once_without_deleting_source`，错误为 `SECURITY: Refusing to open database ... because ... is a shared or temporary directory`。
- `dotnet test tests/RelayLink.IntegrationTests/... -p:EnablePeerTlsTests=true`：**31 通过、38 失败、2 跳过、共 71**；失败均为测试临时目录中的 Server 无法通过审计/历史数据库的安全检查而退出。

当前机器的 `%TEMP%` 与 `%LOCALAPPDATA%` 继承了一个额外的沙箱组读取/修改授权，因此触发了该保护。标准 CI runner 的 ACL 可能不同，但测试夹具依赖外部 ACL 才能通过，说明夹具本身不具备可移植性；任何授予其他账户读取或修改权限的开发机、容器或企业环境都会复现。

**建议**

- 在测试夹具中显式创建私有临时目录（Windows 设置仅当前用户 + SYSTEM/Administrators 的专用 DACL，Unix 设置 `0700`），或提供共享的 `TestDirectory` 辅助类并供所有夹具使用。
- 不为测试增加“跳过安全检查”的开关；安全检查本身应保持严格。
- 在 Windows CI 中加入一条针对该夹具的回归用例，避免只在 Linux 或特定 runner 上通过。

**处置（2026-10-23）**：已修复。`tests/Support/TestDirectory.cs` 在创建后立即收紧 ACL（Windows 仅当前用户、SYSTEM、Administrators；Unix `0700`）。服务端集成夹具和会打开 SQLite 的单元夹具改用该目录。`PrivateTestDirectoryTests` 断言私有目录可以打开数据库，并且额外的非管理员授权（Windows Authenticated Users，Unix other-write）仍被拒绝。没有增加跳过安全检查的开关。

### CR-03（中）互访建立硬编码 20 秒截止时间，与可配置超时不一致

**位置**

- `src/RelayLink.Agent/PeerSessionCoordinator.cs:169`（调用方）、`:231`（目标方）：`deadline.CancelAfter(TimeSpan.FromSeconds(20))`。
- `src/RelayLink.Server/Configuration/ServerConfiguration.cs`：`OpenTimeoutSeconds` 只有下限校验，无上限；`ChannelConfiguration.TargetConnectTimeoutSeconds` 允许 1–300 秒。
- `src/RelayLink.Server/Runtime/ProxyListenerService.cs`：服务端使用 `limits.openTimeoutSeconds` 等待隧道和目标就绪。

**现象**

互访调用方从 loopback 接入开始，最多等待 20 秒完成“请求授权 → 数据绑定 → 目标连接 → 证明交换”。若目标通道配置了大于 20 秒的 `targetConnectTimeoutSeconds`，或服务端配置了更大的 `openTimeoutSeconds`，调用方会在目标仍在连接时先行超时；目标方同样在 20 秒处取消，导致慢 DNS、慢握手或高延迟链路上的互访失败，而普通代理通道不受此限制。

**建议**

- 由服务端在 `PeerOpenGranted`/`PeerOpen` 中下发剩余 `openTimeoutSeconds`（或新增明确的互访建立超时字段），两端按该值设置截止时间。
- 若暂不下发，至少把 20 秒改为 `max(server openTimeoutSeconds, channel targetConnectTimeoutSeconds)` 可配置值，并在文档中写明互访建立时限。
- 增加“目标连接耗时接近配置上限”的集成测试，避免默认值掩盖问题。

**处置（2026-10-23）**：已修复。服务端在 `PeerOpen` 和 `PeerOpenGranted` 中下发 `setupTimeoutMilliseconds = (openTimeoutSeconds + targetConnectTimeoutSeconds) * 1000`。访问方收到授权后按该值重置截止时间；被访问方取下发值与本通道 `targetConnectTimeoutSeconds` 的较大者。v3/v4 帧也保留该字段，避免旧协议路径把超过 20 秒的预算退回固定 20 秒；已发布的旧 Agent 仍忽略未知字段并使用内置 20 秒。单元测试把 30 秒打开超时加 40 秒目标连接超时经 v3、v4 和当前版本封帧后还原，断言两端建立预算为 70 秒。启用互访 TLS 的集成测试断言授权帧携带 20 秒打开超时加 5 秒目标连接超时。

### CR-04（中）管理登录锁定仅按来源 IP 计数

**位置**

- `src/RelayLink.Server/Runtime/AdminLoginGuard.cs:178`、`:180`：键仅为 `ip:<remote>`，`username` 参数被丢弃。
- `src/RelayLink.Server/Program.cs:150`：登录接口使用 `Connection.RemoteIpAddress`。

**现象**

- 多个管理员或所有用户经同一 NAT、负载均衡或反向代理访问管理面时，一个攻击者可以耗尽该来源 IP 的尝试次数，使合法管理员在锁定窗口内无法登录。
- `MaxTrackedStates = 10_000` 满后，新来源在剪枝前会被直接拒绝（返回 429），大规模分布式尝试可导致合法新来源登录失败。

**建议**

- 锁定键改为 `(来源 IP, 用户名)` 或增加用户名维度，同时保留对同一 IP 的宽松总量限制，减少共享出口的连带影响。
- 若部署在可信反向代理之后，提供显式的受信任代理配置（或明确不支持代理并写入文档），不要直接信任客户端提供的转发头。
- 对状态表耗尽返回明确的 503/Retry-After，并增加计数器/日志，便于判断是攻击还是容量问题。

**处置（2026-10-23）**：已修复。锁定键为直连来源地址加用户名，阈值仍是 5 次；同一地址另有 20 次的总量限制。未过期历史仍不淘汰。状态表占满时登录返回 503、`Retry-After` 和警告日志，锁定本身仍是 429。不读取转发头。

### CR-05（中）配置已持久化但下发失败时向管理员返回 500

**位置**

- `src/RelayLink.Server/Runtime/ChannelConfigurationEditor.cs:98`：写入客户端 JSON；`:101` 应用监听；`:120`、`:129` 向在线 Agent 下发配置。
- `src/RelayLink.Server/Runtime/ClientConfigurationEditor.cs:126` 附近同样在持久化后调用 `SendConfigurationUpdateAsync`。
- `src/RelayLink.Server/Runtime/SessionRegistry.cs:80`：`SendConfigurationUpdateAsync` 失败时 `Close()` 会话并抛出。

**现象**

文件写入和 `runtime.ReplaceConfiguration` 成功后，`SendConfigurationUpdateAsync` 可能因待确认队列溢出、控制连接写超时或会话关闭而抛出 `ProtocolException`/`IOException`。此时配置已经持久化且监听已更新，但 API 未捕获这些异常，返回 500；管理员会认为保存失败并重试。会话同时被关闭，Agent 掉线重连。

**建议**

- 区分“保存失败”和“已保存但下发失败”：后者返回 200/207 并在响应中携带 `pushedToAgent=false` 与原因，不要回滚已落盘配置。
- 不要因为一次配置下发失败就关闭整个控制会话；待确认队列溢出可以保留会话并等待重连后按最新快照重新下发。
- 为队列溢出、写超时和会话关闭分别增加测试，断言响应语义与文件状态。

**处置（2026-10-23）**：已修复。配置在监听切换成功后保持落盘。下发队列满、控制连接写超时或会话已关闭时，编辑器返回已保存且 `pushedToAgent=false` 与原因，管理接口使用 200/201 而不是 500。待确认队列满或写入开始前会话已关闭时，`SendConfigurationUpdateAsync` 不关闭控制会话。帧写入或刷新已经开始后失败则关闭会话，避免半帧留下的流错位。队列溢出保持会话，部分写入关闭会话，两种情况都有单元测试；编辑器测试仍检查文件已保存。

### CR-06（中）服务端下发映射在完整校验前即修改监听状态

**位置**

- `src/RelayLink.Agent/PeerSessionCoordinator.cs:44`：`ApplyConfiguration` 先停止并移除不再需要的入口，再遍历新映射。
- `src/RelayLink.Agent/PeerSessionCoordinator.cs:50`：在创建监听前才校验 `LocalAddress` 与 Base64 密钥长度；无效 Base64 会抛 `FormatException`，无效地址抛 `AgentPermanentException`。

**现象**

一份包含无效映射的配置会先关闭已有入口，再在创建新入口途中抛出。异常向上传播后本次控制会话重建；若服务端持续下发同一无效配置，Agent 会反复断开重连。虽然服务端配置校验通常能阻止该输入，但 Agent 侧不应在验证完成前改变运行状态。

**建议**

- 先对所有 `OutboundMappings` 做纯校验（地址、密钥 Base64 与长度、协议组合、映射 ID 唯一性），全部通过后再停止旧入口、创建新入口。
- 将无效快照转换为明确的拒绝原因并记录，避免以通用重连循环处理。

**处置（2026-10-23）**：已修复。`ApplyConfiguration` 先纯校验全部出站映射，失败时记录原因并抛出 `AgentPermanentException`，不停止已有入口。重复 ID、非法 Base64 和 HTTP/UDP 组合有单元测试，并确认原 loopback 端口在拒绝后仍可连接。

## 4. 次要发现

### CR-07（低）`/api/v1/overview` 的 `channelsAvailable` 与仪表盘口径不一致

- 位置：`src/RelayLink.Server/Program.cs:230` 与 `:451`、`:515`。
- 现象：概览接口只判断 HTTP 就绪和在线状态，未像仪表盘那样校验 `transportProtocol == "tcp" || ProtocolVersion >= 4`。连接了 v3 Agent 且配置了 UDP 通道时，概览会把该通道计为可用。
- 建议：抽取统一的 `IsChannelAvailable` 帮助函数，供两个接口和 `GetMappingStatus` 共用，并增加 v3 + UDP 通道的测试。

**处置（2026-10-23）**：已修复。概览、仪表盘、通道列表和映射状态共用 `ChannelAvailability`。v3 Agent 的 UDP 通道和 UDP 映射不计为可用；TCP 与 HTTP 就绪规则保持不变。

### CR-08（低）`FrameWriter` 未校验 DATA 帧长度边界

- 位置：`src/RelayLink.Transport/FrameWriter.cs:15`。
- 现象：写入端只拒绝超过 `MaxControlPayloadLength`（256 KiB）的载荷，而读取端要求 DATA 为 1–32 KiB。直接写入空 DATA 或大于 32 KiB 的 DATA 会生成对端必然拒绝的帧。当前所有调用方都按 32 KiB 分块，因此是防御性问题。
- 建议：在 `FrameWriter` 中复用与 `FrameReader.ValidateLength` 相同的类型/长度校验，并对空 DATA 明确拒绝。

**处置（2026-10-23）**：已修复。`FrameConstraints` 在写出任何字节前执行与读取端相同的长度规则，拒绝空 DATA、超过 32 KiB 的 DATA 和非空 FIN。

### CR-09（低）部分帧超时是总时长而非空闲时间

- 位置：`src/RelayLink.Transport/FrameReader.cs:86-107`。
- 现象：一旦发生“未读满”的半帧，就启动一次固定的 120 秒计时，之后即使持续有进展也不会重置。慢速但稳定的链路可能在传输大载荷时被提前中止。
- 建议：把计时改为“无进展超时”（每次成功读取后重置），或按载荷长度计算上限。

### CR-10（低）配置读取的部分 IO 异常未转换为配置错误

- 位置：`src/RelayLink.Server/Configuration/ConfigurationLoader.cs:78-93`。
- 现象：`Deserialize` 只捕获 `JsonException` 和 `IOException`。`UnauthorizedAccessException`、`SecurityException` 等会越过 `Program.cs` 的 `catch (ConfigurationException)`，以未处理异常方式退出，而不是返回退出码 2 和可读错误。
- 建议：把这些异常转换为 `ConfigurationException`，并为不可读文件/目录补充启动测试。

**处置（2026-10-23）**：已修复。配置读取把 `UnauthorizedAccessException`、`SecurityException` 和同类 I/O 访问失败转为 `ConfigurationException` 并保留内部异常。启动和 `--check-config` 因此以退出码 2 与可读原因结束。

### CR-11（低）会话心跳字段非易失，重复注册的 CTS 未释放

- 位置：`src/RelayLink.Server/Runtime/SessionRegistry.cs:41-42`、`:114-115`、`:13`。
- 现象：`LastHeartbeatUtc`/`LastHeartbeatRtt` 是普通自动属性，分别由控制读循环写入、心跳循环读取；`DateTimeOffset` 和 `TimeSpan?` 都不是单字长类型，理论上可能出现撕裂读。`TryRegister` 在重复注册时创建了新的 `Session`（含 `CancellationTokenSource`）却不调用 `Close`/释放，交给 GC 处理。
- 建议：用 `Volatile`/`Interlocked` 或不可变快照发布心跳状态；重复注册失败时显式取消并释放临时会话。

**处置（2026-10-23）**：已修复。心跳时间与 RTT 通过一个不可变快照一次发布。重复注册失败时取消并释放未被采用的会话。

### CR-12（低）Agent 未校验下发快照的 `clientId` 属于本配置项

- 位置：`src/RelayLink.Agent/ControlSessionWorker.cs:421`。
- 现象：`ValidateSnapshot` 只检查 `ClientId` 非空，没有与本 profile 的 `configuration.ClientId` 比较。正常服务端不会发错，但这是成本很低的纵深防御。
- 建议：增加一致性校验，不匹配时按永久配置错误处理。

**处置（2026-10-23）**：已修复。初始注册和配置更新都把快照 `clientId` 与当前 profile 的 `clientId` 做固定比较。哈希正确但身份不一致时抛出 `AgentPermanentException`。

### CR-13（低）代理监听异常退出时没有日志

- 位置：`src/RelayLink.Server/Runtime/ProxyListenerService.cs:64`、`:86` 使用 `_ = AcceptLoopAsync(...)`。
- 现象：`AcceptLoopAsync` 只在停止时捕获 `SocketException`。非停止状态下的异常会让任务静默结束，监听不再接受新连接且没有错误日志；调用方无法从 `/health/ready` 或状态接口发现。
- 建议：为任务附加 continuation 记录异常并触发就绪状态降级，或让监听循环在可恢复错误下继续。

**处置（2026-10-23）**：已修复。每个监听任务被跟踪。可恢复的 `SocketException` 最多重试 3 次；仍在服务未停止时退出则记录客户端与通道，并把 `/health/ready` 降为 503。失败的监听在下一次成功的配置应用中按原地址重建，就绪随之恢复。

### CR-14（低）UDP 监听处理器异常会泄漏会话或终止监听

- 位置：`src/RelayLink.Transport/DatagramRelay.cs:162`。
- 现象：`handle(session)` 在 `sessions.Add` 的参数求值中同步抛出时，`DatagramSession` 会泄漏且异常直接结束 `RunAsync`；异步任务失败虽被观察（`_ = Task.Exception`），但不会记录错误。
- 建议：先创建并加入会话，再在受保护的 try 中调用处理器；失败时记录并清理该会话，不影响其他来源。

**处置（2026-10-23）**：已修复。监听器先登记会话再调用处理器。同步异常和已完成任务的失败都会清理该会话，并通过回调交给服务端或 Agent 记录。其他来源继续服务。

### CR-15（低）匿名只读接口暴露拓扑细节，依赖部署侧限制

- 位置：`src/RelayLink.Server/Program.cs` 的 `/api/v1/overview`、`/api/v1/dashboard/snapshot`、`/api/v1/clients/**`、`/api/v1/history`。
- 现象：这些接口按设计无需认证，返回客户端 ID、通道 ID、目标主机/端口、安全组 ID、映射目标和活动连接来源地址。README 已说明匿名只读，但一旦管理端口被错误暴露，会形成内网拓扑信息泄露。
- 建议：在部署文档中把“管理端口必须仅对受信来源开放”写成硬性要求（当前已有相关说明，可更醒目）；可考虑为未登录调用方隐藏目标地址和来源 IP，只保留聚合计数。

### CR-16（低）创建映射时使用内存快照而非刚加载的磁盘配置

- 位置：`src/RelayLink.Server/Runtime/ChannelConfigurationEditor.cs:74`：`ToMapping(mappingId, request, runtime.Configuration)`。
- 现象：`ChangeAsync` 已经重新从磁盘加载了 `current`，但创建映射的目标索引/指纹来自 `runtime.Configuration`。如果配置文件被进程外修改而运行时尚未刷新，映射可能固定旧的访问密钥或证书指纹。
- 建议：把完整的 `LoadedConfiguration` 传给变更委托，使用 `current` 计算映射。

**处置（2026-10-23）**：已修复。`ChangeAsync` 把刚从磁盘加载的配置交给变更委托，创建映射只从这份快照派生访问密钥和证书指纹。

### CR-17（低）UDP 会话队列满时静默丢弃，无指标

- 位置：`src/RelayLink.Transport/DatagramRelay.cs:109`，`QueueCapacity = 32`。
- 现象：队列满时 `TryWrite` 返回 false，最新报文被丢弃，但没有计数或日志，流量统计只反映已转发字节，排障时无法区分“应用没发”和“RelayLink 丢弃”。
- 建议：增加丢弃计数并暴露到通道指标/诊断日志，或在文档中明确该丢弃策略。

**处置（2026-10-23）**：已修复可观测性。满队列仍丢弃最新报文。服务端把每次丢弃计入通道 `udpDroppedTotal`，通道列表和仪表盘快照返回该计数。Agent 本机入口最多每 30 秒记一条累计警告。

### CR-18（低）互访请求 ID 未去重，后台任务异常未全部捕获

- 位置：`src/RelayLink.Server/Runtime/PeerRelayRegistry.cs:17`、`:177`。
- 现象：相同 `RequestId` 的重复 `PeerOpenRequest` 会创建多个 `PeerRelay` 并占用配额；`RunAsync` 只捕获 `OperationCanceledException`、`IOException`、`ProtocolException`、`AuditUnavailableException`，其他异常会让 fire-and-forget 任务处于故障状态（`Cleanup` 仍会执行）。
- 建议：跟踪近期 `RequestId` 并拒绝重复请求；把 `RunAsync` 的最终异常统一记录，避免未观察任务异常。

**处置（2026-10-23）**：已修复。同一调用会话在两分钟窗口内拒绝重复 `RequestId`，不新增中继或配额。`RunAsync` 的其他异常记入错误日志，并仍执行清理。

## 5. 已确认的设计边界（不作为缺陷）

- 普通代理数据通道在绑定后为明文原始 TCP；README、需求和 ADR-0008 已明确，敏感业务依赖目标自身 TLS 或受保护链路。
- 控制面默认 TLS，但允许 `tlsEnabled=false`；此时互访和 HTTP 被配置校验禁止，符合 ADR-0005/0007 的取舍。
- 匿名只读管理页是产品能力；本次仅提示其部署约束，不要求改为强制登录。
- Agent 本机页“无需管理密码”是已记录的设计决定；CR-01 要求补充后果分析和本机身份边界选项，而不是简单推翻该决定。
- HTTP 公网入口在目标 Agent 终止 TLS、限制 Host/Origin、强制互访内层 TLS，均有实现与测试；本次未发现策略绕过。

## 6. 实际执行的检查

环境：Windows，.NET SDK 10.0.113，Node.js 24.14.0，pnpm 10.30.3（CI 使用 11.19.0），npm 11.9.0。

| 检查 | 命令 | 结果 |
|---|---|---|
| 还原 | `dotnet restore RelayLink.slnx --configfile NuGet.Config` | 通过 |
| 构建 | `dotnet build RelayLink.slnx -c Release --no-restore -p:SkipAdminWebBuild=true` | 通过，0 警告、0 错误 |
| 单元测试 | `dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj -c Release --no-build -p:SkipAdminWebBuild=true` | **179/180 通过**；1 项失败见 CR-02 |
| 集成测试 | `dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj -c Release --no-build -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true` | **31/71 通过，2 跳过，38 失败**；失败均与 CR-02 的临时目录安全检查有关 |
| 管理前端 | `pnpm --dir src/RelayLink.AdminWeb typecheck`、`test`、`format:check` | 通过；组件测试 9/9 |
| Agent 前端 | `pnpm --dir src/RelayLink.AgentWeb build`、`format:check` | 通过 |
| 网站 | `npm --prefix website ci`、`node scripts/build-website.mjs`、`npm --prefix website test` | 通过；构建 68 个静态页面，测试 5/5 |

未执行：真实 Server/Agent 多进程验收、Docker 构建、Windows/macOS 安装包、目标 SQL Server/UDP/HTTP 业务验收、跨主机 NAT 场景。上述项目仍以 `docs/testing/verification-status.md` 和各验证记录为准。

## 7. 建议处理顺序

1. 先决定 CR-01：记录威胁模型并给出本机管理面的身份边界方案；这是唯一涉及权限提升的发现。
2. 修复 CR-02，使文档中的 Windows 测试命令在常见 ACL 环境下可重复通过。
3. 统一 CR-03 的互访超时来源，补充慢目标测试。
4. 处理 CR-04、CR-05 的管理面可用性与错误语义，再清理 CR-06 的配置应用顺序。
5. 低风险项可按模块分批处理；CR-07、CR-13、CR-17 建议与下一次可观测性改动一起完成。

## 8. 声明

本次审查未修改源代码、测试、配置示例或部署资源；提交仅包含本记录与文档索引。发现项的严重级别基于当前代码与文档中的部署假设，实际风险取决于管理面暴露范围、Agent 运行账户和主机多用户情况。修复时请按仓库规则同步更新相关需求、设计、测试和验证记录。
