const header = document.querySelector("[data-header]");

const syncHeader = () => header?.classList.toggle("scrolled", window.scrollY > 24);
syncHeader();
window.addEventListener("scroll", syncHeader, { passive: true });

document.querySelectorAll(".step > button").forEach((button) => {
    const initialOpen = button.closest(".step").classList.contains("active");
    button.setAttribute("aria-expanded", String(initialOpen));
    document.getElementById(button.getAttribute("aria-controls")).hidden = !initialOpen;
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
            button.textContent = document.documentElement.lang === "en" ? "Copied" : "已复制";
            window.setTimeout(() => (button.textContent = previous), 1600);
        } catch {
            button.textContent = document.documentElement.lang === "en" ? "Copy manually" : "请手动复制";
        }
    });
});


// Preserve bookmarks from the former single-page site.
const legacyRoutes = { '#guide': 'guides/', '#docs': 'docs/', '#updates': 'releases/' };
if (document.body.dataset.page === 'home' && legacyRoutes[location.hash]) {
    location.replace(new URL(legacyRoutes[location.hash], location.href));
}
