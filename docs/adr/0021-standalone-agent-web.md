# 独立 Agent 本机前端

文档 ID：ADR-0021\
状态：Accepted\
日期：2026-09-29

## 背景

Agent 本机监控页原由 `AgentLocalDashboard.cs` 拼接 HTML、内联样式和独立脚本。服务端管理页已采用独立的 React/TypeScript 前端，由 Vite 构建并随 .NET 程序发布。两套实现方式使界面与构建流程不一致，也难以维护 Agent 的多服务端管理表单。

## 决定

新增 `src/RelayLink.AgentWeb` 作为独立 React/TypeScript/Vite 项目。Agent 构建和发布时生成静态资源，复制到 Agent 输出目录的 `wwwroot/`，由现有仅监听 `127.0.0.1` 的 Kestrel 提供页面、脚本和样式。Agent 后端继续提供版本化状态与管理 API，保留本机来源、会话和 CSRF 校验；前端不持有已保存的注册密钥。页面视觉语言与服务端管理前端保持一致，但两者分别构建和发布。

## 备选

- 继续维护 C# 字符串拼接页面：构建简单，但组件、状态与样式难以扩展。
- 在服务端前端项目中增加 Agent 入口：可共用依赖，但两个部署产物及业务边界耦合。

## 后果

Agent 构建环境需要 Node.js 与 pnpm；发布后的 Agent 无需前端运行时。各平台 CI 和 Release 作业须安装锁文件依赖，并检查 Agent 发布目录包含 `wwwroot/index.html`。本机状态 API 和写入权限边界保持原有语义。

关联：[Agent 多服务端技术设计](../design/multi-server-agent.md)、[独立服务端管理前端](0004-standalone-admin-web.md)、[仓库目录规划](../development/repository-layout.md)。
