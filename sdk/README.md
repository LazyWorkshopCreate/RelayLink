# RelayLink SDK（数据面）

SDK 让第三方应用经 RelayLink 访问内网服务。**它不实现中继协议**，只面向「入口」：
授权互访时是访问方 Agent 的 `127.0.0.1` 端口，普通代理时是服务端业务监听地址。

## 能力边界（务必先读）

- SDK **不持有**任何中继密钥：客户端密钥、通道访问密钥、绑定令牌、凭据明文都不进入 SDK 进程。
- SDK **不实现**协议帧、多路复用、打洞或 VPN。
- 数据面链路的加密强度**由通道配置决定**。授权互访默认内层 TLS，SDK 不关闭也不降级；
  普通代理入口的明文体性由部署与业务自身 TLS 承担，SDK 不宣称已加密。
- SDK **不提供**自动重连循环与连接池。通道有连接上限与撤销语义，隐藏重试会造成配额冲突，
  重试策略由应用显式决定。

## 入口发现

- **授权互访入口**可以从本机 Agent 的 v2 只读 API 自动发现（按 `targetClientId` + `targetChannelId` 匹配）。
- **普通代理入口无法自动发现**。本机 Agent 的 `AgentChannelView` / `AgentMappingView` 只上报
  loopback 的实际端口，不包含通道的 `ListenAddress` / `ListenPort`，因此必须显式配置。
- 显式配置永远优先于自动发现。

## 语言

| 语言 | 目录 | 最低版本 | 依赖 | 本机已验证 |
|---|---|---|---|---|
| C#/.NET | `src/RelayLink.Sdk` | netstandard2.0 / net8.0 / net10.0 | 仅 BCL | 是（编译 + 单元测试） |
| Python | `sdk/python` | 3.9 | 仅标准库 | 是（冒烟测试） |
| TypeScript/Node | `sdk/typescript` | Node 20 | 仅内置模块 | 是（tsc 类型检查） |
| Go | `sdk/go` | 1.22 | 仅标准库 | 是（Go 1.27.3：`go vet` + `go build` + conformance 3/3） |
| Java | `sdk/java` | 17 | 仅 JDK | 是（OpenJDK 17.0.21：`javac` + conformance 5/5） |
| Rust | `sdk/rust` | 1.75 | 无外部依赖 | 是（Rust 1.99.1：`cargo build` + conformance 5/5） |

六种实现均已在 **Windows x64** 上编译并跑通 `contract/conformance/data-plane.json` 对应的用例。
Linux / macOS 原生、musl 与 arm64 目标仍未验证，见 [TST-014](../docs/testing/ai-automation-verification.md)。

各语言的 conformance 用例：

- Go：`go test ./relaylink/ -v`（读取 `contract/conformance/data-plane.json`）
- Java：`javac` 编译主代码与 `src/test`，用 JUnit Console Standalone 运行 `ConformanceTest`
- Rust：`cargo test`（`tests/conformance.rs` 读取仓库根契约文件）

## 契约

- `contract/errors.json` — 错误码与退出码映射的单一来源
- `contract/conformance/data-plane.json` — 与语言无关的行为用例

各语言实现必须保证**方法语义、错误码与字段名一致**；接口形状可按语言惯例适配。

## 一致性要求

新实现接入前请确认：

1. 错误码字符串与 `contract/errors.json` 完全一致。
2. `EntryDescriptor` 对空 host 与非 1–65535 端口抛 `invalid_usage`。
3. TCP 支持单向半关闭（关闭发送方向后仍可接收）。
4. UDP 超限报文明确报错，不静默截断。
5. 不引入任何中继密钥或协议帧实现。
