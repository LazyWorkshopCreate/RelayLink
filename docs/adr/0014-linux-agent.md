# ADR-0014：Linux Agent 使用现有通道与互访协议

状态：Accepted\
日期：2026-09-20

## 背景

Agent 原交付范围仅包含 Windows 安装包，但核心实现和互访集成测试已经可以在 Linux 运行。新的部署需求是在 RelayLink Server 所在 Linux 主机运行 Agent，并让指定的另一客户端安全访问仅监听服务端 loopback 的管理页面。

管理页面本质上是 TCP 端点。为它增加专用协议、专用鉴权或服务端直连入口，会重复现有授权互访能力并扩大安全边界。

## 决定

- Agent 正式支持 Windows 和 Linux。Linux 提供 `linux-x64` 自包含发布包，以独立无登录账号和 systemd 托管；Windows 交付方式保持不变。
- 两个平台使用相同的 Agent 配置 schema、线协议、通道与访问映射模型。Linux 不增加平台专用通道字段或管理页面专用协议。
- 服务端同机 Agent 通过普通授权互访通道连接 `127.0.0.1:18080`。Server 管理页只绑定 loopback；只有在访问方客户端配置了指向该通道的映射后，访问方本机 loopback 才出现入口。
- 访问链路继续执行目标 Agent 证书指纹固定、访问密钥证明和 Agent 间内层 TLS。浏览器到访问方 Agent、目标 Agent 到管理页均为各自主机 loopback。
- Linux 程序目录由 root 管理；配置、端到端身份、端口状态和诊断日志放在仅 Agent 服务账号可读写的 `/var/lib/relaylink-agent`，与 Server 的 `/var/lib/relaylink` 分离。升级程序不得删除或重建身份状态。

## 备选

- 为管理页增加专用 Agent→Server 隧道：会引入新的协议、授权、配额和审计路径，不采用。
- 将管理端口直接开放给指定公网 IP：依赖稳定来源地址，并暴露匿名只读页面和登录入口，不作为本需求的默认方案。
- 复用普通云端代理通道：方向与访问方所在位置不符，且普通数据路径不提供互访内层 TLS，不采用。

## 后果

发行流水线增加第四个产物 `RelayLink-Agent-linux-x64-<version>.tar.gz`。Linux Agent 与 Server/Windows Agent 仍需按协议版本同步升级。首次正式交付前须在目标 Linux 发行版完成 systemd 生命周期、文件权限、重启身份复用和真实管理页面互访验收；本地 Linux 集成测试不能替代该部署验收。

同机 Agent 连接控制 TLS 时，`serverHost` 仍须匹配服务端证书 SAN。可以让该域名在本机解析到 loopback 或内网地址，不能通过关闭证书名称校验来解决本机路由问题。

## 关联

- [需求文档](../requirements/requirements.md)
- [技术设计](../design/technical-design.md)
- [Agent 安全互访设计](../design/agent-to-agent.md)
- [Linux 部署说明](../../deploy/linux/README.md)
- [ADR-0005](0005-agent-to-agent-tls.md)
- [ADR-0006](0006-agent-owned-access-ports.md)
