# GitHub Actions 流水线

文档 ID：DEV-003\
状态：Active\
版本：v1.7.0\
更新日期：2026-09-29

流水线分为[日常校验](../../.github/workflows/ci.yml)、[tag 发行](../../.github/workflows/release.yml)和 [GitHub Pages 站点部署](../../.github/workflows/pages.yml)三部分。推送到 `main` 或提交 Pull Request 时，在 Linux 与 Windows 上检查服务端前端的类型、组件测试和格式，以及 Agent 前端的构建和格式，再执行 .NET 单元和集成测试（包括 Agent 互访 TLS 测试）。同时在 GitHub 托管的 `macos-15-intel` 与 `macos-15` runner 上构建 Agent 前端，原生执行含互访 TLS 的集成测试，再发布并运行 `osx-x64`、`osx-arm64` Agent，检查 Mach-O 架构、配置、最小文件权限、launchd 启动与异常退出重启、只读状态页和端到端身份复用；测试脚本拒绝在非 GitHub-hosted runner 上操作系统目录。日常校验不创建 GitHub Release。.NET 测试自身仍会编译测试所需代码，但跳过前端的重复构建。

只有推送 `v1.2.3` 或 `v1.2.3-rc.1` 形式的版本 tag 才触发发行流水线。每个 tag 必须同时提交非空的 `docs/releases/<tag>.md`；缺失说明时在打包前失败。发行先复用日常校验；全部通过后，生成 Linux/Windows `linux-x64`/`win-x64` 自包含服务端压缩包、Linux Agent 自包含压缩包、Windows Agent 自包含及框架依赖各一份程序压缩包和安装包，以及 macOS `osx-x64`（Intel）和 `osx-arm64`（Apple Silicon）Agent 自包含压缩包。最后校验九个产物齐全，以仓库内对应文件作为 GitHub Release 正文，同时附上 SHA-256 校验清单；带预发布后缀的 tag 标记为预发布版。重复运行同一 tag 时更新说明并覆盖旧资产，不再依赖 GitHub 自动生成说明。作业间的临时产物保留 7 天。安装包编译器在 Windows 作业中从 Inno Setup 发布地址取得，并在执行前检查 Authenticode 发布者签名。商业使用 Inno Setup 的许可要求见 [官方说明](https://jrsoftware.org/isorder.php)。

校验和打包作业只有仓库读取权限，检出时不保留 Git 凭据；仅最后的 Release 作业获得 `contents: write`，使用作业内置 `GITHUB_TOKEN`，不需额外仓库密钥或证书。构建使用 .NET 10 正式版 SDK、Node.js 24 与项目声明的 pnpm 版本；前端依赖按锁文件安装。流水线不部署服务、不安装 Agent。Windows 安装包未签名，macOS 包也未做 Developer ID 签名、公证或 stapling；不能把流水线通过视为目标机安装、Gatekeeper、跨网络连通或容量验收。运行结果和失败日志以 GitHub Actions 为准；部署环境验收仍记录在[验证状态](../testing/verification-status.md)。

推送 `v*` tag 时，Pages 流水线从该 tag 对应的提交构建网站，不因普通 `main` 提交更新。构建前校验版本 tag 格式及非空的对应发布说明；构建脚本同时要求每份发布说明都有英文翻译，否则停止部署。构建产物上传到 Pages，中文位于站点根路径，英文位于 `/en/`。Pages 部署与 GitHub Release 流水线分别运行，前者不会等待后者完成；两者结果都应检查。首次运行前须在仓库设置中选择 GitHub Actions 作为 Pages 发布来源，并确认 `github-pages` 环境允许 `v*` tag 部署。Pages 作业只使用内置 `GITHUB_TOKEN` 的仓库读取、Pages 写入和 OIDC 权限，不需额外部署密钥。本站点部署不代表安装包发布或运行环境验收。
