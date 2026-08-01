import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { DOCUMENTATION_CATALOG } from '../src/lib/documentation-catalog.ts';

const SITE_URL = 'https://slimfaas.dev';

const routes = [...new Set(Object.values(DOCUMENTATION_CATALOG).map((entry) => entry.route))];

const xml = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${routes
    .map((route) => `  <url><loc>${new URL(route, `${SITE_URL}/`).toString()}</loc></url>`)
    .join('\n')}
</urlset>
`;

const currentDirectory = path.dirname(fileURLToPath(import.meta.url));
const outputPath = path.join(currentDirectory, '..', 'out', 'sitemap.xml');

await mkdir(path.dirname(outputPath), { recursive: true });
await writeFile(outputPath, xml, 'utf8');

console.log(`Generated sitemap for ${routes.length} routes at ${outputPath}`);
