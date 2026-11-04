# 插件 HTTP/HTTPS 修复验证

文档 ID：TST-010\
状态：Active\
版本：v1.0.0\
更新日期：2026-10-31

本记录覆盖[改进方案](../development/plugin-system-improvement-plan.md) B1、W01、W08 的 HTTP 首包计量部分及 W16 的逐请求失败结果。对应 IR-01、IR-08；架构选择见 [ADR-0033](../adr/0033-http-inspection-request-boundaries.md)。Server/Agent 生命周期及统一历史统计由其他批次验证，本文不宣称所有插件验收已完成。

## 实际变更

- 移除首头检查后调用原始 `PumpAsync` 的实现。生产入口统一使用 `HttpInspection.RelayAsync`，Kestrel 解析每个 HTTP/1.1 请求，YARP 流式转发每个获准请求。
- 每请求规则和允许审计在转发前执行；HTTP/HTTPS 的 keep-alive 和流水线后续请求均重新检查。拒绝返回 403 并关闭；首次拒绝目标零连接。
- 加入完整 HTTP 流的成功写入/读取计数回调，首批请求头不再漏计；按请求报告最终状态、耗时和字节增量，摘要不含查询串、凭据头和正文。
- CL/chunked、100-continue、临时响应、WebSocket 使用成熟组件处理；歧义输入、CONNECT 和非 WebSocket 隧道拒绝。上游流只能消耗一次，断线不重连或重放。
- HTTP 与 SSH 共享全局审批名额。调用点可提供后续操作配额、目标打开/就绪、字节活动、abort 和期限；不让首头期限截断审批等待。
- 服务端增加 YARP 2.3.0 包引用，与 Agent 版本一致。只在内存虚拟连接上托管，不增加 OS 监听端口。
- 独立复核补充自定义上游 CA 的 serverAuth 用途校验：不因替换信任根而接受仅用于客户端认证的证书；名称不匹配仍拒绝。
- 请求结果增加显式 outcome/reason，已开始的 200 响应若截断，仍记失败；未开始最终响应的故障状态为 0。结果审计使用独立 5 秒收尾令牌，业务 Abort 取消后仍尝试写入，失败继续传递给连接所有者。

## 旧实现反例

修改生产实现前，向正式 `HttpInspectionTests` 添加两个行为断言并执行。结果为 **2 项失败、0 项通过**：

1. 同一前缀含允许的 `GET /health` 和应拒绝的 `POST /secret`，实际目标写入包含后一个请求。
2. 请求头实际写入 44 字节，回调计数为 0。

原始结果保存在忽略目录 `work/plugin-remediation-http/results/b1-before.trx`。改用完整 Relay API 后，将这两个行为扩展为真实 loopback 双向流测试，没有保留不安全旧 API 仅为让原测试继续调用。

## 正式测试覆盖

测试代码：[协议与转发测试](../../tests/RelayLink.UnitTests/HttpInspectionRelayTests.cs)、[证书与快照测试](../../tests/RelayLink.UnitTests/HttpInspectionTests.cs)、[真实进程矩阵](../../tests/RelayLink.IntegrationTests/HttpInspectionProcessTests.cs)。

独立复核追加：[证书用途回归](../../tests/RelayLink.UnitTests/HttpInspectionTlsTests.cs)、[请求结果回归](../../tests/RelayLink.UnitTests/HttpInspectionOutcomeTests.cs)。

| 场景 | 断言 |
|---|---|
| 顺序 keep-alive 与流水线 | 第二请求 deny/approve 不到目标，每次有独立决定，查询串及凭据头不入摘要 |
| 允许的流水线及 chunked 响应 | 响应边界、trailer 和下一请求保持分离，最终状态正确 |
| 首包计量 | 请求仅含头时，计数与目标实际收到的请求字节相同且非零 |
| CL 与 chunked 正文、Expect | 正文跨 write 分片完整到达；100/103 不被记成最终状态，最终 201 正确 |
| 无效消息 | CL+TE、重复 Host/CL、未知编码、CONNECT、非 origin-form、HTTP/1.0、未知升级及超大头均拒绝；首头拒绝不打开目标 |
| 非法 chunk | 失败关闭，余下字节不能转为未经检查的后续请求 |
| WebSocket | 升级前先有允许决定，成功 101 后双向传输 |
| 审计与失败 | 允许审计失败时目标零连接；目标断线不重新调用打开回调、不重放 POST |
| 请求结果 | 截断 200 为 failed；目标打开失败为 failed/status 0；门槛审计失败触发 Abort 后仍完成结果回调；success 与 denied 明确区分 |
| 期限与配额 | 首次静默输入有界；审批超过首头期限仍等到自身超时；等待租约释放，容量不足不打开目标 |
| 真实进程 | server-peer/server-jump × HTTP/HTTPS 四组合，后续禁止请求均未到达真实目标 |

## 执行记录

环境为本机 Windows、.NET SDK 10.0.114，所有网络测试只监听 `127.0.0.1`，每个 Agent 有独立目录。证书与秘密为临时测试材料，不写入报告或提交。

```powershell
dotnet test tests/RelayLink.UnitTests/RelayLink.UnitTests.csproj --configuration Release -p:SkipAdminWebBuild=true --artifacts-path artifacts/plugin-http --filter 'FullyQualifiedName~HttpInspection' --logger 'trx;LogFileName=b1-after.trx' --results-directory work/plugin-remediation-http/results
dotnet test tests/RelayLink.IntegrationTests/RelayLink.IntegrationTests.csproj --configuration Release -p:SkipAdminWebBuild=true -p:EnablePeerTlsTests=true --artifacts-path artifacts/plugin-http --filter 'FullyQualifiedName~HttpInspectionProcessTests|FullyQualifiedName~Http_inspection|FullyQualifiedName~Peer_http_inspection|FullyQualifiedName~Https_inspection' --logger 'trx;LogFileName=b1-process.trx' --results-directory work/plugin-remediation-http/results
```

单元专项已执行 **27 项通过、0 项失败**；集成专项已执行 **7 项通过、0 项失败**，含新增四组合和既有三个 HTTP/HTTPS 进程场景。TRX 位于上述忽略目录，不作为产品配置提交。

随后新增 [HttpInspectionTlsTests](../../tests/RelayLink.UnitTests/HttpInspectionTlsTests.cs) 的 3 个证书用途用例。修复前为 **2 通过、1 失败**，仅 clientAuth 叶证书被错误接受；为自定义链设置 serverAuth application policy 后 **3/3 通过**，同时保留 serverAuth、无用途限制证书与名称不匹配断言。命令过滤 `FullyQualifiedName~HttpInspectionTlsTests`，使用 `--artifacts-path work/b2-artifacts`；独立结果为 `work/b2-evidence/http-tls-before.trx` 和 `http-tls-after.trx`。这是追加专项结果，不回写前述 27 项历史执行数量。

请求结果追加专项 **5/5 通过**：过滤 `FullyQualifiedName~HttpInspectionOutcomeTests`，使用同一独立 artifacts 路径，结果为 `work/b2-evidence/http-outcome-after.trx`。这些测试使用真实 loopback TCP 流触发截断响应、目标打开失败和审计拒绝，并校验终态、请求关联及目标零重放。

补丁合并后再次执行 `FullyQualifiedName~HttpInspection`，合计 **35/35 通过**，结果为 `work/b2-evidence/http-all-after.trx`。未重复此前已通过的进程矩阵，最终全仓集成结果以 [总体验证记录](plugin-system-remediation-verification.md) 为准。

尚未在本专项执行 Linux/macOS、旧版 Agent 二进制矩阵或容量压测。上游 1xx 由 HTTP 客户端正确消费，最终状态作为请求结果；不承诺逐字节保留所有临时响应或逐跳头。WebSocket 只验证升级和双向字节，不提供帧内容审计。管理员批准闭环仍未实现，approve 分支有界等待后拒绝。

2026-11-04 补充回归：新增 POST（Content-Length/chunked）头+正文精确计一次、pipeline 第二请求跨多次 read 分片仍被检查 2 组用例，`HttpInspectionRelayTests` 与 `HttpInspectionOutcomeTests` 过滤组 **34/34 通过**；`PluginLifecycleTests` 增加半关闭排空、目标故障双侧释放、后续审批计数、decision/response 审计读回 10 例，**23/23 通过**（重跑一致）。全量计数见 [TST-009 §7](plugin-system-remediation-verification.md#7-2026-11-04-复核收口)。
