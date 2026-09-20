# macOS Agent 部署

RelayLink 为 macOS 提供两个自包含 Agent 包。Intel Mac 使用 `RelayLink-Agent-osx-x64-<version>.tar.gz`，Apple Silicon Mac 使用 `RelayLink-Agent-osx-arm64-<version>.tar.gz`。可用 `uname -m` 判断架构：`x86_64` 对应 `osx-x64`，`arm64` 对应 `osx-arm64`。两个版本使用同一配置 schema、协议和通道模型，不包含 macOS 专用通道逻辑。

当前发行包未做 Developer ID 签名和 Apple 公证，不能视为面向不受管设备的正式签名发行版。生产交付应在发布链路中加入签名、公证和 stapling，并按组织的 Gatekeeper/MDM 策略安装；不要通过全局关闭 Gatekeeper 绕过校验。

## 目录和账号

建议由管理员或 MDM 创建专用的隐藏服务账号与同名组 `_relaylink-agent`，并为它分配本机未占用的 UID/GID。不同组织的目录服务和 UID 规划不同，本仓库不提供硬编码 UID 的账号创建命令。

按以下方式准备目录：

- 程序解压到 `/Library/RelayLink/Agent`，所有者为 `root:wheel`，目录和程序分别保持 `0755`，服务账号不可写。
- 配置、Agent 生成的端到端身份、端口状态和诊断日志放在 `/Library/Application Support/RelayLink/Agent`，所有者为 `_relaylink-agent:_relaylink-agent`，目录为 `0700`，配置为 `0600`。
- 标准输出目录 `/Library/Logs/RelayLink` 由 root 创建，所有者为 `_relaylink-agent:_relaylink-agent`，权限为 `0750`。

从管理页面下载该客户端的 Agent JSON，保存为 `/Library/Application Support/RelayLink/Agent/agent.json`。配置包含客户端密钥，不得提交到仓库、复制到程序目录或授予其他用户读取权限。安装服务前先以前述服务账号执行配置检查：

```sh
sudo -u _relaylink-agent /Library/RelayLink/Agent/RelayLink.Agent \
  --config '/Library/Application Support/RelayLink/Agent/agent.json' \
  --check-config
```

## 安装 launchd 服务

将 [relaylink-agent.plist](relaylink-agent.plist) 安装为 `/Library/LaunchDaemons/com.relaylink.agent.plist`，所有者设为 `root:wheel`，权限设为 `0644`。加载前检查格式：

```sh
sudo plutil -lint /Library/LaunchDaemons/com.relaylink.agent.plist
sudo launchctl bootstrap system /Library/LaunchDaemons/com.relaylink.agent.plist
sudo launchctl enable system/com.relaylink.agent
sudo launchctl kickstart -k system/com.relaylink.agent
```

查看运行状态和最近日志：

```sh
sudo launchctl print system/com.relaylink.agent
log show --predicate 'process == "RelayLink.Agent"' --last 30m
tail -n 100 /Library/Logs/RelayLink/agent.stderr.log
```

Agent 本机只读状态页默认监听 `http://127.0.0.1:18081/`；实际端口以 Agent JSON 中的 `dashboardPort` 为准，设为 `0` 时关闭。它只用于查看状态，通道仍由服务端统一维护。

## 升级和卸载服务

升级时先停止服务，仅替换 `/Library/RelayLink/Agent` 中的程序文件，保留 Application Support 中的配置、身份和端口状态，然后重新加载：

```sh
sudo launchctl bootout system/com.relaylink.agent
sudo launchctl bootstrap system /Library/LaunchDaemons/com.relaylink.agent.plist
sudo launchctl kickstart -k system/com.relaylink.agent
```

移除服务时执行 `sudo launchctl bootout system/com.relaylink.agent`，再删除 plist 和程序目录。包含密钥与身份的 Application Support 目录默认保留，需由管理员在确认不再恢复客户端后单独备份或安全清理。

## 验收边界

安装后应确认管理页面显示客户端在线，本机状态页可打开；重启 macOS 后服务自动上线、身份和本机互访入口端口保持不变。Intel 与 Apple Silicon 均须在真实硬件或对应架构的 macOS 虚拟机验证 launchd 生命周期、文件权限、Gatekeeper、断网重连和 TCP 转发。其他平台上完成交叉发布只能证明产物可生成，不能替代上述实机验收。

日常 CI 会在 GitHub 托管的 Intel 与 Apple Silicon macOS runner 上执行 [test-macos-agent.sh](../../scripts/test-macos-agent.sh)，原生验证发布架构、配置检查、launchd 生命周期、异常退出恢复、目录权限、本机状态页和身份复用。该脚本包含系统账号和 `/Library` 目录操作，只允许 GitHub-hosted runner 执行。托管 runner 无法在重启后延续同一作业，也不能模拟真实睡眠/唤醒或浏览器下载产生的 quarantine 属性，因此这些项目仍需自托管或实际设备验收。
