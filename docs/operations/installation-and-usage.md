# 安装与使用指南

文档 ID：OPS-001\
状态：Active\
版本：v2.4.0\
更新日期：2026-09-30

RelayLink 只转发 TCP 字节流，不替代目标服务自身的账号、权限、TLS 和网络访问控制。

## 1. 选择部署方式

| 组件 | 推荐方式 | 适用环境 | 详细说明 |
|---|---|---|---|
| Server | Docker Compose | Linux Docker Engine | [Docker Compose 部署](../../deploy/docker/README.md) |
| Server | systemd | Linux 主机原生部署 | [Linux 部署](../../deploy/linux/README.md#server) |
| Server | Windows Service | Windows 主机原生部署 | [Windows 部署](../../deploy/windows/README.md#server) |
| Agent | 安装包 | Windows Server 2012 R2 及以上 | [Windows Agent 安装](../../deploy/windows/README.md#agent) |
| Agent | systemd | Linux x64 | [Linux Agent 部署](../../deploy/linux/README.md#agent) |
| Agent | launchd | macOS Intel / Apple Silicon | [macOS Agent 部署](../../deploy/macos/README.md) |

## 2. 用 Docker Compose 启动 Server

### 2.1 前置条件

- Linux Docker Engine。
- Docker Compose v2。
- PowerShell 7，用于首次生成配置和安全的管理员密码哈希。
- Agent 能访问 Docker 主机的控制端口和数据端口。

### 2.2 启动

在仓库根目录执行：

```powershell
pwsh ./scripts/start-server-compose.ps1 -AgentServerHost tunnel.example.com
```

把 `tunnel.example.com` 换成 Agent 实际能够访问的 DNS 名称或 IP。脚本会提示输入管理密码，然后完成以下操作：

- 在 `.local/docker-server` 创建配置、客户端和数据库目录。
- 生成只含密码哈希的 `server.json`。
- 构建 Server 和管理前端镜像。
- 在临时容器中完成配置预检，再后台启动 `relaylink-server` 容器。

默认端点：

| 用途 | 地址或端口 |
|---|---|
| 管理页面 | `http://127.0.0.1:18080/` |
| Agent 控制连接 | `7443/tcp` |
| Agent 数据连接 | `7444/tcp` |

生成的入门配置关闭了隧道 TLS，只用于受控环境验证普通代理。生产使用或 Agent 互访前，按照 [Docker TLS 配置](../../deploy/docker/README.md#启用-tls)挂载证书并启用 TLS。

### 2.3 查看与停止

```powershell
docker compose -f deploy/docker/compose.yaml ps
docker compose -f deploy/docker/compose.yaml logs -f --tail 200
pwsh ./scripts/stop-server-compose.ps1
```

停止脚本不会删除 `.local/docker-server` 中的配置和数据库。

## 3. 原生部署 Server

构建机需要 .NET 10 SDK、Node.js、pnpm 和 PowerShell。

```powershell
# Linux Server
./scripts/publish.ps1 -RuntimeIdentifier linux-x64 -Component Server

# Windows Server
./scripts/publish.ps1 -RuntimeIdentifier win-x64 -Component Server
```

发布后按目标平台安装服务：

- Linux：程序放在 `/opt/relaylink/server`，配置放在 `/etc/relaylink`。
- Windows：使用 `deploy/windows/install-server-service.ps1` 注册服务。

启动前必须检查配置：

```text
RelayLink.Server --config <server.json> --check-config
```

## 4. Server 配置清单

参考 [Linux 示例](../../config/examples/server.example.json) 或 [Windows 示例](../../config/examples/server.windows.example.json)。不要把真实配置、密钥、证书私钥或密码哈希提交到仓库。

### 4.1 必填设置

| 配置 | 作用 | 建议 |
|---|---|---|
| `tunnel.agentServerHost` | 下载的 Agent 配置所使用的服务端地址 | 填 Agent 可达的 DNS 或 IP，不能填 `0.0.0.0` |
| `tunnel.port` | 控制连接端口 | 默认示例为 `7443` |
| `tunnel.dataPort` | 数据连接端口 | 默认示例为 `7444`；不能与控制端口相同 |
| `dashboard.listenAddress` | 管理页面绑定地址 | 优先绑定内网地址或 `127.0.0.1` |
| `dashboard.admin.passwordHash` | 管理员密码哈希 | 使用 `scripts/new-admin-password-hash.ps1` 生成 |
| `clientsDirectory` | 客户端配置目录 | 服务账号必须可读写 |

### 4.2 TLS 与网络边界

- 控制和数据端口只向 Agent 来源开放。
- 管理页面和普通业务代理端口只向受信任网络开放。
- Agent 互访要求控制 TLS；`trustedCaPemPath` 只能包含 CA 公钥证书。
- 普通代理数据当前是明文 TCP。敏感业务应使用业务自身 TLS、受信隔离链路或网络层加密。
- 应用层安全组限制来源地址，但不提供链路加密。

### 4.3 SQLite 数据

流量历史和审计日志默认可共用一个 SQLite 数据库，建议保留 90 天。

- 数据库目录必须允许服务账号写入，否则服务可能无法启动，登录或建连也可能被拒绝。
- WAL 模式会产生 `-wal` 和 `-shm` 文件。运行中备份时不能只复制主 `.db` 文件。
- 旧 `.jsonl` 历史路径会在首次启动时迁移到同名 `.db`；确认迁移结果前不要删除旧文件。

## 5. 创建并安装 Agent

### 5.1 下载专属配置

1. 打开管理页面并登录。
2. 创建客户端。
3. 下载该客户端的 Agent JSON 配置。
4. 每台 Agent 使用独立客户端 ID 和密钥，不得共用配置。

客户端 ID 使用 1–64 位小写字母、数字、下划线或连字符，首位必须是字母或数字。页面会自动把大写字母转换为小写。

### 5.2 安装

| 平台 | 操作 | 注意事项 |
|---|---|---|
| Windows | 运行自包含或框架依赖 Agent 安装包并选择下载的 JSON | 两种包均需要 x64 和 VC++ 2015–2022 x64；框架依赖包另需 .NET 10 ASP.NET Core Runtime x64 |
| Linux | 发布 `linux-x64`，按 systemd 文档安装 | 程序目录只读，状态目录仅服务账号可写 |
| macOS Intel | 使用 `osx-x64` 包 | 按 launchd 文档创建服务账号和状态目录 |
| macOS Apple Silicon | 使用 `osx-arm64` 包 | 不要混用 Intel 包 |

Windows 已有安装时：

- “仅更新”保留配置、身份、端口状态和日志。
- 对旧单服务端安装，“仅更新”会在启动新 Agent 前运行独立转换器；原配置和状态留作受保护的回退材料。
- “重新配置”要求新的 JSON，并清除 Agent 管理的旧状态。
- 安装器会授予 Agent 服务账号对 `C:\ProgramData\RelayLink\Agent\agent.json` 的修改权限，供本机页面增删、停用和更新服务端配置；“仅更新”也会修复旧安装的只读权限。
- 服务注册失败会中止安装并显示底层原因，不会静默成功。

### 5.3 确认在线

- 管理页面中客户端应显示“在线”。
- Agent 本机状态页默认为 `http://127.0.0.1:18081/`。
- `dashboardPort` 设为 `0` 时，状态页和本机 API 均关闭。

本机只读 API：

| 路径 | 返回内容 |
|---|---|
| `/api/v1/status` | 完整状态快照 |
| `/api/v1/channels` | 被访问通道 |
| `/api/v1/mappings` | Agent 互访入口及实际本机端口 |
| `/api/v2/status` | 按 `profileId` 分组的状态 |
| `/api/v2/channels` | 按 `profileId` 分组的通道 |
| `/api/v2/mappings` | 按 `profileId` 分组的互访入口 |

这些接口仅监听 `127.0.0.1`，不返回密钥或证书指纹。多于一个服务端配置项时，旧 `/api/v1/*` 和 `/api/status` 返回 409，应使用 v2 接口。不要把它们转发到 LAN 或公网。

## 6. 创建普通代理通道

1. 在管理页面选择已在线的目标客户端。
2. 新增通道，填写目标主机和目标端口。
3. 保持“仅允许授权客户端互访”关闭。
4. 选择服务端监听地址和端口。
5. 按需绑定应用层安全组并保存。
6. 等待通道显示可接入，再从调用方连接服务端地址。

默认监听地址 `0.0.0.0` 表示全部 IPv4 网卡，不是调用方使用的连接地址。调用方应连接服务端真实 IP 或域名。管理页会从 `19000` 起建议可用端口；若保存前端口已被占用，重新打开新增表单获取建议值。

目标服务必须能从 Agent 主机访问，并继续使用目标服务自己的认证凭据。

## 7. 创建 Agent 互访入口

1. 在被访问方新增通道并启用“仅允许授权客户端互访”。
2. 保持“启用 Agent 间端到端加密”开启。
3. 等待目标 Agent 首次上线并自动登记证书指纹。
4. 将管理页指纹与目标机命令输出带外核对：

   ```text
   RelayLink.Agent --config <agent.json> --show-e2e-fingerprint
   ```

5. 在访问方客户端中创建指向目标客户端和通道的访问入口。
6. 从访问方状态页或 `/api/v1/mappings` 读取实际 `127.0.0.1:<端口>`。

互访通道不会开放云端业务代理端口。只有链路已受信隔离，或业务协议自身提供端到端加密和认证时，才考虑关闭 Agent 间加密；关闭后服务端及链路观察者可以读取或篡改业务内容。

证书重装或轮换不会自动覆盖已固定的身份。入口 ID 由目标客户端 ID 和通道 ID 生成，不可修改；需要调整时删除并重建。升级旧配置前应移除 `channels[].e2eCertificateSha256`，客户端级身份规则详见[安全互访设计](../design/agent-to-agent.md)。

## 8. 通过互访访问 Server 管理页

推荐把管理页绑定到 `127.0.0.1:18080`，再在 Server 同机部署一个 Linux Agent：

1. 为同机 Agent 创建目标为 `127.0.0.1:18080` 的授权互访通道。
2. 只给指定访问方创建入口。
3. 在访问方浏览器打开其本机 `127.0.0.1:<入口端口>`。

该方案复用标准 Agent 互访，不需要把管理端口暴露到公网。完整步骤见 [Linux 部署说明](../../deploy/linux/README.md#通过互访通道访问服务端管理页)。

## 9. 日志与故障定位

| 位置 | 查看方式 |
|---|---|
| Docker Server 运行日志 | 宿主 `.local/docker-server/data/logs/server-*.jsonl`、`server-error-*.jsonl`，容器内 `/var/lib/relaylink/logs/` |
| Linux Server 运行日志 | systemd 模板指定 `/var/lib/relaylink/logs/server-*.jsonl`、`server-error-*.jsonl` |
| Windows Server 运行日志 | 配置目录的 `logs/`；配置为 `C:\ProgramData\RelayLink\server.json` 时，目录为 `C:\ProgramData\RelayLink\logs` |
| Linux Agent 运行日志 | `/var/lib/relaylink-agent/logs/agent-*.jsonl`、`error-*.jsonl` |
| Windows Agent 运行日志 | `C:\ProgramData\RelayLink\Agent\logs\agent-*.jsonl`、`error-*.jsonl` |
| macOS Agent 运行日志 | `/Library/Application Support/RelayLink/Agent/logs/agent-*.jsonl`、`error-*.jsonl` |
| Windows Agent 业务连接诊断 | `C:\ProgramData\RelayLink\Agent\state\<profileId>\relaylink-diagnostics.jsonl` |

服务端与 Agent 通过 `RelayLink.Logging` 使用相同的 Serilog 文件策略。服务端默认写入配置目录的 `logs/`，可用 `RelayLink.Server --config <server.json> --log-directory <目录>` 指定独立目录；相对路径按启动工作目录解析。服务账号必须可写该目录。Docker 与 systemd 模板使用 `/var/lib/relaylink/logs/`，升级与重启时保留数据目录；原有 systemd 安装须应用新版模板，或在现有启动参数中增加日志目录。

服务端日志按连接 ID 记录打开、数据绑定、目标就绪、转发进度、EOF、取消和最终结果。Agent 运行日志继续写入配置目录的 `logs/`。两端运行日志不再使用控制台或 Windows Event Log 提供器；控制台保留配置预检及日志库自身写入失败的错误，因此 `docker compose logs` 与 `journalctl` 主要用于这些启动诊断。文件为 UTF-8 JSONL，包含时间、级别、消息、异常及可用的 `profileId`、`clientId`、会话或连接标识；不记录配置正文、密钥、令牌或业务载荷。本机页面后端与 Agent 共用同一日志管道，配置保存失败记为 Error，并保留底层文件系统异常，页面仍只返回通用错误。服务端 SQLite 审计及其成功前持久化门槛保持原样，不由运行日志替代。

- `agent-YYYYMMDD[_NNN].jsonl`：Info/Warning，按天或单文件达到 10 MiB 时轮转，保留 14 天；过期文件在后续打开或轮转时清理，停机期间不执行清理。默认不启用 Debug/Trace，Microsoft 框架默认 Warning，宿主生命周期保留 Info。
- `error-YYYYMMDD[_NNN].jsonl`：Error/Critical，采用相同轮转规则，但不按数量或时间自动删除。永久保留指程序不自动清理；升级、重新配置及卸载也保留此目录。
- 服务端对应文件名为 `server-YYYYMMDD[_NNN].jsonl` 与 `server-error-YYYYMMDD[_NNN].jsonl`，分别采用相同的普通与错误保留策略；请备份错误日志，避免手工清理数据目录。
- 控制连接 TCP、TLS、认证失败与自动重连沿用 Warning，写入普通日志并保留 14 天；配置校验失败、配置保存失败及宿主意外退出写入错误日志。`--check-config` 与 `--show-e2e-fingerprint` 工具模式仍使用控制台，不创建运行日志。
- 现有逐业务连接诊断文件仍位于 `state/<profileId>/`，超过 4 MiB 后轮换到 `.1`，用于字节计数与阶段分析。

文件日志默认每条写入刷新，正常退出时关闭并刷新文件。日志目录继承父目录的 Windows ACL，应放在受保护的数据目录；Unix 日志目录为 `0700`。文件系统拒绝写入或磁盘已满时，日志库的内部错误输出到标准错误，不保证持久化；Error 文件需由运维备份并监控磁盘空间。Server 的 `--check-config` 同样不创建日志目录。

### 9.1 注册被拒绝

Agent 告警包含 `ErrorCode`、`RejectionReason`，异常消息包含注册/配置确认/控制会话阶段及固定说明；服务端记录相同原因和客户端 ID、来源 IP。常见原因如下：

| RejectionReason | 排查方向 |
|---|---|
| ClientNotFound | 配置连接到了错误服务端，或客户端 ID 已被删除；核对并重新下载正确配置 |
| ClientDisabled | 在服务端明确重新启用该客户端 |
| InvalidSecret | 重新下载当前客户端配置，通过 Agent 导入修改；不要在日志中粘贴密钥 |
| IdentityInvalid / IdentityMismatch | 本机身份无效或不匹配服务端固定身份；核对身份迁移或轮换，另一台 PC 使用独立客户端 ID |
| DuplicateSession | 另一实例使用了同一客户端 ID；核对运行实例与服务状态 |
| ConfigurationMismatch / ClientConfigurationChanged | 核对服务端配置与两端版本，重新获取当前配置后重试 |

新字段兼容旧 v3 错误帧。只有新版服务端与新版 Agent 均生效时，Agent 才能显示精确原因；旧服务端仅返回错误码，新 Agent 会提示到服务端拒绝审计查看详细原因。拒绝仍为 Warning、保留 14 天；日志不包含提交或期望的密钥、身份指纹。TLS 未建立、协议帧头无效或认证前配额饱和时可能直接断开，客户端应查看本地网络/TLS 异常并结合服务端日志定位。

排查步骤：

1. 记录故障发生时间和服务端连接 ID。
2. 对照 Server 与 Agent 日志中的同一连接 ID。
3. 检查 `to agent` / `to caller` 与 `toTargetBytes` / `toServerBytes` 是否增长。
4. 找出哪个方向先出现 EOF、取消或超时。
5. 再结合业务服务日志判断认证、协议或应用层故障。

RDP 初始协商成功或双向已有字节，不代表图形会话已经正常，仍需实际登录验证。

## 10. 升级与备份

- 升级 Server 或 Agent 会断开现有连接，安排维护窗口。
- Server 与 Agent 应一起升级，不保证新旧版本混用。
- Docker 升级前备份 `.local/docker-server/config` 和完整 SQLite 数据目录。
- 原生部署升级前备份服务端配置、客户端目录、证书及完整 SQLite 数据目录。
- Agent 升级只替换程序，保留配置、身份和端口状态目录。
- 不要在服务运行时只复制 SQLite 主文件作为备份。
