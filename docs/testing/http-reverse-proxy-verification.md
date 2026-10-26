# HTTP/HTTPS 代理验证记录

文档 ID：TST-005\
状态：Active\
版本：v1.2.0\
更新日期：2026-10-12\
关联：[REQ-001N](../requirements/2026-10-12-http-reverse-proxy.md)、[DES-008](../design/http-reverse-proxy.md)、[实施计划](../development/http-proxy-implementation-plan.md)

## 1. 本机环境与实际进程

Windows 本机，.NET SDK 10.0.112；使用随机空闲 IPv4 loopback 端口。HttpProxyProcessTests 每个案例启动独立 RelayLink.Server、目标 RelayLink.Agent、访问方 RelayLink.Agent 进程及 Kestrel Web 上游，使用临时注册/访问密钥、测试 CA 和目标 Agent 身份。公网模式也经过真实 Server 业务监听与数据绑定；互访模式经过真实本机映射、双侧数据绑定、内层 TLS、访问证明、应用解帧与目标 HTTP 宿主。

临时配置及证书在系统临时 GUID 目录，旧包在被忽略的 .local/verification。测试结束停止本次创建的进程并清理各自临时目录；没有安装系统服务、修改系统信任/hosts、生产配置或外部实例。TLS 客户端使用自定义测试根信任并正常校验名称，HTTP 入口测试不忽略证书错误；既有 raw 测试的外层连接探测方式与这些入口 TLS 证据分别看待。

## 2. 已执行检查

最终 Windows Release 配置单元 125/125、集成 87/87（EnablePeerTlsTests、v3/v4 真旧包矩阵均开启）通过，无跳过。常规 CI 不开启旧包开关，对应集成 73 项；HTTP 专项 23/23 单独通过。四入口额外核对 Server/Agent 只读状态与 URL、日志无测试注册密钥/查询/正文，以及显式关闭客户端响应和 WebSocket 后通道 active/pending 归零。首次资源计数检查因夹具仍持有流式响应和 WebSocket 而超时，显式关闭后四例及全量复跑通过，不把该失败掩盖为运行时成功。访问方示例 localAddress 字段经严格 schema 与配置校验后通过。

| 验收 | 本机证据 | 边界或未覆盖 |
|---|---|---|
| A85 | 严格配置/快照/帧 v3/v4/v5 单元测试；rc.8 v3 与 rc.9 v4 真二进制 raw 互访、半关闭、UDP 门槛；v4 HTTP 过滤/拒绝且 raw 映射继续在线 | 新 Agent 不自动降级旧 Server；旧二进制矩阵本机执行，不隐式下载到常规 CI |
| A86 | 公网 HTTP、HTTPS，互访 HTTP、HTTPS 四个真实进程案例；目标虚拟传输不绑定 OS 业务监听 | 公网路径使用 loopback 端口模拟，本机结果不代表外部防火墙/DNS 可达 |
| A87 | 原始 Host/Origin 拒绝、缺失保留、覆写、伪造 Forwarded/X-Forwarded-* 清理；公网来源 IP 与互访不伪造 IP；HTTPS 上游链/错名拒绝 | 三种 Host 模式配置校验与转换实现；三种 Host 模式均有实际请求；真实业务浏览器登录/CSRF/跳转/Cookie 需目标环境验证 |
| A88 | 四入口 ws/wss 子协议、二进制双向数据、正常关闭；流式首块早于后续块完成，嵌套浏览器 TLS 可用 | 没有长期慢链路或生产吞吐压测 |
| A89 | 入口测试根 CA/中间 CA 链及名称校验；目标 Agent 才持浏览器私钥；HTTP 互访禁降级；HTTP 证明绑定模式、摘要、scheme、mapping、authority；上游 HTTPS 独立信任/名称验证 | 本轮没有 OS 抓包，不以成功握手代替生产线路抓包或失陷控制平面安全承诺 |
| A90 | 撤销/关闭、请求体 413、WS 缺 Origin 拒绝；上游收到 GET/POST 后中断，502 且请求计数各为 1；现有审计/ACL/配额/掉线回归；应用记录非法长度/截断拒绝 | 关闭上游跨请求复用以收紧内部重试；长期句柄/内存及业务故障压测未执行 |
| A91 | v5 集中下发、HttpStatus/readiness、缺资源隔离、策略及映射重启激活、只读状态/URL、脱敏示例 | de76df7 的 Linux/Windows 与 macOS Intel/Apple Silicon CI 及原生 launchd 验证全部成功；证书链补充的最终 SHA 继续按同一 CI 核对 |

Accepted 表示采用需求方向；以上目标环境缺口未执行，REQ-001N 不标为 Done，不创建 Release/tag 或发布安装包。

附加检查：管理前端 build/typecheck、9/9 测试和 format:check；Agent 前端 build/typecheck/format:check；网站构建 64 页及 5/5 检查；Markdown UTF-8/LF、相对链接和 JSON 语法、git diff --check 均通过。NuGet 当前源审计 Agent 及传递包未报告已知漏洞，该结果只表示查询当时的源数据，不代表通用安全保证。测试结束后核对没有本轮 Server/Agent 残留进程，未安装服务或系统信任。

## 3. 复现

常规 CI 不下载私有旧资产：

```powershell
dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release -p:SkipAdminWebBuild=true
dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:EnablePeerTlsTests=true -p:SkipAdminWebBuild=true
```

完整旧二进制验证先从仓库既有 rc.8/rc.9 Release 下载框架依赖 ZIP，并核对各自 SHA256SUMS，再解压到被忽略目录。不得用当前编译结果替代旧 Agent：

```powershell
$env:RELAYLINK_LEGACY_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v3-compatibility/agent/RelayLink.Agent.dll').Path
$env:RELAYLINK_LEGACY_V4_AGENT_ASSEMBLY = (Resolve-Path '.local/verification/v4-compatibility/agent/RelayLink.Agent.dll').Path
dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:EnablePeerTlsTests=true -p:EnableLegacyAgentTests=true -p:EnableLegacyV4AgentTests=true -p:SkipAdminWebBuild=true
```

本轮 rc.8/rc.9 ZIP 均按发行清单验证 SHA256，通过后运行未修改 DLL。EnableLegacyV4AgentTests 是单独开关，开启后缺少对应环境变量会失败，不能静默跳过。Frontend 与网站检查沿用 [CI 说明](../development/ci.md)。

## 4. 远端流水线

本机验收完成后推送 origin/main；仅 CI 的 main 分支 push 会触发，Release/Pages 只由 v* tag 触发。本轮不创建 tag、不执行发布或部署。已核对 de76df74f33e17ef460a74a5d6cfdc2e44ce1915 的 CI 38159703114 (private source CI run 38159703114)：网站、Ubuntu、Windows、macOS x86_64、macOS arm64 五项全部 success。随后独立批次补全入口 PEM 中间链，单元 125/125、完整本机集成 87/87 和 HTTP 专项 23/23 通过。最终提交的 Actions 结果以 [main CI 列表](https://github.com/LazyWorkshopCreate/RelayLink/actions/workflows/ci.yml?query=branch%3Amain) 的对应 headSha 为准，流水线保留实际运行证据，不能用此前成功替代最终提交核对。
