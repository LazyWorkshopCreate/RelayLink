import { cp, mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(scriptDirectory, "..");
const sourceDirectory = path.join(root, "website");
const outputDirectory = path.join(root, "artifacts", "site");
const releasesDirectory = path.join(root, "docs", "releases");

const versionParts = (value) => {
    const match = value.match(/^v(\d+)\.(\d+)\.(\d+)(?:-([a-z]+)\.(\d+))?$/i);
    if (!match) return [0, 0, 0, "", 0];
    return [Number(match[1]), Number(match[2]), Number(match[3]), match[4] ?? "", Number(match[5] ?? 0)];
};

const compareVersions = (left, right) => {
    const a = versionParts(left);
    const b = versionParts(right);
    for (const index of [0, 1, 2]) {
        if (a[index] !== b[index]) return b[index] - a[index];
    }
    if (a[3] !== b[3]) return a[3] ? 1 : -1;
    return b[4] - a[4];
};

const normalizeMarkdown = (value) =>
    value
        .replace(/\[([^\]]+)\]\([^)]+\)/g, "$1")
        .replace(/\*\*([^*]+)\*\*/g, "$1")
        .trim();

const parseRelease = async (fileName) => {
    const markdown = await readFile(path.join(releasesDirectory, fileName), "utf8");
    const lines = markdown.replace(/\r\n/g, "\n").split("\n");
    const title = normalizeMarkdown(lines.find((line) => line.startsWith("# "))?.slice(2) ?? fileName.replace(/\.md$/, ""));
    const version = fileName.replace(/\.md$/, "");
    const sections = [];
    const introLines = [];
    let currentSection;

    for (const line of lines.slice(1)) {
        if (line.startsWith("## ")) {
            currentSection = { title: normalizeMarkdown(line.slice(3)), items: [] };
            sections.push(currentSection);
            continue;
        }
        if (line.startsWith("- ") && currentSection) {
            currentSection.items.push(normalizeMarkdown(line.slice(2)));
            continue;
        }
        if (!currentSection && line.trim()) introLines.push(normalizeMarkdown(line));
    }

    return {
        version,
        title,
        intro: introLines.join(" "),
        sections,
    };
};

await rm(outputDirectory, { recursive: true, force: true });
await mkdir(path.join(outputDirectory, "assets"), { recursive: true });
await mkdir(path.join(outputDirectory, "en"), { recursive: true });

const sourceHtml = await readFile(path.join(sourceDirectory, "index.html"), "utf8");
const englishHtml = sourceHtml
    .replace('<html lang="zh-CN">', '<html lang="en">')
    .replace(
        "RelayLink · 让内网 TCP 服务可控地被访问",
        "RelayLink · Controlled access to private TCP services",
    )
    .replace(
        "RelayLink 是一个基于 .NET 的开源反向 TCP 代理，让云端应用在内网无需开放公网入站端口的情况下访问 TCP 服务。",
        "RelayLink is an open-source reverse TCP proxy built on .NET. It lets cloud applications reach private-network TCP services without exposing inbound ports on the private network.",
    )
    .replaceAll('href="./assets/', 'href="../assets/')
    .replaceAll('src="./assets/', 'src="../assets/')
    .replace('<link rel="alternate" hreflang="zh-CN" href="./" />', '<link rel="alternate" hreflang="zh-CN" href="../" />')
    .replace('<link rel="alternate" hreflang="en" href="./en/" />', '<link rel="alternate" hreflang="en" href="./" />')
    .replace('<link rel="alternate" hreflang="x-default" href="./" />', '<link rel="alternate" hreflang="x-default" href="../" />');

await Promise.all([
    writeFile(path.join(outputDirectory, "index.html"), sourceHtml, "utf8"),
    writeFile(path.join(outputDirectory, "en", "index.html"), englishHtml, "utf8"),
    cp(path.join(sourceDirectory, "styles.css"), path.join(outputDirectory, "assets", "styles.css")),
    cp(path.join(sourceDirectory, "app.js"), path.join(outputDirectory, "assets", "app.js")),
    cp(path.join(sourceDirectory, "locale.js"), path.join(outputDirectory, "assets", "locale.js")),
    cp(path.join(root, "src", "RelayLink.AdminWeb", "public", "relaylink-icon.svg"), path.join(outputDirectory, "assets", "relaylink-icon.svg")),
]);

const releaseFiles = (await readdir(releasesDirectory))
    .filter((fileName) => /^v.+\.md$/i.test(fileName))
    .sort((left, right) => compareVersions(left.replace(/\.md$/, ""), right.replace(/\.md$/, "")));
const releases = await Promise.all(releaseFiles.map(parseRelease));
const englishReleaseTranslations = JSON.parse(
    await readFile(path.join(sourceDirectory, "release-translations.en.json"), "utf8"),
);
const missingEnglishReleases = releases
    .map((release) => release.version)
    .filter((version) => !englishReleaseTranslations[version]);
if (missingEnglishReleases.length > 0) {
    throw new Error(`Missing English release translations: ${missingEnglishReleases.join(", ")}`);
}

const englishReleases = releases.map((release) => ({
    version: release.version,
    ...englishReleaseTranslations[release.version],
}));
const releaseCatalog = { zh: releases, en: englishReleases };
const releaseScript = `window.RELAYLINK_RELEASES = ${JSON.stringify(releaseCatalog, null, 2)};\n`;

await Promise.all([
    writeFile(path.join(outputDirectory, "assets", "releases.js"), releaseScript, "utf8"),
    writeFile(path.join(outputDirectory, ".nojekyll"), "", "utf8"),
]);

console.log(`Built RelayLink website with ${releases.length} release entries at ${outputDirectory}`);
