# SSH 插件修复验证

文档 ID：TST-011\
状态：Active\
版本：v1.0.0\
更新日期：2026-10-31

## 范围

本批对应[改进方案](../development/plugin-system-improvement-plan.md) B2 的 W02、W03、W06、W07，同时为 B3 提供目标认证、字节计数、审批容量和终态回调。覆盖 IR-02、IR-03、IR-05，以及 CR-03、CR-06、CR-12、CR-16。身份和动作门槛见 [ADR-0032](../adr/0032-ssh-target-trust-and-action-gates.md)。

实现位于 `SshInspection`、`SshInspectionSession`、`InspectionPolicy`；不使用库的 channel `PipeAsync`，避免它覆盖 RelayLink 的请求决策。原有前门口令认证保留，目标主机认证增加固定指纹校验。配置/API 外层、Server/Agent 生命周期和页面状态由对应批次联调。

## 配置

`inspection.ssh.targetHostKeySha256` 为目标 SSH 主机公钥的 OpenSSH SHA-256 指纹。管理员从可信渠道取得目标公钥，可使用 `ssh-keygen -l -E sha256 -f <目标主机公钥文件>` 查看；不能以一次未经核实的网络扫描代替身份登记。该字段不是 RelayLink 前门键指纹。

以下指纹是全零摘要，仅说明字段结构，不对应可连接的目标：

```json
{
  "inspection": {
    "approvalTimeoutSeconds": 60,
    "ssh": {
      "targetHostKeySha256": "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
      "allowShell": true,
      "allowExec": true,
      "allowSubsystem": false,
      "requireApproval": false,
      "deniedCommandPrefixes": ["blocked-example"]
    }
  }
}
```

缺少指纹的存量配置可加载，但 SSH 插件拒绝业务连接。非空指纹格式错误、HTTP 字段混入 SSH 策略、SSH 字段混入 HTTP 策略，以及任何 forwarding 开关设为 true，均拒绝配置。命令前缀最多 64 条，每条 1–1024 字符且无控制字符。策略配置实际文件加载由 B4 的外层校验回归覆盖；本轮没有新增管理页面的策略表单。

## 正式回归与旧实现证据

先新增 `SshInspectionSecurityTests`，再修改产品实现。7 个初始回归在本轮最初构建的旧 Server 程序集上全部失败，证据为本机忽略目录 `work/b2-evidence/b2-before.trx`：

| 反例 | 旧实现结果 |
|---|---|
| 缺少或不匹配目标指纹，两种情况 | 仍允许目标认证和 exec |
| 首次 exec 前耗尽审计容量 | 请求拒绝前已经打开一次目标连接 |
| 第一次 exec 成功后发送被策略拒绝的 exec | 后续请求被库的请求转发处理器放行 |
| 第二个 channel 的终端初始化 | 所需终端请求未按所属 channel 正确传递 |
| 第 33 个初始化请求 | 继续成功，没有上限 |
| SSH 专属策略 schema | 配置校验报仅允许 HTTP/HTTPS |

原桥接测试补充了有效目标指纹，并让 exec 明确请求响应，避免把 `WantReply=false` 的“发送成功”误当成目标已接受。

修复后的正式测试额外覆盖：forwarding 配置负例、指纹格式、后续 channel 的审计故障、关闭单个 channel 不关闭其他 channel、初始化字节和 channel 数量上限、建立期限短于审批期限、实际 payload 计数与首次目标认证通知，以及目标认证后超过建立期限的数据读取。还验证 shell/exec/subsystem 禁用、subsystem 名称审计、拒绝审计异常由会话拥有者观察，以及关闭审计中的 7/5 字节和退出码 42。

2026-10-31，Windows / .NET SDK 10.0.114，下面命令重新构建当前引用项目后 **26 通过、0 失败、0 跳过**，约 6 秒。结果保存在 `work/b2-evidence/b2-after.trx`。真实 Server/Agent 进程测试由生命周期批次另行记录。

实现过程中的旧桥接回归曾暴露“数据在成功响应之前发送”导致等待的问题，已通过响应完成后启动 pump 修正；计量测试也曾在远端读到数据后立即检查、早于本端写完成回调，现已等待计量回调后验证精确值。这些问题均已纳入最终通过的测试断言，未通过放宽业务结果处理。

```powershell
dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release --artifacts-path work/b2-artifacts -p:SkipAdminWebBuild=true --filter FullyQualifiedName~SshInspection --logger "trx;LogFileName=b2-after.trx" --results-directory work/b2-evidence
```

使用独立 artifacts 目录，避免并行批次争用构建输出。旧程序集验证阶段在首次构建后使用 `BuildProjectReferences=false`；修复验证必须重新完整构建引用项目，不以旧程序集结果代替当前代码结果。

## 生命周期与限制

每个 SSH 连接最多 32 个 channel；每个 channel 初始化请求最多 32 项、累计 64 KiB，请求任务最多 64 项；接收窗口 64 KiB。每个 channel 单独串行处理请求，关闭后取消任务并释放对应状态。数据先等待成功响应，再开始转发，EOF 和关闭有独立收尾。

首次目标认证成功通知由 Server 调用方转换为 active/pending 状态；字节回调只计 channel payload，不计 SSH/TLS 包装开销。每个已启动 channel 结束后写 `inspection_session_closed`，关联连接 ID、SSH channel ID、双向 payload、耗时和可用的目标退出码/信号；关闭审计失败只记录诊断，不假装回滚已经执行的动作。审批期间暂停建立期限，使用独立审批期限及全局有界容量。前门输入、目标握手和阻塞写入仍有期限。

本批测试使用 loopback 上真实 SSH 协议客户端和目标、临时受限目录及测试秘密，不依赖外部 SSH 服务。本文件不把这组模块测试称为完整 Server 与双 Agent 进程矩阵；后者及旧模式回归以生命周期和总验证记录为准。完整审批界面、一次性桥应用登录、凭据发放、SSH 转发桥接及终端全程录制未在本批实现。

2026-11-04 补充回归：env 按 channel 隔离且启动后不重放、signal 只转发到所属 channel、每 channel 65 并发触发 `RequestsInFlight` 上限、双 channel 转发中取消后无残留任务，另有策略跨应用字段/前缀边界/审批期限与真实文件加载 tag 校验。`SshInspectionSecurityTests|InspectionConfigurationPersistenceTests|InspectionConfigurationRegressionTests|ConfigurationTests` 过滤组 **103/103 通过**（重跑一致）。总计数与收口见 [TST-009 §7](plugin-system-remediation-verification.md#7-2026-11-04-复核收口)。
