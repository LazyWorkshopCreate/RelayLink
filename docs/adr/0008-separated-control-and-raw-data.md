# ADR-0008：控制与数据入口分离，普通代理使用原始 TCP 流

状态：Accepted\
日期：2026-09-18

## 背景

原方案让控制和数据连接共用一个端口，并在每条数据连接上叠加外层 TLS 与逐块 `DATA/FIN` 帧。RDP 经云端代理登录后黑屏；此前补上 `TCP_NODELAY` 将初始协商耗时从约 300 ms 降到约 80 ms，但未消除黑屏。该现象尚不能单独归因于 TLS 或帧化。用户决定先分离控制与数据通道，并以简单双向流复制作为普通代理的基线。

## 决定

- Server 分别监听 `tunnel.port`（控制）和 `tunnel.dataPort`（数据）。未显式设置数据端口时使用控制端口加一；若溢出或冲突，配置检查失败。Agent 下载配置包含明确的 `dataPort`；旧 Agent 配置缺少此字段时同样使用 `serverPort + 1`。
- 控制入口只接受 `Register`，数据入口只接受 `BindData` 或 `PeerBindData`。业务连接只能由已完成控制认证及配置确认的会话发出 `Open` 后创建 Pending；数据入口校验会话、连接 ID、通道、期限及单次令牌后才允许绑定。控制会话结束时取消其待建立和已建立的普通代理连接。
- 普通代理数据连接只交换 `BindData/BindAccepted`；Agent 目标就绪的 `TargetReady` 与 Server 允许转发的 `Start` 均走已认证控制连接。随后数据连接不再发送 `DATA/FIN/RESET` 帧，也不使用外层 TLS；Server 与 Agent 各自执行两个有界缓冲的 TCP 异步复制循环。单向 EOF 通过相对端 socket 的 `Shutdown(Send)` 传播，另一方向继续运行。业务字节不增加前缀或进行变换。
- 控制连接继续按 `tunnel.tlsEnabled` / Agent `useTls` 使用外层 TLS 与服务端证书验证。客户端安全互访仍要求控制 TLS，以保护映射访问密钥的下发。互访业务流原本强制使用内层 TLS；该部分已由 [ADR-0016](0016-optional-peer-traffic-encryption.md) 改为按目标通道选择内层 TLS 或认证后的明文直接复制。互访连接在数据端口上的外层传输不使用 TLS。

## 安全与兼容性后果

普通代理的数据连接和业务载荷现在**不由 RelayLink 加密或完整性保护**。一次性绑定令牌也出现在明文数据入口握手中；令牌不可复用，但链路窃听或主动攻击仍可能造成劫持、篡改或拒绝服务。部署时必须以受信网络、专用链路或 IPsec/VPN 保护 Agent↔Server 数据入口；敏感业务应启用自身的端到端 TLS。安全组仅限制可达性，不提供链路保密性。不得把 `tunnel.tlsEnabled=true` 解释为普通业务数据受 RelayLink TLS 保护。

这是一项传输兼容性变更：`NTP1` 帧头的 version 从 1 升至 2，旧 Server/Agent 不得混用，错误版本应在握手时立即拒绝；部署时必须在防火墙中允许新的数据端口。该设计不声称已经解决 RDP 登录黑屏，仍需跨主机 mstsc 实测。互访路径保留特殊封装的依据是其安全要求，以及本机双 Agent 随机字节、半关闭和并发集成实验；这些实验不能证明所有业务协议均无影响。

## 备选

- 继续共享端口及逐块帧化：实现简单，但无法建立最小处理的对照基线。
- 保留数据外层 TLS、仅移除业务帧：仍需解决 TLS 上原生 TCP 半关闭及业务兼容性验证，本次不采用。
- 全局取消互访内层 TLS：无法按通道保留安全默认值，不采用；后续按 [ADR-0016](0016-optional-peer-traffic-encryption.md) 允许管理员对单个通道显式关闭。

## 关联

- [传输、管理与审计需求](../requirements/2026-09-18-runtime-and-audit.md)
- [技术设计](../design/technical-design.md)
- [安全互访设计](../design/agent-to-agent.md)
- [ADR-0001](0001-independent-tcp-tunnels.md)：原首期提议，已由本决定替代其共享入口及普通数据帧化部分。
