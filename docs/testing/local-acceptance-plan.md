# 本地多场景验收方案

文档 ID：TST-002\
状态：Active\
版本：v1.0.0\
更新日期：2026-09-18

本方案用于在开发机上重复验证控制/数据分离、两个 Agent 和多个普通通道的 TCP 通信，并结合真实进程集成测试覆盖授权互访、管理、安全组及审计。它不替代跨主机部署、Windows Service 安装升级或 24 小时容量验收。验收依据见[需求文档](../requirements/README.md)、[技术设计](../design/technical-design.md)和[当前验证状态](verification-status.md)。

## 1. 隔离与前置条件

- Windows PowerShell 7、.NET 10 SDK、pnpm；首次运行需可访问 NuGet。前端测试还需已安装 `src/RelayLink.AdminWeb` 的依赖。控制 TLS/端到端测试需要当前账号可导入测试证书私钥。
- 不使用仓库中的真实 `.local` 配置，不更改已安装的 `RelayLinkAgent` 服务或现有 18080 管理页。脚本每次在 `.local/local-acceptance/<随机 ID>/` 创建新配置、随机密钥、日志和 SQLite 库，所有监听仅绑定 `127.0.0.1`。该目录被 Git 忽略，包含测试密钥，不得提交或公开。
- 测试脚本发布当前工作树中的 Server、Agent、模拟目标及模拟调用方到本次目录，再启动一个服务端、两个 Agent、三个带不同响应标记的目标。成功后进程保留运行，供人工查看。端口由 OS 选择，实际值只以 `state.json` 为准，不要硬编码。
- 建议先记录 `git rev-parse --short HEAD` 和 `git status --short`。有未提交修改时，验收结论对应工作树而非仅对应提交号。

## 2. 自动化执行顺序

在仓库根目录执行：

```powershell
pwsh ./scripts/start-local-acceptance.ps1 -ConnectionsPerChannel 8 -BytesPerConnection 1048576
dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release --no-restore -p:SkipAdminWebBuild=true
dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release --no-restore -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true
pnpm --dir src/RelayLink.AdminWeb test
git diff --check
```

第一条命令会打印本次 `runDirectory`、管理页 URL、各端口和 PID，并写入 `state.json`。如需更高负载，可调整每通道连接数和每连接字节数，但应记录 CPU、内存、运行时版本和总字节数；默认的 3 × 8 × 1 MiB 只用于功能并发验收，不是容量承诺。脚本报错时会停止本次已启动的进程，保留日志；不要因为一条命令失败而把后续项标为通过。

| 编号 | 场景与执行入口 | 通过标准 |
|---|---|---|
| L01 | 脚本启动与 `/api/v1/overview` | 2 个 Agent 在线、3 条普通通道可接入；本次端口与已有实例不冲突 |
| L02 | 向控制/数据入口发送错误首帧及未认证 Bind | 两个入口均返回拒绝；不能据此建立业务流 |
| L03 | 3 个带 `ALPHA-`、`BETA-`、`GAMMA-` 标记的目标，各发 64 KiB 随机载荷 | 三条通道响应标记准确，随机字节逐字节一致；请求半关闭后收到响应 EOF |
| L04 | 三个调用进程同时运行，每通道默认 8 条连接、每条 1 MiB | 24 条连接全部通过，三个通道标记不串流，两个方向计数非零，最终活动连接归零 |
| L05 | 查询 `/api/v1/history` 与本次 SQLite 文件 | `accept-a/alpha`、`accept-a/beta`、`accept-b/gamma` 都出现分钟样本，彼此按客户端与通道分开 |
| I01 | `ServerTunnelTests` | 控制/数据隔离、认证、原始流、管理写入与 CSRF、安全组、通道变更撤销、连接定向断开、审计持久化与写入故障拒绝均通过 |
| I02 | `PeerConnectionTests`（显式 `EnablePeerTlsTests=true`） | 双 Agent 控制 TLS、互访加密/明文模式、两种模式错误访问证明拒绝、半关闭、双通道并发大流量、在线下发、模式切换/禁用撤销、端口冲突轮换均通过 |
| U01 | 单元测试和前端组件测试 | 两套测试全部通过，不以编译成功代替测试通过 |

脚本完成后可再做只读现场确认：浏览器访问输出的 `dashboardUrl`，或执行 `Invoke-RestMethod <dashboardUrl>api/v1/overview`；匿名访问 `<dashboardUrl>api/v1/admin/audit` 应返回 401。需核对审计事件时，应在本次隔离实例上配置有效测试管理员密码并登录，或查看 I01 的自动化断言；当前脚本的占位密码哈希不能用于登录。

## 3. 失败排查与停止

每次运行的 `server.stderr.log`、`agent-a.stderr.log`、`agent-b.stderr.log`、三份目标日志、并发调用方日志和 `traffic-history.db` 都位于输出的 `runDirectory`。先检查失败场景对应日志与进程是否仍存在，再判断是配置校验、端口占用、目标连接、认证还是存储失败。SQLite 运行时可能存在 `-wal`/`-shm` 文件；不要只复制主 `.db` 文件作为完整备份。

检查运行状态后，用本次打印的绝对 `state.json` 路径停止；先加 `-WhatIf` 可核对将停止的 PID：

```powershell
pwsh ./scripts/stop-local-acceptance.ps1 -StatePath '<runDirectory>\state.json' -WhatIf
pwsh ./scripts/stop-local-acceptance.ps1 -StatePath '<runDirectory>\state.json'
```

停止脚本只处理状态文件记录、命令行仍指向本次运行目录的六个进程；PID 已复用或命令行不匹配时拒绝停止，不删除任何证据文件，也不影响安装的 Windows Service。

## 4. 扩展场景与本轮边界

- SQL Server/PostgreSQL 常见 SQL 可参照[安装与使用指南](../operations/installation-and-usage.md)及现有 `scripts/run-local-database-acceptance.ps1`。它需要 Docker Desktop 和 SQLCMD，会另起容器并留下测试数据；运行前核对脚本与当前配置 schema。旧 `run-local-database-peer-acceptance.ps1` 仍使用已删除的通道级指纹字段，不应直接作为当前版本的通过证据；端到端路径以 I02 为本轮自动化证据。
- 对生产相近负载，应另外测多小时长连接、慢读背压、突发拒绝、进程异常终止后的审计补记、证书轮换、跨主机 RTT/丢包和真实数据库/远程桌面。普通代理是明文 TCP，本方案仅用 loopback；不证明公网链路的机密性。
- Windows Agent 安装包的“仅更新/重新配置”、真实服务重启与旧端到端身份轮换是独立的破坏性验收，需要专用测试机及备份；本脚本不会运行安装包或清空本机已有配置。

## 5. 2026-09-18 本机执行记录

环境：Windows，.NET SDK `10.0.200-preview.0.26103.119`，pnpm `11.19.0`；基线提交 `0948d44`，工作树有未提交改动。本次目录为 `.local/local-acceptance/eaf7463e8c314148b53a7883264f6767/`，管理页为 `http://127.0.0.1:59162/`，控制/数据端口为 59155/59156，三条业务端口为 59186/59191/59192。

- L01–L05：通过。2 个 Agent、3 条通道在线；错误首帧拒绝；3 条通道各 1 条 64 KiB 和各 8 条 1 MiB 并发随机字节、标记隔离与半关闭通过；双向累计分别为 25,362,432/25,362,585 字节，活动连接归零，三个客户端/通道组合都有 SQLite 分钟样本。
- I01–I02：24/24 通过；U01：.NET 单元 36/36、前端组件 6/6 通过。匿名审计查询在本次真实进程上返回 401。`stop-local-acceptance.ps1 -WhatIf` 正确识别六个本次进程，但为便于检查，尚未真正停止它们。
- 未执行本轮 Docker SQL、跨主机、安装包升级、长时间/容量测试；这些不能标为通过。
