# 对外介绍材料

文档 ID：DOC-001\
状态：Active\
版本：v1.1.0\
更新日期：2026-09-19

这个目录管理 RelayLink 的**对外介绍材料**：一份 22 页演示文稿、一篇公众号文章，以及生成它们的源文件与脚本。

材料原先放在 `artifacts/relaylink-intro/`（该目录被根 `.gitignore` 忽略），2026-09-19 迁入 `docs/intro/` 纳入版本管理，并按 [文档管理规则](../development/documentation-policy.md) 统一文件和目录命名。旧目录已删除，不要再去那里修改。

## 目录结构

| 路径 | 说明 |
|---|---|
| `relaylink-intro.pptx` | 演示文稿成品（22 页，随每次重建覆盖） |
| `wechat-article.md` | 公众号文章正文，插图用相对路径引用 `assets/` |
| `wechat-article.html` | 由 md 生成的网页版，方便预览和贴到编辑器 |
| `pages.json` | **页码与清单的唯一事实来源**，含 deck / article 文件名 |
| `slides/*.slide` | 每页一件的幻灯片源（slidep DSL），页码用占位符 |
| `story.md` | 讲稿：叙事节奏、每页要说什么 |
| `design.md` | 版式说明：每页的视觉分工 |
| `assets/pNN-*.png` | 插图成品，`NN` 为页码 |
| `sync.mjs` | 一体化入口：重建 PPTX、渲染插图、同步文章页码、自检 |
| `build-images.mjs` | 插图渲染器（HTML 模板 → PNG） |
| `build-article.mjs` | md → HTML 转换 |
| `legacy-bak/` | 迁移前的幻灯片备份，已被 `slides/` 取代，仅本地留档 |

`.build/`、`.cache/`、`.slidep/` 是过程产物，已被本目录的 `.gitignore` 忽略。

## 重建

脚本用 Node 22 运行，全部路径基于脚本自身位置，迁目录不需要改代码：

```bash
node sync.mjs all        # 全量：PPTX + 插图 + 文章 + 文档页码 + 自检（推荐）
node sync.mjs deck       # 仅重建 PPTX
node sync.mjs images     # 仅重渲染插图
node sync.mjs article    # 同步文章插图文件名 + 重新生成 HTML
node sync.mjs docs       # 仅同步 story.md / design.md 里的页码
node sync.mjs table      # 打印页码清单
node sync.mjs check      # 一致性自检
node sync.mjs add  <slug> <位置> [--chapter N] [--image] [--caption 标题]
node sync.mjs move <slug> <目标位置>
```

Windows 上 slidep 需要用 node 同目录的 `slidep.cmd`，脚本已经处理；插图渲染优先用 Playwright 自带的 Chromium，失败再退回 Edge。

## 改动时的三条规矩

1. **页码不要手改。** `pages.json` 是唯一事实来源，幻灯片里用 `{{PAGE}}` / `{{TOTAL}}` / `{{CHn_FROM}}` / `{{CHn_TO}}` 占位，由 `sync.mjs` 渲染。加页用 `sync.mjs add`、调序用 `sync.mjs move`，然后跑 `sync.mjs all`。
2. **一页有两份源。** 需要插图的页面，`slides/<slug>.slide` 和 `build-images.mjs` 里的 HTML 模板各有一份，改内容两边都要改，否则 PPTX 和插图会不一致。
3. **事实口径不能漂。** 设计基线（如 100 客户端 / 500 通道 / 1000 连接 / 100 Mbps）是目标值，没有压测数据支撑时不能写成实测结论；"真实环境投产"与"通过容量验收"必须分开表述。文章的依据来自 [验证状态](../testing/verification-status.md)。
