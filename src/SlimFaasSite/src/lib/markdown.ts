// lib/markdown.ts
import hljs from 'highlight.js';
import { load } from 'cheerio';

export async function renderMarkdownWithHighlight(rawHtml: string): Promise<string> {
    const $ = load(rawHtml);

    // ✅ 1) Convert Mermaid code fences -> <div class="mermaid">...</div>
    // (beaucoup de parseurs Markdown sortent <code class="language-mermaid">...</code>) :contentReference[oaicite:1]{index=1}
    $('pre code.language-mermaid').each((_, el) => {
        const code = $(el).text();
        const $pre = $(el).parent('pre');

        const $div = $('<div></div>').addClass('mermaid').text(code); // text() = échappement sûr
        $pre.replaceWith($div);
    });

    // ✅ 2) Highlight.js sur le reste uniquement
    $('pre code').each((_, element) => {
        const code = $(element).text();
        const result = hljs.highlightAuto(code);
        $(element).html(result.value);
        $(element).addClass('hljs');
    });

    // ⬇️ Wrap tables for horizontal scrolling
    $('table').each((_, el) => {
        const $table = $(el);
        if (!$table.parent().hasClass('table-scroll')) {
            $table.wrap(
                '<div class="table-scroll" role="region" aria-label="Scrollable table" tabindex="0"></div>'
            );
        }
    });

    return $.html();
}
