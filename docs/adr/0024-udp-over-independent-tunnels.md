# ADR-0024：UDP 使用独立 TCP 数据隧道

状态：Proposed\
日期：2026-10-05

## 背景

用户明确新增 UDP 转发。原 [ADR-0001](0001-independent-tcp-tunnels.md) 与 [ADR-0008](0008-separated-control-and-raw-data.md) 的 TCP 架构已经包含会话认证、一次性绑定、审计门槛、资源限制及互访内层 TLS。直接新增公网 UDP 数据入口需要另行解决认证、重放、NAT 映射与加密。

## 实施方案

普通与互访 UDP 均按入口来源 IP/端口建立独立会话，复用已有 TCP 数据隧道，增加长度封装保持 UDP 边界；互访保留现有访问证明及可选内层 TLS。新增通道协议和空闲时限字段，旧通道默认 TCP；线协议使用 v4 承载 UDP；新版 Server 按 [ADR-0025](0025-v3-client-compatibility.md)保留 v3 TCP 兼容。原 TCP 直接复制及半关闭决定继续有效。

本 ADR 记录实现采用的架构取舍，详细方案尚未独立评审，因此保持 Proposed；用户“支持 UDP”指示已明确扩展首期范围，不意味着所有实施参数均已评审。

## 备选

- 原生公网 UDP 数据隧道：延迟语义更接近 UDP，但需新认证、加密、抗重放和 NAT 协议。
- 全局复用隧道：降低连接数量，增加流量隔离、公平调度和故障扩散复杂度。
- 只支持普通入口：不能覆盖既有授权互访使用模式。

## 后果

复用现有安全与生命周期机制；每来源独占数据连接和目标 socket，需空闲回收和有界排队。TCP 重传存在队头阻塞，不能承诺实时 UDP 性能。无广播/组播与来源透传；不重放已丢弃报文。UDP 的相关 Agent 须升级到 v4；v3 Agent 保留 TCP，兼容策略由 ADR-0025 确定。

## 关联

[需求](../requirements/2026-10-05-udp-forwarding.md)、[设计](../design/udp-forwarding.md)、[验证状态](../testing/verification-status.md)。
