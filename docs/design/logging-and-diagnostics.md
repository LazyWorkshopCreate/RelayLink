# RelayLink 日志与诊断技术设计

文档 ID：DES-005\
状态：Accepted\
版本：v1.0.0\
更新日期：2026-09-30\
配套需求：[日志与诊断需求](../requirements/2026-09-30-logging-and-diagnostics.md)

## 1. 设计边界与组件

采用 Serilog 及官方 File sink，统一 Server/Agent 的结构化日志、按级别分流和轮转保留策略。本文记录采用方案，Accepted 不表示全部目标环境验收已完成；证据和未完成项见[验证状态](../testing/verification-status.md)。

| 组件 | 实现入口 | 职责 |
|---|---|---|
| 共享日志库 | [RelayLink.Logging/FileLogging.cs](../../src/RelayLink.Logging/FileLogging.cs) | 日志级别、JSONL 格式、双输出、大小/日期轮转与保留 |
| Agent 宿主 | [Program.cs](../../src/RelayLink.Agent/Program.cs)、[AgentFileLogging.cs](../../src/RelayLink.Agent/AgentFileLogging.cs) | 解析配置路径、启动日志、接入 ILogger、关闭刷新 |
| Agent 本机 Web | [AgentLocalDashboard.cs](../../src/RelayLink.Agent/AgentLocalDashboard.cs) | 复用进程 ILoggerFactory；保存失败写 Error，不记录请求正文 |
| Server 宿主 | [Program.cs](../../src/RelayLink.Server/Program.cs) | 选择日志目录、接入 ILogger、启动及宿主失败落盘 |
| 控制拒绝 | [ControlRejectionReason.cs](../../src/RelayLink.Protocol/ControlRejectionReason.cs)、[TunnelAcceptorService.cs](../../src/RelayLink.Server/Runtime/TunnelAcceptorService.cs)、[ControlSessionWorker.cs](../../src/RelayLink.Agent/ControlSessionWorker.cs) | 固定原因、两端日志、错误帧与拒绝审计 |
| 业务诊断 | [AgentDiagnosticLog.cs](../../src/RelayLink.Agent/AgentDiagnosticLog.cs) | 每配置项的阶段、字节及耗时 JSONL |

共享日志库不依赖 Protocol、Server 或 Agent；Protocol 不依赖 Serilog、文件 I/O 或宿主。业务代码继续使用 `ILogger`。SQLite 审计及分钟流量历史使用既有独立服务，不改成文件日志。

## 2. 日志格式、级别及输出

运行文件使用 UTF-8 无 BOM JSONL，每行一个 Serilog `JsonFormatter(renderMessage: true)` 事件。标准字段为 `Timestamp`（含时区）、`Level`、`MessageTemplate`、`RenderedMessage`、可选的 `Exception` 与 `Properties`。`Properties.SourceContext` 表示日志类别，scope 中的 `profileId`、`clientId`、会话/连接标识按现有字段名保留。进程全局事件不必包含服务端归属。

`Microsoft.Extensions.Logging` 的 Information/Warning/Error/Critical 分别对应 Serilog Information/Warning/Error/Fatal。默认最低 Information，Microsoft 框架最低 Warning，`Microsoft.Hosting.Lifetime` 保留 Information。默认不启用 Debug/Trace；不开放新的动态级别或保留配置。

| 组件 / 文件 | 级别过滤 | 轮转 | 清理 |
|---|---|---|---|
| Agent `agent-YYYYMMDD[_NNN].jsonl` | Information ≤ level < Error | 每个自然日或 10 MiB | 保留 14 天 |
| Agent `error-YYYYMMDD[_NNN].jsonl` | Error/Fatal | 同上 | 不按时间或数量删除 |
| Server `server-YYYYMMDD[_NNN].jsonl` | Information ≤ level < Error | 同上 | 保留 14 天 |
| Server `server-error-YYYYMMDD[_NNN].jsonl` | Error/Fatal | 同上 | 不按时间或数量删除 |

两个子 logger 的过滤互斥，错误不会重复写入普通日志。两条输出均设置 `retainedFileCountLimit: null`；普通输出另设 14 天 `retainedFileTimeLimit`，错误输出不设时间上限，避免默认文件数量上限删掉旧错误。日期按运行主机本地时间划分；过期清理在 sink 打开/轮转时触发，服务停止时无清理任务。单条事件可使文件略超 10 MiB，下一条触发大小轮转，阈值不是截断事件的硬限制。

普通连接失败、注册拒绝与重连沿用 Warning；配置校验/保存失败和宿主意外退出写 Error/Fatal。业务诊断文件的 `errorType` 是阶段元数据，不能据此将其视为永久保留的 Error 运行日志。

## 3. 路径、启动与部署

| 场景 | 运行日志目录 |
|---|---|
| Agent | 配置文件所在目录的 `logs/`；Windows 标准安装为 `C:\ProgramData\RelayLink\Agent\logs` |
| Server 默认 | 配置文件所在目录的 `logs/` |
| Server 自定义 | `--log-directory <目录>`；相对路径按启动工作目录解析 |
| Linux Server systemd / Docker | 模板指定 `/var/lib/relaylink/logs` |
| Docker 宿主 | 默认 `.local/docker-server/data/logs`，随持久化数据卷保留 |

日志目录由服务账号写入，Windows 继承父目录 ACL，Unix 设为 `0700`。Server 使用独立目录时须事先授予服务账号权限；Linux 原生部署步骤见[部署说明](../../deploy/linux/README.md)。安装程序目录不用于日志。Agent 升级、重新配置和卸载保留 `logs/`；Server 升级/重启保留指定数据目录，人工清理和备份由运维负责。

正常运行在读取配置前创建日志实例，使配置校验失败也能记录；`--check-config` 及 Agent 指纹查询模式不创建运行日志。调用 `AddWindowsService` / `UseWindowsService` 后清除默认提供器，再通过 `AddSerilog` 接入 `ILogger`，避免 Windows Event Log 自动注册事件源。Agent Web 宿主复用外部 `ILoggerFactory`，不单独配置控制台日志。

使用 File sink 默认同步、非缓冲写入，每个事件刷新；不引入异步日志队列。宿主异常由顶层捕获并记录 Fatal，正常退出通过 logger 的 Dispose 关闭刷新。不承诺强制结束进程、断电或磁盘故障下最后记录已持久化；逐事件刷新不等同于每条调用磁盘 `fsync`。SelfLog 输出到标准错误用于诊断日志库失败；日志目录本身不可创建时，也只能依赖进程标准错误。`docker compose logs` / `journalctl` 主要用于这些启动诊断，正常运行事件应查看文件。

## 4. 控制拒绝协议与诊断

`ErrorMessage` 保留原有 `code` 数值，增加可选数值 `reason`；未设置时省略。线协议保持 v3，既有错误码不重编号。错误示例（无真实部署数据）：

```json
{"code":0,"reason":5}
```

此例表示 `AuthFailed / IdentityMismatch`。原因定义如下；客户端可读说明来自本地 `Describe()`，不使用远端自由文本。

| reason | 标识 | 含义 | 对应 code |
|---|---|---|---|
| 1 | ClientNotFound | 客户端 ID 未注册 | AuthFailed |
| 2 | ClientDisabled | 客户端已停用 | AuthFailed |
| 3 | InvalidSecret | 密钥格式或值无效 | AuthFailed |
| 4 | IdentityInvalid | 身份指纹缺失或格式无效 | AuthFailed |
| 5 | IdentityMismatch | 与已固定身份不匹配 | AuthFailed |
| 6 | DuplicateSession | 同一 ID 已有控制会话 | DuplicateSession |
| 7 | InvalidFirstFrame | 首帧不是 Register | ProtocolError |
| 8 | ConfigurationMismatch | 确认的 session/摘要不一致 | ConfigurationMismatch |
| 9 | ClientConfigurationChanged | 注册期间配置变化 | AuthFailed |

服务端先校验客户端与密钥，再检查身份和会话占用；原因细分不绕过认证、身份固定或容量限制。服务端运行日志包含固定原因、错误码、经过控制字符过滤及长度限制的客户端 ID、来源 IP；审计记录保留 `agent_session_rejected`。审计 reasonCode 沿用小写形式，如 `identity_mismatch`、`duplicate_session`；InvalidSecret 沿用 `authentication_failed`，新增不存在/停用分别使用 `client_not_found` / `client_disabled`。

Agent 在注册响应、配置确认响应和在线控制读循环中解析错误帧。`AgentPermanentException` 保存 ErrorCode 和 RejectionReason；Warning 事件记录两者，异常消息包含阶段及本地固定说明，外层 scope 保留 `profileId`、`clientId`。明确拒绝使用原有慢速重试，另一配置项继续运行。

兼容与失败规则：

- 新 Agent 收到旧帧时保留 code，原因显示未提供，并提示检查服务端审计；旧客户端按既有 JSON 反序列化规则忽略新增字段。
- 未知原因转为未识别原因；未知 code 回退到 ProtocolError；详情解析失败产生固定错误，不附带远端载荷或解析异常正文。
- 不回显提交或期望的密钥、指纹，也不发送本地异常堆栈。
- TLS 尚未建立、协议帧头无效或认证前配额饱和时可能无法发送错误帧。配额饱和直接关闭连接，服务端记录 CapacityExceeded 与 `unauthenticated_connection_limit`；Agent 沿用本地网络/TLS/协议异常。

具体运维操作见[注册被拒绝排障](../operations/installation-and-usage.md#91-注册被拒绝)。

## 5. 业务诊断与敏感信息

Agent 业务诊断位于 `state/<profileId>/relaylink-diagnostics.jsonl`，包含 UTC 时间、服务端归属、客户端/连接/通道 ID、阶段、双向字节、耗时、空闲时长和错误类型。达到 4 MiB 时移到 `.1`，覆盖之前的一个历史文件。写入受单实例 semaphore 保护；I/O 与权限异常被抑制，避免诊断中断业务连接。它不包含控制注册失败，也不提供错误永久归档。

运行日志、错误帧和业务诊断均不得包含凭据、令牌、完整配置、CA 正文、私钥、请求正文、SQL 或 TCP 载荷。Agent 本机保存失败只记录文件系统异常及配置项 ID，HTTP 返回通用 500。不逐 DATA 帧记录运行日志，不以日志查询替代现有审计/流量查询，也不新增日志导出接口。

## 6. 验证及未完成项

自动化验证覆盖级别分流、scope/异常、历史普通文件清理、历史错误文件保留、大小轮转与重启追加、拒绝原因和安全回退、真实 Server 进程拒绝及 Agent 文件记录、配置保存失败、启动错误与预检无副作用。测试阈值可缩小以触发轮转，生产默认仍为 10 MiB。

构建版本、测试数量及执行环境以[验证状态](../testing/verification-status.md)为准。跨平台服务账号权限、注册期间配置变更竞态、旧二进制跨版本运行及生产升级未全部验收；不将这些项目写成已完成。
