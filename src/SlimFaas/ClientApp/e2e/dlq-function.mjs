import { createServer } from 'node:http';
import { createHash } from 'node:crypto';

let fixed = false;
let last = null;
const server = createServer(async (request, response) => {
  if (request.url === '/health') { response.end('ready'); return; }
  if (request.url === '/fix') { fixed = true; response.end('fixed'); return; }
  if (request.url === '/last') {
    response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify(last)); return;
  }
  const hash = createHash('sha256'); let length = 0;
  for await (const chunk of request) { hash.update(chunk); length += chunk.length; }
  last = { method: request.method, url: request.url, length, sha256: hash.digest('hex'),
    elementId: request.headers['slimfaas-element-id'], attempt: request.headers['slimfaas-try-number'] };
  response.writeHead(fixed ? 200 : 400); response.end(fixed ? 'processed' : 'fix application first');
});
const url = new URL(process.env.ASPNETCORE_URLS);
server.listen(Number(url.port), url.hostname);
