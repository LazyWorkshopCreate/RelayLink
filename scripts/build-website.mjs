import { cp, mkdir, readFile, readdir, rm, writeFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceDirectory = path.join(root, "website");
const outputDirectory = path.join(root, "artifacts/site");
const require = createRequire(path.join(sourceDirectory, "package.json"));
const { marked } = require("marked");
const { JSDOM } = require("jsdom");
const sanitize = require("sanitize-html");
const template = await readFile(path.join(sourceDirectory, "index.html"), "utf8");
const localeScript = await readFile(path.join(sourceDirectory, "locale.js"), "utf8");
const catalog = JSON.parse(await readFile(path.join(sourceDirectory, "content.json"), "utf8"));
const escape = value => String(value).replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");
const route = entry => `${entry.section}/${entry.slug}/`;
const sourceRoutes = new Map(catalog.map(entry => [entry.source, route(entry)]));
const relative = (from, to) => {
    const value = path.posix.relative(from, to);
    return value ? value + (to === "" || to.endsWith("/") ? "/" : "") : "./";
};
const github = source => `https://github.com/LazyWorkshopCreate/RelayLink/blob/main/${source.split("/").map(encodeURIComponent).join("/")}`;
const routes = new Set();
for (const entry of catalog) {
    if (!/^[a-z0-9-]+$/.test(entry.slug) || !["guides", "docs"].includes(entry.section) || routes.has(route(entry))) throw new Error(`Invalid or duplicate content route: ${entry.slug}`);
    routes.add(route(entry));
    if (path.relative(root, path.resolve(root, entry.source)).startsWith("..")) throw new Error(`Source outside repository: ${entry.source}`);
    await stat(path.join(root, entry.source));
}
const releaseFiles = (await readdir(path.join(root, "docs/releases"))).filter(file => /^v\d+\.\d+\.\d+(?:-[a-z]+\.\d+)?\.md$/i.test(file));
const parts = version => version.replace(/^v/, "").split(/[.-]/);
releaseFiles.sort((a, b) => {
    const left = parts(a.replace(/\.md$/, "")), right = parts(b.replace(/\.md$/, ""));
    for (const i of [0, 1, 2]) if (+left[i] !== +right[i]) return +right[i] - +left[i];
    if (!left[3] || !right[3]) return !left[3] ? -1 : 1;
    return right[3].localeCompare(left[3]) || +right[4] - +left[4];
});
const translations = JSON.parse(await readFile(path.join(sourceDirectory, "release-translations.en.json"), "utf8"));
const releases = [];
for (const file of releaseFiles) {
    const version = file.replace(/\.md$/, "");
    const source = `docs/releases/${file}`;
    const markdown = await readFile(path.join(root, source), "utf8");
    const translation = translations[version];
    if (!translation?.title || !translation.intro || !Array.isArray(translation.sections) || translation.sections.some(section => !section.title || !Array.isArray(section.items))) throw new Error(`Missing or invalid English release translation: ${version}`);
    const headings = [...markdown.matchAll(/^## (.+)$/gm)];
    if (headings.length !== translation.sections.length) throw new Error(`Incomplete English release sections: ${version}`);
    for (let i = 0; i < headings.length; i++) {
        const section = markdown.slice(headings[i].index, headings[i + 1]?.index ?? markdown.length);
        if ([...section.matchAll(/^- /gm)].length !== translation.sections[i].items.length) throw new Error(`Incomplete English release items: ${version}, section ${i + 1}`);
    }
    sourceRoutes.set(source, `releases/${version}/`);
    releases.push({ version, source, markdown, translation, title: markdown.match(/^# (.+)$/m)?.[1] ?? version, summary: markdown.split(/\r?\n/).find(line => line.trim() && !line.startsWith("#")) ?? version });
}
await rm(outputDirectory, { recursive: true, force: true });
await mkdir(path.join(outputDirectory, "assets"), { recursive: true });
for (const file of ["styles.css", "app.js"]) await cp(path.join(sourceDirectory, file), path.join(outputDirectory, "assets", file));
await cp(path.join(root, "src/RelayLink.AdminWeb/public/relaylink-icon.svg"), path.join(outputDirectory, "assets/relaylink-icon.svg"));
await writeFile(path.join(outputDirectory, ".nojekyll"), "");

const renderMarkdown = async (markdown, source, current, locale) => {
    const dom = new JSDOM(sanitize(marked.parse(markdown), {
        allowedTags: [...sanitize.defaults.allowedTags, "img"],
        allowedAttributes: { ...sanitize.defaults.allowedAttributes, img: ["src", "alt", "width", "height"], code: ["class"] },
        allowedSchemes: ["https", "http", "mailto"],
    }));
    const document = dom.window.document;
    for (const element of document.querySelectorAll("a[href], img[src]")) {
        const attribute = element.tagName === "IMG" ? "src" : "href";
        const value = element.getAttribute(attribute);
        if (/^(?:[a-z]+:|\/\/|#)/i.test(value)) continue;
        const [file, fragment] = value.split("#");
        const resolved = path.posix.normalize(path.posix.join(path.posix.dirname(source), decodeURIComponent(file)));
        if (resolved.startsWith("../")) throw new Error(`Link outside repository: ${source}: ${value}`);
        if (element.tagName === "IMG") {
            const target = `assets/repository/${resolved}`;
            await mkdir(path.dirname(path.join(outputDirectory, target)), { recursive: true });
            await cp(path.join(root, resolved), path.join(outputDirectory, target));
            element.setAttribute(attribute, relative(current, target));
        } else if (sourceRoutes.has(resolved)) {
            element.setAttribute(attribute, relative(current, `${locale === "en" ? "en/" : ""}${sourceRoutes.get(resolved)}`) + (fragment ? `#${fragment}` : ""));
        } else {
            const info = await stat(path.join(root, resolved));
            element.setAttribute(attribute, (info.isDirectory() ? github(resolved).replace("/blob/", "/tree/") : github(resolved)) + (fragment ? `#${fragment}` : ""));
        }
    }
    const used = new Map();
    const headings = [...document.querySelectorAll("h2, h3, h4")];
    for (const heading of headings) {
        const slug = heading.textContent.trim().toLowerCase().replace(/[^\p{L}\p{N}_\s-]/gu, "").replace(/\s/g, "-");
        const count = used.get(slug) ?? 0;
        used.set(slug, count + 1);
        heading.id = slug + (count ? `-${count}` : "");
    }
    document.querySelector("h1")?.remove();
    // The installation guide historically links Windows deployment with #server.
    if (source === "deploy/windows/README.md") {
        const heading = document.getElementById("服务端");
        if (heading) heading.insertAdjacentHTML("beforebegin", '<span id="server"></span>');
    }
    const toc = headings.map(heading => `<a href="#${escape(heading.id)}" class="toc-${heading.tagName.toLowerCase()}">${escape(heading.textContent)}</a>`).join("");
    const html = document.body.innerHTML;
    dom.window.close();
    return { html, toc };
};
const cards = entries => `<div class="content-list">${entries.map(entry => `<a class="content-card" href="${entry.href}"><div><h2>${escape(entry.title)}</h2><p>${escape(entry.summary)}</p></div><span aria-hidden="true">→</span></a>`).join("")}</div>`;
const titleBlock = (title, summary) => `<div class="page-heading"><p class="section-number">RELAYLINK</p><h1>${escape(title)}</h1><p>${escape(summary)}</p></div>`;
let pageCount = 0;
for (const locale of ["zh", "en"]) {
    const prefix = locale === "en" ? "en/" : "";
    const pages = [{ route: "", title: "RelayLink · 让内网 TCP / UDP 服务可控地被访问", home: true, section: "home" }];
    const quickstart = (await readFile(path.join(sourceDirectory, "quickstart.html"), "utf8"))
        .replaceAll(" hidden", "").replaceAll('aria-expanded="false"', 'aria-expanded="true"');
    pages.push({ route: "guides/quickstart/", title: "快速开始", section: "guides", content: `<section class="section content-page quickstart-heading"><div class="shell"><a href="../">返回列表</a>${titleBlock("快速开始", "四个步骤建立第一条通道")}<nav class="quickstart-toc" aria-label="On this page"><a href="#guide">部署步骤</a><a href="#deployment-docs">部署文档</a></nav></div></section>${quickstart.replace('class="doc-links"', 'class="doc-links" id="deployment-docs"')}<div class="shell quickstart-pager"><a href="../installation-and-usage/"><span>下一篇</span> · <span>安装与使用指南</span></a></div>` });
    for (const section of ["guides", "docs", "releases"]) {
        const title = { guides: "使用说明", docs: "文档中心", releases: "发布日志" }[section];
        const summary = { guides: "按部署方式选择指南，查看完整步骤。", docs: "查阅设计、安全边界与开发规范。", releases: "查看各版本的更新、兼容性与验证限制。" }[section];
        const entries = section === "releases" ? releases.map(release => ({ ...release, title: locale === "en" ? release.translation.title : release.title, summary: locale === "en" ? release.translation.intro : release.summary, href: `${release.version}/` })) : catalog.filter(entry => entry.section === section).map(entry => ({ ...entry, href: `${entry.slug}/` }));
        if (section === "guides") entries.unshift({ title: "快速开始", summary: "四个步骤建立第一条通道", href: "quickstart/" });
        pages.push({ route: `${section}/`, title, section, content: `<section class="section content-page"><div class="shell">${titleBlock(title, summary)}${cards(entries)}</div></section>` });
    }
    const articles = [...catalog.map(entry => ({ ...entry, route: route(entry) })), ...releases.map(release => ({ ...release, section: "releases", route: `releases/${release.version}/` }))];
    for (const entry of articles) {
        const current = prefix + entry.route;
        const englishRelease = locale === "en" && entry.translation;
        const markdown = englishRelease ? `# ${entry.translation.title}\n\n${entry.translation.intro}\n\n${entry.translation.sections.map(section => `## ${section.title}\n\n${section.items.map(item => `- ${item}`).join("\n")}`).join("\n\n")}\n\n${entry.translation.outro ?? ""}` : entry.markdown ?? await readFile(path.join(root, entry.source), "utf8");
        const rendered = await renderMarkdown(markdown, entry.source, current, locale);
        const title = englishRelease ? entry.translation.title : entry.title;
        const siblings = articles.filter(candidate => candidate.section === entry.section);
        const index = siblings.indexOf(entry);
        const previous = siblings[index - 1] ?? (entry.section === "guides" ? { route: "guides/quickstart/", title: "快速开始" } : undefined);
        const pager = [previous, siblings[index + 1]].map((candidate, i) => candidate ? `<a href="${relative(current, prefix + candidate.route)}"><span>${i === 0 ? "上一篇" : "下一篇"}</span> · <span>${escape(englishRelease ? candidate.translation.title : candidate.title)}</span></a>` : "").join("");
        const note = locale === "en" && !englishRelease ? '<p class="source-note">正文沿用仓库中文原文；页面导航提供英文。</p>' : "";
        pages.push({ route: entry.route, title, section: entry.section, content: `<section class="section content-page"><div class="shell"><nav class="breadcrumbs" aria-label="Breadcrumb"><a href="${relative(current, prefix + entry.section + "/")}">返回列表</a><span> / ${escape(title)}</span></nav>${titleBlock(title, "")}${note}<div class="article-layout"><aside class="article-toc"><strong>目录</strong>${rendered.toc}</aside><article class="prose" lang="${englishRelease ? "en" : "zh-CN"}">${rendered.html}<p><a class="repository-source" href="${github(entry.source)}">仓库原文</a></p></article></div><nav class="article-pager" aria-label="Article navigation">${pager}</nav></div></section>` });
    }
    for (const page of pages) {
        const current = prefix + page.route;
        let html = template.replaceAll("./assets/", relative(current, "assets") + "/");
        if (!page.home) html = html.replace(/<main id="main">[\s\S]*?<\/main>/, () => `<main id="main">${page.content}</main>`);
        else html = html.replace("{{CONTENT_LINKS}}", `<section class="section docs-section"><div class="shell">${titleBlock("开始使用", "").replaceAll("h1>", "h2>")}${cards([
            { title: "使用说明", summary: "按部署方式选择指南，查看完整步骤。", href: "guides/" },
            { title: "文档中心", summary: "查阅设计、安全边界与开发规范。", href: "docs/" },
            { title: "发布日志", summary: "查看各版本的更新、兼容性与验证限制。", href: "releases/" },
        ])}</div></section>`);
        html = html.replace(/<title>.*?<\/title>/, `<title>${escape(page.title)}${page.home ? "" : " · RelayLink"}</title>`)
            .replace(/<body>/, `<body data-page="${page.home ? "home" : page.section}">`)
            .replaceAll("{{HOME}}", relative(current, prefix))
            .replaceAll("{{GUIDES}}", relative(current, prefix + "guides/"))
            .replaceAll("{{DOCS}}", relative(current, prefix + "docs/"))
            .replaceAll("{{RELEASES}}", relative(current, prefix + "releases/"))
            .replaceAll("{{LANGUAGE}}", relative(current, (locale === "en" ? "" : "en/") + page.route));
        const dom = new JSDOM(html, { runScripts: "outside-only", url: "https://relaylink.invalid/" + current });
        const document = dom.window.document;
        document.documentElement.lang = locale === "en" ? "en" : "zh-CN";
        for (const [lang, target] of [["zh-CN", page.route], ["en", "en/" + page.route], ["x-default", page.route]]) document.querySelector(`[hreflang="${lang}"]`).setAttribute("href", relative(current, target));
        document.querySelector(".language-link").textContent = locale === "en" ? "中文" : "EN";
        document.querySelector(".language-link").setAttribute("aria-label", locale === "en" ? "Switch to Chinese" : "Switch to English");
        for (const anchor of document.querySelectorAll("a[href]:not(.repository-source)")) {
            const source = anchor.getAttribute("href").replace("https://github.com/LazyWorkshopCreate/RelayLink/blob/main/", "");
            if (sourceRoutes.has(source)) {
                anchor.href = relative(current, prefix + sourceRoutes.get(source));
                anchor.removeAttribute("target");
                anchor.removeAttribute("rel");
            }
        }
        dom.window.eval(localeScript);
        const heading = document.querySelector(".page-heading h1");
        if (heading && !page.home) document.title = `${heading.textContent} · RelayLink`;
        const description = document.querySelector(".page-heading > p:last-child")?.textContent || heading?.textContent;
        if (description && !page.home) document.querySelector('meta[name="description"]').content = description;
        document.querySelector(`.main-nav a[href="${relative(current, prefix + (page.home ? "" : page.section + "/"))}"]`)?.setAttribute("aria-current", "page");
        await mkdir(path.join(outputDirectory, current), { recursive: true });
        await writeFile(path.join(outputDirectory, current, "index.html"), dom.serialize() + "\n", "utf8");
        dom.window.close();
        pageCount++;
    }
}
console.log(`Built ${pageCount} static pages (${releases.length} releases per language) at ${outputDirectory}`);
