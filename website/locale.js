const siteLocale = document.documentElement.lang.toLowerCase().startsWith("en") ? "en" : "zh";

const english = {
    "RelayLink · 让内网 TCP 服务可控地被访问": "RelayLink · Controlled access to private TCP services",
    "RelayLink 是一个基于 .NET 的开源反向 TCP 代理，让云端应用在内网无需开放公网入站端口的情况下访问 TCP 服务。": "RelayLink is an open-source reverse TCP proxy built on .NET. It lets cloud applications reach private-network TCP services without exposing inbound ports on the private network.",
    "跳到正文": "Skip to content",
    "RelayLink 首页": "RelayLink home",
    "主导航": "Main navigation",
    "产品": "Product",
    "使用": "Guide",
    "文档": "Docs",
    "动态": "Updates",
    "内网不开入站端口， 服务仍然可达。": "No inbound ports. Private services stay reachable.",
    "开源 · .NET · TCP": "OPEN SOURCE · .NET · TCP",
    "内网不开放": "No inbound ports.",
    "入站端口，": "Private services",
    "服务仍然可达。": "stay reachable.",
    "RelayLink 让 Agent 主动连接服务端，把分散在不同内网中的 SQL Server、RDP 与其他 TCP 服务，变成可集中配置、可授权、可审计的连接。": "RelayLink lets Agents connect outward to the Server, turning SQL Server, RDP, and other TCP services across private networks into centrally configured, authorized, and auditable connections.",
    "开始部署": "Start deploying",
    "了解工作方式": "How it works",
    "支持范围": "Supported platforms",
    "协议": "Protocol",
    "首期仅 TCP": "TCP only for now",
    "RelayLink 连接拓扑示意图": "RelayLink connection topology",
    "连接永远由 Agent 发起": "Connections always start from the Agent",
    "认证 · 配置 · 中继": "Auth · Config · Relay",
    "控制 / 数据": "Control / Data",
    "内网侧只需具备出站连接能力": "Only outbound connectivity is required inside the private network",
    "01 / 产品介绍": "01 / PRODUCT",
    "不是另一张网络， 只是那根可靠的管子。": "Not another network. Just a dependable pipe.",
    "不是另一张网络，": "Not another network.",
    "只是那根": "Just a dependable",
    "可靠的管子。": "pipe.",
    "RelayLink 不做 VPN、P2P 打洞，也不解析业务协议。它只负责建立受控的 TCP 字节流，让应用继续使用原来的地址、账号、权限和 TLS。": "RelayLink is not a VPN, does not perform P2P hole punching, and does not inspect application protocols. It establishes controlled TCP byte streams while applications keep their existing addresses, accounts, permissions, and TLS.",
    "一次普通代理连接的流程": "Flow of a standard proxied connection",
    "主动上线": "Connect outward",
    "每台 Agent 使用独立客户端 ID 和密钥，从内网主动建立控制连接。": "Each Agent uses its own client ID and secret to establish an outbound control connection.",
    "集中下发": "Distribute centrally",
    "Server 统一管理客户端、通道和访问映射，认证后下发运行配置。": "The Server manages clients, channels, and access mappings, then distributes runtime configuration after authentication.",
    "按需转发": "Relay on demand",
    "访问到来时建立独立数据隧道，双向复制字节并正确处理背压与半关闭。": "A dedicated data tunnel is created for each incoming connection, with bidirectional byte copying, backpressure, and half-close handling.",
    "连接模式": "CONNECTION MODE",
    "云端访问内网目标": "Cloud-to-private access",
    "云端应用连接 Server 的代理端口，Server 经已认证 Agent 转发到内网目标。": "A cloud application connects to the Server proxy port, and the Server relays traffic through an authenticated Agent to the private target.",
    "安全模型": "SECURITY MODEL",
    "授权的 Agent 互访": "Authorized Agent-to-Agent access",
    "访问方使用本机回环入口；业务流默认端到端 TLS，Server 只中继密文。": "The caller uses a local loopback endpoint. Application traffic uses end-to-end TLS by default, so the Server only relays ciphertext.",
    "明确边界": "EXPLICIT BOUNDARIES",
    "透明转发，不碰业务数据": "Transparent relay, no payload inspection",
    "不解析 SQL / TDS 等业务协议": "Does not parse SQL, TDS, or other application protocols",
    "不替代目标服务身份认证": "Does not replace authentication on the target service",
    "不提供 UDP、VPN 或 IP 层组网": "Does not provide UDP, VPN, or IP-layer networking",
    "安全边界要先看清": "Understand the security boundary first",
    "普通代理数据通道完成绑定后直接复制原始 TCP 字节，不提供链路加密。敏感业务应启用业务自身 TLS，或部署在受保护的网络链路中；应用层安全组不能替代加密。": "After binding, a standard proxy data channel copies raw TCP bytes and does not add transport encryption. Sensitive applications should use their own TLS or a protected network path. Application-layer security groups are not a substitute for encryption.",
    "02 / 使用说明": "02 / GUIDE",
    "从一台 Server， 到第一条可用通道。": "From one Server to your first working channel.",
    "从一台 Server，": "From one Server",
    "到第一条可用通道。": "to your first working channel.",
    "Linux Docker 主机是最快的起点。下面保留关键路径；生产部署前请完整阅读安装指南并配置 TLS、网络访问控制与备份。": "A Linux Docker host is the quickest starting point. The steps below cover the essential path. Before production, read the full guide and configure TLS, network access controls, and backups.",
    "部署步骤": "Deployment steps",
    "启动 Server": "Start the Server",
    "准备 Linux Docker Engine、Compose v2 与 PowerShell 7，然后在仓库根目录执行：": "Install Docker Engine, Compose v2, and PowerShell 7 on Linux, then run this command from the repository root:",
    "复制": "Copy",
    "将域名替换为 Agent 实际可达的 DNS 名称或 IP。": "Replace the hostname with a DNS name or IP address that the Agent can reach.",
    "创建客户端": "Create a client",
    "管理页面 · 独立凭据": "Admin UI · Unique credentials",
    "打开管理页面并登录，为每台 Agent 创建独立客户端，再下载专属 JSON 配置。": "Sign in to the admin UI, create a separate client for every Agent, and download its dedicated JSON configuration.",
    "客户端配置含访问密钥，不得共用，也不要提交到仓库。": "Client configurations contain access secrets. Do not share them between Agents or commit them to the repository.",
    "安装 Agent": "Install the Agent",
    "Windows 使用安装包；Linux 使用 systemd；macOS 使用 launchd。安装时使用刚下载的配置。": "Use the installer on Windows, systemd on Linux, and launchd on macOS. Install with the configuration you just downloaded.",
    "Agent 本机状态页默认位于": "The local Agent status page is available by default at",
    "添加通道": "Add a channel",
    "目标地址 · 授权范围": "Target address · Access scope",
    "确认客户端在线后创建通道，填写 Agent 所在内网可达的目标主机和端口。": "After the client is online, create a channel and enter a target host and port reachable from the Agent's private network.",
    "普通通道可配置来源 IP 安全组；Agent 互访则创建访问映射。": "Standard channels can use source-IP security groups. Agent-to-Agent access uses access mappings.",
    "默认端点": "Default endpoints",
    "管理页面": "Admin UI",
    "控制连接": "Control connection",
    "数据连接": "Data connection",
    "Agent 状态": "Agent status",
    "投产前检查": "Before production",
    "限制端口来源、启用控制 TLS、备份配置和 SQLite 数据，并在目标环境完成验证。": "Restrict port sources, enable control TLS, back up configuration and SQLite data, and validate in the target environment.",
    "部署文档": "Deployment documentation",
    "完整安装与使用指南": "Complete installation and usage guide",
    "总览 →": "Overview →",
    "Docker Compose 部署": "Docker Compose deployment",
    "Windows 部署": "Windows deployment",
    "macOS Agent": "macOS Agent",
    "03 / 文档中心": "03 / DOCUMENTATION",
    "从第一次部署， 到看懂每个取舍。": "From the first deployment to every design tradeoff.",
    "从第一次部署，": "From your first deployment",
    "到看懂每个取舍。": "to every design tradeoff.",
    "按任务进入文档。操作指南描述已经可以执行的路径；设计文档和 ADR 记录协议、安全边界及重要架构决定。": "Start with the task at hand. Operations guides cover executable paths; design documents and ADRs explain the protocol, security boundaries, and major architectural decisions.",
    "入门": "Getting started",
    "安装与使用指南": "Installation and usage",
    "选择部署方式、启动 Server、安装 Agent、创建通道": "Choose a deployment, start the Server, install an Agent, and create a channel",
    "项目概览": "Project overview",
    "定位、连接模式、能力边界与仓库入口": "Positioning, connection modes, boundaries, and repository map",
    "部署": "Deployment",
    "Linux Server 容器化启动与数据目录": "Containerized Linux Server startup and data directories",
    "Server 与 Agent 的 systemd 部署": "systemd deployment for the Server and Agent",
    "Windows Service 与 Agent 安装包": "Windows Service and Agent installer",
    "Intel / Apple Silicon Agent 与 launchd": "Intel and Apple Silicon Agents with launchd",
    "架构与安全": "Architecture and security",
    "技术设计": "Technical design",
    "总体架构、协议、配置与故障处理": "Architecture, protocol, configuration, and failure handling",
    "Agent 安全互访": "Secure Agent-to-Agent access",
    "访问授权、内层 TLS 与身份校验": "Access authorization, inner TLS, and identity verification",
    "架构决策记录": "Architecture decision records",
    "理解关键方案为何这样设计": "Why the key design choices were made",
    "开发与验证": "Development and verification",
    "仓库目录": "Repository layout",
    "项目职责、文件归属与命名规则": "Project responsibilities, file ownership, and naming",
    "验证状态": "Verification status",
    "已经执行的检查与仍待补齐的证据": "Completed checks and evidence that is still outstanding",
    "贡献指南": "Contribution guide",
    "本地检查与协作约定": "Local checks and collaboration conventions",
    "找不到需要的内容？": "Looking for something else?",
    "打开完整文档索引 →": "Open the complete documentation index →",
    "04 / 发布日志与动态": "04 / RELEASES & UPDATES",
    "版本在前进， 边界也写在同一页。": "Progress and limitations, documented together.",
    "版本在前进，": "Progress and limitations,",
    "边界也写在同一页。": "documented together.",
    "读取发布说明…": "Loading release notes…",
    "正在从仓库发布说明生成时间线…": "Building the timeline from repository release notes…",
    "候选版本不等同于容量或长期稳定性验收结论。": "A release candidate is not evidence of capacity or long-term stability validation.",
    "查看 GitHub Releases →": "View GitHub Releases →",
    "让连接先成立。 剩下的，交给业务本身。": "Establish the connection. Let the application handle the rest.",
    "让连接先成立。": "Establish the connection.",
    "剩下的，交给业务本身。": "Let the application handle the rest.",
    "在 GitHub 查看源码": "View source on GitHub",
    "阅读设计与 ADR →": "Read design docs and ADRs →",
    "开源反向 TCP 代理 · Built with .NET": "Open-source reverse TCP proxy · Built with .NET",
};

if (siteLocale === "en") {
    document.title = english[document.title] ?? document.title;

    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    let node;
    while ((node = walker.nextNode())) {
        const original = node.nodeValue;
        const key = original.trim().replace(/\s+/g, " ");
        if (english[key]) {
            const leading = original.match(/^\s*/)?.[0] ?? "";
            const trailing = original.match(/\s*$/)?.[0] ?? "";
            node.nodeValue = `${leading}${english[key]}${trailing}`;
        }
    }

    document.querySelectorAll("[aria-label], [title], meta[name='description']").forEach((element) => {
        for (const attribute of ["aria-label", "title", "content"]) {
            const value = element.getAttribute(attribute);
            if (value && english[value]) element.setAttribute(attribute, english[value]);
        }
    });
}

const languageLink = document.querySelector(".language-link");
if (languageLink) {
    if (siteLocale === "en") {
        languageLink.href = "../";
        languageLink.textContent = "中文";
        languageLink.setAttribute("aria-label", "Switch to Chinese");
    }

    const syncLanguageHash = () => {
        const base = siteLocale === "en" ? "../" : "./en/";
        languageLink.href = `${base}${window.location.hash}`;
    };
    syncLanguageHash();
    window.addEventListener("hashchange", syncLanguageHash);
}

window.RELAYLINK_LOCALE = siteLocale;
