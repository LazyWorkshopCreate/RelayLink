# ADR-0033：可审计 HTTP 使用 Kestrel 与逐请求转发

状态：Accepted\
日期：2026-10-31

## 背景

[IR-01](../testing/plugin-system-independent-review-2026-10-31.md) 复现了首请求允许后，后续 HTTP 请求绕过规则和审计；IR-08 复现了预读请求字节漏计。首包解析后转为无限制字节复制，无法成为 HTTP 策略的安全边界。本决定落实已授权实施的[改进方案 B1](../development/plugin-system-improvement-plan.md)，不改变既有目标 Agent HTTP 反向代理的位置。

## 决定

- Server 引用与 Agent 相同的 `Yarp.ReverseProxy 2.3.0`。已认证的调用侧流通过虚拟 `IConnectionListener` 接入 Kestrel，仅启用 HTTP/1.1，不新增网络监听端口。每条可审计连接拥有自己的虚拟宿主、上游客户端和清理生命周期，连接数继续受外层配额限制。
- Kestrel 负责完整消息边界、请求体和流水线解析。每个请求在交给 YARP 前完成规则匹配、必要的有界等待及允许审计；未允许的请求不向目标发送头或正文。首次拒绝不调用目标连接回调。
- YARP 使用 `HttpMessageInvoker` 流式转发，禁止代理发现、Cookie 容器、自动重定向和自动解压。HTTP 版本固定为 1.1；保留应用 Host，不添加第二套目标路由。处理 Content-Length、chunked、`Expect: 100-continue` 和最终响应；中间 1xx 不被错误记成最终状态。WebSocket 仅在其升级请求获准后进入双向传输，不解释帧内容。
- 每条 RelayLink 连接只允许取得一次上游流。`ConnectCallback` 的第二次调用直接失败，不能因池连接失效重连或重放请求。上游关闭后关闭调用连接，后续请求由调用方新建业务连接；不恢复原连接。
- HTTPS 在接入 Kestrel 前终结调用侧 TLS。允许审计后才取得目标腿、发起上游 TLS 并校验名称与信任链，然后发送 HTTP 请求。证书路径使用运行时解析值，磁盘保存方式由 B4 负责。
- 自定义上游 CA 只替换信任根，不放宽证书用途。重建信任链时仍要求 TLS serverAuth 用途；仅含 clientAuth 的叶证书必须拒绝，名称校验保持有效。没有 EKU 扩展的证书按 X.509 的不限用途语义处理。
- 拒绝有歧义的 CL+TE、重复 Host/Content-Length、未支持的传输编码、CONNECT、非 WebSocket 升级和非 origin-form 请求目标。Kestrel 在移除冲突 Content-Length 时保留的 `X-Content-Length` 标记一律拒绝，包括调用方自己提供该头的情况。
- 头部、缓冲区和审批并发有上限，正文流式处理。首次输入、TLS 建立、审批、阻塞写和半关闭使用各自期限，不把审批时限或建立时限施加于整场业务转发。HTTP 与 SSH 共享 256 个全局未决审批名额，后续操作还服从外层提供的等待配额。
- 字节计量取目标侧应用流边界：成功写入的 HTTP 请求字节、从目标读取的 HTTP 响应字节，包含头部与消息编码，排除腿部和应用 TLS 开销。逐请求结果保留该边界的增量，避免首包漏计。它不等于原始调用方报文字节数，因为转发器会正规化 HTTP 编码及逐跳头。
- 每个进入策略处理的请求均输出独立结果：`success`、`denied`、`failed` 或 `cancelled`，并保留请求 ID、原因、状态和字节增量。`success` 表示转发器完成本次操作，不表示业务状态码必为 2xx。已开始返回 200 但响应体截断时仍记 `failed`；故障发生且没有开始最终响应时状态记 0，不把框架默认 200 当成成功。结果回调使用独立的 5 秒收尾令牌，业务取消、撤销或 abort 不抢先取消结果审计；审计本身不可用则向连接所有者传播故障，不重试业务操作。

## 备选

- 继续扩展自制“首头解析后复制”：无法安全识别后续请求、请求体和升级，拒绝。
- 完整自研 HTTP/1.1 请求/响应状态机：需要独立维护消息歧义、临时响应、背压和错误恢复，当前已有成熟组件和虚拟宿主模式，不采用。
- 禁用全部 keep-alive：可以作为明确声明的短期限制，但仍必须准确处理首请求边界；本次实现保留逐请求复用和流水线。

## 后果与验证

策略拒绝返回 HTTP 403 并关闭连接；解析或不支持的消息返回 4xx，严重转发故障直接关闭。上游关闭后的新请求不透明重连。这些行为只作用于两类可审计 HTTP/HTTPS 通道。

每连接虚拟宿主的资源由通道/全局连接配额约束，仍需后续容量测量，不能将本次正确性测试写成容量结论。RDP、管理员审批闭环和桥应用不由本决定实现。

正式反例与执行结果见 [HTTP 修复验证](../testing/plugin-http-remediation.md)。实现依据：[YARP 直接转发](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/direct-forwarding?view=aspnetcore-10.0)、[Kestrel 10 的 HTTP/1 消息体处理](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Servers/Kestrel/Core/src/Internal/Http/Http1MessageBody.cs)、[YARP 2.3 转发实现](https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Forwarder/HttpForwarder.cs)。
