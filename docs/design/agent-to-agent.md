# Agent 安全互访技术设计

文档 ID：DES-002\
状态：Draft\
版本：v0.4.0\
更新日期：2026-09-18\
调研日期：2026-09-17

## 1. 范围与调研结论

`Register`/`ConfigUpdate` 通过独立控制入口下发配置；普通 `Open`/`BindData` 路径在数据入口完成绑定后直接复制明文 TCP 字节，服务端可见业务数据。这一路径不满足互访的访问方身份与端到端加密要求。

[TLS 1.3 RFC 8446](https://www.rfc-editor.org/rfc/rfc8446.html) 定义认证密钥交换和应用数据保密/完整性；[.NET SslStream 文档](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream?view=net-10.0) 确认它可包裹任意可读写 Stream。因此在两端 Agent 间的服务端帧中继之上运行内层 `SslStream`，避免自行设计密码协议。证书验证不能返回恒真；访问方对目标证书做固定 SHA-256 指纹校验。访问密钥经控制快照下发，互访配置仍强制开启 Agent↔Server **控制连接** TLS；数据端口不叠加外层 TLS，绑定令牌和元数据需要受信网络保护，业务载荷由内层 TLS 保护。详见 [ADR-0008](../adr/0008-separated-control-and-raw-data.md)。

## 2. 配置模型

每个服务端客户端文件增量字段（旧文件无这些字段时默认维持原行为）：

普通通道的可加载配置格式见 [客户端示例](../../config/examples/client.example.json)。启用授权模式时，管理员在管理页面切换开关，服务端生成独立随机密钥；先按本机 CLI 获取目标 Agent 指纹并在管理侧核对。随后在访问方客户端管理页面添加映射，服务端复制该目标通道的密钥和指纹并下发至访问方 Agent。不得手工将真实密钥写入仓库示例。

```text
channels[].authorizedClientsOnly : bool（默认 false）
channels[].accessSecret          : Base64 恰好 32 字节，仅授权模式必填
channels[].e2eCertificateSha256  : 64 位十六进制，证书登记完成后必填
outboundMappings[]:
  mappingId                      : 本客户端内唯一标识
  enabled                        : bool
  localAddress                   : 固定 127.0.0.1
  targetClientId                 : 已存在且启用的另一客户端
  targetChannelId                : 对方已启用的授权通道
  accessSecret                   : 与目标通道密钥一致的 Base64 值
  targetCertificateSha256        : 固定的对方端到端证书指纹
```

所有字段由 Server 校验、持久化和下发。Agent 本地配置文件仍仅存自身注册所需的 Server 地址、控制/数据端口、客户端 ID、客户端密钥、控制 TLS CA 和重连参数；不存映射或目标访问密钥。目标 Agent 本地端到端证书私钥是自动生成的身份状态，不是业务配置；当前实现将 PFX 写入 Agent 本地配置文件所在目录，用 Windows 当前用户 ACL 或 Unix `0600` 权限保护。部署时须将 Agent 配置放在受保护的数据目录，不提交仓库或由 Server 下载私钥。首次启动后可执行 `RelayLink.Agent --config <配置路径> --show-e2e-fingerprint` 获取本机证书 SHA-256 指纹；管理员应带外核对后写入被访问通道。新证书不得自动覆盖已有固定指纹。

端口所有权现由 Agent 持有：基础配置的 `outboundPortRangeStart`/`outboundPortRangeEnd` 默认 20000–59999；首次在范围内选空闲端口，按映射 ID 写入 Agent 配置目录的 `<clientId>.ports.json`。再次启动优先绑定原端口；若占用，扫描范围内其他端口并原子更新状态文件。服务端旧 `localPort` 字段只为读取历史配置保留，不再参与快照或写入新的映射。服务端验证目标引用、密钥相等、指纹相等，并阻止将授权模式通道同时开放原云端代理监听。密钥不进入匿名 API、历史记录或日志；证书指纹作为非秘密元数据可出现在匿名通道 API。受保护管理 API 可生成/保存密钥，但不回显密钥。

Windows Schannel 在运行时需把文件私钥加载到当前用户密钥提供程序，但无需预先安装证书到系统证书库。服务端部署源仍是 PEM，Agent 身份源仍是本地 PFX。

## 3. 协议与状态

沿用 `NTP1` magic，线协议版本升级为 `2`；互访必须两端运行支持这些类型的 Agent，旧 Agent 与新 Server 不兼容。已实现帧：

| 帧 | 发送方向 | 作用 |
|---|---|---|
| `PeerOpenRequest` | 访问 Agent→Server 控制连接 | `requestId`、`mappingId`；**不含业务字节或明文密钥** |
| `PeerOpen` | Server→被访问 Agent 控制连接 | 一次性 `connectionId`、调用方 `clientId`、目标通道及被访问方绑定令牌 |
| `PeerOpenGranted` | Server→访问 Agent 控制连接 | 本次连接 ID、访问方绑定令牌 |
| `PeerOpenRejected` | Server→访问 Agent 控制连接 | 拒绝原因 |
| `PeerBindData` | 两端 Agent→Server 数据连接 | 当前会话、连接、角色及一次性随机令牌，恰好消费一次 |
| `PeerBindAccepted` | Server→两端 Agent | 两条数据连接配对成功，进入只转发密文状态 |
| `PeerMappingStatus` | 访问 Agent→Server 控制连接 | 当前会话与配置版本、实际成功绑定的映射 ID 和 loopback 端口；不含密钥 |
| `Data/Fin/Reset` | 两端 Agent↔Server 数据连接 | 原样转发内层 TLS 记录及结束信号 |

访问方 Agent 已通过现有独立客户端密钥注册；Server 从会话中取得调用方 ID，只按 Server 自己的已确认映射解析目标，不能相信请求自带目标 ID、目标端口或访问密钥。Server 不把访问密钥塞进 `PeerOpen`；被访问 Agent 使用自己的已确认通道快照取密钥。访问方在内层 TLS 握手时验证目标证书指纹；随后被访问方发出每连接 32 字节随机挑战，访问方在 TLS 内返回带域分隔的 HMAC-SHA256 证明，输入包含调用方 ID、目标通道 ID、连接 ID 和挑战。证书固定将挑战交换绑定到目标 Agent 的 TLS 会话；被访问方使用固定时间比较验证证明，验证之前不连接业务目标；双方不得在错误时降级为明文。

内层 TLS Stream 写入到有界帧化适配器，Server 只验证帧类型、长度、配对关系与资源限额，将 DATA/FIN/RESET 原样送给另一个 Agent，不解码内层记录。每个方向只允许一个 reader 和一个 writer；32 KiB DATA 上限、写超时和取消机制沿用现有传输限制。应用 TCP 半关闭不能提前丢弃另一方向回包；本机 Windows 双 Agent 随机字节、半关闭和并发实验已通过，但不能据此声称 RDP 等所有应用均兼容。

业务 TCP 半关闭在内层 TLS 应用数据中表示：每段采用 4 字节大端长度加最多 32 KiB 载荷，长度 0 为单向 EOF；另一方向仍能返回数据。不能通过提前发送 TLS `close_notify` 来表示业务 EOF，因为 Windows Schannel 会过早终止回包。两方向都结束后才关闭帧化隧道。

## 4. 顺序与失效

Agent 每次应用服务端快照后，先绑定本机端口并保存端口状态，再在认证控制连接上上报 `PeerMappingStatus`。服务端校验会话、已确认的配置版本、映射归属、端口范围与同一报告内不重复，然后仅在该会话在线时展示上报地址。配置更新时先确认新版本，再上报新地址；断线时服务端清除会话状态。本机网页默认在 `http://127.0.0.1:18081/`，可用 `dashboardPort` 调整，设为 0 关闭；只读显示当前通道、访问目标及已监听地址，不展示任何密钥。

```text
本机应用 → 访问 Agent loopback Accept → Server 验证已认证会话及 mappingId
        → 目标 Agent 确认目标通道及容量 → 双 Agent 各自主动 BindData
        → Server 配对密文管道 → 内层 TLS + 固定证书 → 密钥证明
        → 目标 Agent 才拨号业务目标 → 双向密文流转发
```

服务端 Bind 建立时限由 `openTimeoutSeconds` 控制；Agent 内层 TLS、授权证明和目标 Connect 各受独立超时与取消约束。两端会话、映射或目标通道禁用时拒绝新请求；已建立连接在数据隧道断开或超时后清理。每次请求生成新的随机连接 ID 和双侧独立随机 Bind token；重放 token、跨会话 token、角色错置都拒绝。服务端监控只累计互访密文字节，不把它冒充业务有效载荷，也不写入普通通道流量历史。

## 5. 验收矩阵

以 [A18–A22](../requirements/requirements.md#新增验收) 为准：双 Agent/多映射/多通道流量隔离、非授权和错误密钥拒绝且目标零 Connect、TLS 指纹错误与篡改拒绝、内层密文抓取、半关闭、大流和并发、配额/取消/重连、动态保存下发、既有云端端口不回归。单机模拟只能验证逻辑；真实跨主机 NAT 与 24 小时容量仍需专项环境，不标为通过。
