# macOS 经 RelayLink 互访通道使用 RDP 静置卡死排查与修复记录

文档 ID：TST-016\
状态：Active\
版本：v1.0.0\
更新日期：2026-11-25

本文记录 macOS 上 `Windows App` 经 RelayLink 加密互访通道连接远端 Windows 远程桌面（`mstsc`，TCP 3389）时，连接静置一段时间后出现画面卡死与重连失败的实机排查证据、根因分析、代码修复及自动化验证结果。所有客户端标识、IP 地址、密钥与凭据均已脱敏。

## 1. 问题现象与排查范围

- **用户侧现象**：在 macOS（Apple Silicon，macOS 27.2.1）上通过 `Windows App`（`11.4.1.3092`）连接本机 `127.0.0.1:<互访映射端口>` 访问远端 Windows 桌面；正常交互时流畅，但静置一段时间（例如息屏或无操作约 15–35 分钟）后再回来操作，画面直接卡死且无法自动恢复，只能关闭会话并重启客户端。
- **链路拓扑**：`Windows App`（macOS） → 本机回环入口 `127.0.0.1:<port>` → macOS 访问端 `RelayLink.Agent` → 服务端数据隧道端口 → 目标 Windows 被访问端 `RelayLink.Agent` → `127.0.0.1:3389`。
- **排查方式**：通过只读诊断核查 macOS 系统日志（`log show`）、电源日志（`pmset`）、网络与 TCP KeepAlive 参数（`sysctl`/`route`/`ifconfig`）、macOS Agent 运行二进制版本、本机 Agent 状态接口、服务端匿名状态与分钟流量历史接口，并对照 `RelayLink` 源码分析连接生命周期。

## 2. 现场证据汇总

### 2.1 系统与配置事实

- **macOS 系统与 TCP 参数**：macOS 默认系统级 TCP KeepAlive 阈值为 `net.inet.tcp.keepidle = 7200000`（2 小时）、`net.inet.tcp.keepintvl = 75000`、`net.inet.tcp.keepcnt = 8`、`net.inet.tcp.always_keepalive = 0`。实测单套接字调用 `SO_KEEPALIVE` 与 `TCP_KEEPALIVE=30`、`TCP_KEEPINTVL=10`、`TCP_KEEPCNT=3` 可成功生效并回读一致。
- **电源与睡眠**：`pmset -g` 显示 macOS 主机关闭了系统休眠（`sleep 0`、`tcpkeepalive 1`），仅在无操作 10 分钟后关闭显示器（`displaysleep 10`）；问题期间未发生整机系统休眠。
- **部署版本**：macOS 上运行的 `RelayLink.Agent` 构建对应 `v0.1.0-rc.12` 基线，与排查前主线中的互访数据转发及保活代码一致。
- **服务端运行参数**：服务端 `/api/v1/overview` 显示当前生效配置为 `tcpIdleTimeoutSeconds = 900`（15 分钟）、`halfCloseDrainTimeoutSeconds = 300`（5 分钟）。

### 2.2 客户端日志与流量时间线

从 macOS 统一日志提取到两次完整复现记录：

1. **场景 A（息屏静置约 35 分钟后恢复）**：
   - 会话建立约 17 分钟后显示器息屏，无界面与键盘/鼠标交互。
   - 亮屏后约 41 秒，连接出现典型传输断链日志：
     - `DynVC: Channel::StartWrite failed`
     - 9 秒后：`TsTcpDctTransport::GetSendBuffer(): Attempt to get buffer while tcp transport is NULL` / `IHMaybeSendPDU(): GetBuffer failed!`
     - 21 秒后：`RDP_WAN: Client connMonitor goto CMSTATE_DROPPED (total-missed=12)....try reconnect!`
   - 随后客户端未能自动重建 TCP 连接，直到手动重启 `Windows App`。
2. **场景 B（底层隧道被中断并静置约 10 分钟后返回操作）**：
   - 底层数据隧道中断瞬间再次触发 `Channel::StartWrite failed` → `tcp transport is NULL` → `CMSTATE_DROPPED (total-missed=12)....try reconnect!`。
   - 服务端分钟流量历史在随后的 10 分钟内显示该 RDP 通道接入数为 `0`（客户端并未实际对本机回环入口重新发起 TCP 建连）。
   - 用户返回窗口尝试输入时，客户端持续抛出数百条 `SendInputInternal failed! / Failed to open input batch!`，表现为窗口完全卡死。

### 2.3 排查范围边界

- macOS Agent 运行在受限服务账号下，其目录权限（`0700`）与 `Windows App` 沙盒容器的 macOS TCC 权限阻止普通用户直接读取落盘日志；上述现场证据全部来自 macOS 统一日志（`log show`）、`pmset`、`lsof`/网络状态、本机 Agent API 与服务端状态/历史 API。

## 3. 根因分析

结合现场现象与仓库源码，确认问题由以下四点叠加造成：

1. **【主因 1】互访（Peer）通道的两段公网数据隧道完全未启用 `TcpKeepAlive`**
   - 普通代理通道在 [ControlSessionWorker.cs](../../src/RelayLink.Agent/ControlSessionWorker.cs) 与 [ProxyListenerService.cs](../../src/RelayLink.Server/Runtime/ProxyListenerService.cs) 中均对公网数据连接与本地目标连接开启了 `TcpKeepAlive.Enable(...)`（30 秒空闲、10 秒间隔、3 次重试）。
   - 但授权互访通道（`mstsc` 等 `authorizedClientsOnly=true` 通道）的两段公网数据隧道完全漏开了 KeepAlive：
     - Agent 侧 [PeerSessionCoordinator.cs](../../src/RelayLink.Agent/PeerSessionCoordinator.cs) 的 `ConnectDataAsync` 建立至服务端数据端口的 TCP 连接后从未调用 `TcpKeepAlive.Enable(client.Client)`，仅在进入转发循环后对本机 `127.0.0.1` 回环套接字开启了 KeepAlive；
     - Server 侧 [DataAcceptorService.cs](../../src/RelayLink.Server/Runtime/DataAcceptorService.cs) 与 [PeerRelayRegistry.cs](../../src/RelayLink.Server/Runtime/PeerRelayRegistry.cs) 也未对互访数据隧道套接字启用 `TcpKeepAlive`。
   - **后果**：RDP 静置无应用字节传输时，客户端网络 NAT / 运营商 CGNAT / 云端防火墙在数分钟空闲后静默回收 NAT 映射或流表项，形成 TCP 半开黑洞；重新操作时，本地内核接收下发的小包但对端永不响应，连接长期处于阻塞或假死状态。

2. **【主因 2】`limits.tcpIdleTimeoutSeconds` 默认 `900` 秒，且 OS KeepAlive 不刷新应用层空闲计时**
   - [ServerConfiguration.cs](../../src/RelayLink.Server/Configuration/ServerConfiguration.cs) 将 `TcpIdleTimeoutSeconds` 默认设为 `900`（15 分钟），服务端在注册时将该值下发给两端 Agent，在 [PeerRelayRegistry.cs](../../src/RelayLink.Server/Runtime/PeerRelayRegistry.cs) 和 [PeerSessionCoordinator.cs](../../src/RelayLink.Agent/PeerSessionCoordinator.cs) 中同时以 `RelayIdleWatch` 计时。
   - 操作系统层 TCP KeepAlive 探测帧不会触发应用层 `idle.Touch()`。因此即使开启了 TCP KeepAlive，只要 RDP 会话静置超过 15 分钟无应用层报文，RelayLink 仍会按配置主动取消该互访连接。

3. **【叠加缺陷】互访转发 `idle.Touch()` 时机、黑洞中止与小包延迟**
   - `PeerSessionCoordinator` 原实现在 `socket.ReceiveAsync` 读到数据后、尚未完成向 `tls`/`framed` 对端写入前即调用 `idle.Touch()`；当公网隧道已进入写阻塞/半开状态时，客户端发出的输入包或心跳包会持续错误刷新空闲超时。
   - `RelayEncryptedSocketAsync` / `RelayPlainSocketAsync` 传给 `RelayPump.CompleteBidirectionalAsync` 的 `abort` 回调只释放本地 `socket`，未显式关闭底层 `PeerDataConnection` 公网连接；同时 `caller`/`target` 本地套接字未设 `NoDelay = true`，加密互访每段还将 4 字节长度头与载荷拆成两次 `tls.WriteAsync`。

4. **【客户端表现放大】macOS `Windows App` 对本机回环断链卡死**
   - 当本机 `127.0.0.1` 入口对应的远端链路断开或黑洞时，macOS `Windows App` 在 `CMSTATE_DROPPED` 后长期停留在 `tcp transport is NULL` 而不重建本地 TCP 连接，导致用户感知为画面完全卡死而非及时弹出重连提示或自动恢复。

## 4. 修复方案与代码改动

本次保持既有协议格式与配置默认值兼容，完成如下修复：

- **[DataAcceptorService.cs](../../src/RelayLink.Server/Runtime/DataAcceptorService.cs)**：在受理每个数据连接后（覆盖普通代理 `BindData` 与互访 `PeerBindData`）立即调用 `TcpKeepAlive.Enable(client.Client)`。
- **[PeerRelayRegistry.cs](../../src/RelayLink.Server/Runtime/PeerRelayRegistry.cs)**：在 `RunAsync` 启动双向帧转发前，显式对 `caller.Socket` 与 `target.Socket` 调用 `TcpKeepAlive.Enable(...)`，确保绕过监听器的绑定路径也开启 KeepAlive。
- **[PeerSessionCoordinator.cs](../../src/RelayLink.Agent/PeerSessionCoordinator.cs)**：
  - `ConnectDataAsync` 建立互访公网数据隧道后调用 `TcpKeepAlive.Enable(client.Client)`；
  - `AcceptLoopAsync` 接受本机访问方连接与 `HandleTargetAsync` 连接目标端口时设置 `NoDelay = true`；
  - 为 `PeerDataConnection` 暴露 `Abort()` 并在 `RelayEncryptedSocketAsync` / `RelayPlainSocketAsync` 的 `abort` 回调中同时中止本地套接字与公网数据隧道；
  - 将 `idle.Touch()` 移至对端写入或本地 `SendAsync` 完成之后，避免写阻塞期间误续命空闲计时；
  - `RelayEncryptedSocketAsync` 使用单缓冲区将 4 字节长度头与载荷合并为单次 `WriteAsync` 写入。
- **[PeerKeepAliveAndIdleTests.cs](../../tests/RelayLink.UnitTests/PeerKeepAliveAndIdleTests.cs)**：新增 6 项单元测试，验证服务端受理口、互访双侧隧道、Agent 数据建连 KeepAlive 与 `NoDelay`、写阻塞不刷新空闲超时与隧道中止、以及长度头载荷合并写与半关闭。
- **[安装与使用指南](../operations/installation-and-usage.md)**：在 §4.3 补充 TCP KeepAlive 覆盖范围、不重置 `tcpIdleTimeoutSeconds` 的行为说明，以及 RDP/SSH 长连接的配置建议。

## 5. 部署、配置建议与验收边界

- **双端同步升级**：TCP KeepAlive 需分别由访问端 Agent、服务端与被访问端 Agent 各自对其持有的公网数据隧道套接字开启；建议同步升级 Server 与相关 Agent，否则未升级一侧的公网段仍无法发送 KeepAlive 探测帧。
- **服务端空闲超时配置**：如需允许 RDP / SSH 等低频交互会话长时间静置不断开，建议在服务端配置中将 `limits.tcpIdleTimeoutSeconds` 设为 `0`（关闭应用层空闲强断，由 `30s / 10s / 3` TCP KeepAlive 负责 NAT 保活与死链回收）或按需调大（例如 `7200` 以上）后重启 Server；默认配置维持 `900` 秒不变。
- **验收边界**：跨公网 NAT/CGNAT 下的长静置保活效果以及 macOS `Windows App` 实机交互恢复仍需在升级部署后的真实链路环境中持续验证；本记录仅代表仓库自动化单元测试与集成测试已通过。

## 6. 自动化验证记录

| 检查命令 | 结果 | 说明 |
|---|---|---|
| `dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Debug --no-restore -p:SkipAdminWebBuild=true --filter "FullyQualifiedName~PeerKeepAliveAndIdleTests\|FullyQualifiedName~PeerRelayReliabilityTests\|FullyQualifiedName~RuntimeCorrectnessTests"` | 22/22 通过 | 覆盖新增 6 项互访 KeepAlive / 空闲超时 / 合并写测试与既有中继可靠性、运行时正确性单测；0 警告 0 错误 |
| `dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release --no-restore -p:SkipAdminWebBuild=true` | 298/299 通过 | 唯一失败项为并行分支尚在修复中的 `SdkDataPlaneTests.EntryDescriptor_ipv6_tcp_half_close_cancellation_and_udp_roundtrip` |
| `dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release --no-build -p:SkipAdminWebBuild=true --filter "FullyQualifiedName!~SdkDataPlaneTests"` | 296/296 通过 | 排除 `SdkDataPlaneTests` 后的全部单元测试（含 `PeerKeepAliveAndIdleTests` 6/6）通过 |
| `dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release --no-restore -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true --filter "FullyQualifiedName~PeerConnectionTests"` | 19/19 通过 | 全部启用的端到端互访加密/明文/UDP/HTTP/半关闭集成测试通过（未启用 `LEGACY_AGENT_TESTS`） |
