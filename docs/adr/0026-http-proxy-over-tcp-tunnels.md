# ADR-0026：目标 Agent 执行 HTTP 代理并复用普通与加密互访隧道

状态：Accepted\
日期：2026-10-12

## 背景

用户明确要求公网访问与客户端授权互访都支持 HTTP/HTTPS、Host/Origin 覆写，按目标 Agent 处理 HTTP 的方向重新评估，保留互访链路加密，并于 2026-10-12 授权分批实现及本机验收。Server 无法在保留密文中继时执行互访 HTTP 头转换；两种模式应在目标 Agent 统一处理。本 ADR 的原 Server HTTP 方案尚为 Proposed，按最新方向修订，不改写已 Accepted 的 TCP/UDP 决定。

## 决定

- 保留 raw TCP/UDP 与现有访问边界；目标 Agent 使用独立 Kestrel + YARP IHttpForwarder，Server/访问方 Agent 不处理 HTTP。
- 优先以自定义 Kestrel Transport 接收已授权业务流，不创建能绕过授权的裸 loopback 代理端口。连接绑定 profile、通道、配置版本和可信来源上下文，不能由 HTTP 头选目标。
- 公网 HTTPS 经 Server 原样到目标 Agent 终止；HTTP 是明文，敏感公网服务采用 HTTPS。目标 Agent→Web 上游 TLS 独立验证，不把入口 TLS 自动延伸至上游。
- HTTP 互访强制保留既有 Agent 内层 TLS、固定身份和访问证明；证明后才进入 HTTP 入口。浏览器 HTTPS 嵌套其中并在目标 Agent 终止，不以其存在为由关闭互访加密。
- Host/Origin 按目标通道集中下发，原始来源先校验再覆写；公网/互访分别限定允许入口，映射动态端口使用已确认 entry 上下文。HTTPS 上游有效 Host 与逻辑 TLS authority 初期保持一致。
- 入口证书私钥、上游信任资源预置于目标 Agent 受限目录，Server 只维护资源 ID；不复制私钥到快照、不自动安装浏览器根证书。互访 HTTPS 明确 loopback 名称解析与 SAN/信任部署要求。
- HTTP 新增版本/能力门槛与 readiness，使用 v5；旧 v3/v4 保留已有 raw 能力，过滤不支持 HTTP 通道/映射，不静默降级。HTTP 控制连接要求 TLS，互访两端都须支持 HTTP 模式。
- 第一阶段 HTTP/1.1、独立入口、策略/证书重启激活，不增加热更新、写入型仪表盘、共享域名入口或请求重放。

## 备选

1. Server YARP：公网可行，但互访需解密或另造旁路，不能统一满足当前要求，不采用。
2. 访问方 Agent YARP：能保留隧道加密，但公网缺少访问方 Agent，需要两套策略执行点，不采用。
3. 目标 Agent 裸 loopback HTTP 监听：适配简单，但本机授权旁路与来源关联风险高，只用于隔离 PoC 对照；若虚拟传输不可行，必须另评审受认证接入方案，不能直接退回裸端口。
4. Server 终止公网 TLS 后再加密到目标 Agent：引入另一套 TLS/元数据跳点、Server 持私钥且可见明文；先采用浏览器 TLS 透传到目标 Agent。
5. 仅让目标应用允许公开来源：仍是具体故障的优先配置方式，但不能替代通用代理能力。
6. TCP 字符串替换或自写 HTTP：承担拆包、消息边界、Upgrade/TLS 安全，不采用。

## 后果

目标 Agent 成为 HTTP 信任端点，负责入口证书、流式资源、上游连接池和请求诊断，需评估跨平台运行时与发布成本。Server 保持中继/协调职责；HTTPS/互访业务无须在 Server 解密，但明文 HTTP 与连接元数据依然可见，不承诺抵抗失陷控制平面的新信任模型。

HTTP 需要升级相关 Agent、快照/证明版本和就绪状态；不能沿用旧端透明 TCP 兼容假设。自定义 Kestrel 接入、TLS 嵌套、来源上下文、证书名称、动态映射端口、资源释放与无重放需通过 PoC。架构方向已由用户明确采用并授权实施；实现采用协议 v5、YARP 2.3.0 与无 OS 监听的虚拟传输。为避免失效上游连接复用导致内部重试，首版关闭上游跨请求复用；相关证据与未完成的目标环境验证见 [HTTP 验证](../testing/http-reverse-proxy-verification.md)。

## 关联

[需求 REQ-001N](../requirements/2026-10-12-http-reverse-proxy.md)、[设计 DES-008 与一手来源](../design/http-reverse-proxy.md)、[普通流 ADR-0008](0008-separated-control-and-raw-data.md)、[互访加密 ADR-0016](0016-optional-peer-traffic-encryption.md)、[旧端兼容 ADR-0025](0025-v3-client-compatibility.md)、[互访设计](../design/agent-to-agent.md)、[验证状态](../testing/verification-status.md)。
