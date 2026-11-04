# ADR-0028：服务端分别终结两侧加密的可审计通道

状态：Proposed\
日期：2026-10-28

## 背景

服务端插件要审计 SSH、HTTP 等内容，就必须在服务端看到插件所声明的业务输入。当前有三条相关路径，都不能直接复用：

- 普通代理在数据端口上原始复制 TCP。应用自己的 SSH 或 TLS 对服务端仍是密文，数据端口本身也没有 RelayLink TLS，见 [ADR-0008](0008-separated-control-and-raw-data.md)。
- 授权互访的默认加密是两个 Agent 之间的内层 TLS，服务端原样转发密文，见 [ADR-0016](0016-optional-peer-traffic-encryption.md)。关掉该加密只会变成明文中继，调用方到目标 Agent 的路径上没有服务端这个加密终点。
- HTTP 反向代理故意在目标 Agent 终止 HTTP 和浏览器 TLS，避免服务端看见明文，见 [ADR-0026](0026-http-proxy-over-tcp-tunnels.md)。

用户要求新增两类可选通道：一类类似端到端，但服务端分别处理两侧加密和解密；另一类类似标准代理，服务端作为中间人，并允许未加密应用流量。两类都用于服务端流量审计。

## 决定

- 增加 `inspectionMode`：`none`、`server-peer`、`server-jump`。缺省为 `none`。既有通道保持 `none`，不自动迁移。
- `server-peer` 保留授权互访、映射和访问证明。访问方 Agent 与服务端、服务端与目标 Agent 各自做 TLS，服务端是两侧的加密终点。服务端在两段明文之间运行插件。这替换的是「Agent 到 Agent 的一跳内层 TLS」，不是把 `endToEndEncryptionEnabled` 设为 false。
- `server-jump` 保留服务端监听和安全组。调用方直接连接监听地址。服务端在监听侧终结应用加密或接受明文，在目标侧通过目标 Agent 连接到既有 `targetHost` 和 `targetPort`。服务端到目标 Agent 的数据腿使用 TLS，即使应用本身是明文。
- 腿部 TLS 使用服务端已有证书和 Agent 已信任的控制面 CA。绑定令牌和访问证明仍在应用处理之前完成。TLS 证明的是服务端身份，不代替映射授权。
- 目标 Agent 解开自己这一侧的腿部 TLS 后，把字节原样写到目标 TCP。应用层的 SSH 或 HTTPS 重新加密由服务端插件完成，不由目标 Agent 解析。
- 插件失败、策略拒绝或模式不被对端支持时关闭连接，禁止降级成 `none` 的原始复制或旧内层 TLS。
- 新模式使用新的协议版本隔离。旧 Agent 的快照中不出现这些通道和依赖它们的入口。
- 本决定不修改 `none` 通道，也不把现有 HTTP 反向代理改到服务端。
- 通道类型由传输协议、应用转发方式、是否授权互访和 `inspectionMode` 共同决定，只在创建被访问通道时选择。编辑不能改变这个组合；管理端按类型显示对应字段。更换类型必须删除后重建。Agent 间加密开关仍属于端到端互访和 UDP 互访内部选项，可以单独修改。

## 备选

- 只关闭现有端到端加密：服务端能看见字节，但数据腿没有加密终点，也不区分「审计通道」和「受信网络里的明文互访」。拒绝。
- 继续使用 Agent 之间的内层 TLS，在服务端挂插件：服务端看不到内容，审计无法成立。拒绝。
- 把现有 HTTP 反向代理改成服务端解密：与已经采纳的目标 Agent 方案相反，也会让未要求审计的 Web 通道暴露明文。拒绝。
- 在所有数据端口外再套一层 TLS：会改变既有普通代理和互访的延迟与半关闭行为。本次只给两种新模式加腿部 TLS。

## 后果

选择新模式即表示部署方接受服务端在内存中处理该通道的业务明文，并保存插件所需的目标凭据。服务端失陷时，这些通道不再具备 Agent 之间端到端的机密性。`none` 的端到端通道不受影响。

跳板代理的公网监听侧若配置为明文应用，暴露范围与今天的普通代理相同；新增的保护只覆盖服务端到目标 Agent 这一段。HTTPS 和 SSH 的调用方必须信任服务端出示的证书或主机密钥，而不是目标自己的身份。

Server 和参与这两类通道的 Agent 必须升级。旧端继续使用被过滤后的 `none` 配置。RDP 首期即使走新模式，也不在应用层解密，见 [ADR-0029](0029-application-plugins-and-bridge.md)。

## 关联

- [插件系统需求](../requirements/2026-10-28-plugin-system.md)
- [插件系统设计](../design/plugin-system.md)
- [ADR-0008](0008-separated-control-and-raw-data.md)
- [ADR-0016](0016-optional-peer-traffic-encryption.md)
- [ADR-0026](0026-http-proxy-over-tcp-tunnels.md)
