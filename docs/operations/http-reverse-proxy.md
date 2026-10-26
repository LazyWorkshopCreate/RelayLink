# HTTP/HTTPS 代理部署与验收

文档 ID：OPS-003\
状态：Active\
版本：v1.2.0\
更新日期：2026-10-15\
关联：[需求](../requirements/2026-10-12-http-reverse-proxy.md)、[设计](../design/http-reverse-proxy.md)、[验证](../testing/http-reverse-proxy-verification.md)

## 1. 升级与配置

先升级 Server，再升级目标 Agent；互访还需升级访问方 Agent。HTTP 要求协议 v5，控制连接启用 TLS，HTTP 互访必须 endToEndEncryptionEnabled=true。新 Server 保留 v3 TCP、v4 TCP/UDP；旧 Agent 不接收 HTTP 通道或映射，不会将它们作为 raw 运行。新 Agent 不自动降级连接旧 Server。

Server 客户端配置仍是唯一通道来源。以 [目标配置示例](../../config/examples/http-client.example.json) 和 [访问方示例](../../config/examples/http-caller-client.example.json) 为模板，先替换注册密钥、通道访问密钥和目标 Agent 已固定的身份指纹。示例含占位值且 HTTP 默认禁用，不能原样部署。

公网和互访使用两个通道指向同一目标：web-public 的 authorizedClientsOnly=false，Server 监听业务端口；web-private 的 authorizedClientsOnly=true，只通过访问映射接入，Server 不监听该通道的业务端口。物理 targetHost/targetPort 固定由目标 Agent 连接，http.upstreamAuthority 指定 Web Host 及 HTTPS 名称，不能从浏览器 Host 选目标。

HTTP 策略通过 http 下的 hostMode/hostValue、originMode/originValue 配置。公网允许列表使用实际入口 authority 与 Origin，非默认端口须包含端口，例如 panel.example.test:19443。公网 publicAllowedOrigins 与互访 peerAllowedOrigins 都不能为空：公网和互访都会接受 WebSocket，空列表会在配置校验失败，也不能当成允许任意 Origin。互访 peerAllowedOrigins=["entry"] 表示已确认映射 scheme、host 和动态端口组成的唯一来源；不填写任意 localhost 通配。fixed Origin 必须配置非空允许列表；普通无 Origin 请求不自动补值，WebSocket 缺失 Origin 拒绝。Upgrade 中带逗号或多个值时，只要有一个标记是 websocket，就按 WebSocket 检查 Origin。

webSocketActivityTimeoutSeconds 默认 300。公网通道不能设为 0；互访通道可以显式设 0，表示不因空闲关闭。maxWebSocketConnections 省略或为 0 时等于该通道的 maxConnections，需要给普通请求留额度时再设更小的正数。stripRequestHeaders 只追加要删除的头；Forwarded、X-Forwarded-*、X-Real-IP、True-Client-IP、X-Client-IP 和 CF-Connecting-IP 始终删除。

现有管理页面只扩充 HTTP 模式、就绪状态和 URL 展示，没有 HTTP 策略编辑表单。既有通道编辑保留 ApplicationProtocol/Http，不会将 HTTP 静默改成 raw；修改目标等运行参数仍会使 HTTP 宿主要求重启。新建或修改 HTTP 配置通过服务端 JSON、配置检查、备份及重启完成。

## 2. 目标 Agent 资源

资源目录为 Agent 配置文件所在目录下 state/{profileId}/http-resources，与该 profile 的 identity.pfx 同级目录隔离：

| 配置引用 | 目标 Agent 文件 | 用途 |
|---|---|---|
| certificateRef=panel | panel.crt.pem、panel.key.pem | 入口证书及其私钥 |
| certificateRef=peer-panel | peer-panel.crt.pem、peer-panel.key.pem | 互访浏览器入口证书及私钥 |
| upstreamTrustRef=web-ca | web-ca.ca.pem | HTTPS 上游的自定义根 CA 集合 |

入口 .crt.pem 必须叶证书在前，其后包含部署所需的中间 CA 证书，.key.pem 对应该叶证书；Agent 将中间链交给 Kestrel 的 ServerCertificateChain，不依赖服务端系统信任或 AIA 下载来补链。CA 链材料与上游信任材料也须在有效期内。

资源 ID 只能含受限的小写字母、数字、下划线和短横线，不允许路径。证书及私钥须匹配、有效期当前有效，SAN 覆盖该通道允许的入口名称；缺失、错名或无效证书报告 resource_unavailable。自定义信任只接受无私钥的 CA 证书；省略 upstreamTrustRef 时使用系统信任，HTTPS 上游始终验证链与名称。HTTPS 上游有效 Host 必须与 upstreamAuthority 一致。

部署方为服务账号授予最小读取权限，限制私钥目录其他用户访问；证书和身份不进入 Git、控制快照或 Server。续期先备份并替换资源，再重启目标 Agent，检查新的就绪状态。不要删除或替换 identity.pfx 来续期浏览器证书，两类身份独立。

互访 HTTPS 的 peerAuthorityHost 必须解析到访问方 loopback，并由入口证书 SAN 覆盖。本机入口仍绑定 IPv4 loopback，不接受 IPv6 或非 127.0.0.1 的 IP 字面值。使用 localhost 或包含 127.0.0.1 SAN 的内部 CA 证书时，需要访问方浏览器信任该签发链；自定义域名的 DNS/hosts 由部署方配置。RelayLink 不自动修改系统信任或 hosts，Agent 身份指纹不替代浏览器证书信任。

## 3. 激活与只读状态

按[安装指南](installation-and-usage.md)中的 --check-config 方式检查修改后的 Server 配置，备份当前配置及资源，重启 Server 和相关 Agent，再检查状态。只保存 JSON 或收到 ConfigAck 不表示 HTTP 已激活。

目标 Agent 首次取得快照时加载 HTTP 宿主及资源；运行中策略变化、撤销后重新添加或新增 HTTP 通道要求重启目标 Agent 或显式重建该连接项 Worker，访问方 HTTP 映射变化要求重启访问方 Agent 或显式重建其 Worker。普通控制重连不会热加载策略或证书。既有本机页面的停用/重新启用或导入修改会重建该连接项 Worker，也属于显式重启；它会重新激活 HTTP 配置及资源，不影响其他 profile，未增加新的 HTTP 热加载入口。其他 raw 通道仍按已有配置机制工作。

| HTTP 状态 | 含义 | 操作 |
|---|---|---|
| ready | 指定 policyHash 的入口宿主已激活 | 可发请求；仍可能发生上游连接失败 |
| resource_unavailable | 入口证书、信任或宿主加载失败 | 修正部署资源并重启目标 Agent |
| restart_required | desired 与冻结策略不符或已撤销 | 重启相关 Agent，再核对状态 |
| 无状态 | Agent 离线、旧端无 HTTP 能力或通道禁用 | 核对启用、协议版本及控制连接 |

Agent /api/v2/status 提供各 profile 的通道 HTTP 状态及访问 URL；访问方 URL 仅表示本机映射已绑定，是否能够访问目标还需实际请求验证。Server 只读快照对 HTTP 的 available 还要求目标匹配 readiness，在线状态本身不足以判为可用。动态端口以状态报告为准，不能沿用旧书签端口。

## 4. 本机与业务验收

先验证四种入口实际请求返回：公网 HTTP/HTTPS、互访 HTTP/HTTPS。HTTPS 使用正常证书信任与名称验证，不能通过跳过校验验收。上游捕获应显示预期 Host/Origin；用未知 Origin 请求应返回 403，未知 Host 应返回 421，且不连接 Web 上游。ws/wss 验证子协议、双向数据与关闭；SSE/下载应在响应完成前收到首块。

Server 不解析 HTTP。Agent 离线、ACL/能力/readiness 不满足时，Server 在 HTTP/TLS 前断开，不能期望明文 503；目标入口来源拒绝、体积限制分别返回 403/413，上游 TLS 或连接失败返回 502。默认头/浏览器 TLS 握手限时 15 秒、请求体 30 MiB、头 32 KiB、普通活动空闲 120 秒、WebSocket 活动空闲 300 秒。控制会话断开、撤销或策略变更会终止连接，并释放对应 HTTP 宿主。策略变更后的通道保持 restart_required，直到相关 Agent 重启。

首版不复用上游连接，避免失效池连接触发内部请求重试；每个普通请求会建立新的上游 TCP/TLS，WebSocket 升级后持续复用其专用连接。该取舍未做生产容量压测。不要根据 echo 通过就认定真实应用的 Cookie、公开基址、登录、CSRF、Referer、重定向或浏览器安全上下文兼容，RelayLink 不自动修改这些响应和正文。

本轮可重复自动化命令、进程隔离和清理方式见 [HTTP 验证](../testing/http-reverse-proxy-verification.md)。故障日志仅记录通道、连接、调用方/映射、状态码、耗时及取消，不包含 URL 查询、Authorization、Cookie、正文或私钥。
