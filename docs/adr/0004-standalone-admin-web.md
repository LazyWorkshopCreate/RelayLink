# ADR-0004：独立管理前端并由 Server 托管

状态：Accepted\
日期：2026-09-16

## 背景

管理页面已从只读监控扩展到管理员登录、客户端与通道配置、历史图表。原先放在 Server/wwwroot 的单文件 HTML/JavaScript 难以维护和验收。用户要求管理端成为独立现代前端项目，编译发布后仍由 Server 托管。

## 决定

使用 React、TypeScript、Vite 构建 `src/RelayLink.AdminWeb`；使用 Radix Dialog 实现可访问的管理弹窗，Lucide 提供图标。前端通过同源 `/api/v1` 与 Server 通信，不引入外部 CDN。生产构建输出至该项目的 `dist/`；Server 的 MSBuild 构建/发布目标复制产物到输出/发布目录的 `wwwroot/`，由现有 ASP.NET Core 静态文件中间件托管。匿名访问保持只读；管理写操作仍由服务端会话与 CSRF 校验保护。

## 备选

- 继续维护单文件页面：初期简单，但随着编辑表单、会话状态和图表增加，回归风险和维护成本上升。
- 独立部署前端服务：会增加内网部署、同源认证和运维复杂度，不符合当前单体发布要求。

## 后果

构建 Server 需要 Node.js、pnpm 和已安装的前端依赖。发布物不需要 Node.js。前端构建产物不提交仓库；服务端发布流程须验证 HTML、JS、CSS 均被复制。生产仍须由内网或受控 HTTPS 暴露管理端，不能把匿名只读误认为可以公网开放。

## 关联

- [核心需求](../requirements/2026-09-15-core-requirements.md)
- [客户端删除与标签需求](../requirements/2026-09-21-client-management.md)
- [技术设计](../design/technical-design.md)
- [目录规划](../development/repository-layout.md)
