# ADR-0030：可审计连接的阶段期限与协议 v7

状态：Accepted\
日期：2026-10-31

## 背景

[独立审查](../testing/plugin-system-independent-review-2026-10-31.md)发现，可视互访把建立期限传入整个业务会话，在正常持续传输时断开；审批等待也提前释放 pending。仅删除超时会暴露无效 abort 和无界写入。Server 单侧延长审批还会被目标 Agent 的旧建立期限截断。

用户授权按[改进方案](../development/plugin-system-improvement-plan.md)完成修复。本决定记录该批次采用的协议、资源和兼容边界，不改变普通通道的传输位置。

## 决定

- 线协议升级为 v7，Server 继续接受 v3–v6 的原有 `none` 能力。修复后的可审计通道仅下发给 v7；v6 不接收或接入这些通道。以明确版本隔离替代默默改变旧 v6 的等待和状态字节含义。
- `Open`、`PeerOpen` 和 `PeerOpenGranted` 增加 `inspectionWaitTimeoutMilliseconds`。该值是建立输入、审批、目标连接及上游认证的有界待建立总预算，仅用于首次 active 之前；根据服务端建立期限、通道审批上限、目标连接期限计算，最多 900 秒。它不替代各阶段更短的握手、请求头和连接期限。
- Server 的 TLS/证明使用建立期限；插件读取输入和上游认证使用独立建立期限；审批使用独立审批期限并关联会话撤销。目标 Agent 在证明后使用待放行预算，获准连接时从该时刻重新计算 `targetConnectTimeoutSeconds`，不链接已经过去的旧建立期限。
- 调用方证明后的允许字节只表示“可以向插件提供输入”。调用方必须能发送 HTTP 请求头或 SSH 认证/请求，插件才能决策；此时仍持有 pending，不能标为业务 active。
- v7 新增 `InspectionTargetReady`（26），包含会话 ID、连接 ID和连接耗时。目标 Agent 建立 TCP 后报告，并允许目标腿为服务端插件传输上游认证数据。Server 校验报告属于当前目标会话和已获准打开的连接。目标失败通过受会话校验的 `OpenFailed` 路由到 peer registry；终止后的迟到结果不复活连接。
- 跳板继续在 `TargetReady` 之后发送 `Start`，启动供插件认证使用的目标腿。保留 [ADR-0013](0013-sqlite-audit-gate.md) 的 `BindAccepted` / `Start` 前审计：这些既有 opened 起始事件在可审计路径使用 `prepared` 结果及 `inspection_preparing` 原因，不能解释为业务已成功。
- 上游认证成功后记录 `inspection_session_opened`，发送 `InspectionActive`（25，会话 ID、连接 ID）。Server 与两侧 Agent 这时才释放首次 pending 并转 active；取消待建立总计时器，由 Server 启用业务空闲计时。后续 HTTP 请求和 SSH channel 审批另取得有界 pending 等待租约，不反复改变整个连接状态。
- 业务空闲由 Server 插件按应用活动判定。后续审批的操作租约暂缓空闲计时，释放后重新计时，但不切换 active 或取消审批自身期限。Agent 不重复按腿部加密字节执行业务 idle，因为它无法区分审批、SSH keepalive 和有效 payload；其待建立、阻塞写、半关闭及控制会话撤销仍有界。
- 跳板终止与目标就绪可以并发。pending registry 保留最多 1024 项、两分钟的终止连接和会话归属，忽略匹配的迟到 `TargetReady` / `HttpIngressReady`；未知连接、错误会话和活动连接上的重复就绪仍拒绝，不能因正常撤销而断开同 Agent 的其他连接。
- 允许决定持久化之前不能打开目标；每个后续动作仍由插件自己的审计门槛放行。状态控制通知走控制连接，不插入已开始的业务流。
- 中继异常或撤销取消共享令牌并关闭两侧；正常 EOF 允许另一方向在半关闭期限内排空。所有 Stream 写入有独立阻塞写期限，关闭业务空闲检查也不取消写入期限。会话任务必须被等待和观察，租约与流按所有者释放一次。
- 增加应用层双向字节计数及分钟历史列，保留 `peerCiphertext*` 对既有互访的含义。旧行新增列默认零，不重写历史；连接关闭审计与计数使用成功传输的同一观察点，不计腿部 TLS 或 SSH 传输开销。
- 终止只先取消并阻止新工作。peer 所有转发方向结束后再释放总容量并封存终止计数，覆盖取消时已提交的最后一次成功写入；终止审计恰好一次。

## 备选

- 只把 Server 令牌改为无限生命周期：无法约束审批、对端阻塞及故障残留，拒绝。
- 审批完成后才让调用方发送业务输入：HTTP/SSH 插件缺少决策输入，会互相等待，拒绝。
- 上游认证完成后才启动目标腿：认证必须经目标腿传输，导致死锁，拒绝。
- 沿用 v6 并要求旧端自行忽略新增字段：旧目标仍采用较短期限，不能作为兼容保证，拒绝。

## 后果

两侧 Agent 均需升级到 v7 才能使用修复后的可视互访，跳板要求目标 v7。先升级 Server 可以保留旧 Agent 的 `none` 通道。协议版本变化必须同时更新快照过滤、可用性投影、数据绑定和测试矩阵。

`prepared` 起始事件保留旧审计恢复的关联入口；是否真正开通业务以 `inspection_session_opened`、active 状态及最终结果共同判断。未实现的管理员审批仍只能到期拒绝，本次阶段修复不等于实现批准 API。

## 关联

- [插件系统设计](../design/plugin-system.md)
- [改进方案 B3](../development/plugin-system-improvement-plan.md#b3统一生命周期审批期限和计量基础)
- [v3 兼容决定](0025-v3-client-compatibility.md)
