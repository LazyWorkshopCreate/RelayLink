# ADR-0015：macOS Agent 双架构交付

状态：Accepted
日期：2026-09-20

## 背景

RelayLink Agent 的 Generic Host、配置、控制连接、数据隧道和本机状态页没有依赖 Windows 或 Linux 专用通道语义。现需在 macOS 上运行同一标准 Agent，并同时覆盖 Intel Mac 与 Apple Silicon Mac。macOS 的后台服务管理和软件信任链与现有 Windows Service、systemd 交付不同，需要明确发布与运维边界。

## 决定

- Agent 正式增加 `osx-x64`（Intel）和 `osx-arm64`（Apple Silicon）两个自包含发布包；不增加 macOS Server 发布。
- macOS 与 Windows、Linux Agent 使用同一配置 schema、线协议、身份、通道、互访加密开关、端口状态和本机只读状态页，不增加平台专用通道逻辑。
- 使用系统级 launchd daemon 托管，以独立低权限账号运行。程序目录由 root 管理；配置、客户端密钥、端到端身份和端口状态放入仅服务账号可读写的 Application Support 目录。
- tag 发行流水线在 macOS runner 上交叉生成两个架构的压缩包。仓库提供 launchd plist 和人工部署说明，不在第一期实现图形安装器或通用二进制文件。
- 初始发布包未做 Developer ID 签名和 Apple 公证。正式分发到受 Gatekeeper 管理的设备前，发布方必须补齐签名、公证与 stapling；仓库不指导全局关闭 Gatekeeper。

## 备选方案

- 仅发布 Apple Silicon：不能满足仍在使用的 Intel Mac，拒绝。
- 发布 Universal 2 单文件：会放大产物并增加合并与签名流程；两个明确架构包更容易验证和回滚，首期不采用。
- 为 macOS 单独实现协议或管理页访问路径：会产生行为分叉并绕过既有安全模型，拒绝。
- 只支持前台运行：不满足重启自动恢复和低权限运维要求，拒绝。

## 影响

发行资产由四个增加到六个。macOS 设备需按 CPU 架构选择正确包，并由管理员预置服务账号、目录权限和 launchd 配置。非 macOS 构建机能够验证双 RID 发布，但 launchd、Gatekeeper、文件权限、睡眠/唤醒和真实 TCP 行为必须分别在 Intel 与 Apple Silicon macOS 环境验收。

相关部署步骤见 [macOS Agent 部署](../../deploy/macos/README.md)，需求和验收见 [REQ-001D FR-13](../requirements/2026-09-20-cross-platform-agents.md#fr-13-macos-agent2026-09-20)。
