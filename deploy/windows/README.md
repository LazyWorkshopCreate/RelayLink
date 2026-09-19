# Windows 部署

## 服务端

在构建机运行 `scripts/publish.ps1 -RuntimeIdentifier win-x64 -Component Server`，将发布产物安装到受保护目录，例如 `C:\Program Files\RelayLink\Server`。将 `server.json`、`clients` 目录和 TLS 私钥置于只允许服务账号读取的 `C:\ProgramData\RelayLink`；JSON 路径可使用绝对 Windows 路径。

在管理员 PowerShell 中运行：

```powershell
.\install-server-service.ps1 -ExecutablePath 'C:\Program Files\RelayLink\Server\RelayLink.Server.exe' -ConfigurationPath 'C:\ProgramData\RelayLink\server.json'
```

脚本先执行静态配置检查，再创建自动启动及失败恢复的 Windows Service。若同名服务已存在，脚本会停止并要求管理员显式处理，避免意外替换正在运行的服务。服务账号必须能绑定配置中的控制、数据、业务和仪表盘端口，并读取控制 TLS 证书私钥。Agent 需能访问控制端口 `tunnel.port` 及独立数据端口 `tunnel.dataPort`；普通数据端口当前为明文，须依靠受信隔离链路或网络层加密保护。升级时 Server 与 Agent 必须一起更新。

## Agent

推荐使用安装包。在 Windows 构建机安装 Inno Setup 6.3 或更新版本（需有 `ISCC.exe`），运行：

```powershell
.\scripts\build-agent-installer.ps1 -Version 1.0.0
```

脚本先发布 win-x64 自包含 Agent，再生成 `artifacts/installer/RelayLink-Agent-win-x64-1.0.0.exe`。该安装包不包含任何真实配置、密钥或证书。目标机以管理员权限运行安装包：首次安装必须在向导中选择已有的 Agent JSON 配置文件；未选择、文件不存在或扩展名不对时不能开始安装。安装程序校验配置，复制到 `C:\ProgramData\RelayLink\Agent\agent.json`，然后注册 `RelayLinkAgent` 为自动启动的 Windows Service 并立即启动。安装成功后在所有用户桌面创建“RelayLink Agent Monitor”快捷方式，双击会用系统默认浏览器打开配置对应的 `http://127.0.0.1:dashboardPort/`；未填写端口时使用 18081，设置为 0 时不创建。新下载配置的控制 TLS 信任 CA 已作为 Base64 内容内嵌，不需要单独的 PEM 文件。服务以 `LocalService` 运行，配置目录只授予 LocalService、SYSTEM 和 Administrators 权限；服务账号必须能访问服务端的控制/数据端口及本地业务目标。配置文件只包含服务端地址与端口、客户端 ID、密钥、CA 公钥证书和本机参数，不得包含通道或目标地址。

检测到已有安装时，向导要求选择“仅更新软件程序”或“重新配置”。两种方式都会短暂停止并重启当前安装的服务，不会接管其他目录的同名服务：

- 仅更新只替换 Program Files 中的软件和安装脚本，不要求新的 JSON；保留 ProgramData 中的配置、CA、端到端身份、端口状态及诊断日志。
- 重新配置必须选择新的 JSON，校验通过后删除 `C:\ProgramData\RelayLink\Agent` 中由 Agent 管理的 `agent.json`、`trusted-ca.pem`、`<clientId>.e2e.pfx`、`<clientId>.ports.json` 和 `relaylink-diagnostics.jsonl[.1]`，写入新配置并按新 `dashboardPort` 调整桌面快捷方式。不会删除该目录中的未知文件、其他目录或 Windows 事件日志。原身份重建后指纹会改变；若服务端已固定该客户端的端到端指纹，须先按身份轮换流程处理，否则互访认证会失败。执行前请自行备份需要留存的配置及日志。

旧配置仍可使用 `trustedCaPemPath`：安装前须确保该 PEM 在目标机可读取，安装程序会复制 CA 并改写安装后配置中的路径。新配置使用 `trustedCaPemBase64`，安装时不复制 CA 文件。原始 JSON 及旧配置引用的 PEM 不由安装程序删除，请自行保护或安全清理。

静默安装同样必须显式指定配置：

```powershell
.\RelayLink-Agent-win-x64-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES '/CONFIG=C:\path\agent.json'
```

已有安装的静默升级必须显式传入 `/MODE=update`（无须 `/CONFIG`）或 `/MODE=reconfigure /CONFIG=C:\path\new-agent.json`。卸载程序会停止并删除服务、程序文件和安装程序生成的桌面快捷方式，但有意保留 ProgramData 中的配置、CA 与 Agent 生成的身份/端口状态文件，供管理员决定是否备份或清理；这些文件含敏感材料。安装程序不会验证服务端在线或客户端认证成功，安装后需在服务端管理页确认客户端 Online，再检查 Agent 本机状态页。

也可不使用安装包：先运行 `scripts/publish.ps1 -RuntimeIdentifier win-x64 -Component Agent`，将产物复制到受保护的 `C:\Program Files\RelayLink\Agent`，然后在管理员 PowerShell 中运行：

```powershell
.\install-agent-service.ps1 -ExecutablePath 'C:\Program Files\RelayLink\Agent\RelayLink.Agent.exe' -ConfigurationPath 'C:\path\agent.json'
```

脚本会先执行本地配置检查，再复制配置（旧配置另复制 CA）并安装自动启动且配置失败恢复策略的 Windows Service；没有配置文件不能首次安装。手动复制安装或卸载脚本时，须一并复制同目录的 `agent-monitor-shortcut.ps1`。调试时可直接用 `--config 'C:\ProgramData\RelayLink\Agent\agent.json'` 运行可执行文件。手动运行脚本默认仍是首次安装模式，已有同名服务不会被隐式覆盖。

Agent 默认在本机 `127.0.0.1:18081` 提供只读状态页，显示当前通道和互访入口；可在 `agent.json` 中调整 `dashboardPort`，设为 `0` 则关闭。互访端口由 Agent 在 `outboundPortRangeStart`–`outboundPortRangeEnd`（默认 20000–59999）内自动选择，记录在配置目录的 `<clientId>.ports.json`，重启优先复用，冲突时轮换。服务账号须能写该目录并绑定相应本地端口；不应把状态页代理到公网。
