import hljs from 'highlight.js';
import { load } from 'cheerio';
import path from 'path';
import { getPublicRouteForSource } from './documentation-catalog';

const REPOSITORY_BLOB_URL = 'https://github.com/SlimPlanet/SlimFaas/blob/main';
const REPOSITORY_RAW_URL = 'https://raw.githubusercontent.com/SlimPlanet/SlimFaas/main';

function isRelativeUrl(value: string): boolean {
    return !/^(?:[a-z][a-z\d+.-]*:|\/\/|#|\/)/i.test(value);
}

function rewriteRelativeUrl(
    value: string,
    sourcePath: string,
    target: 'link' | 'asset',
): string {
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
    const publicRoute = getPublicRouteForSource(resolvedPath);

    if (publicRoute !== undefined) {
        return `${publicRoute}${suffix}`;
    }

    const baseUrl = target === 'asset' ? REPOSITORY_RAW_URL : REPOSITORY_BLOB_URL;
    return `${baseUrl}/${resolvedPath}${suffix}`;
}

export async function renderMarkdownWithHighlight(
    rawHtml: string,
    sourcePath: string,
): Promise<string> {
    const $ = load(rawHtml);

    $('pre code.language-mermaid').each((_, el) => {
        const code = $(el).text();
        const $pre = $(el).parent('pre');

        const $div = $('<div></div>').addClass('mermaid').text(code);
        $pre.replaceWith($div);
    });

    $('pre code').each((_, element) => {
        const code = $(element).text();
        const result = hljs.highlightAuto(code);
        $(element).html(result.value);
        $(element).addClass('hljs');
    });

    $('table').each((_, el) => {
        const $table = $(el);
        if (!$table.parent().hasClass('table-scroll')) {
            $table.wrap(
                '<div class="table-scroll" role="region" aria-label="Scrollable table" tabindex="0"></div>'
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

    return $('body').html() ?? '';
}
