# 安装与使用指南

文档 ID：OPS-001\
状态：Active\
版本：v1.11.0\
更新日期：2026-09-20

本文串起首次部署流程。具体命令和平台注意事项分别以 [Linux Server/Agent 部署](../../deploy/linux/README.md)、[Windows 部署及 Agent 安装](../../deploy/windows/README.md)和 [macOS Agent 部署](../../deploy/macos/README.md)为准。RelayLink 仅代理 TCP；业务目标自身的账号、权限和网络访问控制仍需单独配置。

## 1. 准备服务端

构建机需要 .NET 10 SDK、Node.js、pnpm 和 PowerShell。使用 `scripts/publish.ps1 -RuntimeIdentifier linux-x64 -Component Server` 或 `scripts/publish.ps1 -RuntimeIdentifier win-x64 -Component Server` 发布服务端；构建过程会同时生成管理前端静态资源。Agent 可用相同脚本按 `linux-x64`、`win-x64`、`osx-x64` 或 `osx-arm64` 发布；macOS 运行标识只支持 Agent。把发布产物和服务端配置部署到目标主机，并参照 [Linux 配置示例](../../config/examples/server.example.json) 或 [Windows 配置示例](../../config/examples/server.windows.example.json) 设置隧道、管理页面、客户端目录和历史记录路径。

流量历史启用后默认保存 90 天，建议将 `history.filePath` 设为服务账号可写的数据目录中的 `.db` 文件。旧配置若仍指向 `.jsonl`，首次启动会在同目录创建同名 `.db` 并一次性导入旧样本；原 JSONL 不会删除。升级前备份旧文件与目录，确认 SQLite 数据可查询后再按自己的备份策略处理旧文件。SQLite WAL 模式运行时还会生成 `-wal`、`-shm` 文件，不要在运行中只拷贝主 `.db` 文件作为备份。审计日志默认写入同一库，也可通过 `audit.filePath` 指定独立路径，默认保留 90 天。务必确保服务账号对数据库目录有写权限，否则服务无法启动或后续登录/建连将被拒绝。管理员登录后可在页面顶部打开“审计日志”查询，详见[审计与流量设计](../design/audit-and-traffic.md)。

先运行 `scripts/new-admin-password-hash.ps1` 生成管理员密码哈希，填入配置的 `dashboard.admin.passwordHash`。将 `tunnel.agentServerHost` 设为 Agent 可达的服务端 DNS 名称或 IP，管理端添加客户端时会自动预填该地址；不要填经 SSH 转发访问管理页时浏览器中的 `127.0.0.1`。分别设置 `tunnel.port`（控制）和 `tunnel.dataPort`（数据），并只向需要连接的 Agent 开放这两个端口；缺省数据端口为控制端口加一。若启用控制 TLS，配置服务端证书及私钥，并将仅含 CA 公钥证书的 PEM 路径填入服务端 `tunnel.trustedCaPemPath`；下载的 Agent 配置会内嵌其 Base64 内容。客户端安全互访必须启用控制 TLS。普通数据通道当前是明文 TCP，生产部署须提供受信隔离链路或网络层加密，敏感业务还应使用自身的端到端 TLS；安全组本身不提供加密。真实配置、密钥和证书私钥应放在受保护的部署目录，不提交到仓库。启动前用服务端可执行文件的 `--config <配置路径> --check-config` 检查配置，再按对应平台部署文档启动服务。管理页面和普通业务代理端口限制在受信任网络。

## 2. 创建并安装 Agent

在服务端管理页面登录，创建客户端并下载其专属 Agent 配置。客户端 ID 使用 1–64 位小写字母、数字、下划线或连字符，首位为字母或数字；管理页面会把输入的大写字母转成小写。每个客户端使用独立 ID 和密钥，不要让多个 Agent 共用同一份配置。若启用 TLS，CA 公钥证书已内嵌在下载的 JSON 中，不需在目标 Windows 主机另放 CA 文件。

Windows 推荐按 [Windows Agent 安装包说明](../../deploy/windows/README.md#agent) 构建并运行安装包，在首次安装时选择下载的 JSON 配置；没有配置文件不能安装。检测到已有安装时，“仅更新”保留现有配置和日志，“重新配置”要求新的 JSON 并清除 Agent 管理的本地配置、身份、端口状态与诊断日志。安装程序检查配置、注册自动启动的 Windows Service 并启动，在桌面创建通过系统默认浏览器打开本机状态页的快捷方式。Linux 使用 `linux-x64` 自包含包和 [systemd 部署步骤](../../deploy/linux/README.md#agent)。macOS 根据 `uname -m` 选择 `osx-x64`（Intel）或 `osx-arm64`（Apple Silicon）包，并按 [launchd 部署步骤](../../deploy/macos/README.md)安装。Linux 和 macOS 均把配置及 Agent 生成状态放在仅服务账号可读写的目录。安装后在管理页面确认客户端显示在线，并在 Agent 主机通过默认地址 `http://127.0.0.1:18081/` 查看只读状态页；端口可由 Agent 配置调整，设为 0 时关闭。

## 3. 配置通道并使用

在管理页面为在线客户端添加通道，设置通道 ID、目标主机与端口。普通通道默认以 `0.0.0.0` 监听全部 IPv4 网卡，云端监听端口会自动填入服务端从 19000 起找到的下一个可绑定端口，也可手动修改；若打开表单后端口被别的进程占用，保存会报错，需要重新打开新增表单获取建议值。`0.0.0.0` 不是可供调用方连接的地址，调用方应使用服务端实际可达的 IP 或域名及该端口。只需在特定网卡监听时可改填具体内网地址。务必通过安全组或防火墙将业务端口限制在受信任网络；已有通道不会因默认值变化而自动改写。保存后配置由服务端下发；待通道显示可接入，业务字节流便通过 Agent 转发到目标服务。确认目标服务在 Agent 所在主机可达，并使用目标服务自己的认证凭据。

如需从一台内网 Agent 安全访问另一台，在被访问方添加并启用“仅允许授权客户端互访”的通道；目标 Agent 首次认证上线时自动上报证书指纹并固定到客户端配置，通道无需填写指纹。建议将管理页显示的客户端指纹与目标机 `RelayLink.Agent --config <配置路径> --show-e2e-fingerprint` 的输出带外核对，再为访问方添加指向该客户端和通道的端到端访问入口；目标客户端尚未登记时不能创建入口。证书重装或轮换不会自动覆盖固定值，参见 [安全互访设计](../design/agent-to-agent.md)。升级前必须人工删除客户端文件内所有 `channels[].e2eCertificateSha256` 旧字段；旧字段不会自动迁移。若现有访问入口仍引用该目标，应先核对并处理其固定指纹与目标客户端身份，避免重启时配置校验失败。入口 ID 由服务端按“目标客户端 ID-目标通道 ID”生成；创建后不可修改，调整时须删除并重建。访问方 Agent 会在本机自动选取 `127.0.0.1` 端口；实际地址可在 Agent 本机状态页或服务端管理页面查看。此模式不开放被访问通道的云端业务代理端口。

若目标是安全访问服务端自身管理页，在服务端同机部署一个标准 Linux Agent，把管理页仅绑定 `127.0.0.1:18080`，并为该 Agent 创建目标为该地址的授权互访通道；然后只给指定访问方创建入口。访问方在本机浏览器打开其 Agent 状态页显示的 loopback 入口。该方案完全复用普通互访，不需要对外开放管理端口或使用管理页面专用协议，完整命令见 [Linux 部署说明](../../deploy/linux/README.md#通过互访通道访问服务端管理页)。

## 4. 普通代理连接诊断

服务端在系统日志中按连接 ID 记录 `Open`、数据绑定、目标就绪、开始转发、每 10 秒双向字节累计与最后活动间隔、单向 EOF/取消及最终结果。Linux 使用 `sudo journalctl -u relaylink-server --since '30 minutes ago' -o cat` 查看；可用连接 ID 关联同一条连接。Windows Agent 服务将相应阶段与进度写在配置目录下的 `relaylink-diagnostics.jsonl`；默认安装路径为 `C:\ProgramData\RelayLink\Agent\relaylink-diagnostics.jsonl`，超过 4 MiB 时轮换到同目录 `.1` 文件，查看需要管理员权限。日志只记录连接 ID、通道 ID、阶段、耗时、字节数和错误类型，不记录业务载荷、密钥或令牌，但仍应按运维敏感数据保护。

排查 RDP 黑屏时，先记录复现时间与服务端连接 ID，观察服务端 `to agent`/`to caller` 和 Agent `toTargetBytes`/`toServerBytes` 是否继续增长，再看哪个方向先 EOF、取消或超时。初始协商成功或两个方向有字节流，都不能单独证明图形会话正常；需结合实际登录画面验证。
