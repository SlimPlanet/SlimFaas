import hljs from 'highlight.js';
import { load } from 'cheerio';
import path from 'path';
import { getPublicRouteForSource } from './documentation-catalog.ts';

export interface DocumentationHeading { id: string; text: string; level: number }

const REPOSITORY_BLOB_URL = 'https://github.com/SlimPlanet/SlimFaas/blob/main';

function isRelativeUrl(value: string): boolean {
    return !/^(?:[a-z][a-z\d+.-]*:|\/\/|#|\/)/i.test(value);
}

function rewriteRelativeUrl(
    value: string,
    sourcePath: string,
    target: 'link' | 'asset',
): string {
    if (target === 'link' && value.startsWith('https://slimfaas.dev/')) {
        return value.slice('https://slimfaas.dev'.length);
    }
    if (!isRelativeUrl(value)) {
        return value;
    }

    const match = value.match(/^([^?#]*)([?#].*)?$/);
    if (!match || !match[1]) {
        return value;
    }

    const resolvedPath = path.posix.normalize(
        path.posix.join(path.posix.dirname(sourcePath), match[1]),
    );
    const suffix = match[2] ?? '';
    if (resolvedPath === 'demo/bruno-slimfaas-demo.zip') {
        return `/downloads/slimfaas-demo.zip${suffix}`;
    }
    const publicRoute = getPublicRouteForSource(resolvedPath);

    if (publicRoute !== undefined) {
        return `${publicRoute}${suffix}`;
    }

    if (target === 'asset') return `/documentation-assets/${resolvedPath}${suffix}`;
    return `${REPOSITORY_BLOB_URL}/${resolvedPath}${suffix}`;
}

export async function renderMarkdownWithHighlight(
    rawHtml: string,
    sourcePath: string,
): Promise<{ contentHtml: string; headings: DocumentationHeading[] }> {
    const $ = load(rawHtml);
    const headings: DocumentationHeading[] = [];
    const usedIds = new Set<string>();
    $('h1, h2, h3, h4, h5, h6').each((_, element) => {
        const text = $(element).text();
        const base = text.toLowerCase().replace(/[^\p{L}\p{N}\p{M}_\-\s]/gu, '').replace(/\s/g, '-') || 'section';
        let id = base;
        for (let suffix = 1; usedIds.has(id); suffix++) id = `${base}-${suffix}`;
        usedIds.add(id);
        $(element).attr('id', id);
        const level = Number(element.tagName.slice(1));
        if (level === 2 || level === 3) headings.push({ id, text, level });
    });

    $('pre code.language-mermaid').each((_, el) => {
        const code = $(el).text();
        const $pre = $(el).parent('pre');

        const $div = $('<div></div>').addClass('doc-diagram').attr('data-source', code).text(code);
        $pre.replaceWith($div);
    });

    $('pre code').each((_, element) => {
        $(element).parent('pre').addClass('code-block');
        const code = $(element).text();
        const language = $(element).attr('class')?.match(/language-([\w-]+)/)?.[1];
        const result = language && hljs.getLanguage(language)
            ? hljs.highlight(code, { language }) : hljs.highlightAuto(code);
        $(element).html(result.value);
        $(element).addClass('hljs');
    });

    $('table').each((_, el) => {
        const $table = $(el);
        if (!$table.parent().hasClass('doc-table')) {
            $table.wrap(
                '<div class="doc-table" role="region" aria-label="Scrollable table" tabindex="0"></div>'
            );
        }
    });

    $('a[href]').each((_, element) => {
        const href = $(element).attr('href');
        if (href) {
            $(element).attr('href', rewriteRelativeUrl(href, sourcePath, 'link'));
        }
    });

    $('img[src]').each((_, element) => {
        const src = $(element).attr('src');
        if (src) {
            $(element).attr('src', rewriteRelativeUrl(src, sourcePath, 'asset'));
        }
    });

    $('ul').each((_, element) => {
        const links = $(element).children('li').find('a');
        if (links.length === 3 && links.toArray().every(link =>
            /^\/get-started\/(kubernetes|local|docker-compose)$/.test($(link).attr('href') ?? ''))) {
            $(element).addClass('start-cards');
            $(element).children('li').addClass('start-cards__item');
            links.addClass('start-cards__link');
        }
    });

    if (sourcePath === 'docs/home.md') {
        const heading = $('h2').filter((_, element) => $(element).text() === 'CNCF & Community');
        if (heading.length) {
            heading.nextUntil('h2').addBack().wrapAll($('<section></section>').addClass('community').attr('aria-labelledby', heading.attr('id')!));
            heading.addClass('community__title');
            const section = heading.parent();
            section.find('img').addClass('community__logo');
            section.find('ul').addClass('community__links');
            section.find('a').addClass('community__link');
        }
    }

    return { contentHtml: $('body').html() ?? '', headings };
}
