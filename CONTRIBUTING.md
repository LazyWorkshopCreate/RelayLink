# 贡献指南

1. 从 README 和文档索引了解当前状态，再阅读相关需求及设计。
2. 采用短期主题分支，如 `feat/tcp-relay`、`fix/session-cleanup`、`docs/protocol`；main 保持可审阅。
3. 提交信息建议使用 `feat:`、`fix:`、`docs:`、`test:`、`chore:`，说明具体变化。
4. 一个提交或 PR 围绕一个完整问题；行为变化同步更新文档、示例和必要测试。
5. PR 说明问题、最终行为、验证方法及实际限制。涉及关键架构取舍时链接 ADR。
6. 不将“计划支持”写成“已经支持”，不把未运行测试写成通过。
7. 提交前运行完整本地校验（`pwsh ./scripts/test.ps1` 或按 [GitHub Actions 流水线](docs/development/ci.md) 执行 .NET 单元/集成测试、前后端与网站构建、六语言 SDK 与 Skill 检查及 `git diff --check`）。

参考：[目录规划](docs/development/repository-layout.md)、[文档规则](docs/development/documentation-policy.md)、[CI 说明](docs/development/ci.md)。
