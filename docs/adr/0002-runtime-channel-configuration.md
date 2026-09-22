# ADR-0002：通道配置运行时下发

状态：Superseded（由 [ADR-0009](0009-revoke-connections-on-channel-change.md) 替代）\
日期：2026-09-16

## 背景

管理员需要在仪表盘中新增、删除、修改和禁用通道，并要求保存后自动下发到在线 Agent。此前设计要求通道变更重启服务端后才生效。

## 决定

服务端在保存前校验完整配置，原子替换对应客户端 JSON，并在成功绑定新增监听后切换运行时快照。在线 Agent 通过既有 TLS 控制连接接收 `ConfigUpdate` 帧，验证配置哈希后原子替换内存快照并以 `ConfigAck` 确认。新业务连接使用已确认版本；当时决定已有业务连接不被该操作主动中断，现已由 [ADR-0009](0009-revoke-connections-on-channel-change.md) 更改。

## 后果

通道变更不再需要重启服务端或 Agent。端口被外部进程占用等运行时绑定失败会使保存失败。手工直接改配置文件、服务端基础端点、客户端密钥和账号配置仍不监听文件变化，需按运维窗口重启。

## 关联

- [核心需求](../requirements/2026-09-15-core-requirements.md)
- [技术设计](../design/technical-design.md)
