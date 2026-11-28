# 变更申请标准流程

C 类结构性变更永远不能在一步内完成。按以下五步走，不要跳步。

## 第 1 步：确认这是 C 类变更

只有这些类型走申请：`client.create`、`client.delete`、`channel.create`、`channel.delete`、`mapping.create`、`channel.security`。**凭据相关变更（含删除）不走申请**：凭据写入与删除始终由管理员在管理页面完成。

显示名、tag、上限、超时、启停这类改动属 B 类，用 `channels set` 直接执行，不需要申请。

## 第 2 步：准备 payload 文件

```json
{
  "channelId": "shanghai-sql",
  "displayName": "上海 SQL",
  "listenAddress": "0.0.0.0",
  "listenPort": 21433,
  "targetHost": "127.0.0.1",
  "targetPort": 1433,
  "maxConnections": 50,
  "targetConnectTimeoutSeconds": 20
}
```

**payload 中不得出现任何秘密**：`secret`、`password`、`privateKey` 等字段会被服务端以 `validation_failed` 拒绝。凭据只能在管理页面输入。

## 第 3 步：dry-run 并把计划展示给用户

```bash
relaylink changes submit --type channel.create --target shanghai-01 --payload ./channel.json --dry-run
```

输出会说明「将提交什么、目标是谁」。**把这段展示给用户并获得明确同意**，不要自行决定继续。

## 第 4 步：提交申请

```bash
relaylink changes submit --type channel.create --target shanghai-01 --payload ./channel.json \
  --reason "为上海节点新增 SQL 通道" --json
```

记下返回的 `changeRequestId`。提交成功**不等于**变更生效。

## 第 5 步：报告并（如用户要求）查询

```bash
relaylink changes get <changeRequestId> --json
```

状态含义：

| 状态 | 含义 | 可以说什么 |
|---|---|---|
| `pending` | 等待管理员批准 | 「已提交，等待管理员在管理页面批准」 |
| `approved` | 已批准，尚未执行 | 「已批准，等待执行」——**不能说已生效** |
| `applying` | 正在执行 | 「正在执行」 |
| `applied` | 已执行完成 | 「已生效」，这是唯一可以说生效的状态 |
| `rejected` | 管理员拒绝 | 报告拒绝原因 |
| `cancelled` | 已撤销 | 报告已撤销 |
| `expired` | 超过 24 小时未处理 | 报告已过期，需要重新提交 |

## 标准话术

提交后：

> 已提交变更申请（ID: xxx），类型：channel.create，目标：shanghai-01。
> 该变更需要管理员在服务端管理页面的「变更审批」面板批准后才会执行。
> 批准前不会产生任何配置变更。

被拒绝后：

> 该变更申请已被管理员拒绝，原因：xxx。
> 如需继续，请确认后重新提交。

## 禁止事项

- 不得声称 `pending` 或 `approved` 的申请已经生效。
- 不得为绕过审批而改用 B 类命令做等价操作。
- 不得重复提交同一变更；同一目标已有未完成申请时服务端会返回 `conflict`，这时应报告既有申请 ID。
