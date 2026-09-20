# 仓库目录规划

文档 ID：DEV-001\
状态：Active\
版本：v1.4.0\
更新日期：2026-09-20

```text
RelayLink/
├── README.md                 产品介绍及文档入口
├── AGENTS.md                 仓库级代理协作规则
├── CONTRIBUTING.md           人员及提交协作规则
├── .editorconfig             基本格式
├── .gitattributes            文本换行规则
├── .gitignore                本机产物及敏感文件排除
├── .github/workflows/        GitHub Actions 流水线
├── docs/
│   ├── README.md             文档索引
│   ├── requirements/         需求和验收标准
│   ├── design/               技术设计与协议说明
│   ├── adr/                  架构决策记录
│   ├── development/          开发及文档管理规则
│   ├── operations/           安装与使用指南
│   ├── testing/              验证状态和测试证据
│   └── intro/                对外介绍材料的源、插图与重建脚本
├── src/
│   ├── RelayLink.Protocol/   帧协议、DTO、错误码
│   ├── RelayLink.Transport/  TLS、转发和连接生命周期
│   ├── RelayLink.AdminWeb/   React/TypeScript 管理端源码和构建配置
│   ├── RelayLink.Server/     Linux/Windows 服务端、内网仪表盘与管理 API
│   └── RelayLink.Agent/      Windows/Linux/macOS 客户端服务
├── tests/
│   ├── RelayLink.UnitTests/  配置、帧、状态机及令牌测试
│   └── RelayLink.IntegrationTests/ TCP/TLS、故障和 SQL 集成测试
├── tools/                    模拟目标与模拟调用方
├── config/examples/          可提交的虚构配置示例
├── deploy/linux/             Linux systemd 模板及部署说明
├── deploy/macos/             macOS launchd 模板及部署说明
├── deploy/windows/           Windows Server/Agent Service 脚本及说明
└── scripts/                  开发、验证和打包脚本
```

## 目录职责

- 当前已进入开发阶段，根目录使用 `RelayLink.slnx` 与各项目 csproj。共享库不得依赖 Server 或 Agent；Protocol 不依赖宿主和配置文件 I/O。
- 各目录剩余的 `.gitkeep` 仅标识尚未放入对应类别的实际文件；出现实际内容时应删除该占位。
- config/examples 仅存脱敏示例，命名 `*.example.json`。真实部署配置放仓库外；临时本地配置如必须留在仓库目录，放被忽略的 `.local/`。
- deploy 保存实际部署模板；docs/design 解释策略。模板形成后，设计文档链接它，避免长期维护两份相同模板。
- scripts 保存可重复执行的工具脚本；实验文件放被忽略的 work/，构建产物放被忽略的 artifacts/。
- .github/workflows 保存只读日常验证与 tag 发行流水线，不存真实配置或部署凭据。
- tools 保存独立的模拟目标与模拟调用方程序，不作为服务端或 Agent 的运行依赖。
- 需要新增 docs/operations 等类别时，须在有实际内容后创建并加入索引，不预建大量空文档。
- docs/intro 是演示文稿与公众号文章的唯一位置，`pages.json` 是页码来源；生成的 PPTX 与插图提交，`.build/`、`.cache/`、`.slidep/` 和 `legacy-bak/` 由目录内 .gitignore 排除。
- 管理端源码放 `src/RelayLink.AdminWeb`；构建生成的 `dist/` 不提交。Server 构建/发布时复制产物到输出目录的 `wwwroot/`，不再手工维护服务端静态页面；参见 [ADR-0004](../adr/0004-standalone-admin-web.md)。

## 命名

项目、程序集、命名空间使用 RelayLink 前缀。Linux 部署路径使用 `/opt/relaylink`、`/etc/relaylink`，服务账号建议 `relaylink`。协议中的 NTP1 属于原设计线格式标识，不能因为品牌改名静默修改；后续变更必须更新协议设计和兼容性说明。
