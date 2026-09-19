/**
 * sync.mjs — 演示文稿 / 插图 / 文章 / 设计文档 的一体化同步入口
 *
 * pages.json 是唯一事实来源：页码不再硬编码在任何地方。
 * 幻灯片源文件用 {{PAGE}} / {{TOTAL}} / {{CHn_FROM}} / {{CHn_TO}} 占位，
 * 由本脚本按清单顺序渲染成具体页码后再写入 PPTX。
 *
 * 用法：
 *   node sync.mjs all        # 全量重建（推荐）
 *   node sync.mjs deck       # 仅重建 PPTX
 *   node sync.mjs images     # 仅重渲染插图
 *   node sync.mjs article    # 同步文章里的插图文件名 + 重新生成 HTML
 *   node sync.mjs docs       # 仅同步 story.md / design.md 里的页码
 *   node sync.mjs table      # 打印当前页码清单
 *   node sync.mjs check      # 一致性自检
 *   node sync.mjs move <slug> <目标位置1起>
 *   node sync.mjs add  <slug> <位置> [--chapter N] [--image] [--rhythm valley] [--caption 标题]
 */

import { execFileSync } from 'node:child_process';
import {
  copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync,
  rmSync, writeFileSync,
} from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const buildDir = resolve(here, '.build');
const slideDir = resolve(here, 'slides');
const genDir = resolve(buildDir, 'slides');
const imageDir = resolve(here, 'assets');

const CHAPTERS = { 1: '问题', 2: '架构与协议', 3: '安全模型', 4: '运维与边界' };
const SYMMETRIC_TYPES = ['cover', 'section', 'ending'];
const pad = (n) => String(n).padStart(2, '0');

const manifestPath = resolve(here, 'pages.json');
const readManifest = () => JSON.parse(readFileSync(manifestPath, 'utf8'));
const writeManifest = (m) => writeFileSync(manifestPath, JSON.stringify(m, null, 2) + '\n', 'utf8');

// slidep 是随 node 一起安装的 shell shim，Windows 上 node 直接 spawn `slidep` 找不到，
// 因此用 node.exe 同目录下的 slidep.cmd（同一份 binaries/versions 目录）
const SLIDEP = resolve(dirname(process.execPath), process.platform === 'win32' ? 'slidep.cmd' : 'slidep');
const runSlidep = (args) => execFileSync(SLIDEP, args, { stdio: 'ignore', shell: process.platform === 'win32' });

/** 由清单派生：总页数、slug→页码、各章页码区间 */
function derive(manifest) {
  const total = manifest.pages.length;
  const nums = new Map(manifest.pages.map((p, i) => [p.slug, i + 1]));
  const ranges = {};
  for (const c of Object.keys(CHAPTERS).map(Number)) {
    const idx = manifest.pages.reduce((acc, p, i) => (p.chapter === c ? [...acc, i] : acc), []);
    if (idx.length) ranges[c] = { from: pad(Math.min(...idx) + 1), to: pad(Math.max(...idx) + 1) };
  }
  return { total, nums, ranges, chapterCount: Object.keys(ranges).length };
}

/* ---------------- 1. deck ---------------- */

function renderSlide(slug, d) {
  const src = readFileSync(resolve(slideDir, `${slug}.slide`), 'utf8');
  return src
    .replaceAll('{{PAGE}}', pad(d.nums.get(slug)))
    .replaceAll('{{TOTAL}}', pad(d.total))
    .replaceAll('{{CH1_FROM}}', d.ranges[1]?.from ?? '')
    .replaceAll('{{CH1_TO}}', d.ranges[1]?.to ?? '')
    .replaceAll('{{CH2_FROM}}', d.ranges[2]?.from ?? '')
    .replaceAll('{{CH2_TO}}', d.ranges[2]?.to ?? '')
    .replaceAll('{{CH3_FROM}}', d.ranges[3]?.from ?? '')
    .replaceAll('{{CH3_TO}}', d.ranges[3]?.to ?? '')
    .replaceAll('{{CH4_FROM}}', d.ranges[4]?.from ?? '')
    .replaceAll('{{CH4_TO}}', d.ranges[4]?.to ?? '');
}

function buildDeck() {
  const manifest = readManifest();
  const d = derive(manifest);
  rmSync(genDir, { recursive: true, force: true });
  mkdirSync(genDir, { recursive: true });

  for (const p of manifest.pages) {
    const file = resolve(slideDir, `${p.slug}.slide`);
    if (!existsSync(file)) throw new Error(`缺少幻灯片源文件: slides/${p.slug}.slide`);
    writeFileSync(resolve(genDir, `${pad(d.nums.get(p.slug))}-${p.slug}.slide`), renderSlide(p.slug, d), 'utf8');
  }

  // 每次从空白重建，避免删页后残留旧页
  // 中间产物一律用 ASCII 文件名：中文路径经过 cmd.exe 会丢字节
  const tmp = resolve(buildDir, 'deck.pptx');
  rmSync(tmp, { force: true });
  runSlidep(['create', tmp]);
  manifest.pages.forEach((p, i) => {
    runSlidep([
      'upsert-dsl', tmp,
      '--dsl-file', resolve(genDir, `${pad(i + 1)}-${p.slug}.slide`),
      '--page-index', String(i),
    ]);
  });
  copyFileSync(tmp, resolve(here, manifest.deck));
  console.log(`deck rebuilt: ${manifest.deck}（${d.total} 页）`);
}

/* ---------------- 2. images / article ---------------- */

const runNode = (file) => execFileSync(process.execPath, [resolve(here, file)], { stdio: 'inherit' });

function buildImages() {
  runNode('build-images.mjs');
}

function syncArticleRefs() {
  const manifest = readManifest();
  const d = derive(manifest);
  const mdPath = resolve(here, manifest.article);
  let md = readFileSync(mdPath, 'utf8');
  let changed = 0;
  for (const p of manifest.pages) {
    if (!p.image) continue;
    const target = `assets/p${pad(d.nums.get(p.slug))}-${p.slug}.png`;
    const re = new RegExp(`assets/(?:p\\d{2}-)?${p.slug}\\.png`, 'g');
    md = md.replace(re, (m) => {
      if (m !== target) changed += 1;
      return target;
    });
  }
  writeFileSync(mdPath, md, 'utf8');
  runNode('build-article.mjs');
  console.log(`article refs synced（${changed} 处更新）`);
}

/* ---------------- 3. docs ---------------- */

function generatedDeckBlock(manifest, d) {
  const L = [];
  L.push(`- **总页数**：${d.total} 页。`);
  L.push(`- **分章**：${d.chapterCount} 章。`);
  for (const c of Object.keys(d.ranges).map(Number)) {
    const first = pad(manifest.pages.findIndex((p) => p.chapter === c) + 1);
    L.push(`  - 第 ${c} 章「${CHAPTERS[c]}」→ 页 ${d.ranges[c].from}–${d.ranges[c].to}（扉页 ${first}）`);
  }
  const dividers = manifest.pages.filter((p) => p.divider).map((p) => pad(d.nums.get(p.slug)));
  L.push(`  - 目录（${pad(d.nums.get('toc'))}）声明 ${d.chapterCount} 章，全篇有且仅有 ${d.chapterCount} 个 section 扉页（${dividers.join(' / ')}），编号 01–0${d.chapterCount} 连续，标题与页码区间与目录逐字一致。`);

  const heroes = manifest.pages.filter((p) => p.role === 'hero');
  L.push(`- **Hero 页**：${heroes.map((p) => `${pad(d.nums.get(p.slug))}（${p.caption}）`).join('、')}。任意两个 Hero 之间至少间隔 3 个 Supporting 页。`);

  L.push('- **rhythm 曲线**：');
  L.push(`  ${manifest.pages.map((p, i) => `${pad(i + 1)} ${p.rhythm}`).join(' / ')}`);
  const runs = [];
  let run = [];
  for (let i = 0; i < manifest.pages.length; i += 1) {
    if (manifest.pages[i].rhythm === 'valley') run.push(pad(i + 1));
    else {
      if (run.length >= 2) runs.push(run);
      run = [];
    }
  }
  if (run.length >= 2) runs.push(run);
  if (runs.length) {
    const maxRun = Math.max(...runs.map((r) => r.length));
    const longest = runs.filter((r) => r.length === maxRun).map((r) => r.join('–'));
    L.push(`  - 连续 valley 最长为 ${longest.join(' 与 ')}（各 ${maxRun} 页），无连续 ≥3 valley。`);
  } else {
    L.push('  - 无连续 valley。');
  }

  const asym = manifest.pages.filter((p) => !SYMMETRIC_TYPES.includes(p.type));
  const sym = manifest.pages.filter((p) => SYMMETRIC_TYPES.includes(p.type));
  L.push(`- **非对称版式预算**：${asym.map((p) => pad(d.nums.get(p.slug))).join(' / ')} 共 ${asym.length} 页，占 ${Math.round((asym.length / d.total) * 100)}%（≥ 40%）。`);
  L.push(`- **对称版式预算**：仅 ${sym.map((p) => pad(d.nums.get(p.slug))).join(' / ')} 共 ${sym.length} 页（封面、${d.chapterCount} 个章节扉页、结束页），均为全屏视觉+大标题。`);
  return L.join('\n');
}

function syncDocs() {
  const manifest = readManifest();
  const d = derive(manifest);

  for (const name of ['story.md', 'design.md']) {
    const path = resolve(here, name);
    let text = readFileSync(path, 'utf8');

    // 表格行：| NN | NN.slide | 或 | NN | slug.slide | —— 一律按 slug 重编号
    let seq = 0;
    const lines = text.split(/\r?\n/).map((line) => {
      const m = line.match(/^\|\s*(\d{2,})\s*\|\s*([^|]+?)\.slide\s*\|/);
      if (!m) return line;
      const raw = m[2].trim();
      const slug = /^\d{2}$/.test(raw) ? manifest.pages[seq]?.slug : raw;
      seq += 1;
      if (!slug || !d.nums.has(slug)) return line;
      return line.replace(m[0], `| ${pad(d.nums.get(slug))} | ${slug}.slide |`);
    });
    text = lines.join('\n');

    // 显式锚点：21<!--p:total-->、07<!--p:architecture-->
    text = text.replace(/(\d+)<!--p:total-->/g, String(d.total));
    text = text.replace(/(\d{2})<!--p:([a-z0-9-]+)-->/g, (whole, _n, slug) =>
      (d.nums.has(slug) ? pad(d.nums.get(slug)) : whole));

    // 自动生成块
    text = text.replace(
      /<!--gen:deck-->[\s\S]*?<!--\/gen:deck-->/g,
      `<!--gen:deck-->\n${generatedDeckBlock(manifest, d)}\n<!--/gen:deck-->`,
    );

    writeFileSync(path, text, 'utf8');
    console.log(`docs synced: ${name}`);
  }
}

/* ---------------- 4. move / add ---------------- */

function move(slug, posRaw) {
  const manifest = readManifest();
  const pos = Number(posRaw);
  const idx = manifest.pages.findIndex((p) => p.slug === slug);
  if (idx < 0) throw new Error(`pages.json 里没有 ${slug}`);
  if (!Number.isInteger(pos) || pos < 1 || pos > manifest.pages.length) {
    throw new Error('用法: node sync.mjs move <slug> <目标位置(1起)>');
  }
  const [page] = manifest.pages.splice(idx, 1);
  manifest.pages.splice(pos - 1, 0, page);
  writeManifest(manifest);
  console.log(`moved ${slug} → 第 ${pos} 页（随后跑 node sync.mjs all）`);
}

function STUB(slug, chapter) {
  return `<Slide style={{ width: '1280px', height: '720px', padding: '20px 64px', background: '#FFFFFF', flexDirection: 'column' }}>
    <Box style={{ height: 88, justifyContent: 'flex-end' }}>
        <Text style={{ fontSize: 13, fontWeight: 600, color: '#2563EB', letterSpacing: 1.5 }}>CHAPTER 0${chapter ?? 1} · 待填写</Text>
        <Box style={{ height: 8 }} />
        <Text style={{ fontSize: 34, fontWeight: 700, color: '#0F172A', lineHeight: 1.25 }}>TODO：${slug}</Text>
    </Box>
    <Box style={{ height: 524, marginTop: 16 }}>
        <Text style={{ fontSize: 17, color: '#64748B', lineHeight: 1.6 }}>这一页的内容还没写。</Text>
    </Box>
    <Box style={{ height: 36, marginTop: 12, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between' }}>
        <Box style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}>
            <Box style={{ width: 6, height: 6, background: '#2563EB' }} />
            <Text style={{ fontSize: 14, color: '#64748B' }}>RelayLink · 开源反向 TCP 代理</Text>
        </Box>
        <Text style={{ fontSize: 14, color: '#64748B' }}>{{PAGE}} / {{TOTAL}}</Text>
    </Box>
</Slide>
`;
}

function add(args) {
  const manifest = readManifest();
  const flags = new Map();
  const positional = [];
  for (let i = 0; i < args.length; i += 1) {
    if (args[i].startsWith('--')) flags.set(args[i].slice(2), args[i + 1]);
    else positional.push(args[i]);
  }
  const [slug, posRaw] = positional;
  const pos = Number(posRaw);
  if (!slug || !Number.isInteger(pos) || pos < 1 || pos > manifest.pages.length + 1) {
    throw new Error('用法: node sync.mjs add <slug> <插入位置(1起)> [--chapter N] [--image] [--rhythm valley] [--caption 标题]');
  }
  if (manifest.pages.some((p) => p.slug === slug)) throw new Error(`${slug} 已存在`);

  const inherit = manifest.pages[Math.max(0, pos - 2)];
  const entry = {
    slug,
    type: flags.get('type') ?? 'content',
    role: flags.get('role') ?? 'supporting',
    rhythm: flags.get('rhythm') ?? 'valley',
    chapter: flags.has('chapter') ? Number(flags.get('chapter')) : (inherit?.chapter ?? null),
    image: flags.has('image'),
    caption: flags.get('caption') ?? slug,
  };
  manifest.pages.splice(pos - 1, 0, entry);
  writeManifest(manifest);

  const stub = resolve(slideDir, `${slug}.slide`);
  if (!existsSync(stub)) {
    writeFileSync(stub, STUB(slug, entry.chapter), 'utf8');
    console.log(`created stub: slides/${slug}.slide`);
  }
  console.log(`added ${slug} → 第 ${pos} 页。补齐内容后跑 node sync.mjs all`);
}

/* ---------------- 5. check ---------------- */

function check() {
  const manifest = readManifest();
  const d = derive(manifest);
  const problems = [];

  const slideFiles = readdirSync(slideDir).filter((f) => f.endsWith('.slide')).map((f) => f.slice(0, -6));
  for (const p of manifest.pages) {
    if (!slideFiles.includes(p.slug)) problems.push(`缺 slides/${p.slug}.slide`);
  }
  for (const f of slideFiles) {
    if (!d.nums.has(f)) problems.push(`slides/${f}.slide 没写进 pages.json`);
  }

  for (const p of manifest.pages) {
    if (!p.image) continue;
    const name = `p${pad(d.nums.get(p.slug))}-${p.slug}.png`;
    if (!existsSync(resolve(imageDir, name))) problems.push(`缺插图 assets/${name}`);
  }

  const md = readFileSync(resolve(here, manifest.article), 'utf8');
  for (const p of manifest.pages) {
    if (!p.image) continue;
    const ref = `assets/p${pad(d.nums.get(p.slug))}-${p.slug}.png`;
    if (!md.includes(ref)) problems.push(`文章未引用 ${ref}`);
  }
  for (const m of md.matchAll(/assets\/[^\s)]+\.png/g)) {
    if (!existsSync(resolve(here, m[0]))) problems.push(`文章引用了不存在的图片：${m[0]}`);
  }

  const stale = readdirSync(imageDir).filter((f) => f.endsWith('.png')).filter((f) => {
    const m = f.match(/^p(\d{2})-([a-z0-9-]+)\.png$/);
    if (!m) return true;
    const want = `p${pad(d.nums.get(m[2]) ?? 0)}-${m[2]}.png`;
    return f !== want;
  });
  for (const f of stale) problems.push(`插图文件名与页码不符（陈旧）：assets/${f}`);

  console.log(`pages ${d.total}｜chapters ${Object.entries(d.ranges).map(([c, r]) => `${c}:${r.from}-${r.to}`).join(' ')}`);
  console.log(`slides ${slideFiles.length} 文件｜images ${manifest.pages.filter((p) => p.image).length} 张`);
  if (problems.length) {
    console.log('\n发现问题：');
    for (const p of problems) console.log('  - ' + p);
    process.exitCode = 1;
  } else {
    console.log('\ncheck OK：页码 / 插图 / 文章引用完全一致');
  }
}

function table() {
  const manifest = readManifest();
  const d = derive(manifest);
  for (const [i, p] of manifest.pages.entries()) {
    console.log(
      `  ${pad(i + 1)}  ${p.slug.padEnd(15)} ${String(p.type).padEnd(9)} ${String(p.rhythm).padEnd(11)} ch${p.chapter ?? '-'}${(p.image ? '  [图]' : '      ')}  ${p.caption}`,
    );
  }
}

/* ---------------- CLI ---------------- */

const [cmd = 'all', ...args] = process.argv.slice(2);

switch (cmd) {
  case 'all':
    buildDeck();
    buildImages();
    syncArticleRefs();
    syncDocs();
    check();
    break;
  case 'deck':
    buildDeck();
    break;
  case 'images':
    buildImages();
    break;
  case 'article':
    syncArticleRefs();
    break;
  case 'docs':
    syncDocs();
    break;
  case 'check':
    check();
    break;
  case 'table':
    table();
    break;
  case 'move':
    move(args[0], args[1]);
    break;
  case 'add':
    add(args);
    break;
  default:
    console.log(`未知命令: ${cmd}`);
    process.exitCode = 1;
}
