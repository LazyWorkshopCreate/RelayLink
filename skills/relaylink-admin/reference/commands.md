# 完整命令参考

所有命令都接受全局选项。`--json` 输出 `{"schemaVersion":1,"requestId":"...","data":{...}}`，失败时输出 `{"schemaVersion":1,"error":{...}}`。

## 全局选项

| 选项 | 说明 |
|---|---|
| `--profile <name>` | 选择 profile。存在多个时必须显式指定。 |
| `--entry <host:port>` | 显式指定自动化入口，覆盖自动发现。 |
| `--dashboard-port <n>` | 本机 Agent 只读 API 端口，缺省 18081。 |
| `--json` | 结构化输出。脚本与 agent 只能用这个。 |
| `--dry-run` | 只做校验与计划展示，不提交任何变更。 |
| `--yes` / `-y` | 跳过交互确认。 |
| `--wait` | 等待人工批准（受超时约束）。 |
| `--timeout <s>` | 整体超时秒数。 |
| `--verbose` | 输出诊断细节。 |
| `--spki <sha256>` | 固定服务端证书 SPKI 摘要。 |
| `--accept-spki` | 首次连接时接受带外核对过的证书。 |

## 配对

```bash
relaylink pair [--wait] [--scope <s>]... [--subject <name>]
```

发起配对，输出 `USER_CODE=XXXX-XXXX`。用户码必须原样转达给用户，由其在服务端控制台核对并批准。

## 认证

```bash
relaylink auth whoami     # 查看当前令牌的主体、scope、到期时间
relaylink auth profiles   # 列出本地 profile
relaylink auth logout     # 删除本地令牌（服务端授权仍有效）
```

`auth logout` 只删本地副本。要让令牌失效必须在管理页面撤销。

## 只读查询

```bash
relaylink doctor                        # 本地配置与入口探测
relaylink server health                 # 就绪状态（server.read）
relaylink server overview               # 聚合视图（server.read）
relaylink server version                # 服务端版本（server.read）
relaylink clients ls                    # 客户端列表（clients.read）
relaylink channels ls <clientId>        # 通道列表（channels.read）
relaylink mappings ls <clientId>        # 访问映射（mappings.read）
relaylink changes ls                    # 变更申请列表（changes.read）
relaylink changes get <id>              # 单条申请（changes.read）
```

## 受控写入（B 类）

```bash
relaylink channels set <clientId> --channel <channelId> \
  [--display-name <v>] [--tag <v>]... [--idempotency-key <uuid>]
```

直接生效并写审计。**不能**用它修改加密开关、审计模式、通道类型、目标地址；那些属于 C 类。

## 变更申请（C 类）

```bash
relaylink changes submit --type <t> --target <clientId> \
  [--channel <channelId>] --payload <file> [--reason <text>]
relaylink changes cancel <id>
```

支持的类型：`client.create`、`client.delete`、`channel.create`、`channel.delete`、`mapping.create`、`channel.security`。**`credential.delete` 已移除**：凭据写入与删除始终由管理员在管理页面完成，自动化接口不接受其申请。

`channel.create` / `channel.delete` / `channel.security` 的 `target` 需要 `clientId` 与 `channelId`。可以显式给 `--channel <channelId>`；**不给时 CLI 会从 `--payload` 文件里的 `channelId` 自动补齐**（SKILL.md 的示例即采用这种形式），payload 必须包含该字段。`mapping.create` 同理需要 `mappingId`。

## skill 安装

```bash
relaylink skill install --host <host> [--force]
```

只做目录复制，不在运行时从远端下载或更新。

## 入口发现

优先级：

1. `--entry host:port`
2. profile 中的 `entry`
3. 本机 Agent `http://127.0.0.1:<dashboardPort>/api/v2/mappings` 中匹配到的授权互访入口

**普通代理入口无法自动发现。** 本机 Agent 的 v2 视图只上报授权互访的实际 loopback 端口，不包含通道的 `ListenAddress`/`ListenPort`，因此普通代理入口必须用 `--entry` 显式给出。多个 profile 命中时 CLI 会报错要求显式指定，不会按「第一个」猜测。
