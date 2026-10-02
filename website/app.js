const header = document.querySelector("[data-header]");

const syncHeader = () => header?.classList.toggle("scrolled", window.scrollY > 24);
syncHeader();
window.addEventListener("scroll", syncHeader, { passive: true });

document.querySelectorAll(".step > button").forEach((button) => {
    button.addEventListener("click", () => {
        const step = button.closest(".step");
        const panel = document.getElementById(button.getAttribute("aria-controls"));
        const willOpen = !step.classList.contains("active");

        document.querySelectorAll(".step").forEach((candidate) => {
            candidate.classList.remove("active");
            const candidateButton = candidate.querySelector("button");
            const candidatePanel = document.getElementById(candidateButton.getAttribute("aria-controls"));
            candidateButton.setAttribute("aria-expanded", "false");
            candidatePanel.hidden = true;
        });

        if (willOpen) {
            step.classList.add("active");
            button.setAttribute("aria-expanded", "true");
            panel.hidden = false;
        }
    });
});

document.querySelectorAll("[data-copy]").forEach((button) => {
    button.addEventListener("click", async () => {
        try {
            await navigator.clipboard.writeText(button.dataset.copy);
            const previous = button.textContent;
            button.textContent = window.RELAYLINK_LOCALE === "en" ? "Copied" : "已复制";
            window.setTimeout(() => (button.textContent = previous), 1600);
        } catch {
            button.textContent = window.RELAYLINK_LOCALE === "en" ? "Copy manually" : "请手动复制";
        }
    });
});

const releaseFeed = document.querySelector("[data-release-feed]");
const releaseSummary = document.querySelector("[data-release-summary]");
const locale = window.RELAYLINK_LOCALE === "en" ? "en" : "zh";
const releaseCatalog = window.RELAYLINK_RELEASES ?? {};
const releases = Array.isArray(releaseCatalog) ? releaseCatalog : (releaseCatalog[locale] ?? []);
const messages = locale === "en"
    ? {
        emptyTitle: "No releases yet",
        emptyText: "Visit the GitHub repository for current information",
        emptyFeed: "No release notes were found.",
        current: "Latest release candidate",
        count: (value) => `${value} releases`,
        latest: "Latest release",
        release: "Release",
        collapse: "Collapse",
        expand: "Expand",
        details: "details",
    }
    : {
        emptyTitle: "暂无版本",
        emptyText: "请查看 GitHub 仓库",
        emptyFeed: "尚未找到发布说明。",
        current: "当前最新候选版本",
        count: (value) => `共 ${value} 次发布`,
        latest: "Latest release",
        release: "Release",
        collapse: "收起",
        expand: "展开",
        details: "详情",
    };

const escapeHtml = (value) =>
    String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");

const inlineMarkup = (value) =>
    escapeHtml(value).replace(/`([^`]+)`/g, "<code>$1</code>");

if (releaseFeed && releaseSummary) {
    if (releases.length === 0) {
        releaseSummary.innerHTML = `<strong>${messages.emptyTitle}</strong><span>${messages.emptyText}</span>`;
        releaseFeed.innerHTML = `<p class="release-loading">${messages.emptyFeed}</p>`;
    } else {
        releaseSummary.innerHTML = `<strong>${escapeHtml(releases[0].version)}</strong><span>${messages.current} · ${messages.count(releases.length)}</span>`;
        releaseFeed.innerHTML = releases
            .map(
                (release, index) => `
                    <article class="release-entry${index === 0 ? " open" : ""}">
                        <div class="release-meta">
                            <span>${index === 0 ? messages.latest : messages.release}</span>
                            <strong>${escapeHtml(release.version)}</strong>
                        </div>
                        <div class="release-copy">
                            <h3>${escapeHtml(release.title)}</h3>
                            <p>${inlineMarkup(release.intro)}</p>
                        </div>
                        <button class="release-toggle" type="button" aria-label="${index === 0 ? messages.collapse : messages.expand} ${escapeHtml(release.version)} ${messages.details}" aria-expanded="${index === 0}" aria-controls="release-details-${index}">
                            <svg aria-hidden="true" viewBox="0 0 16 16"><path d="m4 6 4 4 4-4" /></svg>
                        </button>
                        <div class="release-details" id="release-details-${index}" ${index === 0 ? "" : "hidden"}>
                            ${release.sections
                                .filter((section) => section.items.length > 0)
                                .slice(0, 3)
                                .map(
                                    (section) => `
                                        <section class="release-section">
                                            <h4>${escapeHtml(section.title)}</h4>
                                            <ul>${section.items.map((item) => `<li>${inlineMarkup(item)}</li>`).join("")}</ul>
                                        </section>`,
                                )
                                .join("")}
                        </div>
                    </article>`,
            )
            .join("");

        releaseFeed.querySelectorAll(".release-toggle").forEach((button) => {
            button.addEventListener("click", () => {
                const entry = button.closest(".release-entry");
                const details = entry.querySelector(".release-details");
                const open = !entry.classList.contains("open");
                entry.classList.toggle("open", open);
                details.hidden = !open;
                button.setAttribute("aria-expanded", String(open));
                button.setAttribute("aria-label", `${open ? messages.collapse : messages.expand} ${entry.querySelector(".release-meta strong").textContent} ${messages.details}`);
            });
        });
    }
}
