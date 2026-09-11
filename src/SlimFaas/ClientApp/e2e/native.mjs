import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { createWriteStream } from 'node:fs';
import { access, mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { once } from 'node:events';

const root = fileURLToPath(new URL('../../../../', import.meta.url));
export async function startNative(output) {
  const runtime = process.env.SLIMFAAS_E2E_RUNTIME ?? resolve(root, 'artifacts/traffic-e2e/runtime/SlimFaas');
  const fibonacci = resolve(root, 'artifacts/traffic-e2e/fibonacci/Fibonacci.dll');
  await Promise.all([access(runtime), access(fibonacci)]);
  const directory = await mkdtemp(resolve(tmpdir(), 'slimfaas-traffic-'));
  const gates = new Set();
  let released = false;
  const gate = createServer((_, response) => {
    if (released) response.end('ready');
    else { gates.add(response); response.on('close', () => gates.delete(response)); }
  });
  gate.listen(0, '127.0.0.1'); await once(gate, 'listening');
  const base = Number(process.env.SLIMFAAS_E2E_PORT_BASE ?? 38020);
  const command = ['dotnet', fibonacci];
  const workload = (replicas, gated) => ({
    command: gated ? [process.execPath, fileURLToPath(new URL('./gated-function.mjs', import.meta.url)), `http://127.0.0.1:${gate.address().port}/gate`, ...command] : command,
    workingDirectory: root,
    environment: { ASPNETCORE_URLS: 'http://127.0.0.1:{port}', Logging__LogLevel__Default: 'Warning' },
    annotations: {
      'SlimFaas/Function': 'true', 'SlimFaas/ReplicasMin': String(replicas),
      'SlimFaas/ReplicasAtStart': String(replicas || 1), 'SlimFaas/TimeoutSecondBeforeSetReplicasMin': '120',
      ...(gated ? {} : { 'SlimFaas/SubscribeEvents': 'Public:fibo-public' }),
    },
    health: { path: '/health', periodSeconds: 1, timeoutSeconds: 2, startupTimeoutSeconds: 120 },
  });
  // Emit block YAML; template placeholders remain quoted scalar values.
  const yaml = (value, depth = 0) => Object.entries(value).map(([key, item]) => {
    const prefix = '  '.repeat(depth) + JSON.stringify(key) + ':';
    if (Array.isArray(item)) return prefix + '\n' + item.map(entry => '  '.repeat(depth + 1) + '- ' + JSON.stringify(entry)).join('\n');
    if (item && typeof item === 'object') return prefix + '\n' + yaml(item, depth + 1);
    return prefix + ' ' + JSON.stringify(item);
  }).join('\n');
  const manifest = resolve(directory, 'traffic.yaml');
  await writeFile(manifest, yaml({ schemaVersion: 1, name: 'traffic-e2e',
    cluster: { nodes: 3, entrypointPort: base, nodeHttpPortBase: base + 1, raftPortBase: base + 100, nodeLogLevel: 'Warning' },
    processPorts: { from: base + 200, to: base + 299 }, state: { mode: 'ephemeral', directory: resolve(directory, 'state') },
    functions: { fibonacci1: workload(0, true), fibonacci4: workload(2, false) },
  }));
  const log = createWriteStream(output);
  const processHandle = spawn(runtime, ['local', 'up', '-f', manifest], {
    cwd: root, env: { ...process.env, SlimFaas__EnableFront: 'true' }, detached: true, stdio: ['ignore', 'pipe', 'pipe'],
  });
  processHandle.stdout.pipe(log, { end: false }); processHandle.stderr.pipe(log, { end: false });
  let startupError;
  processHandle.on('error', error => { startupError = error; });
  return {
    url: `http://127.0.0.1:${base + 1}`, peerUrl: `http://127.0.0.1:${base + 2}`,
    check() { if (startupError) throw startupError; if (processHandle.exitCode !== null) throw new Error(`Native runtime exited: ${processHandle.exitCode}. See ${output}`); },
    release() { released = true; for (const response of gates) response.end('ready'); },
    async close() {
      for (const response of gates) response.destroy();
      gate.closeAllConnections(); await new Promise(done => gate.close(done));
      if (processHandle.exitCode === null && processHandle.pid) {
        const stopped = once(processHandle, 'exit');
        processHandle.kill('SIGTERM');
        const deadline = setTimeout(() => { try { process.kill(-processHandle.pid, 'SIGKILL'); } catch { /* Already stopped. */ } }, 15_000);
        try { await stopped; } finally { clearTimeout(deadline); }
      }
      await new Promise(done => log.end(done));
      await rm(directory, { recursive: true, force: true });
    },
  };
}
