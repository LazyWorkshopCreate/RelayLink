# RelayLink

<img src="src/RelayLink.AdminWeb/public/relaylink-icon.svg" alt="RelayLink 项目图标" width="48" height="48">

RelayLink 是基于 .NET 的反向 TCP 代理，用于让云端内网应用访问分布在不同内网中的 TCP 服务，例如 SQL Server。部署在内网的 Windows Agent 主动连接服务端，不需要为 Agent 所在网络开放公网入站端口。

## 连接模式

- **云端访问内网目标**：云端应用连接服务端的内网代理端口；服务端通过已认证 Agent 建立数据隧道，由 Agent 连接对应的内网目标。
- **客户端安全互访**：访问方 Agent 在本机 `127.0.0.1` 提供入口，经服务端转发到被访问方 Agent。被访问通道可以限定为仅授权客户端访问；两端使用访问密钥校验与内层 TLS 保护业务流，服务端只转发密文。

两种模式均只代理 TCP 字节流，不解析或修改 SQL/TDS 内容。客户端互访的本机入口端口由访问方 Agent 在配置范围内选择，并在端口冲突时轮换。

## 主要能力

- 每个 Agent 使用独立客户端 ID 和密钥认证；通道及访问映射由服务端集中管理，认证后下发。
- 控制连接与按业务连接建立的数据隧道使用独立服务端端口。控制连接默认使用 TLS；普通代理数据通道完成绑定后直接复制原始 TCP 字节，不提供链路加密。客户端安全互访要求控制 TLS，并在两端 Agent 之间建立内层 TLS。
- 服务端管理页面支持匿名只读查看状态、通道和流量历史；管理员登录后可管理客户端、通道、访问映射及普通通道的来源 IP 安全组，并查看登录与连接生命周期审计日志。
- Agent 提供本机只读状态页，展示通道和可用的访问方地址。Windows Agent 可作为开机自动启动的 Windows Service 运行。

## 组成与边界

服务端支持 Linux 和 Windows，Agent 面向 Windows。后端使用 C# / .NET，管理前端使用 React 和 TypeScript。控制与数据入口只应对 Agent 开放；普通数据入口为明文 TCP，敏感业务必须使用业务自身 TLS 或受保护的网络链路。普通通道可用应用层安全组限制来源 IP；部署环境的安全组或防火墙仍须限制业务端口及管理页面的可达范围。

RelayLink 不提供 UDP、VPN/IP 层组网或 P2P 打洞，也不替代目标服务自身的身份认证。TCP 连接中断后不会重放业务请求或恢复原连接。

## 快速开始

发布并启动 Linux 或 Windows 服务端，准备服务端配置、管理账号及所需的 TLS 证书；在管理页面登录后创建客户端并下载其 Agent 配置。随后在目标 Windows 主机使用该配置安装 Agent，待管理页面显示客户端在线后添加通道，便可从受信任网络连接通道的服务端监听地址。安装步骤、配置准备和客户端互访用法见[安装与使用指南](docs/operations/installation-and-usage.md)。

## 仓库结构

```text
RelayLink/
├── RelayLink.slnx           .NET 解决方案
├── .github/workflows/       GitHub Actions 流水线
├── src/
│   ├── RelayLink.Server/   服务端与管理 API
│   ├── RelayLink.Agent/    Windows Agent
│   ├── RelayLink.Protocol/ 协议与消息模型
│   ├── RelayLink.Transport/ TCP、TLS 与流量转发
│   └── RelayLink.AdminWeb/ React 管理前端
├── tests/                  单元与集成测试
├── tools/                  模拟目标与模拟调用方
├── config/examples/        脱敏配置示例
├── deploy/                 Linux、Windows 部署资源
├── scripts/                构建与开发脚本
└── docs/                   需求、设计及开发文档
```

各目录的详细职责见[仓库目录规划](docs/development/repository-layout.md)。

## 开源项目与第三方组件

| 组件 | 说明 |
|---|---|
| [.NET / ASP.NET Core](https://dotnet.microsoft.com/en-us/apps/aspnet)、`Microsoft.Extensions.Hosting.WindowsServices` | 服务端与 Agent 的运行框架、管理 API、Windows Service 托管 |
| [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/) | 服务端分钟流量历史与审计日志的 SQLite 持久化 |
| [React / React DOM](https://react.dev/) | 管理页面的组件与浏览器渲染 |
| [Radix Dialog](https://www.radix-ui.com/primitives/docs/components/dialog)、[Lucide](https://lucide.dev/) | 管理页面的弹窗交互与图标 |
| [TypeScript](https://www.typescriptlang.org/)、[Vite](https://vite.dev/)、[Node.js](https://nodejs.org/)、[pnpm](https://pnpm.io/) | 前端类型检查、依赖管理和静态资源构建；发布后的服务端不需要 Node.js 或 pnpm |
| [xUnit.net](https://xunit.net/)、[Vitest](https://vitest.dev/)、[Testing Library](https://testing-library.com/)、[jsdom](https://github.com/jsdom/jsdom)、[Prettier](https://prettier.io/) | .NET 与前端测试、浏览器环境模拟及代码格式检查 |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | 生成 Windows Agent 安装包；不作为服务运行依赖 |

## 文档

- [文档索引](docs/README.md)
- [需求与功能边界](docs/requirements/requirements.md)
- [技术设计](docs/design/technical-design.md)
- [客户端安全互访设计](docs/design/agent-to-agent.md)
- [配置示例](config/examples/)
- [安装与使用指南](docs/operations/installation-and-usage.md)
- [Linux 部署说明](deploy/linux/README.md)
- [Windows 部署与 Agent 安装包](deploy/windows/README.md)
- [GitHub Actions 流水线](docs/development/ci.md)
- [贡献指南](CONTRIBUTING.md)
