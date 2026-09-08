import { access, readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { load } from 'cheerio';
import { DOCUMENTATION_CATALOG } from '../src/lib/documentation-catalog.ts';

const out = path.resolve('out');
const ids = new Map<string, Set<string>>();
const pages = new Map<string, ReturnType<typeof load>>();
for (const entry of Object.values(DOCUMENTATION_CATALOG)) {
    const filename = entry.route === '/' ? 'index.html' : `${entry.route.slice(1)}.html`;
    const $ = load(await readFile(path.join(out, filename), 'utf8'));
    const headings = $('[id]').toArray().map(el => $(el).attr('id')!);
    if (new Set(headings).size !== headings.length) throw new Error(`Duplicate IDs on ${entry.route}`);
    ids.set(entry.route, new Set(headings));
    pages.set(entry.route, $);
}
let checked = 0;
for (const [route, $] of pages) {
    for (const element of $('a[href], img[src]').toArray()) {
        const target = $(element).attr(element.tagName === 'img' ? 'src' : 'href')!;
        if (!target.startsWith('/') && !target.startsWith('#')) continue;
        const url = new URL(target, `https://slimfaas.dev${route}`);
        const targetRoute = url.pathname.replace(/\/$/, '') || '/';
        if (ids.has(targetRoute)) {
            const hash = decodeURIComponent(url.hash.slice(1));
            if (hash && !ids.get(targetRoute)!.has(hash)) throw new Error(`${route}: missing anchor ${target}`);
        } else {
            const file = path.resolve(out, '.' + url.pathname);
            if (!file.startsWith(out + path.sep)) throw new Error(`Invalid local link: ${target}`);
            await access(file).catch(() => { throw new Error(`${route}: missing asset/route ${target}`); });
        }
        checked++;
    }
    if ($('a[href*="/mcp"]').length) throw new Error(`MCP link remains on ${route}`);
}
const sitemap = await readFile(path.join(out, 'sitemap.xml'), 'utf8');
if (sitemap.includes('/mcp')) throw new Error('MCP remains in sitemap');
if ((await readdir(out)).includes('mcp.html')) throw new Error('Stale MCP page remains in export');
const index = JSON.parse(await readFile(path.join(out, 'search-index.json'), 'utf8')) as { url: string }[];
for (const entry of index) {
    const url = new URL(entry.url, 'https://slimfaas.dev');
    if (!ids.has(url.pathname) || (url.hash && !ids.get(url.pathname)!.has(decodeURIComponent(url.hash.slice(1))))) throw new Error(`Broken search result ${entry.url}`);
}
console.log(`Checked ${pages.size} pages, ${checked} local links/assets and ${index.length} search entries.`);
