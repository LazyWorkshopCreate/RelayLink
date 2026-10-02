# Windows 部署

## 服务端

在构建机运行 `scripts/publish.ps1 -RuntimeIdentifier win-x64 -Component Server`，将发布产物安装到受保护目录，例如 `C:\Program Files\RelayLink\Server`。将 `server.json`、`clients` 目录和 TLS 私钥置于只允许服务账号读取的 `C:\ProgramData\RelayLink`；JSON 路径可使用绝对 Windows 路径。

在管理员 PowerShell 中运行：

```powershell
.\install-server-service.ps1 -ExecutablePath 'C:\Program Files\RelayLink\Server\RelayLink.Server.exe' -ConfigurationPath 'C:\ProgramData\RelayLink\server.json'
```

脚本先执行静态配置检查，再创建自动启动及失败恢复的 Windows Service。若同名服务已存在，脚本会停止并要求管理员显式处理，避免意外替换正在运行的服务。服务账号必须能绑定配置中的控制、数据、业务和仪表盘端口，并读取控制 TLS 证书私钥。Agent 需能访问控制端口 `tunnel.port` 及独立数据端口 `tunnel.dataPort`；普通数据端口当前为明文，须依靠受信隔离链路或网络层加密保护。升级时 Server 与 Agent 必须一起更新。

## Agent

推荐使用安装包。目标机最低支持 Windows Server 2012 R2 x64，两种安装包均要求 Microsoft Visual C++ 2015–2022 Redistributable x64。自包含包携带 .NET 10 运行时；框架依赖包还要求目标机预装 .NET 10 ASP.NET Core Runtime x64。在 Windows 构建机安装 Inno Setup 6.3 或更新版本（需有 `ISCC.exe`），运行：

```powershell
.\scripts\build-agent-installer.ps1 -Version 1.0.0
```

脚本分别发布两种 win-x64 Agent 程序，并生成 `artifacts/installer/RelayLink-Agent-win-x64-self-contained-1.0.0.exe` 和 `RelayLink-Agent-win-x64-framework-dependent-1.0.0.exe`。程序发布目录分别为 `artifacts/installer-publish/<构建编号>/win-x64/self-contained/RelayLink.Agent` 和 `.../framework-dependent/RelayLink.Agent`；两种包均不包含任何真实配置、密钥或证书。目标机以管理员权限运行安装包后，依赖检查页显示操作系统、x64、VC++ 及 .NET 状态；自包含包的 .NET 项显示已内置，框架依赖包缺少 .NET 10 ASP.NET Core Runtime x64 时阻止安装。静默安装执行相同检查。首次安装必须在向导中选择已有的 Agent JSON 配置文件；未选择、文件不存在或扩展名不对时不能开始安装。安装程序校验配置，复制到 `C:\ProgramData\RelayLink\Agent\agent.json`，然后通过兼容 Windows PowerShell 4.0 的 WMI 注册逻辑将 `RelayLinkAgent` 注册为自动启动的 Windows Service 并立即启动。服务注册或启动失败会使安装失败并显示底层原因，不会静默完成。安装成功后在所有用户桌面创建“RelayLink Agent Monitor”快捷方式，双击会用系统默认浏览器打开配置对应的 `http://127.0.0.1:dashboardPort/`；未填写端口时使用 18081，设置为 0 时不创建。新下载配置的控制 TLS 信任 CA 已作为 Base64 内容内嵌，不需要单独的 PEM 文件。服务以 `LocalService` 运行，配置目录只授予 LocalService、SYSTEM 和 Administrators 权限；服务账号必须能访问服务端的控制/数据端口及本地业务目标。配置文件只包含服务端地址与端口、客户端 ID、密钥、CA 公钥证书和本机参数，不得包含通道或目标地址。

同一构建还会在 `artifacts/release/` 生成两种程序 ZIP，名称分别带 `self-contained` 和 `framework-dependent`；GitHub Release 同时附上这四个 Windows Agent 产物。

安装包默认启用详细日志，并在退出时把日志副本保存到 `C:\ProgramData\RelayLink\Agent\installer-logs`。日志包含依赖结论、安装阶段、脚本退出码和错误原因，但不写入 Agent JSON 正文、客户端密钥或 CA 内容。

检测到已有安装时，向导要求选择“仅更新软件程序”或“重新配置”。两种方式都会短暂停止并重启当前安装的服务，不会接管其他目录的同名服务：

- 仅更新在停服后清理 `C:\Program Files\RelayLink\Agent` 根目录中已知的 Agent 程序文件、运行时文件及旧版 `wwwroot` 页面资源，再写入完整的新版本，不要求新的 JSON；旧版 `program` 子目录会被清理，服务路径改回根目录。Inno 卸载记录和安装脚本保留。首次启动新 Agent 前运行独立转换器；旧配置（包括 Windows PowerShell 生成的 UTF-8 BOM 文件）、端到端身份和端口状态迁入新格式及 `state/primary/`，原材料作为受保护的回退来源保留；转换失败时服务保持停止。两种安装包可通过此模式互相切换。
- 重新配置必须选择新格式 JSON。安装预检在停止旧服务前识别并拒绝旧版单服务端文件；完整校验通过后才清理 Agent 管理的配置、旧身份和端口状态以及 `state/<profileId>/`，写入新配置并按新 `dashboardPort` 调整桌面快捷方式。清理旧版服务账号专有的身份文件前会取得删除权限。不会删除未知文件、其他目录或 Windows 事件日志。原身份重建后指纹及入口端口可能改变；若服务端已固定指纹，须先按身份轮换流程处理。执行前请备份需要留存的配置及日志。

首次安装与重新配置只接受新格式 JSON，其中 `servers[]` 至少可含一个配置项，TLS 信任材料使用各项的 `trustedCaPemBase64`。旧版服务端下载的单服务端文件应先用 `RelayLink.Agent.ConfigMigrator.exe --input <旧文件> --output <新文件>` 转换，再交给安装包；安装包直接拒绝旧格式。原始 JSON 及旧配置引用的 PEM 不由安装程序删除，请自行保护或安全清理。

静默安装同样必须显式指定配置：

```powershell
.\RelayLink-Agent-win-x64-self-contained-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES '/CONFIG=C:\path\agent.json'
```

已有安装的静默升级必须显式传入 `/MODE=update`（无须 `/CONFIG`）或 `/MODE=reconfigure /CONFIG=C:\path\new-agent.json`；显式选择重新配置即确认清理本机旧状态，交互安装仍会显示确认框。安装脚本失败时静默安装返回非零。卸载程序会停止并删除服务、程序文件和安装程序生成的桌面快捷方式，但有意保留 ProgramData 中的配置、CA 与 Agent 生成的身份/端口状态文件，供管理员决定是否备份或清理；这些文件含敏感材料。安装程序不会验证服务端在线或客户端认证成功，安装后需在服务端管理页确认客户端 Online，再检查 Agent 本机状态页。

也可不使用安装包：分别运行 `scripts/publish.ps1 -RuntimeIdentifier win-x64 -Component Agent -WindowsAgentMode SelfContained` 或 `-WindowsAgentMode FrameworkDependent`。两种输出位于 `artifacts/publish/win-x64/<模式>/RelayLink.Agent`；选择其一复制到受保护的 `C:\Program Files\RelayLink\Agent`，切换构建模式前先清理旧版程序与运行时文件，然后在管理员 PowerShell 中运行：

```powershell
.\install-agent-service.ps1 -ExecutablePath 'C:\Program Files\RelayLink\Agent\RelayLink.Agent.exe' -ConfigurationPath 'C:\path\agent.json'
```

脚本会先检查新格式配置，再复制配置并安装自动启动且配置失败恢复策略的 Windows Service；没有配置文件不能首次安装。手动复制安装或卸载脚本时，须一并复制同目录的 `agent-monitor-shortcut.ps1` 和配置转换器。调试时可先运行转换器，再用 `--config 'C:\ProgramData\RelayLink\Agent\agent.json'` 运行 Agent。手动运行脚本默认仍是首次安装模式，已有同名服务不会被隐式覆盖。

Windows Server 2012 / 2012 R2 若因系统自带 Windows PowerShell 版本较旧而无法执行上述安装脚本，可在确认程序与配置已复制完成后，以管理员身份运行仅依赖系统 WMI 与 `sc.exe` 的兼容注册脚本：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\register-agent-service-legacy.ps1
```

脚本默认使用 `C:\Program Files\RelayLink\Agent\RelayLink.Agent.exe` 和 `C:\ProgramData\RelayLink\Agent\agent.json`，注册前会执行配置检查。若安装失败留下了同名服务，先用 `sc.exe qc RelayLinkAgent` 核对目标，再显式传入 `-ReplaceExisting` 重建；该选项会停止并删除同名服务。非默认路径须分别传入 `-ExecutablePath` 与 `-ConfigurationPath`，并自行确保 `LocalService` 对程序和配置可读、对配置所在状态目录可写。

Agent 默认在本机 `127.0.0.1:18081` 提供按服务端分组的状态页；新增和修改连接项都只能导入服务端下载的单服务端新格式 JSON。已注册项可在页面停用和重新启用：停用会断开该项连接并停止重连，保留配置、密钥和身份，Agent 重启后仍停用；重新启用后恢复连接。删除仍在本机页面操作。无需管理密码。写请求校验本机来源及页面会话的 CSRF 令牌。可在 `agent.json` 中调整 `dashboardPort`，设为 `0` 则关闭页面与 API。互访端口由 Agent 在 `outboundPortRangeStart`–`outboundPortRangeEnd`（默认 20000–59999）内自动选择，分别记录在配置目录的 `state/<profileId>/ports.json`，重启优先复用，冲突时轮换。服务账号须能写该目录并绑定相应本地端口；不应把状态页代理到公网。
