# 文档索引

| 文档 | ID | 状态 | 用途 |
|---|---|---|---|
| [需求文档](requirements/requirements.md) | REQ-001 | Draft | 范围、功能与验收 |
| [技术实现文档](design/technical-design.md) | DES-001 | Draft | 架构、协议、配置与部署设计 |
| [首期隧道方案](adr/0001-independent-tcp-tunnels.md) | ADR-0001 | Proposed | 记录独立数据隧道的方案取舍 |
| [运行时通道配置](adr/0002-runtime-channel-configuration.md) | ADR-0002 | Superseded | 历史上的监听更新及配置下发决定 |
| [流量历史持久化](adr/0003-traffic-history-persistence.md) | ADR-0003 | Superseded | 历史 JSONL 流量快照方案 |
| [SQLite 分钟流量历史](adr/0010-sqlite-minute-traffic-history.md) | ADR-0010 | Accepted | 分钟聚合、端到端流量与 90 天保留 |
| [SQLite 审计与放行门槛](adr/0013-sqlite-audit-gate.md) | ADR-0013 | Accepted | 登录和连接开始前审计落库、查询与故障拒绝 |
| [审计与流量设计](design/audit-and-traffic.md) | DES-003 | Draft | 审计事件与 SQLite 流量实现、迁移及验收设计 |
| [独立管理前端](adr/0004-standalone-admin-web.md) | ADR-0004 | Accepted | React/Vite 管理项目及 Server 托管方式 |
| [安全互访设计](design/agent-to-agent.md) | DES-002 | Draft | Agent↔Agent 授权、内层 TLS 与协议增量 |
| [互访架构决策](adr/0005-agent-to-agent-tls.md) | ADR-0005 | Superseded（身份登记部分） | 服务端协调、密文中继与原身份登记取舍 |
| [客户端级端到端身份](adr/0011-client-level-e2e-identity.md) | ADR-0011 | Accepted | Agent 自动上报、首次固定、拒绝旧通道指纹字段 |
| [Agent 自主管理入口端口](adr/0006-agent-owned-access-ports.md) | ADR-0006 | Accepted | 本机端口选择、持久化、状态上报及只读页面 |
| [Agent 配置内嵌受信 CA](adr/0007-embedded-agent-trust-ca.md) | ADR-0007 | Accepted | 下载配置中的 CA 信任材料与旧配置兼容 |
| [控制和原始数据分离](adr/0008-separated-control-and-raw-data.md) | ADR-0008 | Accepted | 独立数据端口、单次绑定、普通 TCP 流复制及安全取舍 |
| [通道变更撤销连接](adr/0009-revoke-connections-on-channel-change.md) | ADR-0009 | Accepted | 实际变更后按通道或映射撤销待建立和已建立连接 |
| [应用层安全组](adr/0012-application-security-groups.md) | ADR-0012 | Accepted | 普通通道按来源 IP/CIDR 限制访问及规则变更撤销连接 |
| [Linux Agent](adr/0014-linux-agent.md) | ADR-0014 | Accepted | Linux 自包含发布、systemd 托管及复用既有互访访问服务端管理页 |
| [macOS Agent](adr/0015-macos-agent.md) | ADR-0015 | Accepted | Intel/Apple Silicon 自包含发布与 launchd 托管 |
| [目录规划](development/repository-layout.md) | DEV-001 | Active | 文件归属及项目职责 |
| [文档管理规则](development/documentation-policy.md) | DEV-002 | Active | 命名、状态、更新和引用规则 |
| [GitHub Actions 流水线](development/ci.md) | DEV-003 | Active | CI 检查、构建产物和安全边界 |
| [发布说明](releases/README.md) | DOC-002 | Active | 各版本 GitHub Release 的仓库内唯一说明来源 |
| [安装与使用指南](operations/installation-and-usage.md) | OPS-001 | Active | 服务端与 Agent 安装、首次配置和连接验证 |
| [验证状态](testing/verification-status.md) | TST-001 | Active | 已执行检查与未完成验收证据 |
| [本地多场景验收方案](testing/local-acceptance-plan.md) | TST-002 | Active | 隔离启动、场景判定、运行记录与安全停止 |
| [对外介绍材料](intro/README.md) | DOC-001 | Active | 演示文稿与公众号文章的源文件、插图及重建脚本 |

需求及首期技术文档来自 2026-09-15 的调研设计，2026-09-16 迁入 RelayLink 并统一名称；2026-09-17 增补互访设计及本机验证。此目录中的文件是后续维护入口；原交付副本保留为交付记录，不再作为同步维护对象。

## 状态说明

Draft：待评审；Proposed：待采纳的架构决定；Accepted：已明确采纳；Active：当前采用的工作规则；Superseded：被后续决定替代。文档被采纳不代表功能已经实现。
