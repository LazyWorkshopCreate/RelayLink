# GitHub Actions 流水线

文档 ID：DEV-003\
状态：Active\
版本：v1.2.0\
更新日期：2026-09-20

流水线分为[日常校验](../../.github/workflows/ci.yml)和 [tag 发行](../../.github/workflows/release.yml)两部分。推送到 `main` 或提交 Pull Request 时，仅在 Linux 与 Windows 上执行前端类型检查、组件测试与格式检查，以及 .NET 单元和集成测试（包括 Agent 互访 TLS 测试）；不生成发布包，也不创建 GitHub Release。.NET 测试自身仍会编译测试所需代码，但跳过管理前端的生产构建。

只有推送 `v1.2.3` 或 `v1.2.3-rc.1` 形式的版本 tag 才触发发行流水线。每个 tag 必须同时提交非空的 `docs/releases/<tag>.md`；缺失说明时在打包前失败。发行先复用日常校验；全部通过后，分别生成 Linux/Windows `linux-x64`/`win-x64` 自包含服务端压缩包和 Windows Agent 安装包，最后校验三个产物齐全，以仓库内对应文件作为 GitHub Release 正文，同时附上 SHA-256 校验清单；带预发布后缀的 tag 标记为预发布版。重复运行同一 tag 时更新说明并覆盖旧资产，不再依赖 GitHub 自动生成说明。作业间的临时产物保留 7 天。安装包编译器在 Windows 作业中从 Inno Setup 发布地址取得，并在执行前检查 Authenticode 发布者签名。商业使用 Inno Setup 的许可要求见 [官方说明](https://jrsoftware.org/isorder.php)。

校验和打包作业只有仓库读取权限，检出时不保留 Git 凭据；仅最后的 Release 作业获得 `contents: write`，使用作业内置 `GITHUB_TOKEN`，不需额外仓库密钥或证书。构建使用 .NET 10 正式版 SDK、Node.js 24 与项目声明的 pnpm 版本；前端依赖按锁文件安装。流水线不部署服务、不安装 Agent。生成的安装包不是签名发行版，不能把流水线通过视为目标机安装、跨网络连通或容量验收。运行结果和失败日志以 GitHub Actions 为准；部署环境验收仍记录在[验证状态](../testing/verification-status.md)。
