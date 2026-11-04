# Agent 本机写接口使用同源会话与 CSRF

文档 ID：ADR-0034\
状态：Accepted\
日期：2026-11-04\
取代：[ADR-0027](0027-agent-local-management-token.md)

## 背景

2026-11-04 用户明确决定移除 Agent 本机页面的“本机管理令牌”输入框：在单用户本机场景下，要求用户从配置目录读取令牌再手工粘贴没有实际收益。ADR-0027 的令牌原本用于阻止同一台主机上的其他用户通过 loopback 改写以服务账号运行的 Agent 的出站连接。

本机页面只监听 `127.0.0.1`，并且每个请求都校验 Host 与 Origin。写会话 Cookie 为短期 HttpOnly、SameSite=Strict，写请求还必须携带同一会话的 CSRF 令牌。这些校验可以继续阻止其他网页伪造写请求，但不能区分同一主机上的不同用户。

## 决定

- 删除 `local-management.token` 文件的生成与读取，删除请求头 `X-RelayLink-Local-Token` 的要求，删除页面的“本机管理令牌”输入框。已存在的令牌文件不再使用，也不需要迁移。
- 本机写接口继续要求：仅绑定 `127.0.0.1`；每个请求精确校验 Host 为 `127.0.0.1:<dashboardPort>`；Origin 必须与页面同源（非 GET 请求必须携带匹配的 Origin）；由 `GET /api/v2/admin/session` 取得的短期 HttpOnly、SameSite=Strict 写会话 Cookie；以及同一会话的 `X-RelayLink-CSRF` 令牌。
- `useTls=false` 的 `CONNECT_WITHOUT_TLS` 确认保留；页面仍要求手工输入该字符串。
- 部署前提仍是单用户主机，或由能管理该 Agent 身份的管理员操作。多用户主机不要用 LocalSystem、root 或其他共享服务账号运行 Agent；专用低权限服务账号仍是推荐做法。

## 备选

- 保留令牌但由 Agent 自动注入页面：任何能打开本机页面的用户都能取得令牌，隔离效果与移除令牌相同，还会保留不必要的文件与接口。
- 保留手工令牌：用户已明确拒绝该交互。
- 增加密码登录：与“本机页面不设密码”的既有设计冲突，也无法解决服务账号与交互用户不是同一身份的问题。

## 后果

- 同机任何能打开 `http://127.0.0.1:18081/` 的用户都可以新增、修改、删除或启停服务端连接；跨站网页仍不能写入。多用户主机上的这一风险由部署前提承担，而不是由页面交互承担。
- 本机页面无需再提示令牌；API 客户端和自动化脚本不再需要读取令牌文件或设置请求头。旧的令牌文件可以保留或删除，均不影响运行。
- ADR-0027 被本决定取代；相关的需求、设计、运维说明和测试同步移除令牌。

关联：[ADR-0027](0027-agent-local-management-token.md)、[多服务端需求](../requirements/2026-09-28-multi-server-agent.md)、[多服务端设计](../design/multi-server-agent.md)、[安装与使用指南](../operations/installation-and-usage.md)。
