import { readdir, readFile, writeFile, mkdir, copyFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { load } from 'cheerio';
import { loadDocumentationPage } from '../src/lib/documentation.ts';
import { DOCUMENTATION_CATALOG } from '../src/lib/documentation-catalog.ts';
import type { DocumentationId } from '../src/lib/documentation-catalog.ts';
import type { SearchEntry } from '../src/lib/search.ts';
import { createZip } from './zip.mts';

const repository = path.resolve('../..');
const publicDirectory = path.resolve('public');
const search: SearchEntry[] = [];
const assets = new Set<string>();
for (const id of Object.keys(DOCUMENTATION_CATALOG) as DocumentationId[]) {
    const page = await loadDocumentationPage(id);
    const $ = load(page.contentHtml);
    const route = DOCUMENTATION_CATALOG[id].route;
    search.push({ title: page.title, url: route, text: page.description });
    $('h2, h3').each((_, element) => {
        const content: string[] = [];
        let sibling = $(element).next();
        while (sibling.length && !sibling.is('h1, h2, h3')) {
            content.push(sibling.text()); sibling = sibling.next();
        }
        search.push({ title: `${page.title} — ${$(element).text()}`, url: `${route}#${$(element).attr('id')}`, text: content.join(' ').replace(/\s+/g, ' ').trim() });
    });
    $('img[src^="/documentation-assets/"]').each((_, element) => {
        assets.add($(element).attr('src')!.split(/[?#]/)[0].slice('/documentation-assets/'.length));
    });
}
await mkdir(publicDirectory, { recursive: true });
await writeFile(path.join(publicDirectory, 'search-index.json'), JSON.stringify(search));
await rm(path.join(publicDirectory, 'documentation-assets'), { recursive: true, force: true });
for (const asset of assets) {
    const source = path.resolve(repository, asset);
    if (!source.startsWith(repository + path.sep)) throw new Error(`Asset escapes repository: ${asset}`);
    const target = path.join(publicDirectory, 'documentation-assets', asset);
    await mkdir(path.dirname(target), { recursive: true });
    await copyFile(source, target);
}
const collection = path.join(repository, 'demo/bruno-slimfaas-demo');
async function collect(directory: string): Promise<{ name: string; content: Buffer }[]> {
    const files: { name: string; content: Buffer }[] = [];
    for (const entry of (await readdir(directory, { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
        if (entry.name.startsWith('.') || entry.name === 'node_modules') continue;
        const absolute = path.join(directory, entry.name);
        if (entry.isDirectory()) files.push(...await collect(absolute));
        else if (entry.isFile()) files.push({ name: `slimfaas-demo/${path.relative(collection, absolute).split(path.sep).join('/')}`, content: await readFile(absolute) });
    }
    return files;
}
await mkdir(path.join(publicDirectory, 'downloads'), { recursive: true });
await writeFile(path.join(publicDirectory, 'downloads/slimfaas-demo.zip'), createZip(await collect(collection)));
await copyFile(path.join(repository, '.bin/install-local-demo.sh'), path.join(publicDirectory, 'downloads/install-local-demo.sh'));
console.log(`Prepared ${search.length} search sections, ${assets.size} local assets and the Bruno download.`);
