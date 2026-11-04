# ADR-0032：SSH 目标身份与逐动作放行门槛

状态：Accepted\
日期：2026-10-31

## 背景

[独立审查](../testing/plugin-system-independent-review-2026-10-31.md) 的 IR-02、IR-03 复现了陌生 SSH 主机接收目标口令，以及允许审计失败后仍执行命令。IR-05 与原审查 CR-06、CR-16 又指出策略配置被拒、channel 初始化状态共享和审计任务无人观察。用户已明确要求按[改进方案](../development/plugin-system-improvement-plan.md)分批实施。

服务端到目标 Agent 的 TLS 只证明 Agent 身份，不能证明 Agent 后方 SSH 服务的主机身份。此外，当前依赖包 `Microsoft.DevTunnels.Ssh 3.12.42` 的 channel `PipeAsync` 会注册请求转发处理器；在策略处理器之后注册它会覆盖请求响应任务。实现必须保留唯一的动作授权入口，不能依赖这个方法承担受审计的请求转发。

## 决定

1. `inspection.ssh.targetHostKeySha256` 保存管理员提供的目标主机公钥 SHA-256 指纹。格式使用 OpenSSH 的 `SHA256:` 加无填充 Base64，摘要恰为 32 字节。摘要输入为 SSH 公钥线格式，不是 PEM 文本，也不是 RelayLink 前门主机键。
2. 不自动信任首次连接的目标。陌生键或变更键必须在发送目标口令或进行用户私钥认证之前拒绝。通过主机认证后才从秘密库取得目标认证材料。
3. 缺少指纹的既有 SSH 通道保持可加载，但不接受业务连接；运行时失败关闭。管理层显示配置不完整，管理员通过可信渠道取得目标公钥后补充。非法非空指纹是配置错误。首期只支持单指纹；CA、自动登记和多键轮换不在本次实现范围内。
4. 允许决定必须先持久化，再首次连接目标、打开目标 channel 或发送 shell/exec/subsystem。目标认证和动作是否被接受分别记录为结果，不把允许决定写成已经执行成功。
5. 每个 caller channel 独立保存初始化请求、目标 channel 和传输状态。初始化队列最多 32 项、累计 64 KiB；每条 SSH 连接最多 32 个 channel。每个 channel 同时处理的请求任务最多 64 个，超限关闭会话。channel 数据接收窗口限制为 64 KiB，并在读取后返还窗口，保持背压。
6. `pty-req` / `env` 仅在动作启动前缓存；消费后清除。`window-change` / `signal` 仅转发到已经启动的对应 channel，不缓存到后续 channel。每个 channel 仅接受一个 shell/exec/subsystem 启动动作，其他请求仍经过策略门槛。
7. 应用数据、EOF、关闭由 RelayLink 自己管理，不调用会附加请求转发处理器的 channel `PipeAsync`。先发送动作成功响应，再启动向调用方输出数据，兼容收到成功响应后才安装读取器的客户端。
8. TCP、agent、X11 forwarding 尚无完整桥接实现，三个 `allow*Forwarding=true` 配置均明确拒绝；运行时也防御直接构造的不受支持策略。默认拒绝保持不变。
9. 所有认证、请求审计和数据转发任务归属本次会话，异常被观察并进入终止路径。使用独立的建立、目标认证、审批和写入期限；审批使用与 HTTP 共用的全局 256 项容量，结束释放一次。首次审批到期后返回失败并关闭未建立目标的 SSH 会话；已有连接上的审批失败只结束对应 channel。

## 后果

部署升级后必须为要启用的 SSH 可审计通道登记目标指纹。该值是公开的信任材料，不是凭据；通道配置仍不得包含目标口令或私钥。更换指纹属于运行配置修改，按通道撤销既有连接。

SSH 目标主机键与 RelayLink 自己向调用方出示的前门主机键有不同生命周期；不能将两者混用。删除重建后的前门主机键清理由凭据生命周期修复负责。

命令前缀规则仅匹配 exec 字符串。它不提供 shell 沙箱，也不保证阻止通过已允许的 shell 命令进行同等操作。首期审批仍为等待并到期拒绝，不能据此宣称管理员批准界面已实现。

## 关联与验证

- [SSH 修复验证](../testing/plugin-ssh-remediation.md)
- [审计放行门槛](0013-sqlite-audit-gate.md)
- [插件系统设计](../design/plugin-system.md)
- [依赖包固定提交中的 channel pipe 实现](https://github.com/microsoft/dev-tunnels-ssh/blob/e65278de0bf73b7730d82a9469714677f5fc496a/src/cs/Ssh/PipeExtensions.cs)
