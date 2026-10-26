# HTTP/HTTPS 反向代理需求

文档 ID：REQ-001N\
状态：Accepted\
版本：v1.3.0\
更新日期：2026-10-15\
需求日期：2026-10-12\
配套：[技术设计与 YARP 评估](../design/http-reverse-proxy.md)、[ADR-0026](../adr/0026-http-proxy-over-tcp-tunnels.md)

## 1. 背景与决定边界

内网或 loopback Web 服务经 TCP 通道访问时，浏览器 Host、Origin 不会随目标地址变为 localhost。HTTP/WebSocket 来源拒绝属于应用层校验，不能通过修改 TCP 地址解决，也不能简单按 CORS 错误处理。

用户已明确：保留现有 TCP 透传；公网访问与客户端授权互访均支持 HTTP/HTTPS 代理及按通道 Host、Origin 覆写；按目标 Agent 执行 HTTP 代理的方向设计，保留互访链路加密。2026-10-12 已授权审查修正文档、分批实现并独立提交、本机验收、推送与 CI 检查，暂不发布。该授权采用此需求方向；实现和验收证据单独记录，Accepted 不表示所有目标环境验证已完成。

## 2. 范围与访问模型

- 保留既有 TCP/UDP 原始转发及互访行为；新增应用模式 HTTP，底层传输仍为 TCP，不在原始复制循环做 HTTP 头字符串替换。
- 两种入口均由目标 Agent 的 HTTP 代理执行相同的 Host/Origin 校验与转换，支持固定 Web 目标、HTTP/HTTPS、ws/wss、SSE 和流式上传/下载。
- 公网入口为 Server 的通道监听；互访入口为访问方 Agent 的 loopback 端口。Server 不终止浏览器 HTTPS、不修改 HTTP 内容；访问方 Agent 不解析 HTTP。
- 保留现有 authorizedClientsOnly 边界：false 的通道提供普通入口，true 的通道只允许映射互访且不监听公网端口。可用两个通道指向同一 Web 目标覆盖两种场景；本轮不额外允许一个公开通道绕过既有边界接受映射。
- HTTP 互访必须开启现有 Agent 间内层 TLS，拒绝 endToEndEncryptionEnabled=false；原始 TCP/UDP 的可选加密行为保持。
- 首阶段建议一通道一入口、HTTP/1.1；不增加共享 443 域名路由、HTTP/2/3、gRPC、CONNECT 正向代理、正文/Cookie/重定向改写、自动证书签发、请求重放、热更新或新的写入型仪表盘。

## 3. 功能要求

### FR-HTTP-01 模式、能力与兼容

缺少 applicationProtocol 时保持 raw。HTTP 仅允许 transportProtocol=tcp。新增 HTTP 能力要求目标 Agent 理解应用模式、策略及入口上下文；HTTP 互访还要求访问方理解映射模式和入口 scheme。必须明确协商能力与版本，旧端不得把 HTTP 通道当普通 TCP 下发或运行。继续支持已有 v3/v4 的 raw TCP/UDP 和互访，具体矩阵见设计。

### FR-HTTP-02 集中配置与目标代理

通道、HTTP 策略、目标地址、访问映射仍由 Server 集中维护，经认证控制连接下发不可变快照；Agent 不自行维护通道业务配置。目标 Agent 将已经授权的业务流交给专用 HTTP 入口，再固定转发至管理员定义的 Web 目标。业务入口与 Agent 状态页隔离，不创建可绕过隧道或映射授权的本机 HTTP 监听。

HTTP 策略及证书采用重启激活，不新增热更新。desired、active 与 HTTP readiness 明确区分；策略不一致、证书缺失或能力不足时拒绝该 HTTP 通道，不影响其他兼容通道。入口端口按底层 TCP 检查冲突；互访端口仍由访问方 Agent 选择并报告。

### FR-HTTP-03 Host 与 Origin

目标 Agent 提供 Host 的 upstream（默认）、preserve、fixed 策略，校验转换前的原始 Host。公网与互访分别限定允许的外部 authority；互访非默认端口以当前已确认映射入口为准，不放宽为任意 localhost 端口。

Origin 默认 preserve；fixed 必须先通过非空的允许来源列表，再覆写固定值。比较包含 scheme、host、有效 port，无路径，拒绝通配符、null、非法或多值。普通无 Origin 请求保持缺失，WebSocket 无 Origin 握手拒绝。公网与互访通道都接受 WebSocket，因此对应的允许来源列表必须非空；列表为空不得解释为允许任意 Origin，此类 WebSocket 在运行时拒绝。普通非 WebSocket 请求仅在列表非空时匹配已有 Origin。Upgrade 按逗号分隔的协议标记识别 websocket，不能只比较整段是否等于 websocket。两种访问方式可有不同允许来源，但使用同一目标通道转换规则。禁止拿来源校验替代映射授权、目标登录或 CSRF 保护。

优先配置目标应用实际公开来源；覆写为兼容手段，不自动改 Referer、添加宽松 CORS 头或取消目标身份认证。HTTPS 上游 Host 与 TLS 名称关系须明确且验证。

### FR-HTTP-04 WebSocket 与流式

访问授权及互访内层 TLS/访问证明完成后，目标 Agent 才接收 HTTP；Host、原始 Origin、请求配额校验完成后才连接 Web 上游。支持 HTTP/1.1 Upgrade、子协议、双向帧及正常/异常关闭。升级后不修改帧内容，不全量缓冲流式上传、下载或 SSE。WebSocket 活动空闲默认 300 秒；0 表示无限，仅互访通道可以显式配置，公网入口禁止。maxWebSocketConnections 为 0 时与通道连接上限相同，设为正数时单独限制并发 WebSocket，且不得超过通道连接上限。连接断开、取消、服务停止及配置失配均释放资源一次，不恢复原请求或重放载荷。策略失配、通道禁用或移除时释放并移出对应 HTTP 宿主，不把已释放宿主留在内存中。

### FR-HTTP-05 TLS 终止与证书

公网 HTTPS：浏览器 TLS 原样经 Server 和普通数据隧道，在目标 Agent 终止；私钥不放 Server、不通过控制快照下发。公网 HTTP 没有该 TLS 保护，只能用于受保护网络或显式接受明文的测试，不作为敏感服务公网默认入口。

HTTP/HTTPS 互访：始终保留访问方 Agent→目标 Agent 内层 TLS、固定目标身份及访问证明。HTTP 浏览器字节位于该加密链路内；HTTPS 浏览器 TLS 再嵌套于其内，在目标 Agent 终止。两层 TLS 的身份与证书用途独立，禁止关闭内层 TLS 来迁就 HTTPS。

目标 Agent→Web 上游独立选择 HTTP 或 HTTPS；HTTPS 校验链和名称，HTTP 仅在受信目标段使用。互访加密的端点是 Agent，不自动覆盖浏览器→访问方 Agent 或目标 Agent→上游。

互访 HTTPS 须提供浏览器访问名称、loopback 解析和受信 SAN 证书的可行方案；不能承诺任意随机 localhost 入口自动通过浏览器证书校验。证书/信任材料由部署方预置到目标 Agent 的受限资源目录，Server 只配置资源标识；材料缺失、过期、名称不符必须失败。

### FR-HTTP-06 来源上下文、资源与诊断

可信来源由已认证、绑定到连接和配置版本的上下文提供，不取自客户端 Forwarded/X-Forwarded-*，也不取自 X-Real-IP、True-Client-IP、X-Client-IP、CF-Connecting-IP。这四项与 Forwarded/X-Forwarded-* 始终删除；stripRequestHeaders 可再追加精确头名称，不能用空列表重新打开内置项。公网保留 Server 观察到的来源 IP；互访保留已证明的 callerClientId/mappingId，不将目标 Agent、Server 或 loopback 地址伪装成浏览器公网 IP。

沿用隧道连接/pending、客户端/通道配额和审计门槛；在目标 Agent 增加头、体积、并发请求、TLS 与流式超时限制。Server 只记录隧道信息，HTTP 请求状态与拒绝类别由目标 Agent 记录，不记录 Authorization、Cookie、查询、正文或密钥。目标 Agent 配额与上游池不得因 Server 不解析 HTTP 而遗漏。

## 4. 验收草案

保留 A85–A91 编号；以下是完整验收范围，本机与 CI 的实际证据和目标环境缺口见 [HTTP 验证记录](../testing/http-reverse-proxy-verification.md)，不把未执行的浏览器业务或长期观测标为通过。

| 编号 | 验收标准 | 所需证据 |
|---|---|---|
| A85 | raw 缺省、版本/能力隔离；旧 v3/v4 保留原能力且不能误运行 HTTP；互访 HTTP 禁止关闭加密 | 配置/摘要与新旧 Server/Agent 进程矩阵 |
| A86 | 公网与互访分别覆盖 HTTP/HTTPS，固定目标与入口隔离；策略只在目标 Agent 执行，无旁路 | 四种入口真实进程、端口/模式/上下文负例 |
| A87 | 两种入口的三种 Host 策略、原始 Origin 允许/拒绝/缺失/null/多值；映射换端口正确；上游 TLS 名称约束 | 目标请求捕获、来源负例、真实浏览器 TLS |
| A88 | 两种入口 ws/wss、子协议、双向数据、关闭、中断、SSE 与慢读写；嵌套 TLS 不破坏流 | 实际客户端与长期资源观测 |
| A89 | 公网 TLS 在目标 Agent 终止；互访内层 TLS 不降级；证书链/名称/私钥边界、来源上下文抗伪造 | 抓包、证书正负例、上下文/访问证明测试 |
| A90 | 离线、审计失败、队列/配额、目标 TLS、配置失配、断线、取消、池过期和停止正确失败，无重放且资源释放一次 | 故障注入、目标请求计数及内存/句柄观测 |
| A91 | 集中下发、资源引用、重启就绪、状态与最终示例一致；两种模式平台兼容 | Server Windows/Linux、两侧 Agent Windows/Linux/macOS 验证 |

## 5. 当前交付

已按目标 Agent 方向实现 YARP/Kestrel 虚拟入口、v5 能力隔离、两种访问模式及 Host/Origin 策略，保留 raw TCP/UDP 和互访加密。配置及部署资源说明见 [运维](../operations/http-reverse-proxy.md)，执行结果见 [验证](../testing/http-reverse-proxy-verification.md)。暂不发布；真实业务浏览器及长期容量不在本机测试结论内，需求保持 Accepted，未标为 Done。
