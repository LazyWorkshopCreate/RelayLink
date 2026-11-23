---
name: relaylink-admin
description: 通过 relaylink CLI 管理 RelayLink 服务端设置。只读查询、受控写入与变更申请。所有动作仅经 CLI 与端到端加密通道，需服务端控制台人工批准。适用于「查看 RelayLink 客户端/通道/流量」「修改通道显示名或标签」「提交结构性变更申请」这类请求。
---

# RelayLink 管理 skill

本 skill 把 `relaylink` CLI 包装成 AI agent 可用的动作面。**你只能通过 CLI 动作，不得直连 HTTP 端点，也不得自行实现 TLS、证书固定、令牌或配对流程。**

## 前置要求

- 已安装 `relaylink` CLI，且版本在 `>=0.1.0 <1.0.0` 区间。
- 目标宿主需存在 Node.js 运行时（`scripts/relaylink.mjs` 依赖它）。
- 已完成配对：用 `relaylink auth whoami` 自检。若返回退出码 `3`，说明未配对，按下方「首次配对」处理。

## 版本与 scope 检查（先做）

```bash
relaylink auth whoami --json
```

- 退出码 `9`：CLI 与自动化 API 版本不兼容。**停止执行并提示升级 CLI**，不要降级、不要改用手工 HTTP。
- 退出码 `3`：未配对或令牌失效。转「首次配对」。
- 退出码 `4`：当前令牌 scope 不足。**停止**，告知用户需要重新配对并申请哪个 scope，**不得改写参数重试**。

## 首次配对

配对需要两个阶段，中间必须由人在服务端控制台批准：

```bash
relaylink pair --scope clients.read --scope channels.read --json
```

CLI 会输出 `USER_CODE=XXXX-XXXX` 与 `requestId`。**你必须把用户码原样转达给用户**，并明确说明：需要在服务端管理页面的「自动化授权」面板核对这个用户码后点击批准。不要自行批准，不要改写用户码，不要把用户码写入任何文件。

用户批准后（必须带 --request 与 --code 续接原申请，否则会创建第二个申请）：

```bash
relaylink pair --wait --request <requestId> --code <userCode> --json
```

## 风险分级（决定你能做什么）

| 级别 | 内容 | 你的行为 |
|---|---|---|
| A 只读 | 状态、客户端、通道、映射、审计、历史、版本 | 直接执行 |
| B 受控写 | 显示名、tag、上限、超时、启停通道、安全组 | 直接执行，但每次执行后报告实际变更 |
| C 结构性写 | 客户端/通道增删、映射创建、加密模式变更、凭据删除 | **只能提交申请**，批准前不得声称已生效 |
| D 身份与授权 | 签发/撤销令牌、管理员口令、会话 | 不可操作，告知用户只能在管理页面完成 |

**凭据写入（口令与私钥）永不经本 CLI。** 遇到这类需求，明确告知用户只能在管理页面输入。

## 常用动作

```bash
# 只读
relaylink server overview --json
relaylink clients ls --json
relaylink channels ls <clientId> --json
relaylink mappings ls <clientId> --json
relaylink changes ls --json

# B 类：受控写入
relaylink channels set <clientId> --channel <channelId> --display-name "新名称" --json
```

B 类写入前建议先 `--dry-run` 展示计划：

```bash
relaylink channels set <clientId> --channel <channelId> --display-name "新名称" --dry-run
```

## C 类：变更申请（必须先 dry-run + 人工确认）

```bash
# 1. 先看计划，不提交
relaylink changes submit --type channel.create --target <clientId> --payload ./channel.json --dry-run

# 2. 向用户展示计划并取得明确同意

# 3. 提交申请
relaylink changes submit --type channel.create --target <clientId> --payload ./channel.json --reason "为上海节点新增 SQL 通道" --json

# 4. 报告申请 ID 与待批准状态，然后查询
relaylink changes get <changeRequestId> --json
```

提交后只能报告「已提交，等待管理员在管理页面批准」。**不得声称变更已生效**，即使状态是 `approved` —— 只有 `applied` 才是真正执行完成。

## 禁止事项

1. 不得读取 `~/.relaylink/` 下的凭据文件或设备私钥。
2. 不得读取服务端配置文件或日志原件。
3. 不得在输出中回显任何疑似秘密的字段。发现这类字段立即停止并报告异常，不要写入任何文件。
4. 不得为「让命令成功」而修改参数、放宽 scope 或绕过 dry-run。
5. 不得猜测 profile：存在多个 profile 时必须让用户用 `--profile` 指定。

## 输出解析

只用 `--json` 的 `data` 字段。`schemaVersion` 是 CLI 输出契约版本，与文档版本、API 版本无关。

参考文档：
- `reference/commands.md` — 完整命令与选项
- `reference/scopes.md` — 命令到 scope 的映射
- `reference/change-requests.md` — 变更申请的标准流程与话术
