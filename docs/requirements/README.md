# RelayLink 需求索引

本目录按需求首次提出日期和主题拆分。日期前缀表示该组需求首次进入范围的日期；后续修订继续更新原主题文档，不另建“最终版”副本。

| 文档 | ID | 状态 | 范围 |
|---|---|---|---|
| [核心需求](2026-09-15-core-requirements.md) | REQ-001A | Accepted | 项目目标、核心功能、SQL 接入、非功能要求、A01–A17 |
| [Agent 能力需求](2026-09-17-agent-capabilities.md) | REQ-001B | Accepted | 授权互访、本机入口、Windows 安装包、A18–A28 |
| [传输、管理与审计需求](2026-09-18-runtime-and-audit.md) | REQ-001C | Accepted | 控制/数据分离、流量历史、管理安全、审计、A28–A41 |
| [跨平台 Agent 需求](2026-09-20-cross-platform-agents.md) | REQ-001D | Accepted | Linux 与 macOS Agent、A42–A45 |
| [客户端删除与标签需求](2026-09-21-client-management.md) | REQ-001E | Accepted | 客户端删除、客户端/通道 tag 与列表筛选、A46–A48；已实现，验证证据见验证状态文档 |
| [Agent 本机只读 HTTP API 需求](2026-09-21-agent-local-http-api.md) | REQ-001F | Done | 与状态页共端口的 loopback 只读 API、A49–A50；实现与自动化验收已完成 |
| [客户端互访可选加密需求](2026-09-22-optional-peer-encryption.md) | REQ-001G | Done | 互访通道默认加密及可选明文直接复制、A51–A53；实现与自动化验收已完成 |

需求编号和验收编号沿用原文，不因拆分重排。当前存在两个历史 `A28`：一个属于 Windows 安装包升级，一个属于控制/数据入口验证；本次只做结构拆分，不改写既有编号语义。

`Accepted` 表示需求内容已经冻结；只有需求及其全部验收均完成时才使用 `Done`。当前实现与验收进度以[技术实现文档](../design/technical-design.md)和[验证状态](../testing/verification-status.md)为准。
