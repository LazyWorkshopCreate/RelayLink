# UDP 转发技术设计

文档 ID：DES-006\
状态：Draft\
版本：v1.3.0\
更新日期：2026-10-23\
调研日期：2026-10-05\
关联：[需求](../requirements/2026-10-05-udp-forwarding.md)、[ADR-0024](../adr/0024-udp-over-independent-tunnels.md)

## 传输与兼容

保留 `NTP1`、控制与数据端口分离、独立隧道绑定及认证。线协议当前为 4；新版 Server 同时接受 v3/v4，按首个注册帧锁定控制会话版本，响应使用同版本帧头。v3 的初始与更新快照排除 UDP 通道及 UDP 目标映射，序列化删除新增 UDP 字段，按旧字段顺序计算配置摘要，保留 TCP 普通代理和新旧 Agent 间的 TCP 互访。UDP 仅在参与端均为 v4 时放行；v1/v2、未知版本和连接内版本切换被拒绝。数据绑定必须与所属控制会话版本一致，错误版本不消耗一次性令牌。兼容决定见 [ADR-0025](../adr/0025-v3-client-compatibility.md)。JSON schemaVersion 不变；旧通道文件缺少增量字段仍默认 TCP。普通 TCP 绑定后维持原始字节复制；UDP 在同一条已绑定 TCP 数据连接中使用报文封装。每来源会话独占数据隧道，不复用多个会话。

UDP 应用记录为 **4 字节大端有符号非负长度 + 对应载荷**，范围 0–65507；0 是空报文，TCP EOF 才是隧道结束。读取长度越界或未读全载荷即终止会话，不能投递部分报文。这个记录不使用 NTP1 Data 帧头。互访时记录写入访问证明完成后的 `FramedDuplexStream`（明文）或其上的 `SslStream`（加密）；外层 Data 的 32 KiB 上限不变，大报文可跨多个 Data 和 TLS 记录，重新组装后才发送 UDP。TCP 互访的半关闭记录不变。

`ChannelSnapshot` 下发协议与空闲超时；`OutboundMappingSnapshot` 从目标配置派生相同字段，不在映射 JSON 中重复保存。普通 Open 按当前配置版本解析协议；PeerOpen/PeerOpenGranted 增加协议字段，两 Agent 对照各自快照拒绝不一致模式。目标通道协议或超时变化时同时推送依赖它的访问方快照，重建对应本机入口。短暂快照不同步期间拒绝建立，绝不误将 TCP 字节解释为 UDP。

## 会话与生命周期

`DatagramListener` 每个入口持有一个独占绑定的 UdpClient；以来源 IPEndPoint 为键维护有限会话表。收到首包后先创建并登记 `DatagramSession`，再写入有界队列并调用处理器。处理器同步抛出或任务失败时只取消并释放该来源，把异常交给宿主记录，其他来源继续接收。每会话独立队列、取消源及目标 socket。普通安全组和 Agent 在线检查在创建队列前执行；每报文再次检查来源规则。

服务端和 Agent 沿用现有容量、待建立与审计门槛。目标 UDP socket 通过异步 DNS 解析选择首个 IPv4/IPv6 地址并 Connect，只有该目标地址/端口的回包参与转发。UDP Connect 表示本机解析与 socket 准备完成，**不证明目标服务在线或可达**；状态中的目标结果须按该语义理解。目标连接超时覆盖 DNS 与 socket 初始化。

`DatagramRelay` 运行两个异步方向，一个读取 UDP/队列并写入流，一个读取完整应用记录并发送 UDP。空闲时限从转发开始计算，两方向每次成功转发刷新。任一方向退出后取消另一方向，并等待两个任务结束；空闲超时正常结束，控制断线、撤销和服务停止按外部取消结束。UDP 没有半关闭。监听停止取消所有入口会话；后台每秒清理已结束的队列和取消源；本机端口的持久化仍只记录映射端口，不记录载荷或会话。

## 资源与指标

固定每会话 32 报文队列，满时丢弃最新报文；65507 字节上限意味着单会话排队载荷至多约 2 MiB，另有双向当前报文、socket 和框架缓冲。服务端每丢弃一次就增加该通道的 `udpDroppedTotal`，并出现在通道与仪表盘指标里，不逐包写警告。Agent 本机入口按同一策略丢弃，并最多每 30 秒记一条累计警告。入口会话数受通道/客户端上限约束；转发任务受原全局和待建立限额约束。该内存上界不是容量承诺，高配置上限仍需实际压测和适当缩小限额。RelayLink UDP socket 的发送缓冲至少设为 128 KiB，避免 macOS 默认缓冲限制大报文；它仍受系统上限约束，不修改系统全局参数。写超时默认 30 秒；普通服务端侧沿用 `blockedWriteTimeoutSeconds`。满队列、离线、容量或 ACL 丢弃均不重试载荷。

普通通道流量累加 UDP 载荷字节，不计 4 字节头；互访继续统计外层 Data 载荷，包含应用头、访问证明以及加密模式下 TLS 开销。空报文的载荷字节为 0。连接列表和配额的“连接”对 UDP 表示来源会话。审计复用 proxy/peer 起止事件与连接 ID，不保存 UDP 载荷。普通 UDP 数据与普通 TCP 一样不提供隧道加密；需保密时使用业务自身保护或互访 TLS。

## 运维和限制

业务入口防火墙须按 UDP 放行；控制/数据端口仍只放行 TCP。Linux Docker Compose 采用 host 网络，不需新增每通道端口映射；采用自定义 bridge 网络时须显式发布 `/udp`。TCP 与 UDP 可使用同数值端口，既有 TCP 控制/数据/管理入口不占 UDP 端口。

不保留调用方原 IP 至目标，目标看到 Agent socket 地址；不做广播/组播、发现报文改写、QUIC 路由、重试或无损迁移。源地址可能伪造，部署安全组、防火墙及业务认证仍须限制入口。跨公网性能会受 TCP 隧道重传和队头阻塞影响。

实现使用官方支持取消令牌的 [UdpClient.ReceiveAsync](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.udpclient.receiveasync?view=net-10.0) 与 [SendAsync](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.udpclient.sendasync?view=net-10.0)。API 依据与工程选择分开：32 报文队列、60 秒默认空闲和独立 TCP 隧道是本项目实施方案。

测试覆盖与目标环境缺口见[验证状态](../testing/verification-status.md)。
