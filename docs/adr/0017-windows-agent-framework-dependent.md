# ADR-0017：Windows Agent 使用框架依赖安装包

状态：Superseded（由 [ADR-0019](0019-dual-windows-agent-distribution.md) 取代）\
日期：2026-09-22

## 背景

Windows Agent 原先以 `win-x64` 自包含方式发布，安装包携带完整 .NET 运行时。新增安装依赖检查需求后，用户明确决定不再发布自包含 Windows Agent，并要求安装程序检查 .NET Runtime。Agent 使用 ASP.NET Core 提供本机状态页，因此仅有基础 `Microsoft.NETCore.App` 仍不足以运行。

## 决定

Windows Agent 改为 `win-x64` 框架依赖发布。安装包不携带 .NET 运行时，目标机必须预装 .NET 10 ASP.NET Core Runtime x64；安装程序同时确认注册表中存在 10.x 的 `Microsoft.NETCore.App` 与 `Microsoft.AspNetCore.App` 共享框架。任一缺失都在依赖页显示失败、提供 .NET 10 官方下载链接，并阻止交互或静默安装。

Linux 与 macOS Agent、Windows/Linux Server 的交付方式不随本决定改变，继续沿用各自既有发布设置。

## 备选

- 保持 Windows Agent 自包含并把 .NET 标记为随包提供：不符合用户明确决定，拒绝。
- 只检查基础 .NET Runtime：Agent 使用 ASP.NET Core，本机状态页可能因共享框架缺失而无法启动，拒绝。
- 由安装包自动下载并安装运行时：扩大安装器的网络访问和供应链职责，离线部署及下载完整性更复杂，本期不采用。

## 后果

Windows 安装包体积下降，但安装前必须单独部署受支持的 .NET 10 ASP.NET Core Runtime x64，并随微软安全更新维护。离线环境需提前准备官方运行时安装程序。安装包按主版本 10 检查共享框架，允许已安装的 10.x 补丁版本；正式部署仍应安装当前受支持的最新安全补丁。

## 关联

- [Windows Agent 安装与依赖需求](../requirements/2026-09-22-windows-agent-installation.md)
- [技术实现文档](../design/technical-design.md)
- [Windows 部署](../../deploy/windows/README.md)
