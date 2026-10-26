import assert from "node:assert/strict";
import { test } from "node:test";
import { readFile, readdir, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const site = path.join(root, "artifacts/site");
const walk = async directory => (await Promise.all((await readdir(directory, { withFileTypes: true })).map(entry => entry.isDirectory() ? walk(path.join(directory, entry.name)) : path.join(directory, entry.name)))).flat();
const pages = (await walk(site)).filter(file => file.endsWith(".html"));
const documents = new Map(await Promise.all(pages.map(async file => [file, new JSDOM(await readFile(file, "utf8")).window.document])));
const catalog = JSON.parse(await readFile(path.join(root, "website/content.json"), "utf8"));
const versions = (await readdir(path.join(root, "docs/releases"))).filter(file => /^v\d+\.\d+\.\d+(?:-[a-z]+\.\d+)?\.md$/i.test(file));

test("every generated page has content, metadata, and a reciprocal language route", () => {
    assert.equal(pages.length, 2 * (5 + catalog.length + versions.length));
    for (const [file, document] of documents) {
        assert.equal(document.querySelectorAll("h1").length, 1, file);
        assert.ok(document.querySelector("main")?.textContent.trim(), file);
        assert.ok(document.title, file);
        assert.ok(document.querySelector('meta[name="description"]').content, file);
        assert.ok(document.querySelector('.main-nav [aria-current="page"]'), file);
        const value = document.querySelector(".language-link").getAttribute("href");
        const counterpart = path.resolve(path.dirname(file), value, "index.html");
        const other = documents.get(counterpart);
        assert.ok(other, `${file}: ${value}`);
        assert.equal(path.resolve(path.dirname(counterpart), other.querySelector(".language-link").getAttribute("href"), "index.html"), file);
        assert.ok(!document.documentElement.innerHTML.includes("{{"), file);
        const source = document.querySelector(".repository-source");
        if (source) assert.match(source.getAttribute("href"), /^https:\/\/github.com\/LazyWorkshopCreate\/RelayLink\/blob\/main\//, file);
    }
});

test("all local navigation, anchors, images, and scripts resolve under a project subpath", async () => {
    const errors = [];
    for (const [file, document] of documents) {
        const current = "https://example.invalid/RelayLink/" + path.relative(site, file).split(path.sep).join("/").replace(/index.html$/, "");
        for (const element of document.querySelectorAll("a[href], link[href], script[src], img[src]")) {
            const value = element.getAttribute("href") ?? element.getAttribute("src");
            if (/^(?:[a-z]+:|\/\/)/i.test(value)) continue;
            const url = new URL(value, current);
            if (!url.pathname.startsWith("/RelayLink/")) { errors.push(`${file}: escaped site base ${value}`); continue; }
            let target = path.join(site, decodeURIComponent(url.pathname.slice("/RelayLink/".length)));
            try {
                if ((await stat(target)).isDirectory()) target = path.join(target, "index.html");
                await stat(target);
                if (url.hash && documents.has(target)) assert.ok(documents.get(target).getElementById(decodeURIComponent(url.hash.slice(1))));
            } catch { errors.push(`${path.relative(site, file)}: missing ${value}`); }
        }
    }
    assert.deepEqual(errors, []);
});

test("home stays concise and release details retain every source section and item", async () => {
    for (const prefix of ["", "en/"]) {
        const home = documents.get(path.join(site, prefix, "index.html"));
        assert.equal(home.querySelector(".steps, .release-feed, .prose"), null);
        assert.equal(home.querySelectorAll(".content-card").length, 3);
        const list = documents.get(path.join(site, prefix, "releases/index.html"));
        assert.equal(list.querySelectorAll(".content-card").length, versions.length);
        for (const version of versions) {
            const source = await readFile(path.join(root, "docs/releases", version), "utf8");
            const detail = documents.get(path.join(site, prefix, `releases/${version.replace(/\.md$/, "")}/index.html`));
            assert.equal(detail.querySelectorAll(".prose h2").length, [...source.matchAll(/^## /gm)].length);
            assert.equal(detail.querySelectorAll(".prose li").length, [...source.matchAll(/^- /gm)].length);
            assert.equal(detail.querySelector(".prose script, .prose iframe, .prose [onclick]"), null);
        }
    }
});

test("legacy bookmarks redirect and quick-start interactions work without content fetching", async () => {
    const script = await readFile(path.join(site, "assets/app.js"), "utf8");
    assert.match(script, /'#guide': 'guides\/'/);
    assert.match(script, /'#docs': 'docs\/'/);
    assert.match(script, /'#updates': 'releases\/'/);
    const dom = new JSDOM(await readFile(path.join(site, "guides/quickstart/index.html"), "utf8"), { runScripts: "outside-only" });
    assert.equal(dom.window.document.querySelectorAll(".step-panel[hidden]").length, 0);
    dom.window.eval(script);
    const button = dom.window.document.querySelector('[aria-controls="step-2-panel"]');
    button.click();
    assert.equal(button.getAttribute("aria-expanded"), "true");
    assert.equal(dom.window.document.getElementById("step-2-panel").hidden, false);
    assert.equal(dom.window.document.getElementById("step-1-panel").hidden, true);
    dom.window.close();
});

test("UDP pages expose usage, limits, draft status, and release compatibility", () => {
    for (const prefix of ["", "en/"]) {
        const home = documents.get(path.join(site, prefix, "index.html"));
        assert.match(home.querySelector(".hero-facts").textContent, /UDP/);
        assert.doesNotMatch(home.querySelector("main").textContent, /不提供 UDP、|TCP only for now|首期仅 TCP/);
        const guideLink = home.querySelector('.hero-udp-note a');
        assert.equal(guideLink.getAttribute("href"), "guides/udp-forwarding/");
        assert.match(home.querySelector(".hero-udp-note").textContent, /rc\.9/);
        assert.match(home.querySelector(".hero-udp-note").textContent, /v3/);
        const guide = documents.get(path.join(site, prefix, "guides/udp-forwarding/index.html"));
        for (const term of ["v3/v4", "rc.9", "60", "65507", "32", "广播", "组播", "SQL Browser", "rc.8"]) assert.ok(guide.querySelector(".prose").textContent.includes(term), term);
        for (const [slug, status] of [["udp-forwarding", "Draft"], ["udp-requirements", "Draft"], ["udp-tunnels", "Proposed"]]) {
            const article = documents.get(path.join(site, prefix, `docs/${slug}/index.html`));
            assert.ok(article.querySelector(".prose").textContent.includes(status), slug);
        }
    }
});
