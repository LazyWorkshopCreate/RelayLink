import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const md = readFileSync(resolve(here, 'wechat-article.md'), 'utf8');

const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const inline = (s) =>
  esc(s)
    .replace(/!\[([^\]]*)\]\(([^)]+)\)/g, '<img alt="$1" src="$2">')
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^*])\*([^*]+)\*/g, '$1<em>$2</em>');

const lines = md.split(/\r?\n/);
const out = [];
let i = 0;
let listType = null;
let docTitle = 'RelayLink';

const closeList = () => {
  if (listType) {
    out.push(`</${listType}>`);
    listType = null;
  }
};

while (i < lines.length) {
  const line = lines[i];

  if (/^!\[[^\]]*\]\([^)]+\)\s*$/.test(line.trim())) {
    closeList();
    out.push(`<figure>${inline(line.trim())}</figure>`);
    i += 1;
    continue;
  }

  // 一级标题只作为文档标题（<title>），不render成可见大标题：
  // 贴到公众号时标题由编辑器单独填，正文里再出现一次会重复。
  if (/^#\s+/.test(line)) {
    closeList();
    docTitle = line.replace(/^#\s+/, '').trim();
    i += 1;
    continue;
  }
  if (/^##\s+/.test(line)) {
    closeList();
    out.push(`<h2>${inline(line.replace(/^##\s+/, ''))}</h2>`);
    i += 1;
    continue;
  }
  if (/^>\s?/.test(line)) {
    closeList();
    out.push(`<blockquote>${inline(line.replace(/^>\s?/, ''))}</blockquote>`);
    i += 1;
    continue;
  }
  if (/^-\s+/.test(line)) {
    if (listType !== 'ul') {
      closeList();
      out.push('<ul>');
      listType = 'ul';
    }
    out.push(`<li>${inline(line.replace(/^-\s+/, ''))}</li>`);
    i += 1;
    continue;
  }
  if (/^\d+\.\s+/.test(line)) {
    if (listType !== 'ol') {
      closeList();
      out.push('<ol>');
      listType = 'ol';
    }
    out.push(`<li>${inline(line.replace(/^\d+\.\s+/, ''))}</li>`);
    i += 1;
    continue;
  }
  if (line.trim() === '') {
    closeList();
    i += 1;
    continue;
  }
  closeList();
  const buf = [line];
  i += 1;
  while (i < lines.length && lines[i].trim() !== '' && !/^(#|>|-\s|\d+\.\s)/.test(lines[i]) && !/^!\[[^\]]*\]\([^)]+\)\s*$/.test(lines[i].trim())) {
    buf.push(lines[i]);
    i += 1;
  }
  out.push(`<p>${inline(buf.join(''))}</p>`);
}
closeList();

const html = `<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>${esc(docTitle)}</title>
<style>
  /* 正文 14px；小标题、引言、行内代码与垂直间距按 14/17 ≈ 0.82 等比缩小；一级标题只进 <title>，正文不出现 */
  :root{--ink:#1F2328;--gray:#57606A;--line:#E4E8EE;--accent:#2563EB;--bg:#FFFFFF}
  *{box-sizing:border-box}
  body{margin:0;background:#F3F5F8;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","Microsoft YaHei","PingFang SC",sans-serif;color:var(--ink)}
  .wrap{max-width:760px;margin:0 auto;background:var(--bg);padding:40px 36px 60px;min-height:100vh}
  h2{font-size:18px;line-height:1.4;margin:36px 0 15px;padding-left:11px;border-left:3px solid var(--accent)}
  p{font-size:14px;line-height:1.85;margin:0 0 16px;color:#2C333A;text-align:justify}
  blockquote{margin:0 0 23px;padding:13px 16px;background:#EEF2F8;border-left:3px solid var(--accent);border-radius:0 6px 6px 0;font-size:13px;line-height:1.8;color:#42506B}
  blockquote p{margin:0;font-size:13px;color:#42506B}
  strong{color:#111827;font-weight:600}
  code{background:#EEF2F8;padding:2px 5px;border-radius:4px;font-family:ui-monospace,Consolas,"Courier New",monospace;font-size:12px;color:#1D4ED8;letter-spacing:.3px}
  figure{margin:23px 0 25px}
  figure img{width:100%;display:block;border:1px solid var(--line);border-radius:8px}
  ul,ol{margin:0 0 16px;padding-left:20px}
  li{font-size:14px;line-height:1.85;margin-bottom:7px;color:#2C333A}
  hr{border:none;border-top:1px solid var(--line);margin:30px 0}
  @media(max-width:640px){.wrap{padding:23px 16px 46px}h2{font-size:16px}p,li{font-size:13px}}
</style>
</head>
<body><div class="wrap">
${out.join('\n')}
</div></body></html>
`;

writeFileSync(resolve(here, 'wechat-article.html'), html, 'utf8');
console.log('article html written, blocks:', out.length);
