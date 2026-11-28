# AI 自动化与 SDK 验证记录

文档 ID：TST-014\
状态：Active\
版本：v1.3.0\
更新日期：2026-11-06\
配套：[需求 REQ-001P](../requirements/2026-11-05-ai-automation-and-sdk.md)、[设计 DES-010](../design/ai-automation-and-sdk.md)、[ADR-0035](../adr/0035-ai-automation-over-peer-channel.md)、[ADR-0036](../adr/0036-third-party-sdk-over-entries.md)

本文件记录本轮实际执行的检查与证据，以及**明确未验证**的部分。需求 A108–A119 只有在这里有证据的条目才可视为通过。

> **2026-11-25 补充说明**：下文在 2026-11-06 阶段记录的 C#/.NET `RelayLink.Sdk`（仅编译 `net10.0`）、Python 仅冒烟、TypeScript 仅 `tsc` 与 Java 缺少构筑清单等缺口，已按 [2026-11-25 代码审查记录（TST-015）](code-review-2026-11-25.md) 全量补齐多目标构建与六语言一致性测试套件并更新到 §9 闭环结果。

## 1. 环境

- Windows，.NET SDK `10.0.114`，Node.js `24.14.0`，Python `3.12.10`，pnpm `11.19.0`。
- 本轮为做真实验收，在用户级安装专用工具链：**Go `1.27.3`**（winget GoLang.Go）、**OpenJDK `17.0.21.0.2`**（winget Microsoft.OpenJDK.17）、**Rust `1.99.1` stable-msvc**（winget Rustlang.Rustup + `rustup default stable-msvc`）；JUnit Console Standalone `1.10.2` 从 Maven Central 下载到被忽略的 `sdk/java/build/`。工具链不进入仓库提交。
- 所有网络测试只监听 `127.0.0.1`；证书、密钥与状态库均为临时测试材料，位于被忽略的临时目录，不进入提交。
- 全部结论仍来自 Windows 本机；Linux/macOS 原生、musl、arm64 未执行。
- **A116 端到端验收额外环境**：Docker `29.9.2`（Windows Desktop，`relaylink-server:local` 镜像由 `deploy/docker/Dockerfile` 构建）、DSH 用户级 skill 目录 `~/.dsh/skills/relaylink-admin`（复制自仓库 `skills/relaylink-admin`，HMR 后被本会话 catalog 识别）。临时材料位于 `.local/`（已 gitignore，不提交）。

## 1.1 A116 本机 Docker + 本机 Agent + DSH skill 端到端验收

**拓扑**（全部在 2026-11-06 本机真实执行）：

1. **Docker 服务端**：`relaylink-server:local` 容器，bridge 网络 + 端口映射 `127.0.0.1:7443/7444/18083/18084`。容器内 automation 仍只绑定 `127.0.0.1:18082` 且只提供 TLS；因 Windows Docker Desktop 的 `--network host` 不映射到宿主机，容器内用 `socat` 把 `0.0.0.0:18083` 转发到容器内 `127.0.0.1:18082`、把 `0.0.0.0:18084` 转发到容器内 `127.0.0.1:18080`（管理页）。转发仅做网络可达性桥接，不改变 automation 的 loopback + TLS 边界。
2. **两个本机 Agent 进程**（`ops-agent` 被访问方 / `ops-caller` 访问方），经 tunnel TLS 连 Docker 服务端，`clientsOnline=2`。`ops-agent` 承载 `automation-carrier` 授权互访通道（`authorizedClientsOnly=true`、`endToEndEncryptionEnabled=true`、E2E 证书指纹绑定），target 指向 Windows 本机转发入口 `127.0.0.1:18083`；`ops-caller` 的 `outboundMappings.to-automation` 经授权互访在本机开 `127.0.0.1:37499`。**CLI 只连这个互访入口，不直连 automation 端口**。
3. **DSH skill**：`relaylink-admin` 被复制到 `~/.dsh/skills/relaylink-admin` 并进入本会话 skill catalog；全部 CLI 动作经 `scripts/relaylink.mjs` 包装脚本（`RELAYLINK_BIN` 指向 `dotnet publish` 出的单文件 `relaylink.exe`，`RELAYLINK_HOME` 隔离凭据）。

**经 skill 完整链路（全部读 `--json` 的 `data`）**：

| 动作 | 结果 |
|---|---|
| `auth whoami --json`（配对前） | exit `3` 未配对，按 SKILL.md 转首次配对 |
| `pair --scope ... --json`（经互访入口） | exit `5`，输出 `USER_CODE` 与 `requestId`；管理页可见 `state=pending` 且用户码一致 |
| 管理页面人工批准 | HTTP 204，`state=approved` |
| `pair --wait --request <id> --code <code> --json` | exit `0`，`data.grantId` / `data.requestId` |
| `auth whoami --json` | exit `0`，`data.scopes` 与申请一致 |
| `server overview --json` | exit `4` `scope_insufficient`（令牌无 `server.read`）——按 skill 规则**停止，不改写参数重试** |
| `clients ls` / `channels ls ops-agent` / `mappings ls ops-agent` / `changes ls` | 全部 exit `0`，结构化 `data`（服务端真实返回两个在线客户端、授权互访通道等） |
| `channels set ... --dry-run --json` | exit `0`，输出计划，无服务端变更 |
| `channels set ... --json`（缺 `channels.write`） | exit `4` `scope_insufficient`，按规则停止 |
| `changes submit --type channel.create ... --dry-run` | exit `0`，服务端申请列表为空 |
| `changes submit ... --json` | exit `0`，`state=pending`；`changes get <id>` 仍 `pending`；通道列表无新通道（批准前不生效） |
| `changes cancel` / 撤销授权等 D 类 | 未执行（skill 明确不可操作，符合设计） |

**验收暴露并修复的真实缺口**：SKILL.md 的 C 类示例 `changes submit --target <clientId> --payload <file>` 不带 `--channel`，而服务端 `BuildTargetKey` 要求 `target.clientId + channelId`，首个真实提交返回 `validation_failed: target 字段不完整`。修 CLI：未给 `--channel` 时从 payload 的 `channelId` 自动补齐 `target.channelId`（`mapping.create` 同理补 `mappingId`）；加进程级回归用例 `Cli_change_submission_without_channel_option_succeeds`；`skills/relaylink-admin/reference/commands.md` 与 DSH 副本同步说明。

**Windows Docker Desktop 固有限制（如实记录）**：`--network host` 下容器端口不映射到 Windows 宿主机；容器内 `127.0.0.1` 监听无法被 `-p` 直接暴露；用 socat 转发桥接，属验收环境适配，不是产品代码改动。Windows bind mount 目录在 Linux 里权限过宽，`AuditService` 拒绝在 `/var/lib/relaylink` 建库，故审计/历史 DB 放到容器内命名卷 `/app/data`（`chmod 700`）；配置/证书的 `SECURITY: ... readable by others` 告警是 Windows ACL 语义差异，不影响运行。

## 2. 三轮实现复核与修复轮记录

实现经三轮只读复核（多 agent 并行、未修改任何文件）与三轮修复：

| 轮 | 基线 | 发现 | 修复提交 |
|---|---|---|---|
| 1（六维度，含 1 项失败） | 28e1e21（文档期） | 6 类问题：事实、矛盾、编号、规则、逻辑、遗漏 | 文档期修复 |
| 2（三维度） | 6a81d4a | 5 项中危实现缺口（jti 窗口、限流键、对账误判、幂等死代码、审计门槛）+ 1 高危功能缺口（pair 续接无私钥） | 复核修复轮 |
| 3（三维度） | 840bbd6 | 契约 JSON 严格解析失败（破坏性）、SPKI 续接缺口、限流桶无界增长 | 复核修复轮 |

### 2.1 复核修复轮新增回归测试

- **单元（+9）**：`IdempotencyCacheTests` 5 项；证明窗口边界 4 项（lifetime 61s 拒 / 60s 过、iat 超前 61s 拒 / 60s 过）。
- **集成（+3）**：并发换取恰好一枚成功；版本中间件负例（400 `unsupported_api_version`、`/info` 豁免）；Approved 状态连错 5 次 `pairing_locked`。

### 2.2 复核确认成立、无需重审的项

换令牌单事务防双签发、证明校验链完整、`automationInstanceId` 持久化、三处回环回指校验无绕过、C 类 applying/approved→rejected 状态机、jti 重放窗口闭合（180s 无正宽度重放窗）。

## 3. 构建结果

| 项目 | 结果 |
|---|---|
| `dotnet build RelayLink.slnx -c Debug -p:SkipAdminWebBuild=true` | 通过，0 警告 0 错误 |
| `RelayLink.Sdk`（netstandard2.0 / net8.0 / net10.0 三目标） | 通过 |
| TypeScript SDK `tsc --noEmit` | 通过 |
| AdminWeb `pnpm typecheck` | 通过 |
| Go SDK `go vet ./relaylink/` + `go build ./relaylink/` | 通过 |
| Java SDK `javac`（JDK 17）主代码编译 | 通过（修复 `UdpEntry` 一处不可达 catch） |
| Rust SDK `cargo build` / `cargo test` | 通过（修复 Windows `TimedOut` 错误码映射） |

## 4. 测试结果

| 套件 | 命令 | 结果 |
|---|---|---|
| 单元测试 | `dotnet test tests/RelayLink.UnitTests --configuration Release -p:SkipAdminWebBuild=true` | **393/393 通过** |
| 集成测试 | `dotnet test tests/RelayLink.IntegrationTests --configuration Release -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true` | **151/151 通过** |
| 管理前端 | `pnpm --dir src/RelayLink.AdminWeb test` | **12/12 通过** |
| Go SDK conformance | `go test ./relaylink/ -v` | **3/3 通过** |
| Java SDK conformance | `javac` + `junit-platform-console-standalone execute` | **5/5 通过** |
| Rust SDK conformance | `cargo test` | **5/5 通过** |

### 4.1 单元测试覆盖

`tests/RelayLink.UnitTests/AutomationTests.cs` 与新增测试类：

- **scope 语义**、**用户码**、**设备证明**（正常验签 / 篡改 / 错公钥 / 时间窗 / 指纹 / `htu`/`htm`）、**配置校验**（loopback、端口冲突、区间、证书路径）。
- **回环回指**：非授权形态被拒；授权互访形态通过；关闭内层 TLS 被拒；非回环目标不受限。
- **幂等三态**（`IdempotencyCacheTests`）、**证明窗口边界**。
- **A109 端口变更**：`Automation_port_change_revalidates_existing_loopback_channels` —— 已存在的非授权互访回环通道所指端口被改成 `automation.port` 时，`ValidateServerUpdate` 全量扫描拒绝。
- **A114 审计故障注入**（`AuditUnavailableTests` 2 项）：审计库路径为目录时 `AuditService.RecordAsync` 抛 `AuditUnavailableException`；写前闸门 `RequireAuditAsync` 原样传播该异常而不吞掉。

### 4.2 集成测试覆盖（真实 Server 进程 + 真实 TLS）

`tests/RelayLink.IntegrationTests/AutomationEndpointTests.cs`：

| 用例 | 验证内容 | 对应验收 |
|---|---|---|
| `/info` 能力探测 | TLS 可达、返回 `apiVersions` 与持久化 `automationInstanceId` | A108 |
| 未启用时端点不存在 | 端口无监听，调用抛网络异常 | A108 |
| 配对全链路 | 未批准不能换取；错误用户码被拒；正确用户码换取成功；二次换取被拒 | A110 |
| 令牌绑定设备 | 缺少设备证明时 401 | A111 |
| scope 强制 | 只读令牌可读、写操作返回 403 `scope_insufficient` | A112 |
| 撤销即时生效 | 撤销后下一次调用 401 `token_revoked` | A111 |
| 变更申请批准前不生效 | 提交后 `pending`，通道列表为空；批准并执行后通道出现 | A113 |
| 秘密字段被拒 | payload 含 `accessSecret` 时 400 `validation_failed` | A113 |
| 并发换取单签发 | 两个不同 jti 并发，恰好一枚成功 | A110 |
| 版本中间件负例 | 错误头 400 `unsupported_api_version`，`/info` 豁免 | A110 |
| Approved 计锁 | 连错 5 次 `pairing_locked`，正确码也被拒 | A110 |
| **逐值日志扫描** | 记录本用例实际产生的用户码 / 令牌 / 设备私钥（含 D 值），全文扫描所有日志与状态文件确认零泄漏 | A114 |

`tests/RelayLink.IntegrationTests/CliProcessTests.cs`（**A115/A116 进程级**，6 项，真实 `relaylink.dll` 子进程 + `RELAYLINK_HOME` 隔离）：

| 用例 | 验证内容 |
|---|---|
| 退出码矩阵 | `0` 成功（whoami / clients ls）、`4` scope 不足、`6` 业务拒绝；`--json` 结构化错误契约 `{schemaVersion,error:{code,message,retryable,requestId}}` |
| 8 与 9 的区分 | 入口不通返回 `8`；服务端可达且 `/info` 正常时不返回 `9` |
| 自动化未启用 | 端口无监听返回 `8` 且诊断含「无法连接」 |
| 进程输出编码 | 分别按 UTF-8 读取重定向的标准输出和标准错误，核对中文帮助和错误信息 |
| `--dry-run` 无副作用 | C 类提交 dry-run 退出 0，服务端变更申请列表为空 |
| C 类不带 `--channel` | 按 SKILL.md 示例提交，CLI 从 payload 自动补齐 `target.channelId`，`state=pending`（A116 验收暴露的缺口） |

### 4.3 六语言 SDK 数据面一致性

`contract/conformance/data-plane.json` 的用例由各语言各自读取并断言同一批错误码：

| 语言 | 用例 | 结果 |
|---|---|---|
| C# / .NET | 现有 `RelayLink.Sdk` 单元测试 | 通过 |
| Python | `sdk/python` 冒烟 | 通过 |
| TypeScript / Node | `tsc --noEmit` | 通过 |
| Go | `sdk/go/relaylink/conformance_test.go`：描述符负例、端点字符串、拒绝连接映射、传输形态不符 | 3/3 |
| Java | `sdk/java/src/test/.../ConformanceTest.java`：同上 5 项 | 5/5 |
| Rust | `sdk/rust/tests/conformance.rs`：同上 5 项 | 5/5 |

Go / Java / Rust 编译与运行暴露并修复的真实缺陷：Java `UdpEntry` 的 `UnknownHostException` catch 不可达（JDK 17 编译错误）；Rust `classify` 在 Windows 上把「连接未监听回环端口」的 `TimedOut` 错映射为 `TIMEOUT`（应为 `ENTRY_UNAVAILABLE`）；Rust conformance 提取器跨用例泄漏字段。

## 5. 过程中发现并修复的缺陷

1. **Kestrel 无法用 `CreateFromPemFile` 的证书完成 TLS 握手**（Windows 特有）：改为导出 PFX 再 `LoadPkcs12`。
2. **`AutomationPairingService` 依赖注入未注册的 `ServerConfiguration`**：改用 `LoadedConfiguration`。
3. **证明缺 `instanceId`**：服务端返回可读提示，CLI 从 `/info` 与换令牌响应写入 profile；`AuthAsync` / `OpenSessionAsync` 创建的 `ApiClient` 全部带上该值。
4. **`QueryPairingAsync` 缺 body**：服务端 status 端点要求 `body.userCode` 与 `proof.userCodeDigest` 双重一致；补 body 后二段式配对续接可用。
5. **CLI 全局/子命令参数解析**：`--type`/`--payload` 等子命令参数曾被全局解析吞掉；重写 `CliOptions` 分离全局选项与子命令参数，未知选项按位置参数放行。
6. **审计写操作失败关闭**（A114）：新增 `RequireAuditAsync` 写前闸门，B 类 PATCH 与 C 类申请在审计不可用时返回 `503 audit_unavailable` 且不执行变更（原先审计失败被吞掉且变更已执行）。
7. **CLI 凭据隔离**：新增 `RELAYLINK_HOME` 覆盖，进程级测试不再污染真实用户目录。

## 6. 未验证与已知边界

**必须如实看待以下缺口，不得据此声称对应验收已通过：**

- **A116 已在 DSH 一种具名宿主完成全链路**（见 §1.1）。**第二种具名宿主仍未验证**：需求原文要求「在两种具名宿主中完成全链路」，本轮只在 DSH 执行；`skill install --host` 的安装流程未针对第二个宿主跑通。
- **A117 六语言 SDK 已编译并跑 conformance，但跨平台未验证**：Go/Java/Rust 均在 Windows x64 上编译运行；musl、arm64、Linux/macOS 原生均未执行。Python 仍只做冒烟（未跑完整 conformance 用例集）。
- **A118 SDK 管理面未实现**。首期按设计只承诺 .NET 管理面，但 `RelayLink.Sdk` 目前只实现数据面；管理面需复用 `RelayLink.AutomationClient`，尚未接入。
- **A119 跨平台回归未做**：全部结论来自 Windows 本机。
- **审计故障注入是单元级**（`AuditService` + `RequireAuditAsync`），端点层的 `audit_unavailable` 响应由代码审查确认，未做「server 运行中把审计库弄坏」的进程级注入（Windows 上 SQLite 持有文件句柄导致运行时替换不可行）。
- **未做容量与并发压测**。`jti` 重放缓存与配对限流只在功能层面验证。
- **B 类幂等 replayed 回执形状**未在契约中描述；`IdempotencyCache` 24h 清理窗口的过期清扫时机依赖 `DateTimeOffset.UtcNow`、不可注入、未测。
- **退出码 9 的 CLI 半边**无进程级验证（服务端只有 v1，无法构造「服务端可达但版本不兼容」）。

## 7. 安全边界确认

- 未提交任何真实密钥、证书私钥、口令或业务载荷。测试用证书与密钥在临时目录生成，随测试销毁。
- 自动化入口只绑定 `127.0.0.1` 且只提供 TLS；`listenAddress` 取其它值时加载期即拒绝。
- `automation.enabled=false` 时端点路由在进程内不注册，集成用例确认端口无监听。
- 令牌只以摘要形式落库；设备公钥从持久存储读取，不驻留进程内存。
- **逐值日志扫描**（A114）确认用户码、令牌、设备私钥（含私钥标量 D）均不落盘。
