# Windows 部署

## 服务端

在构建机运行 `scripts/publish.ps1 -RuntimeIdentifier win-x64 -Component Server`，将发布产物安装到受保护目录，例如 `C:\Program Files\RelayLink\Server`。将 `server.json`、`clients` 目录和 TLS 私钥置于只允许服务账号读取的 `C:\ProgramData\RelayLink`；JSON 路径可使用绝对 Windows 路径。

在管理员 PowerShell 中运行：

```powershell
.\install-server-service.ps1 -ExecutablePath 'C:\Program Files\RelayLink\Server\RelayLink.Server.exe' -ConfigurationPath 'C:\ProgramData\RelayLink\server.json'
```

脚本先执行静态配置检查，再创建自动启动及失败恢复的 Windows Service。若同名服务已存在，脚本会停止并要求管理员显式处理，避免意外替换正在运行的服务。服务账号必须能绑定配置中的隧道、业务和仪表盘端口，并读取证书私钥。

## Agent

推荐使用安装包。在 Windows 构建机安装 Inno Setup 6.3 或更新版本（需有 `ISCC.exe`），运行：

```powershell
.\scripts\build-agent-installer.ps1 -Version 1.0.0
```

脚本先发布 win-x64 自包含 Agent，再生成 `artifacts/installer/RelayLink-Agent-win-x64-1.0.0.exe`。该安装包不包含任何真实配置、密钥或证书。目标机以管理员权限运行安装包，必须在向导中选择已有的 Agent JSON 配置文件；未选择、文件不存在或扩展名不对时不能开始安装。安装程序校验配置，复制到 `C:\ProgramData\RelayLink\Agent\agent.json`，将 TLS 信任 CA PEM 复制到同目录（若启用 TLS），然后注册 `RelayLinkAgent` 为自动启动的 Windows Service 并立即启动。服务以 `LocalService` 运行，配置目录只授予 LocalService、SYSTEM 和 Administrators 权限；服务账号必须能访问服务端及本地业务目标。配置文件只包含服务端地址、客户端 ID、密钥和本机参数，不得包含通道或目标地址。

启用 TLS 时，安装前须确保配置指定的信任 CA PEM 在目标机可读取；安装程序会复制 CA 并改写安装后配置中的路径。原始 JSON 及 PEM 不由安装程序删除，请自行保护或安全清理。

静默安装同样必须显式指定配置：

```powershell
.\RelayLink-Agent-win-x64-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES '/CONFIG=C:\path\agent.json'
```

已存在同名服务时安装包拒绝覆盖。卸载程序会停止并删除服务与程序文件，但有意保留 ProgramData 中的配置、CA 与 Agent 生成的身份/端口状态文件，供管理员决定是否备份或清理；这些文件含敏感材料。安装程序不会验证服务端在线或客户端认证成功，安装后需在服务端管理页确认客户端 Online，再检查 Agent 本机状态页。

也可不使用安装包：先运行 `scripts/publish.ps1 -RuntimeIdentifier win-x64 -Component Agent`，将产物复制到受保护的 `C:\Program Files\RelayLink\Agent`，然后在管理员 PowerShell 中运行：

```powershell
.\install-agent-service.ps1 -ExecutablePath 'C:\Program Files\RelayLink\Agent\RelayLink.Agent.exe' -ConfigurationPath 'C:\path\agent.json'
```

脚本会先执行本地配置检查，再复制配置/CA 并安装自动启动且配置失败恢复策略的 Windows Service；没有配置文件不能安装。调试时可直接用 `--config 'C:\ProgramData\RelayLink\Agent\agent.json'` 运行可执行文件。手动脚本与安装包都不会修改或删除已有的同名服务。

Agent 默认在本机 `127.0.0.1:18081` 提供只读状态页，显示当前通道和互访入口；可在 `agent.json` 中调整 `dashboardPort`，设为 `0` 则关闭。互访端口由 Agent 在 `outboundPortRangeStart`–`outboundPortRangeEnd`（默认 20000–59999）内自动选择，记录在配置目录的 `<clientId>.ports.json`，重启优先复用，冲突时轮换。服务账号须能写该目录并绑定相应本地端口；不应把状态页代理到公网。
