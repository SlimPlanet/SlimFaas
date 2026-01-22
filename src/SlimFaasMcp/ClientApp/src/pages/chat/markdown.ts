import DOMPurify from "dompurify";
import { marked } from "marked";
import hljs from "highlight.js";
// theme
import "highlight.js/styles/github-dark.css";

export function renderMarkdownToSafeHtml(mdText: string): string {
  const raw = marked.parse(mdText ?? "") as string;
  const wrapper = document.createElement("div");
  wrapper.innerHTML = raw;
  wrapper.querySelectorAll("pre code").forEach((block) => {
    hljs.highlightElement(block as HTMLElement);
  });
  return DOMPurify.sanitize(wrapper.innerHTML, {
    USE_PROFILES: { html: true, svg: false, svgFilters: false, mathMl: false },
  });
}

export function enhanceCodeBlocksWithCopy(container: HTMLElement) {
  container.querySelectorAll(".msg--bot pre").forEach((pre) => {
    const el = pre as HTMLElement & { dataset: any };
    if (el.dataset.copyEnhanced) return;
    el.dataset.copyEnhanced = "1";

    const btn = document.createElement("button");
    btn.className = "code-copy";
    btn.textContent = "Copy";

    const wrap = document.createElement("div");
    wrap.style.position = "relative";
    const parent = pre.parentNode;
    if (!parent) return;
    parent.insertBefore(wrap, pre);
    wrap.appendChild(pre);
    wrap.appendChild(btn);

    btn.addEventListener("click", async () => {
      const code = (pre.querySelector("code") as HTMLElement | null)?.innerText ?? (pre as any).innerText ?? "";
      await navigator.clipboard.writeText(code);
      btn.textContent = "Copied ✓";
      setTimeout(() => (btn.textContent = "Copy"), 1200);
    });
  });
}
