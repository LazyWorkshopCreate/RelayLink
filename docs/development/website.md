# 产品网站维护说明

文档 ID：DEV-004\
状态：Active\
版本：v1.0.0\
更新日期：2026-10-05

网站是独立静态产品站点，源码位于 [website](../../website/)，设计见 [DES-007](../design/website-multipage.md)。不使用 Server/Agent 管理前端的构建命令，也不需要 .NET 构建。

## 本地构建与预览

准备 Node.js 24，在仓库根目录执行：

```sh
npm --prefix website ci
node scripts/build-website.mjs
npm --prefix website test
node scripts/preview-website.mjs
```

预览地址为 `http://127.0.0.1:4178/RelayLink/`，只绑定回环地址，以项目子路径检查实际部署路径；Ctrl+C 停止。修改源码后重新构建并刷新页面。生成物位于 `artifacts/site/`，不会提交。

## 新增或修改内容

1. 使用和设计正文直接编辑仓库对应 Markdown；需要新增网站文章时在 [content.json](../../website/content.json)添加唯一 slug、栏目 `guides` 或 `docs`、仓库内源路径、标题和摘要。
2. 在 [locale.js](../../website/locale.js)补充标题和摘要英文映射。没有完整英文正文的文档页明确标记中文原文，禁止将其描述为已完整翻译。
3. 新版本在 [docs/releases](../releases/)创建正文，并在 [英文发布翻译](../../website/release-translations.en.json)添加同版本的 title、intro、sections；每个章节的标题及条目齐全，有末尾说明时填写 outro。不要把旧版说明更新成当前能力。
4. 运行构建和测试，检查中文及英文对应路径、正文目录、源链接、图片和移动端布局。页面数量按清单和发布文件自动检查；内容范围发生变化时同步需求，不直接修改生成 HTML。

Markdown 支持表格、代码围栏和列表；正文 HTML 经净化，不支持嵌入执行脚本。已纳入目录的文档互相链接站内页，其他仓库资源链接 GitHub。仓库中相对引用必须存在；新增的内部章节链接须与生成标题锚点一致。

## 自动检查与部署

[站点测试](../../website/tests/site.test.mjs)检查每页标题、唯一 h1、描述、当前导航、对应语言、站内路径、章节锚点、静态资源、首页内容边界、全部发布章节和条目以及快速开始折叠。测试以构建输出为输入，须先构建。

[日常 CI](../../.github/workflows/ci.yml)在 PR 和 main 提交上校验站点；[Pages 流水线](../../.github/workflows/pages.yml)仍只在版本 tag 上构建部署。普通分支修改不会自动更新线上网站。发布流程和权限见 [CI 说明](ci.md)。
