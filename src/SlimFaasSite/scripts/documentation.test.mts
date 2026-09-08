import test from 'node:test';
import assert from 'node:assert/strict';
import { access, readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { remark } from 'remark';
import { load } from 'cheerio';
import html from 'remark-html';
import { renderMarkdownWithHighlight } from '../src/lib/markdown.ts';
import { searchDocumentation } from '../src/lib/search.ts';
import { DOCUMENTATION_CATALOG, DOCUMENTATION_ORDER } from '../src/lib/documentation-catalog.ts';
import { createZip } from './zip.mts';

test('headings have unique stable anchors and links stay on the current site', async () => {
    const raw = await remark().use(html).process('# Guide\n## Hello, World!\n## Hello, World!\n## Hello World-1\n[Tour](guided-tour.md#1-read-the-cluster-state)\n[Source](../README.md)\n![Diagram](example.png)');
    const result = await renderMarkdownWithHighlight(String(raw), 'docs/get-started.md');
    assert.deepEqual(result.headings.map(h => h.id), ['hello-world', 'hello-world-1', 'hello-world-1-1']);
    assert.match(result.contentHtml, /href="\/guided-tour#1-read-the-cluster-state"/);
    assert.match(result.contentHtml, /href="https:\/\/github.com\/SlimPlanet\/SlimFaas\/blob\/main\/README.md"/);
    assert.match(result.contentHtml, /src="\/documentation-assets\/docs\/example.png"/);
});

test('Mermaid source remains readable and escaped before client rendering', async () => {
    const result = await renderMarkdownWithHighlight('<pre><code class="language-mermaid">flowchart LR\n A[&lt;img onerror=x&gt;] --&gt; B</code></pre>', 'docs/how-it-works.md');
    assert.match(result.contentHtml, /data-source=/);
    assert.equal(load(result.contentHtml)('img').length, 0);
    assert.equal((result.contentHtml.match(/class="doc-diagram"/g) ?? []).length, 1);
});

test('search matches all terms and prioritizes titles without interpreting regex syntax', () => {
    const entries = [{ title: 'Data', url: '/data', text: 'A queue callback finishes work.' }, { title: 'Queue callbacks', url: '/functions#callbacks', text: 'Complete deferred work.' }];
    assert.equal(searchDocumentation(entries, ' QUEUE callback ')[0].url, '/functions#callbacks');
    assert.equal(searchDocumentation(entries, 'missing').length, 0);
    assert.equal(searchDocumentation(entries, '[').length, 0);
    assert.equal(searchDocumentation(entries, ' ').length, 0);
});

test('navigation includes each public document once except the home page', () => {
    assert.equal(new Set(DOCUMENTATION_ORDER).size, DOCUMENTATION_ORDER.length);
    assert.deepEqual([...DOCUMENTATION_ORDER].sort(), Object.keys(DOCUMENTATION_CATALOG).filter(id => id !== 'home').sort());
    assert.ok(!Object.values(DOCUMENTATION_CATALOG).some(page => page.route === '/mcp'));
});

test('website footer includes the Linux Foundation trademark disclaimer', async () => {
    const footer = await readFile(path.resolve('src/components/Footer.tsx'), 'utf8');
    assert.match(footer, /Copyright .+ a Series of LF Projects, LLC/);
});

test('relative Markdown links and images resolve in the checked-out repository', async () => {
    for (const entry of Object.values(DOCUMENTATION_CATALOG)) {
        const source = path.resolve('../..', entry.sourcePath);
        const parsed = await remark().use(html).process(await readFile(source, 'utf8'));
        const $ = load(String(parsed));
        for (const element of $('a[href], img[src]').toArray()) {
            const value = $(element).attr(element.tagName === 'img' ? 'src' : 'href')!;
            if (/^(?:[a-z][a-z\d+.-]*:|\/\/|#|\/)/i.test(value)) continue;
            const target = path.resolve(path.dirname(source), decodeURIComponent(value.split(/[?#]/)[0]));
            await access(target).catch(() => assert.fail(`${entry.sourcePath}: missing ${value}`));
        }
    }
});

test('ZIP entries have predictable contents and offsets', () => {
    const content = Buffer.from('hello');
    const zip = createZip([{ name: 'collection/hello.txt', content }]);
    const nameLength = zip.readUInt16LE(26);
    assert.equal(zip.readUInt32LE(0), 0x04034b50);
    assert.equal(zip.subarray(30 + nameLength, 30 + nameLength + 5).toString(), 'hello');
    assert.equal(zip.readUInt32LE(14), 0x3610a686); // Known CRC32 of hello.
    const end = zip.length - 22;
    assert.equal(zip.readUInt32LE(end), 0x06054b50);
    assert.equal(zip.readUInt32LE(zip.readUInt32LE(end + 16)), 0x02014b50);
    assert.deepEqual(zip, createZip([{ name: 'collection/hello.txt', content }]));
});

async function filesUnder(directory: string): Promise<string[]> {
    const result: string[] = [];
    for (const entry of await readdir(directory, { withFileTypes: true })) {
        const file = path.join(directory, entry.name);
        if (entry.isDirectory()) result.push(...await filesUnder(file));
        else result.push(file);
    }
    return result;
}

test('registered public route patterns have an API entry and a Bruno example', async () => {
    const repository = path.resolve('../..');
    const reference = await readFile(path.join(repository, 'docs/api-reference.md'), 'utf8');
    const requests = await Promise.all((await filesUnder(path.join(repository, 'demo/bruno-slimfaas-demo'))).filter(file => file.endsWith('.bru')).map(file => readFile(file, 'utf8')));
    const urls = requests.flatMap(request => [...request.matchAll(/url: \{\{baseUrl\}\}([^\s]*)/g)].map(match => match[1].split('?')[0]));
    const routes = new Set<string>();
    for (const folder of ['Endpoints', 'Data', 'PromQL', 'WebSocket']) {
        for (const file of (await filesUnder(path.join(repository, 'src/SlimFaas', folder))).filter(file => file.endsWith('.cs'))) {
            const source = await readFile(file, 'utf8');
            const prefix = source.match(/MapGroup\("([^"]*)"\)/)?.[1] ?? '';
            for (const match of source.matchAll(/\b(app|group)\.Map(?:Get|Post|Delete|Put|Patch|Methods)?\(\s*"([^"]*)"/g)) {
                routes.add((match[1] === 'group' ? prefix : '') + match[2]);
            }
        }
    }
    assert.ok(routes.size > 30);
    for (const route of routes) {
        assert.ok(reference.includes('`' + route + '`'), `Missing API reference for ${route}`);
        const expression = new RegExp('^' + route.replace(/[.*+?^$()|[\]\\]/g, '\\$&').replace(/\{\\\*\\\*\w+\}/g, '.+').replace(/\{\w+\}/g, '[^/]+') + '$');
        assert.ok(urls.some(url => expression.test(url)), `Missing Bruno request for ${route}`);
    }
});
