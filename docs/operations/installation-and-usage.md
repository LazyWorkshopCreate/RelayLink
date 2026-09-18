# 安装与使用指南

文档 ID：OPS-001\
状态：Active\
版本：v1.0.0\
更新日期：2026-09-17

本文串起首次部署流程。具体命令和平台注意事项分别以 [Linux 服务端部署](../../deploy/linux/README.md) 和 [Windows 部署及 Agent 安装](../../deploy/windows/README.md) 为准。RelayLink 仅代理 TCP；业务目标自身的账号、权限和网络访问控制仍需单独配置。

## 1. 准备服务端

构建机需要 .NET 10 SDK、Node.js、pnpm 和 PowerShell。使用 `scripts/publish.ps1 -RuntimeIdentifier linux-x64 -Component Server` 或 `scripts/publish.ps1 -RuntimeIdentifier win-x64 -Component Server` 发布服务端；构建过程会同时生成管理前端静态资源。把发布产物和服务端配置部署到目标主机，并参照 [Linux 配置示例](../../config/examples/server.example.json) 或 [Windows 配置示例](../../config/examples/server.windows.example.json) 设置隧道、管理页面、客户端目录和历史记录路径。

先运行 `scripts/new-admin-password-hash.ps1` 生成管理员密码哈希，填入配置的 `dashboard.admin.passwordHash`。若启用外层 TLS，配置服务端证书及私钥，并让 Agent 信任对应 CA；客户端安全互访必须启用外层 TLS。真实配置、密钥和证书私钥应放在受保护的部署目录，不提交到仓库。启动前用服务端可执行文件的 `--config <配置路径> --check-config` 检查配置，再按对应平台部署文档启动服务。仅向需要连接的 Agent 开放隧道端口；管理页面和普通业务代理端口限制在受信任网络。

## 2. 创建并安装 Agent

在服务端管理页面登录，创建客户端并下载其专属 Agent 配置。每个客户端使用独立 ID 和密钥，不要让多个 Agent 共用同一份配置。若启用 TLS，安装前确保配置引用的 CA PEM 在目标 Windows 主机可读取。

推荐按 [Windows Agent 安装包说明](../../deploy/windows/README.md#agent) 构建并运行安装包，在向导中选择下载的 JSON 配置；没有配置文件不能安装。安装程序检查配置、注册自动启动的 Windows Service 并启动。也可使用同一部署文档中的手动安装脚本。安装后在管理页面确认客户端显示在线，并在 Agent 主机查看默认的本机只读状态页 `http://127.0.0.1:18081/`；端口可由 Agent 配置调整。

## 3. 配置通道并使用

在管理页面为在线客户端添加通道，设置通道 ID、目标主机与端口，以及供云端应用连接的服务端监听地址与端口。保存后配置由服务端下发；待通道显示可接入，从获准访问的网络连接该监听地址，业务字节流便通过 Agent 转发到目标服务。确认目标服务在 Agent 所在主机可达，并使用目标服务自己的认证凭据。

如需两台内网 Agent 安全互访，在被访问方添加并启用“仅允许授权客户端互访”的通道；按 [安全互访设计](../design/agent-to-agent.md) 获取并核对被访问方证书指纹，再为访问方添加指向该客户端和通道的互访入口。访问方 Agent 会在本机自动选取 `127.0.0.1` 端口；实际地址可在 Agent 本机状态页或服务端管理页面查看。此模式不开放被访问通道的云端业务代理端口。
