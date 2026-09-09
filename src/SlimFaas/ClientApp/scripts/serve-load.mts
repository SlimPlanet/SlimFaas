// Node 24: npm run build && node scripts/serve-load.mts
// Synthetic traffic only. Serves the production dashboard on loopback.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, extname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { makeFixtures, fixtureEvents } from '../src/lib/fixtures.ts';

const root = fileURLToPath(new URL('../../wwwroot/', import.meta.url));
const port = Number(process.env.DASHBOARD_LOAD_PORT ?? 6011);
const server = createServer(async (request, response) => {
  const url = new URL(request.url ?? '/', 'http://localhost');
  if (url.pathname === '/status-functions-stream') {
    response.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store' });
    let sequence = 0;
    const state = () => {
      const f = makeFixtures(10000, 10000);
      response.write(`event: state\ndata: ${JSON.stringify({ Functions: f.functions, Jobs: f.jobs,
        Queues: f.queues, SlimFaasNodes: f.slimFaasNodes, SlimFaasReplicas: 3, FrontEnabled: true })}\n\n`);
    };
    state();
    const timer = setInterval(() => {
      // Close a stalled consumer instead of buffering unbounded synthetic traffic.
      if (response.writableLength > 4 * 1024 * 1024) { response.destroy(); return; }
      response.write(`event: activity_batch\ndata: ${JSON.stringify(fixtureEvents(sequence, 100))}\n\n`);
      sequence += 100;
      if (sequence % 1000 === 0) state();
    }, 100);
    response.on('close', () => clearInterval(timer));
    return;
  }
  const path = resolve(root, `.${url.pathname === '/' ? '/index.html' : url.pathname}`);
  if (!path.startsWith(root)) { response.writeHead(403).end(); return; }
  try {
    const content = await readFile(path);
    response.writeHead(200, { 'Content-Type': ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml' } as Record<string, string>)[extname(path)] ?? 'application/octet-stream' });
    response.end(content);
  } catch { response.writeHead(404).end(); }
});
server.listen(port, '127.0.0.1', () => console.log(`Production load fixture: http://127.0.0.1:${port}/#/live/traffic`));
