// Test-only managed workload: every Data request uses the supervisor's signed gateway.
import { createServer } from 'node:http';
const base = process.env.SlimFaas__BaseUrl;
async function call({ path, method = 'GET', body }) {
  const response = await fetch(new URL(path, base), { method, body, signal: AbortSignal.timeout(20_000) });
  await response.arrayBuffer();
  return response.status;
}
if (process.argv.includes('--job')) {
  await fetch(process.env.DATA_TEST_GATE).then(response => response.text());
  for (const kind of ['sets', 'files']) {
    const path = kind === 'sets' ? '/data/sets/job-kept' : '/data/files?id=job-kept';
    if (await call({ path, method: 'POST', body: 'job-data' }) !== 200) throw new Error('Job Data write failed');
  }
} else {
  const address = new URL(process.env.ASPNETCORE_URLS);
  createServer(async (request, response) => {
    try {
      if (request.url === '/health') { response.end('ready'); return; }
      let input = '';
      for await (const chunk of request) input += chunk;
      const status = await call(JSON.parse(input));
      response.writeHead(200, { 'Content-Type': 'application/json' }); response.end(JSON.stringify({ status }));
    } catch (error) { response.writeHead(500); response.end(String(error)); }
  }).listen(Number(address.port), address.hostname);
}
