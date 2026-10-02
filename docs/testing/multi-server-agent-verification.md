# Agent 多服务端验收记录

文档 ID：TST-003\
状态：Active\
版本：v1.0.0\
更新日期：2026-09-28\
对应需求：[REQ-001J](../requirements/2026-09-28-multi-server-agent.md)

## 检查环境与方法

- Windows 开发机执行 .NET 10 单元与集成测试：单元测试 60 项通过；`MultiServerAgentTests` 集成测试 6 项通过。另已执行启用互访 TLS 的完整集成测试，35 项通过。
- Windows Sandbox 中安装官方签名的 VC++ 与 .NET 10 运行时，然后执行本次编译的 Windows 安装包。测试配置使用虚构客户端和密钥；安装后的服务端地址不可达，安装器验收只检查本机格式、状态、权限和服务启动。
- 旧版安装升级使用同路径 Windows Service、旧格式配置与旧式身份/端口状态构造环境；未在 Sandbox 中安装历史版本的二进制。此构造直接覆盖新版安装器读取和迁移的旧状态接口。
- 所有 Sandbox 实例在检查后关闭；宿主机正在运行的 `RelayLinkAgent` 未被停止或修改。测试日志留在被 Git 忽略的 `artifacts/sandbox-test-output/`，其中的测试配置也只含虚构密钥。

## A61–A69 结果

| 编号 | 结果与证据 |
|---|---|
| A61 | 两个独立服务端同时在线并分别应用快照；128 KiB 并发普通通道转发验证字节与服务端归属。`MultiServerAgentTests.One_agent_keeps_two_same_id_servers_isolated_during_parallel_traffic_and_one_failure`。 |
| A62 | 相同客户端、通道和映射 ID 在不同密钥及 CA 下独立工作；错误注册密钥与跨服务端数据绑定令牌被拒；身份文件分区且内容不同。`One_agent_keeps_two_same_id_servers_isolated_during_parallel_traffic_and_one_failure`。 |
| A63 | 一项停机或收到错误快照后，另一项的既有半关闭流及新连接继续工作。`One_agent_keeps_two_same_id_servers_isolated_during_parallel_traffic_and_one_failure`、`Invalid_snapshot_update_on_one_server_does_not_interrupt_other_server`。 |
| A64 | 同名互访入口预留同一端口时轮换；端口被其他进程占用只影响对应入口；单项连接配额耗尽不阻断另一项，连接释放后本项可再次接入。互访端口和配额集成测试及 `AgentConnectionQuotaTests`。 |
| A65 | v2 状态按服务端分组，离线入口不可用，读取响应不含密钥；旧配置及身份/端口状态经独立转换器迁入 `state/primary/`，真实互访流复用原端口。`AgentLocalApiTests`、`Same_mapping_id_on_two_servers_rotates_conflicting_loopback_port_and_relays_to_own_target`。 |
| A66 | 旧状态转换分阶段中断、重试和重复执行已有单元测试；旧配置升级后新增第二项并重启已有集成测试。Sandbox “仅更新”后原配置备份、身份哈希和端口状态不变，重复更新仍不变；状态冲突时安装器返回 1、旧文件不变且服务停止。 |
| A67 | 本机页面在线新增、删除、删最后一项、新增不可达项均已集成测试；删除在线项关闭原活动流，不影响另一项及远端记录。`Local_page_adds_and_removes_live_profiles_without_restarting_agent`。 |
| A68 | 本机页面无需管理密码；无会话或 CSRF 令牌及跨站 Origin 写入均被拒。页面、v1/v2 API、成功和错误响应以及双服务端诊断日志均测试了密钥不泄露；新增表单保存后清空密钥输入。Sandbox 配置 ACL 为 LocalService 读取、Administrators/SYSTEM 完全控制；扫描 40 份测试日志/文本未发现虚构注册密钥。 |
| A69 | Sandbox 新装写入新格式且输入/目标 SHA-256 一致、服务运行；旧格式首次安装和重新配置均在预检阶段拒绝且不停止原服务。新格式重新配置清理旧诊断及受限 ACL 的身份并重启；旧下载经独立转换器生成的新文件可重新配置。“仅更新”迁入原身份与端口状态，重复执行不变；冲突失败返回非零。 |

## 边界

Windows Sandbox 只验证本机安装和迁移行为，不验证不可达测试服务端的在线认证；在线认证、TLS、普通转发及互访归属由集成测试覆盖。Linux systemd 和 macOS launchd 的启动链已接入独立转换器，本次未在对应原生系统执行服务管理器验收。需求和设计文档仍为 Draft，验收结果不改变其评审状态。
