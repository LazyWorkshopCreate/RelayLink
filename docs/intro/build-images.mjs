import { mkdirSync, writeFileSync, readFileSync, readdirSync, statSync, rmSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const outDir = resolve(here, 'assets');
mkdirSync(outDir, { recursive: true });
const htmlDir = resolve(here, '.build', 'images');
mkdirSync(htmlDir, { recursive: true });

// 页码的唯一来源是 pages.json，本文件只负责版面 HTML
const MANIFEST = JSON.parse(readFileSync(resolve(here, 'pages.json'), 'utf8'));
const TOTAL = MANIFEST.pages.length;
const PAGE = new Map(MANIFEST.pages.map((p, i) => [p.slug, i + 1]));
const pad = (n) => String(n).padStart(2, '0');

const CSS = `
*{margin:0;padding:0;box-sizing:border-box}
html,body{background:#fff}
.page{width:1280px;height:720px;background:#FFFFFF;font-family:"Microsoft YaHei","Segoe UI",sans-serif;color:#0F172A;overflow:hidden;position:relative}
.pad{padding:20px 64px;display:flex;flex-direction:column}
.hd{height:88px;display:flex;align-items:flex-end;justify-content:space-between}
.tag{font-size:13px;font-weight:600;color:#2563EB;letter-spacing:2px}
.h1{font-size:34px;font-weight:700;line-height:1.25;margin-top:8px}
.bar{width:4px;height:44px;background:#2563EB}
.bd{height:524px;margin-top:16px;display:flex;flex-direction:column}
.ft{height:36px;margin-top:12px;display:flex;align-items:center;justify-content:space-between;font-size:14px;color:#64748B}
.ft .l{display:flex;align-items:center;gap:8px}
.dot{width:6px;height:6px;background:#2563EB}
.card{border-radius:10px;padding:22px 24px;display:flex;flex-direction:column;justify-content:space-between}
.dark{background:#0F172A;color:#fff}
.soft{background:#EEF2F8}
.line{border:1.5px solid #DCE3ED;border-radius:10px;padding:22px 24px;display:flex;flex-direction:column;justify-content:space-between}
.t{font-size:20px;font-weight:600}
.p{font-size:15px;color:#64748B;line-height:1.65}
.pd{font-size:15px;color:#CBD5E1;line-height:1.65}
.chip{background:#EEF2F8;border-radius:6px;padding:7px 11px;font-size:13px;color:#0F172A}
.chipd{background:#1E293B;border-radius:6px;padding:7px 11px;font-size:13px;color:#60A5FA}
.chipa{background:#FFFFFF;border:1.5px solid #93C5FD;border-radius:6px;padding:6px 11px;font-size:13px;color:#0F172A}
.chipx{background:#DCE3ED;border-radius:6px;padding:7px 11px;font-size:13px;color:#0F172A}
.lb{background:#2563EB;border-radius:6px;padding:6px 11px;font-size:13px;font-weight:600;color:#fff;white-space:nowrap}
.lbd{background:#0F172A;border-radius:6px;padding:6px 11px;font-size:13px;font-weight:600;color:#fff;white-space:nowrap}
.row{display:flex;gap:26px}
.acc{color:#2563EB;font-weight:600}
svg{display:block}
`;

const page = (inner) => `<!doctype html><html><head><meta charset="utf-8"><style>${CSS}</style></head><body><div class="page">${inner}</div></body></html>`;
const foot = (slug) => `<div class="ft"><div class="l"><span class="dot"></span><span>RelayLink · 开源反向 TCP 代理</span></div><span>${pad(PAGE.get(slug))} / ${pad(TOTAL)}</span></div>`;
const head = (tag, title) => `<div class="hd"><div><div class="tag">${tag}</div><div class="h1">${title}</div></div><div class="bar"></div></div>`;

/* ---------- 01 封面 ---------- */
const cover = page(`
<div class="page" style="display:flex;flex-direction:row;position:relative">
  <div style="position:absolute;top:0;right:0;width:660px;height:720px;background:linear-gradient(135deg,#EEF2F8 0%,#FFFFFF 100%)"></div>
  <div style="position:absolute;left:0;bottom:0;width:420px;height:6px;background:#2563EB"></div>
  <div style="width:680px;height:100%;padding:0 40px 0 64px;display:flex;flex-direction:column;justify-content:center;position:relative;z-index:1">
    <div class="tag">OPEN SOURCE · .NET 10 · TCP RELAY</div>
    <div style="height:24px"></div>
    <div style="font-size:76px;font-weight:700;line-height:1.1">RelayLink</div>
    <div style="height:22px"></div>
    <div style="font-size:30px;font-weight:700;line-height:1.35">让云端应用访问<br>任意内网 TCP 服务</div>
    <div style="height:26px"></div>
    <div style="width:72px;height:3px;background:#2563EB"></div>
    <div style="height:24px"></div>
    <div style="font-size:18px;color:#64748B;line-height:1.6">内网不开公网入站端口<span style="color:#0F172A;font-weight:700"> —— 连接永远由 Agent 主动发起</span></div>
    <div style="height:40px"></div>
    <div style="font-size:15px;color:#2563EB;letter-spacing:.5px">github.com/LazyWorkshopCreate/RelayLink</div>
  </div>
  <div style="width:600px;height:100%;display:flex;align-items:center;justify-content:center;position:relative;z-index:1">
    <svg width="540" height="520" viewBox="0 0 540 520">
      <circle cx="270" cy="260" r="196" fill="none" stroke="#DCE3ED" stroke-width="1.5" stroke-dasharray="4 8"/>
      <circle cx="270" cy="260" r="128" fill="none" stroke="#DCE3ED" stroke-width="1.5" stroke-dasharray="4 8"/>
      <path d="M78 92 L228 216" stroke="#2563EB" stroke-width="1.6"/>
      <path d="M470 108 L318 214" stroke="#2563EB" stroke-width="1.6"/>
      <path d="M62 268 L206 258" stroke="#2563EB" stroke-width="1.6"/>
      <path d="M488 272 L340 262" stroke="#2563EB" stroke-width="1.6"/>
      <path d="M108 452 L232 316" stroke="#2563EB" stroke-width="1.6"/>
      <path d="M446 438 L312 312" stroke="#2563EB" stroke-width="1.6"/>
      <path d="M246 234 L214 206 L262 200 Z" fill="#2563EB"/>
      <path d="M300 232 L332 202 L332 250 Z" fill="#2563EB"/>
      <path d="M206 258 L172 248 L172 274 Z" fill="#2563EB"/>
      <path d="M340 262 L374 252 L374 278 Z" fill="#2563EB"/>
      <path d="M240 300 L208 336 L246 348 Z" fill="#2563EB"/>
      <path d="M304 300 L336 336 L298 348 Z" fill="#2563EB"/>
      <circle cx="270" cy="260" r="62" fill="#0F172A"/>
      <circle cx="270" cy="260" r="44" fill="none" stroke="#60A5FA" stroke-width="1.5"/>
      <rect x="244" y="238" width="52" height="10" rx="5" fill="#FFFFFF"/>
      <rect x="244" y="256" width="36" height="10" rx="5" fill="#60A5FA"/>
      <rect x="244" y="274" width="44" height="10" rx="5" fill="#FFFFFF" opacity="0.55"/>
      <circle cx="78" cy="92" r="17" fill="#FFFFFF" stroke="#0F172A" stroke-width="2"/>
      <circle cx="470" cy="108" r="17" fill="#FFFFFF" stroke="#0F172A" stroke-width="2"/>
      <circle cx="62" cy="268" r="17" fill="#FFFFFF" stroke="#0F172A" stroke-width="2"/>
      <circle cx="488" cy="272" r="17" fill="#FFFFFF" stroke="#0F172A" stroke-width="2"/>
      <circle cx="108" cy="452" r="17" fill="#FFFFFF" stroke="#0F172A" stroke-width="2"/>
      <circle cx="446" cy="438" r="17" fill="#FFFFFF" stroke="#0F172A" stroke-width="2"/>
      <rect x="52" y="124" width="52" height="6" rx="3" fill="#DCE3ED"/>
      <rect x="444" y="140" width="52" height="6" rx="3" fill="#DCE3ED"/>
      <rect x="34" y="300" width="52" height="6" rx="3" fill="#DCE3ED"/>
      <rect x="462" y="304" width="52" height="6" rx="3" fill="#DCE3ED"/>
      <rect x="84" y="484" width="52" height="6" rx="3" fill="#DCE3ED"/>
      <rect x="420" y="470" width="52" height="6" rx="3" fill="#DCE3ED"/>
    </svg>
  </div>
</div>`);

/* ---------- 05 架构总览 ---------- */
const archRow = (name, sub, target, tsub) => `
<div style="height:78px;display:flex;align-items:center;gap:10px">
  <div style="width:150px;background:#EEF2F8;border-radius:8px;padding:10px 12px">
    <div style="font-size:15px;font-weight:600">${name}</div><div style="font-size:12px;color:#64748B">${sub}</div>
  </div>
  <div style="width:46px;display:flex;flex-direction:column;align-items:center">
    <div style="font-size:15px;color:#2563EB;font-weight:700">→</div>
    <div style="width:100%;height:2px;background:#2563EB"></div>
  </div>
  <div style="flex:1;background:#fff;border:1.5px solid #DCE3ED;border-radius:8px;padding:10px 12px">
    <div style="font-size:15px;font-weight:600">${target}</div><div style="font-size:12px;color:#64748B">${tsub}</div>
  </div>
</div>`;

/* ---------- 04 为什么是现在 ---------- */
const step = (tag, title, body, accent) => `
  <div style="flex:1;background:${accent ? '#FFFFFF' : '#EEF2F8'};${accent ? 'border:1.5px solid #2563EB;' : ''}border-radius:10px;padding:24px 22px;display:flex;flex-direction:column;justify-content:space-between">
    <div><div style="font-size:13px;font-weight:600;color:#2563EB;letter-spacing:1px">${tag}</div><div style="height:12px"></div><div style="font-size:21px;font-weight:700;line-height:1.35">${title}</div></div>
    <div style="font-size:14.5px;color:#64748B;line-height:1.65">${body}</div>
  </div>`;

const whyNow = page(`<div class="pad">
${head('CHAPTER 01 · 为什么是现在', '中台又成了刚需，第一道坎却是连通性')}
<div class="bd">
  <div style="height:312px;display:flex;gap:12px;align-items:stretch">
    ${step('AI 时代', '数据中台又一次成了刚需', '数据重新变成生产资料。没来得及建的要补，刚被拆掉的要重建——但这一次的窗口更短，容不得再从零规划三年。', false)}
    <div style="width:26px;display:flex;align-items:center;justify-content:center;font-size:26px;color:#2563EB">→</div>
    ${step('零售与中小企业的现实', '数据在几百个门店的内网里', '每家门店一套 POS 或进销存，库在 192.168 段，出口躲在运营商 NAT 后面。总部手上真正有的，往往只有各店报上来的 Excel。', false)}
    <div style="width:26px;display:flex;align-items:center;justify-content:center;font-size:26px;color:#2563EB">→</div>
    ${step('第一道坎', '不是建模，是数据出不来', '指标口径、特征工程、模型训练全排在后面。只要数据还留在各个门店的内网里，中台建得多漂亮都是空的。', true)}
  </div>
  <div class="dark" style="height:200px;margin-top:12px;border-radius:10px;padding:22px 28px;display:flex;flex-direction:column;justify-content:space-between">
    <div><div style="font-size:13px;font-weight:600;color:#60A5FA;letter-spacing:2px">RELAYLINK 的位置</div><div style="height:12px"></div><div style="font-size:28px;font-weight:700;line-height:1.3">它不做中台，它是中台前面那根管子</div></div>
    <div><div class="pd" style="font-size:15px;line-height:1.65">只解决一件事：把分散在全国的内网数据出口打通，让「汇聚」先成立。汇聚之后怎么建模、怎么做特征、怎么训练，交给你的中台和模型。</div><div style="height:10px"></div><div style="font-size:12.5px;color:#64748B;line-height:1.55">也正因为它只做连通，才不碰应用层协议、不做网站托管。</div></div>
  </div>
</div>
${foot('why-now')}
</div>`);

/* ---------- 06 现有方案 ---------- */
const altRow = (name, can, stuck, last) => `
  <div style="height:106px;display:flex;gap:16px;padding:14px 0;${last ? '' : 'border-bottom:1px solid #DCE3ED;'}">
    <div style="width:190px;font-size:16.5px;font-weight:700;line-height:1.45">${name}</div>
    <div style="width:200px;font-size:13.5px;color:#64748B;line-height:1.6">${can}</div>
    <div style="flex:1;font-size:14px;line-height:1.6">${stuck}</div>
  </div>`;

const alternatives = page(`<div class="pad">
${head('CHAPTER 01 · 现有方案', '能连上是一回事，管得住是另一回事')}
<div class="bd">
  <div style="display:flex;gap:28px">
    <div class="dark card" style="width:300px">
      <div><div style="font-size:13px;font-weight:600;color:#60A5FA;letter-spacing:2px">判断</div><div style="height:16px"></div><div style="font-size:30px;font-weight:700;line-height:1.3">缺口不在<br>"能不能连上"<br>在"谁管"</div></div>
      <div><div style="width:52px;height:3px;background:#2563EB"></div><div style="height:18px"></div><div class="pd" style="font-size:15px">RelayLink 把鉴权、配置下发、可观测、撤销做进服务端，而不是留给使用者自己拼。</div><div style="height:12px"></div><div style="font-size:15px;font-weight:600;color:#fff;line-height:1.65">对小型机构和个人，价格与上手难度本身就是硬约束——不是"多花点时间就能学会"。</div></div>
    </div>
    <div style="flex:1">
      <div style="height:40px;display:flex;gap:16px;align-items:center;border-bottom:2px solid #0F172A">
        <div style="width:190px;font-size:12.5px;font-weight:600;color:#64748B;letter-spacing:1px">方案</div>
        <div style="width:200px;font-size:12.5px;font-weight:600;color:#64748B;letter-spacing:1px">能做</div>
        <div style="flex:1;font-size:12.5px;font-weight:600;color:#64748B;letter-spacing:1px">卡在哪</div>
      </div>
      ${altRow('VPN / SD-WAN', '把两地网络拉平，什么协议都能跑。', '要么在门店侧开入站，要么上专线和硬件；几百个点位的设备、证书、账号生命周期本身就是一份全职工作。', false)}
      ${altRow('frp 一类<br>反向代理', '反向连接、端口映射，能力上确实够。', '它给的是能力，不是系统：配置下发、按客户端隔离、撤销存量连接、流量计量都要自己搭。', false)}
      ${altRow('ssh -R /<br>自写脚本', '零成本，几分钟就能打通一条。', '没有身份模型，没有重连与半关闭的语义保证，也没有可观测性。一条好用，一百条是事故。', false)}
      ${altRow('商业穿透 /<br>远程接入 SaaS', '开箱即用，有现成控制台。', '按点位或带宽计费，几百个门店是持续支出；数据要过第三方链路，合规多一道关。', true)}
      <div style="height:47px;margin-top:8px;font-size:12.5px;color:#64748B;line-height:1.55">公平地说：frp 与 ssh -R 都能靠扩展达到要求，仓库文档里的适配判断只针对本项目需求；VPN 也不是不能用，它只是不在小型机构的预算和人力里。</div>
    </div>
  </div>
</div>
${foot('alternatives')}
</div>`);

const architecture = page(`<div class="pad">
${head('CHAPTER 01 · 架构总览', '服务端只负责"被连接"，内网侧零入站端口')}
<div class="bd">
  <div style="height:325px;display:flex;gap:15px">
    <div style="width:252px;border:1.5px dashed #CBD5E1;border-radius:10px;background:#EEF2F8;padding:14px 16px">
      <div style="font-size:14px;font-weight:600;color:#64748B;letter-spacing:1px">云端内网 / VPC</div>
      <div style="height:12px"></div>
      <div style="background:#fff;border-radius:8px;padding:12px 14px"><div style="font-size:16px;font-weight:600">云端业务服务</div><div style="font-size:13px;color:#64748B">tcp:10.20.0.10,21433</div></div>
      <div style="height:10px"></div>
      <div style="background:#fff;border-radius:8px;padding:12px 14px"><div style="font-size:16px;font-weight:600">管理浏览器</div><div style="font-size:13px;color:#64748B">http://10.20.0.10:18080</div></div>
      <div style="height:10px"></div>
      <div style="font-size:13px;color:#94A3B8;line-height:1.5">业务端口与管理页由云安全组限制在受信任网络。</div>
    </div>
    <div style="width:54px;display:flex;flex-direction:column;align-items:center;justify-content:center">
      <div style="font-size:20px;color:#2563EB;font-weight:700">→</div>
      <div style="width:100%;height:2px;background:#2563EB"></div>
      <div style="height:8px"></div>
      <div style="font-size:12px;color:#64748B;text-align:center;line-height:1.4">业务<br>21433</div>
    </div>
    <div style="width:210px;background:#0F172A;border-radius:10px;padding:16px">
      <div style="font-size:18px;font-weight:700;color:#fff">RelayLink Server</div>
      <div style="font-size:12px;color:#60A5FA">Linux / Windows</div>
      <div style="height:16px"></div>
      <div style="height:2px;background:#2563EB;width:40px"></div>
      <div style="height:14px"></div>
      <div style="font-size:13px;color:#fff;line-height:1.7">控制入口 7443 · TLS</div>
      <div style="font-size:13px;color:#fff;line-height:1.7">数据入口 7444 · TCP</div>
      <div style="font-size:13px;color:#CBD5E1;line-height:1.7">业务监听 21433…</div>
      <div style="font-size:13px;color:#CBD5E1;line-height:1.7">管理页 18080</div>
      <div style="height:14px"></div>
      <div style="font-size:12px;color:#94A3B8;line-height:1.5">通道、密钥、访问映射与安全组全部集中在这里。</div>
    </div>
    <div style="width:54px;display:flex;flex-direction:column;align-items:center;justify-content:center">
      <div style="font-size:20px;color:#2563EB;font-weight:700">←</div>
      <div style="width:100%;height:2px;background:#2563EB"></div>
      <div style="height:8px"></div>
      <div style="font-size:12px;color:#64748B;text-align:center;line-height:1.4">Agent<br>主动发起</div>
    </div>
    <div style="width:520px;border:1.5px solid #DCE3ED;border-radius:10px;padding:14px 16px;display:flex;flex-direction:column;justify-content:space-between">
      <div style="font-size:14px;font-weight:600;color:#64748B;letter-spacing:1px">各地内网 · 无任何公网入站端口</div>
      ${archRow('上海 Agent', 'Windows Service', 'SQL Server', '192.168.10.20:1433')}
      ${archRow('成都 Agent', 'Windows Service', 'SQL Server', '192.168.10.20:1433')}
      ${archRow('门店 Agent', 'Windows Service', '其他 TCP 服务', '内网可达即可')}
    </div>
  </div>
  <div class="row" style="height:185px;margin-top:14px">
    <div class="line" style="width:420px"><div><div class="t">出网是唯一前提</div><div style="height:10px"></div><div class="p">只要 Agent 所在网络允许出站 TCP 到控制端口和数据端口，链路就成立。内网不需要任何入站映射、不需要公网 IP、不需要改防火墙放行规则。</div></div><div class="acc" style="font-size:13px">部署前置条件最少化</div></div>
    <div class="soft card" style="width:300px"><div><div class="t">配置在服务端</div><div style="height:10px"></div><div class="p">每客户端一个 JSON 文件：独立密钥、通道、访问映射。改完立即下发新快照。</div></div><div style="font-size:13px;color:#64748B;font-weight:600">集中管理 · 版本化下发</div></div>
    <div class="line" style="width:380px"><div><div class="t">Agent 只认下发的快照</div><div style="height:10px"></div><div class="p">本地不落地通道配置，拿不到快照就不启动转发——避免机器重启后拿一份过期缓存继续转发流量。</div></div><div class="acc" style="font-size:13px">fail closed，不是 fail open</div></div>
  </div>
</div>
${foot('architecture')}
</div>`);

/* ---------- 09 连接时序 ---------- */
const steps = [
  ['1', '业务 TCP 连接到代理端口', ['#2563EB', '#2563EB', 't', 't'], '→'],
  ['2', '预留配额，生成 Pending 与一次性令牌', ['t', '#0F172A', 't', 't'], '·'],
  ['3', 'Open（走已认证的控制连接）', ['t', '#2563EB', '#2563EB', 't'], '→'],
  ['4', '新建 TCP 到数据端口并 BindData', ['t', '#0F172A', '#0F172A', 't'], '←'],
  ['5', '原子消费令牌 → BindAccepted', ['t', '#2563EB', '#2563EB', 't'], '→'],
  ['6', '拨号快照中指定的目标（5 秒子期限）', ['t', 't', '#2563EB', '#2563EB'], '→'],
  ['7', 'TargetReady（回到控制连接）', ['t', '#0F172A', '#0F172A', 't'], '←'],
  ['8', 'Start：校验未超时且会话仍有效', ['t', '#2563EB', '#2563EB', 't'], '→'],
  ['9', '双向复制原始 TCP 字节，不再分帧', ['#2563EB', '#2563EB', '#2563EB', '#2563EB'], '⇄'],
];
const stepRows = steps.map(([n, label, seg, arrow]) => `
<div style="height:28px;display:flex;align-items:center">
  <div style="width:320px;display:flex;align-items:center;gap:10px">
    <span style="font-size:15px;font-weight:700;color:#2563EB;width:22px">${n}</span>
    <span style="font-size:14px">${label}</span>
  </div>
  <div style="flex:1;display:flex;height:2px">
    ${seg.map((c) => `<div style="width:190px;background:${c === 't' ? 'transparent' : c}"></div>`).join('')}
  </div>
  <div style="width:26px;display:flex;align-items:center;justify-content:center;font-size:14px;font-weight:700;color:${arrow === '·' ? '#64748B' : arrow === '←' ? '#0F172A' : '#2563EB'}">${arrow}</div>
</div>`).join('');

const sequence = page(`<div class="pad">
${head('CHAPTER 02 · 连接时序', '一条业务连接：九步建立，20 秒总预算')}
<div class="bd">
  <div style="height:330px;background:#EEF2F8;border-radius:10px;padding:14px 18px">
    <div style="height:46px;display:flex;align-items:center">
      <div style="width:320px;font-size:13px;font-weight:600;color:#64748B">步骤</div>
      <div style="flex:1;display:flex">
        ${['云端调用方', 'Server', 'Agent', '内网目标'].map((x) => `<div style="width:190px;display:flex;justify-content:center;font-size:14px;font-weight:600">${x}</div>`).join('')}
      </div>
    </div>
    ${stepRows}
  </div>
  <div class="row" style="height:180px;margin-top:14px">
    <div class="dark card" style="width:380px"><div><div style="font-size:19px;font-weight:600">20 秒是总预算，不是各步累加</div><div style="height:10px"></div><div class="pd">期限从服务端 accept 那一刻开始计时，覆盖绑定、目标拨号到 Start 的全过程；Agent 按收到的剩余预算自算截止时间。</div></div><div style="font-size:13px;color:#60A5FA;font-weight:600">服务端期限始终权威</div></div>
    <div class="line" style="width:420px"><div><div class="t">每一步都有独立失败语义</div><div style="height:10px"></div><div class="p">目标拒绝、DNS 失败、隧道超时、容量已满各自分类计数，不会被揉成一个笼统的"连接失败"。一个目标出问题不影响其他通道。</div></div><div class="acc" style="font-size:13px">可观测的前提是分类正确</div></div>
    <div class="soft card" style="width:300px"><div><div class="t">失败即关闭，不重放</div><div style="height:10px"></div><div class="p">隧道断开就终止对应业务连接，不恢复原 TCP、不重放 SQL 语句、不自动恢复事务。</div></div><div style="font-size:13px;color:#64748B;font-weight:600">事务结果由业务判断</div></div>
  </div>
</div>
${foot('sequence')}
</div>`);

/* ---------- 12 三层凭据 ---------- */
const cred = (w, cls, num, unit, title, body, tag, numColor, bg) => `
<div class="${cls} card" style="width:${w}px">
  <div>
    <div style="display:flex;align-items:flex-end;gap:6px">
      <span style="font-size:${num === '1' ? 96 : 88}px;font-weight:700;line-height:1;color:${numColor}">${num}</span>
      <span style="font-size:20px;font-weight:600;color:${bg === 'dark' ? '#60A5FA' : '#64748B'}">${unit}</span>
    </div>
    <div style="height:18px"></div>
    <div style="font-size:21px;font-weight:600;color:${bg === 'dark' ? '#fff' : '#0F172A'}">${title}</div>
    <div style="height:12px"></div>
    <div style="font-size:15px;line-height:1.65;color:${bg === 'dark' ? '#CBD5E1' : '#64748B'}">${body}</div>
  </div>
  <div>
    <div style="width:40px;height:3px;background:#2563EB"></div>
    <div style="height:12px"></div>
    <div style="font-size:13px;font-weight:600;color:${bg === 'dark' ? '#60A5FA' : numColor}">${tag}</div>
  </div>
</div>`;

const credentials = page(`<div class="pad">
${head('CHAPTER 03 · 凭据模型', '三层凭据，各管一段，互不复用')}
<div class="bd">
<div class="row">
${cred(370, 'line', '32', '字节', '客户端密钥', '每个客户端一把独立密钥，必须是密码学随机数生成的 ≥ 32 字节，配置检查会拒绝占位值和人工口令。比较使用固定时间算法，密钥不进日志、不进匿名接口、不进状态页。', '管的是"这个客户端是谁"', '#2563EB', 'light')}
${cred(400, 'dark', '1', '次', '一次性绑定令牌', '每条业务连接生成一枚 32 字节随机令牌，随 Open 下发，Agent 拿它去数据端口做 BindData。令牌通过 CAS 从 AwaitingBind 变为 Bound 并立即清除哈希——重复、过期、跨会话、角色错置全部拒绝，失败后不可恢复。', '管的是"这条连接是不是刚被授权的"', '#60A5FA', 'dark')}
${cred(330, 'soft', '2', '道关', '互访授权', '访问方先校验被访问方证书指纹（首次认证后固定到客户端，变化不自动覆盖）；随后被访问方发出每连接 32 字节随机挑战，访问方在 TLS 内返回带域分隔的 HMAC-SHA256 证明。', '管的是"凭什么访问它"', '#64748B', 'light')}
</div>
</div>
${foot('credentials')}
</div>`);

/* ---------- 13 互访端到端加密 ---------- */
const peerTls = page(`<div class="pad">
${head('CHAPTER 03 · 客户端安全互访', '两台内网机器互访，服务端全程只见密文')}
<div class="bd">
<div class="row">
  <div style="width:680px;background:#EEF2F8;border-radius:10px;padding:18px 20px">
    <div style="height:84px;background:#fff;border:1.5px solid #DCE3ED;border-radius:8px;padding:12px 16px;display:flex;align-items:center;gap:14px">
      <div style="width:190px"><div style="font-size:16px;font-weight:600">访问方本机应用</div><div style="font-size:13px;color:#64748B">sqlcmd / 业务程序</div></div>
      <div style="flex:1;display:flex;flex-direction:column;align-items:center">
        <div style="height:2px;width:100%;background:#2563EB"></div>
        <div style="font-size:13px;color:#2563EB;font-weight:600">→ 127.0.0.1:端口</div>
      </div>
      <div style="width:200px;background:#EEF2F8;border-radius:6px;padding:10px 12px"><div style="font-size:16px;font-weight:600">访问方 Agent</div><div style="font-size:13px;color:#64748B">端口由 Agent 自选并上报</div></div>
    </div>
    <div style="height:22px;display:flex;align-items:center;justify-content:center;font-size:14px;color:#94A3B8">↓</div>
    <div style="height:158px;background:#0F172A;border-radius:8px;padding:14px 16px;display:flex;flex-direction:column;justify-content:space-between">
      <div style="display:flex;align-items:center">
        <div style="width:150px"><div style="font-size:15px;font-weight:600;color:#fff">访问方 Agent</div><div style="font-size:12px;color:#60A5FA">内层 TLS 起点</div></div>
        <div style="flex:1;display:flex;flex-direction:column;align-items:center"><div style="width:100%;height:3px;background:#2563EB"></div><div style="height:5px"></div><div style="font-size:12px;color:#CBD5E1">TLS 1.2+ · 证书指纹固定</div></div>
        <div style="width:130px;display:flex;flex-direction:column;align-items:center"><div style="font-size:15px;font-weight:700;color:#fff">Server</div><div style="font-size:12px;color:#94A3B8">只转发 DATA</div></div>
        <div style="flex:1;display:flex;flex-direction:column;align-items:center"><div style="width:100%;height:3px;background:#2563EB"></div><div style="height:5px"></div><div style="font-size:12px;color:#CBD5E1">随机挑战 + HMAC 证明</div></div>
        <div style="width:150px;display:flex;flex-direction:column;align-items:flex-end"><div style="font-size:15px;font-weight:600;color:#fff">被访问 Agent</div><div style="font-size:12px;color:#60A5FA">内层 TLS 终点</div></div>
      </div>
      <div style="background:#1E293B;border-radius:6px;padding:10px 14px;display:flex;align-items:center;justify-content:space-between">
        <span style="font-size:13px;color:#60A5FA">服务端可见：路由元数据、连接时间、密文字节量</span>
        <span style="font-size:13px;color:#fff;font-weight:600">服务端不可见：明文业务载荷</span>
      </div>
    </div>
    <div style="height:22px;display:flex;align-items:center;justify-content:center;font-size:14px;color:#94A3B8">↓ 证明校验通过之后才允许</div>
    <div style="height:84px;background:#fff;border:1.5px solid #DCE3ED;border-radius:8px;padding:12px 16px;display:flex;align-items:center;gap:14px">
      <div style="width:190px"><div style="font-size:16px;font-weight:600">被访问 Agent</div><div style="font-size:13px;color:#64748B">校验密钥与目标通道</div></div>
      <div style="flex:1;display:flex;flex-direction:column;align-items:center"><div style="height:2px;width:100%;background:#2563EB"></div><div style="font-size:13px;color:#2563EB;font-weight:600">→ 才拨号业务目标</div></div>
      <div style="width:200px;background:#EEF2F8;border-radius:6px;padding:10px 12px"><div style="font-size:16px;font-weight:600">内网目标服务</div><div style="font-size:13px;color:#64748B">1433 / 5432 / 任意 TCP</div></div>
    </div>
    <div style="height:104px;margin-top:12px;background:#fff;border-radius:8px;padding:12px 16px;display:flex;flex-direction:column;justify-content:space-between">
      <div style="font-size:15px;color:#64748B;line-height:1.6">授权顺序不能颠倒：任何一环失败，被访问方都不会对目标发起 TCP Connect，更不会传递一个字节的业务数据。错误时也不会降级为明文传输。</div>
      <div style="display:flex;gap:10px">
        ${['证书指纹固定', '32 字节随机挑战', 'HMAC-SHA256 证明', '固定时间比较'].map((x) => `<span class="chip" style="font-size:12px">${x}</span>`).join('')}
      </div>
    </div>
  </div>
  <div style="width:444px;display:flex;flex-direction:column;justify-content:space-between">
    <div class="dark" style="border-radius:10px;padding:26px 24px">
      <div style="font-size:13px;font-weight:600;color:#60A5FA;letter-spacing:2px">为什么不一样</div>
      <div style="height:14px"></div>
      <div style="font-size:28px;font-weight:700;line-height:1.3">服务端是中继者<br>不是解密者</div>
      <div style="height:18px"></div>
      <div class="pd">普通内网穿透通常是"服务端解密再转发"，业务数据对服务端完全可见。RelayLink 的互访路径把 TLS 建在两端 Agent 之间，服务端只配对并转发密文帧。</div>
    </div>
    <div style="margin-top:16px">
      ${['入口只监听访问方本机 loopback，被访问通道不再开放云端代理端口。', '映射与目标授权全在服务端下发，Agent 本地不落地访问密钥，状态页也不展示密钥。', '两端都只主动出网；任一控制会话断开，相关互访连接立即释放。'].map((x, i) => `<div style="display:flex;gap:12px;align-items:flex-start"><span style="font-size:15px;font-weight:700;color:#2563EB;width:22px">${i + 1}</span><span style="flex:1;font-size:15px;color:#64748B;line-height:1.6">${x}</span></div><div style="height:14px"></div>`).join('')}
    </div>
  </div>
</div>
</div>
${foot('peer-tls')}
</div>`);

/* ---------- 18 边界与验证 ---------- */
/* ---------- production 真实环境投产 ---------- */
const production = page(`<div class="pad">
${head('CHAPTER 04 · 落地情况', '它不是纸面设计，已经在真实环境跑起来了')}
<div class="bd">
  <div style="height:300px;display:flex;gap:20px;align-items:stretch">
    <div class="soft card" style="flex:1">
      <div>
        <div style="font-size:13px;font-weight:600;color:#2563EB;letter-spacing:1px">跨公网 RDP</div>
        <div style="height:14px"></div>
        <div style="font-size:40px;font-weight:700;line-height:1.2">连续运行 1 天</div>
        <div style="height:16px"></div>
        <div class="p" style="font-size:14.5px;line-height:1.7">部署在自有服务器上——也就是仓库验证记录里那台。公网访问端经隧道登录内网 Windows 桌面，按真实使用方式跑满了一整天。</div>
      </div>
      <div>
        <div style="height:1px;background:#DCE3ED"></div>
        <div style="height:12px"></div>
        <div style="font-size:13.5px;line-height:1.7">· 图形桌面是对时延最敏感的一类场景</div>
        <div style="font-size:13.5px;line-height:1.7">· 走的是跨公网、跨 NAT 的完整链路，不是本机回环</div>
      </div>
    </div>
    <div class="line" style="flex:1;border:1.5px solid #2563EB">
      <div>
        <div style="font-size:13px;font-weight:600;color:#2563EB;letter-spacing:1px">SQL Server 数据通道</div>
        <div style="height:14px"></div>
        <div style="font-size:40px;font-weight:700;line-height:1.2">已投产</div>
        <div style="height:16px"></div>
        <div class="p" style="font-size:14.5px;line-height:1.7">在另一个环境中完成部署并投入使用。这正是它最主要的目标场景：云端业务穿过反向隧道访问内网数据库。</div>
      </div>
      <div>
        <div style="height:1px;background:#DCE3ED"></div>
        <div style="height:12px"></div>
        <div style="font-size:13.5px;line-height:1.7">· 第一章说的"数据出不来"，堵的就是这条链路</div>
        <div style="font-size:13.5px;line-height:1.7">· 长连接、大回包、突发查询，背压设计在这里才有意义</div>
      </div>
    </div>
  </div>
  <div class="dark" style="height:212px;margin-top:12px;border-radius:10px;padding:22px 28px;display:flex;flex-direction:column;justify-content:space-between">
    <div>
      <div style="font-size:13px;font-weight:600;color:#60A5FA;letter-spacing:2px">把话说严谨一点</div>
      <div style="height:12px"></div>
      <div style="font-size:28px;font-weight:700;line-height:1.3">跑起来 ≠ 压测通过</div>
    </div>
    <div>
      <div class="pd" style="font-size:15px;line-height:1.7">连续运行一天是真实场景下的稳定性观察，不是受控容量验收：它证明的东西是"能用"，不是"能扛多少"。100 客户端 / 500 通道 / 1000 连接 / 100 Mbps 仍然只是拟定基线，24 小时满载多发压力测试尚未执行。</div>
      <div style="height:8px"></div>
      <div style="font-size:13px;color:#94A3B8;line-height:1.55">这两者的差别，是写下这段字时不愿意含糊的地方。</div>
    </div>
  </div>
</div>
${foot('production')}
</div>`);

const boundaries = page(`<div class="pad">
${head('CHAPTER 04 · 边界与验证', '它接下来会补什么，永远不做什么')}
<div class="bd">
<div class="row">
  <div class="dark card" style="width:260px">
    <div><div style="font-size:26px;font-weight:700;line-height:1.3">网络组件<br>不是网站托管方案</div><div style="height:16px"></div><div style="width:44px;height:3px;background:#2563EB"></div></div>
    <div><div class="pd" style="font-size:14px;line-height:1.65">它只负责把传输层字节从一端搬到另一端。<span style="font-weight:600;color:#fff">HTTP 域名路由、虚拟主机、证书托管这类应用层能力不会单独提供。</span></div><div style="height:12px"></div><div class="pd" style="font-size:14px;line-height:1.65">你可以拿它把 443 端口的流量送进内网，但那之后的 Web 服务器该怎么部署，是另一件事。</div><div style="height:14px"></div><div style="font-size:12.5px;color:#64748B;line-height:1.55">首期只代理 TCP 字节流，不解析也不改写业务协议。</div></div>
  </div>
  <div style="flex:1">
    <div style="height:48px;background:#DBEAFE;border-radius:10px;padding:12px 18px;display:flex;align-items:center;gap:14px">
      <span class="lb">真实环境已投产</span>
      <div style="flex:1;font-size:14px;line-height:1.5">跨公网 RDP（自有服务器，连续运行一天）· SQL Server 数据通道（另一环境）——详见上一页的分寸说明。</div>
    </div>
    <div style="height:148px;margin-top:12px;background:#EEF2F8;border-radius:10px;padding:14px 22px;display:flex;flex-direction:column;justify-content:space-between">
      <div style="display:flex;align-items:center;gap:10px">
        <span class="lb">后续会支持</span>
        <div style="display:flex;flex-wrap:wrap;gap:9px">${['UDP', '其他传输层协议', '更多连接方式'].map((x) => `<span class="chipa">${x}</span>`).join('')}</div>
      </div>
      <div style="display:flex;align-items:center;gap:10px">
        <span class="lbd">明确不做</span>
        <div style="display:flex;flex-wrap:wrap;gap:9px">${['HTTP 域名路由', 'HTTP(S) 反向代理与虚拟主机', '证书与站点托管'].map((x) => `<span class="chipx">${x}</span>`).join('')}</div>
      </div>
      <div style="font-size:12.5px;color:#64748B;line-height:1.55">同样不在范围：SOCKS、VPN / IP 层组网、P2P 打洞、SQL Browser、集群与无损迁移、SQL 审计、健康查询、告警、远程升级、断连重放。</div>
    </div>
    <div style="height:148px;margin-top:12px;border:1.5px solid #DCE3ED;border-radius:10px;padding:14px 22px">
      <div style="font-size:17px;font-weight:600">仓库内可重复执行的验证</div>
      <div style="height:10px"></div>
      <div style="font-size:13px;color:#64748B;line-height:1.6">· SQL Server 2022 与 PostgreSQL 17：建表、插入、联表分组、UPDATE、事务回滚全部断言通过。</div>
      <div style="font-size:13px;color:#64748B;line-height:1.6">· 双 Agent 端到端互访：证书固定、访问密钥证明、128 KiB 精确回显与半关闭均通过。</div>
      <div style="font-size:13px;color:#64748B;line-height:1.6">· 32 路并发每路 1 MiB；单元测试 34/34、集成测试 21/21、前端组件测试 5/5。</div>
    </div>
    <div style="height:144px;margin-top:12px;background:#0F172A;border-radius:10px;padding:14px 22px">
      <div style="font-size:17px;font-weight:600;color:#fff">还没验的（照实说）</div>
      <div style="height:10px"></div>
      <div class="pd" style="font-size:13px;line-height:1.6">· 受控的 24 小时容量压测未执行；那组拟定基线<span style="font-weight:600;color:#fff">不是实测容量</span>。</div>
      <div class="pd" style="font-size:13px;line-height:1.6">· 多种 NAT 类型与多层 NAT 的系统性验收、安装包完整装卸、最小权限专项核查未闭环。</div>
      <div class="pd" style="font-size:13px;line-height:1.6">· 审计事件只有设计；RDP 登录后数十秒出画面的根因仍未做关闭 UDP 的 A/B 复测。</div>
    </div>
  </div>
</div>
</div>
${foot('boundaries')}
</div>`);

const pages = [
  ['cover', cover],
  ['why-now', whyNow],
  ['alternatives', alternatives],
  ['architecture', architecture],
  ['sequence', sequence],
  ['credentials', credentials],
  ['peer-tls', peerTls],
  ['production', production],
  ['boundaries', boundaries],
];

const pending = MANIFEST.pages.filter((p) => p.image).map((p) => p.slug);
for (const slug of pending) {
  if (!pages.some(([s]) => s === slug)) throw new Error(`pages.json 声明了插图 ${slug}，但 build-images.mjs 里没有对应版面`);
}
for (const [slug] of pages) {
  if (!PAGE.has(slug)) throw new Error(`build-images.mjs 里的版面 ${slug} 不在 pages.json 中`);
}

// 渲染器：优先 Playwright 自带的 Chromium（稳定），失败才退回 Edge。
// Edge 曾在本机彻底无法启动（连 --version 都无输出），所以顺序不能反。
const RENDERERS = [
  'C:\\Users\\deron\\AppData\\Local\\ms-playwright\\chromium-1234\\chrome-win64\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
];
const MIN_BYTES = 20000;
const imgName = (slug) => `p${pad(PAGE.get(slug))}-${slug}.png`;
const failures = [];

for (const [slug, html] of pages) {
  const htmlPath = resolve(htmlDir, imgName(slug).replace(/\.png$/, '.html'));
  writeFileSync(htmlPath, html, 'utf8');
  const png = resolve(outDir, imgName(slug));
  rmSync(png, { force: true });

  let ok = false;
  for (let attempt = 1; attempt <= 3 && !ok; attempt += 1) {
    for (const bin of RENDERERS) {
      try {
        execFileSync(bin, [
          '--headless=new',
          '--disable-gpu',
          '--no-sandbox',
          '--hide-scrollbars',
          '--force-device-scale-factor=2',
          '--window-size=1280,720',
          `--screenshot=${png}`,
          `file:///${htmlPath.replace(/\\/g, '/')}`,
        ], { stdio: 'ignore', timeout: 60000 });
      } catch {
        /* 渲染器不可用，换下一个 */
      }
      if (existsSync(png) && statSync(png).size > MIN_BYTES) {
        ok = true;
        break;
      }
    }
  }

  if (ok) {
    console.log('rendered', imgName(slug));
  } else {
    failures.push(imgName(slug));
    console.log('FAILED', imgName(slug));
  }
}

if (failures.length) {
  console.log(`\n${failures.length} 张插图未渲染：${failures.join(', ')}`);
  console.log('常见原因：两个渲染器都不可用。可换用 agent-browser skill 的截图能力，或检查 ms-playwright 的 chromium 是否被删。');
} else {
  // 全部成功后才清理上一轮遗留的旧页码文件名
  const wanted = new Set(pages.map(([s]) => imgName(s)));
  for (const f of readdirSync(outDir).filter((x) => x.endsWith('.png'))) {
    if (wanted.has(f)) continue;
    rmSync(resolve(outDir, f), { force: true });
    console.log('removed stale', f);
  }
}
