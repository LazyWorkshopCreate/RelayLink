# Agent 本机写接口使用管理令牌

文档 ID：ADR-0027\
状态：Accepted\
日期：2026-10-23

## 背景

本机 Agent 页面只绑定 `127.0.0.1`，设计上不设管理密码。会话 Cookie 和 CSRF 只能阻止其他网页伪造请求，不能区分同一台主机上的不同用户。若 Agent 以 LocalSystem、root 或其他服务账号运行，普通本机用户可以通过 loopback 导入任意服务端配置，包括 `useTls=false`，从而把该服务账号的出站连接重定向到攻击者控制的服务端。

## 决定

保持只读状态接口匿名，也不增加用户名密码登录。所有本机写接口额外要求请求头 `X-RelayLink-Local-Token`。令牌在本机页面首次启动时生成，写入配置目录的 `local-management.token`，权限限于服务身份和管理员。`useTls=false` 还必须携带精确确认字符串 `CONNECT_WITHOUT_TLS`。成功的本机配置变更写入现有 Agent 文件日志，记录配置项、主机、端口、是否使用 TLS 和客户端 ID，不记录密钥、CA 或令牌本身。

部署前提是单用户主机，或由能读取令牌文件的管理员操作。安装说明要求优先使用专用低权限服务账号，而不是 LocalSystem 或 root。

## 备选

- 继续只依赖 loopback、Cookie 和 CSRF：不能挡住本机其他用户。
- 增加完整的密码登录：与已记录的“本机页面不设管理密码”冲突，也没有解决服务账号与交互用户不是同一身份的问题。
- 把写接口改成 Windows 命名管道或 Unix socket：浏览器不能直接访问，本机页面还得保留 HTTP。

## 后果

不能读取令牌文件的本机用户只能看状态，不能导入或改写配置。管理员从受保护文件复制令牌。旧的只读 API 保持不变。导入明文控制连接的操作多一步确认，并在日志中以 Warning 出现。

关联：[多服务端需求](../requirements/2026-09-28-multi-server-agent.md)、[多服务端设计](../design/multi-server-agent.md)、[安装与使用指南](../operations/installation-and-usage.md)。
