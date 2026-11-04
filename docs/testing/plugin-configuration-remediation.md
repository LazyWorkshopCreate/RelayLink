# 插件配置与凭据改进验证

文档 ID：TST-013\
状态：Active\
版本：v1.0.0\
更新日期：2026-10-31

对应 [DEV-006 B4/B5](../development/plugin-system-improvement-plan.md)，包括 W09–W15；凭据恢复契约见 [ADR-0031](../adr/0031-credential-mutation-and-deletion-recovery.md)。

## 1. 正式测试与观察

- `InspectionConfigurationRegressionTests`：旧实现 4 失败、1 通过，覆盖大小写保留 tag、控制 TLS 和可视互访入口地址。修复后全部通过。
- `InspectionConfigurationPersistenceTests`：通道编辑、客户端编辑和身份登记均保留相对证书/密钥/CA 路径；复制配置目录后从新位置解析。RDP 新增/重新启用、缺少材料的 HTTPS 新增被拒，原文件不变；存量 HTTPS 可编辑其他字段、RDP 可删除。SSH 策略经实际文件加载和其他字段编辑后保持，forwarding=true 的文件加载被拒绝。
- `CredentialLifecycleTests`：预审计失败零变更；原子替换失败无成功审计；结果回执跨重启和重复请求保持一致；同 ID 不同请求拒绝；24 小时之后回执被清理再用同键时，新实际变更使用独立审计 ID，不被旧事件去重吞掉；通道/客户端删除标记阻止读写和主机键继承；清理失败保留标记，恢复匹配 key 后完成清理；检查 key/store 文件权限。
- `CredentialApiTests`：真实 Server 在 key 缺失、格式非法、错误 key、损坏 store 时仍提供管理诊断且 `none` TCP 可用；PUT 重试保留原时间/结果，删除和同 ID 重建后凭据为空，旧回执不能复活秘密。重启缺失 store 的负例同样通过；7 项真实 API/none 通道进程回归全部通过。
- AdminWeb 测试：审计模式下 RDP/HTTPS 新建选项禁用，普通模式的标签仍可选择。原有类型锁定、页面与操作测试保留。

## 2. 证据目录与故障边界

执行命令使用 `dotnet test ... --configuration Release -p:SkipAdminWebBuild=true`，真实进程集成增加 `-p:EnablePeerTlsTests=true`；过滤上述类，TRX 保存于 `work/plugin-remediation-config/results/`。首轮及修复后完整计数见 [总验证记录](plugin-system-remediation-verification.md)。追加验证：3 项路径/策略往返通过，10 项凭据生命周期通过，7 项真实 API 回归通过；后续已纳入全量单元/集成门槛。

Windows 通过持有禁止删除共享的真实文件句柄注入原子替换失败；这验证替换前的权限失败，未模拟物理断电。进程在替换后、结果审计前退出通过重开加密回执模拟；结果审计容量耗尽通过审计服务的真实容量门槛注入。成套恢复测试使用临时测试材料，不操作部署秘密；跨主机灾难恢复演练不在本机测试范围。

本轮未新增秘密读取、私钥上传或凭据发放功能。文件初始化失败后需要恢复匹配文件并重启；旧版本 key-only 情况的人工处理见 ADR-0031。

## 3. 2026-11-04 补充回归

- 配置：控制 TLS 关闭时经真实 `ChannelConfigurationEditor` 保存 server-jump/server-peer 被拒且文件不变；启用中 RDP 可停用（落盘）但重新启用被拒；混合大小写与非规范/重复 `app:` 保留值经真实 `ConfigurationLoader.Load` 抛出 ConfigurationException；SSH 策略跨应用字段、前缀条数（64/65）、长度（1024/1025）、控制字符及审批期限 0/1/300/301 边界由 Loader 校验。
- 凭据：`X-RelayLink-Operation-Id` 回显与缺省生成；5 类坏 key/store 下 PUT/DELETE/GET 均 503；清理标记阻止写入且 GET 返回 409；后台 `CredentialRecoveryService` 补记恰好一条结果审计；权限拒绝下 store fail closed；坏 store 时 server-jump 真实连接被关闭、目标零连接。`CredentialLifecycleTests` **13/13**、新增凭据进程用例 **8/8**、既有凭据 API 回归 **7/7** 通过。
- 分类过滤组 `InspectionConfiguration*|ConfigurationTests|SshInspectionSecurityTests` **103/103** 通过；全量单元 358/358、集成 156/156。证据与命令见 [TST-009 §7](plugin-system-remediation-verification.md#7-2026-11-04-复核收口)。
