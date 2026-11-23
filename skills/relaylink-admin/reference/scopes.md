# 命令与 scope 映射

每个 CLI 动作对应一个确定 scope。令牌缺少该 scope 时 CLI 以退出码 `4` 结束，**不得改写参数重试**。

| 命令 | 级别 | 需要的 scope |
|---|---|---|
| `relaylink pair` | 配对 | — （匿名 + 人工批准） |
| `relaylink auth whoami` | A | 无（只回显自身） |
| `relaylink auth profiles` | A | 无（本地） |
| `relaylink auth logout` | A | 无（本地） |
| `relaylink doctor` | A | 无（本地 + 只读探测） |
| `relaylink server health` / `overview` | A | `server.read` |
| `relaylink server version` | A | `server.read` |
| `relaylink clients ls` | A | `clients.read` |
| `relaylink channels ls` | A | `channels.read` |
| `relaylink channels set` | B | `channels.write` |
| `relaylink mappings ls` | A | `mappings.read` |
| `relaylink changes ls` / `get` | A | `changes.read` |
| `relaylink changes submit` | C | `changes.write` |
| `relaylink changes cancel` | B | `changes.write` |

写 scope 隐含同名读 scope：持有 `channels.write` 即可读取通道；持有 `channels.read` **不能**写入。

## scope 全表

| scope | 说明 |
|---|---|
| `server.read` | 健康、总览、版本 |
| `clients.read` | 客户端列表 |
| `clients.write` | 客户端受控写入（隐含 `clients.read`） |
| `channels.read` | 通道列表 |
| `channels.write` | 通道受控写入（隐含 `channels.read`） |
| `mappings.read` | 访问映射列表 |
| `mappings.write` | 映射受控写入（隐含 `mappings.read`） |
| `security.read` | 安全组读取 |
| `security.write` | 安全组写入（隐含 `security.read`） |
| `audit.read` | 审计事件 |
| `history.read` | 流量历史 |
| `changes.read` | 变更申请读取 |
| `changes.write` | 变更申请提交与撤销（隐含 `changes.read`） |
| `credentials.delete` | 预留 scope，当前无对应端点；凭据写入与删除始终在管理页面完成 |

## 退出码

| 码 | 含义 | agent 应做什么 |
|---|---|---|
| `0` | 成功 | 继续 |
| `2` | 用法错误 | 修正参数，不要重试同一命令 |
| `3` | 未授权 / 未配对 / 令牌失效 | 转配对或提示重新配对 |
| `4` | scope 不足 | **停止**，说明需要哪个 scope 并请求重新配对 |
| `5` | 需要人工批准 | 说明下一步由人操作，不要轮询等待 |
| `6` | 服务端业务拒绝 | 读错误信息，向用户报告，不要盲目重试 |
| `7` | 审计或存储不可用 | 说明服务端暂时不可用，建议稍后重试 |
| `8` | 网络或证书固定失败 | 检查入口配置；**不要把它当成版本问题** |
| `9` | 版本不兼容（仅当连得上） | 提示升级 CLI |

关键区分：旧服务端没有自动化端口时表现为**连不上**，只能报 `8`。`9` 只在成功连接且 `/info` 返回的版本不受支持时出现。不要把 `8` 解释成版本问题。
