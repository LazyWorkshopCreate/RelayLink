# 网站多页设计与调研

文档 ID：DES-007\
状态：Draft\
版本：v1.1.0\
更新日期：2026-10-05\
调研日期：2026-10-05\
关联需求：[REQ-001M](../requirements/2026-10-05-website-multipage.md)

## 现状与调研

仓库事实：`website/index.html` 原来同时承载首页、部署步骤、文档分类与发布动态；`locale.js` 在浏览器运行翻译，`app.js` 从生成的发布数据构造折叠列表，最多展示三个发布章节。站点由版本 tag 触发 Pages 流水线，中文在根目录、英文在 `/en/`。网站与两个 React 管理页面是独立交付物。

外部事实：GitHub Pages 托管静态 HTML、CSS、JavaScript，可执行构建后发布，项目网站位于仓库子路径。[GitHub Pages 官方说明](https://docs.github.com/en/pages/getting-started-with-github-pages/what-is-github-pages)。Marked 可以解析 Markdown，但不会自动净化输出 HTML。[Marked 官方说明](https://marked.js.org/)。以上来源查阅于 2026-10-05。

实现方案：继续使用现有 Node.js 构建入口，生成目录级 `index.html`。相较客户端路由，直接静态页更适合 Pages 的刷新与深层访问；相较迁入大型站点框架，保留原模板和样式能缩小变更范围。新增锁定版本的 Marked、sanitize-html 和 jsdom：分别解析正文、净化输出、操作 HTML 与在构建阶段执行既有翻译映射。依赖仅供构建及测试，发布网站不需要 Node 服务。

本文记录实现方案和建议，不将本次实现选择视为独立架构评审通过；状态保持 Draft。此变化不影响代理架构，不另建传输或配置 ADR。

## 信息结构

| 路径（相对站点根目录） | 职责 |
|---|---|
| `/` | 基本介绍、平台、连接模式、安全边界与分类入口 |
| `guides/` | 快速开始与五份部署/使用文章列表 |
| `guides/quickstart/` | 原首页四步部署流程，保留折叠与复制 |
| `guides/<slug>/` | 使用和各平台部署文档详情 |
| `docs/`、`docs/<slug>/` | 原有技术与开发文档列表和详情 |
| `releases/`、`releases/<version>/` | 全版本列表和完整发布说明 |
| `en/<上述路径>` | 对应英文页面或明确标识中文正文的文档页 |
| `assets/` | 共用 CSS、增强脚本、图标及引用的仓库图片 |

路径表为站点内部路径，不假定托管在域名根目录。所有输出链接按当前目录计算相对路径，支持 GitHub Pages 的 `/RelayLink/` 前缀。每篇文章都有对应语言页和 hreflang 入口；语言切换保持文章路径，不携带可能不同的章节锚点。

## 内容与构建

- [目录清单](../../website/content.json)维护文章 slug、栏目、源 Markdown、标题及摘要；快速开始保存在 [HTML 片段](../../website/quickstart.html)，主页模板与全站页头页尾在 [index.html](../../website/index.html)。
- 中文发布正文直接渲染 `docs/releases/<version>.md`，英文正文由 [翻译目录](../../website/release-translations.en.json)生成；校验版本、章节数、条目数，补齐历史发布资产和结尾限制说明。版本排序比较数值，正式版优先于同版本预发布。
- Markdown 使用 GFM 解析及允许列表净化。保留正文的代码、表格、图片和结构，不执行文档内脚本及事件处理器。Mermaid 源以代码块呈现，本次不引入客户端图表依赖。
- 详情章节生成稳定、去重的锚点与目录。已发布文档的相对链接转换为同语言站内路径，其他仓库文件链接 GitHub 原文；图片复制至 `assets/repository/`。Windows 部署保留旧 `#server` 别名，兼容安装指南中的链接。
- 文章正文保留源文件的状态与语义，英文文档正文不进行零散词语替换；英文首页与快速开始使用既有翻译，发布说明完整翻译，其他文章明确提示原文为中文。
- UDP 站点增补从仓库需求、设计、ADR 和专用使用指南生成四组双语路由；首页只补充协议范围及指南入口，完整限制放详情页。现有 rc.8 与历史发布说明保留其对应版本语义，站点更新不等于 UDP 二进制已经发布。
- 缺少内容源、图片、仓库引用路径、重复路由或缺失发布译文会失败。输出不提交，构建写入被忽略的 `artifacts/site/`。

## 交互与维护

共用页头突出当前栏目；详情含返回列表、目录、前后文章及仓库原文。正文和分类链接直接写入 HTML，无客户端数据获取依赖。JavaScript 仅管理滚动页头、快速开始折叠/复制和旧首页锚点迁移。首页仍保留 `#product`，原 `#guide`、`#docs`、`#updates` 转到新列表。

构建、测试及预览命令见[网站维护说明](../development/website.md)。日常 CI 新增独立站点校验作业；Pages 构建安装锁定依赖并执行站点检查，触发条件继续为版本 tag。本文不授权推送、发布或变更远程设置。
