// Hold a real function process until the browser has observed the cold-start wait.
import { spawn } from 'node:child_process';
const [gate, executable, ...args] = process.argv.slice(2);
const response = await fetch(gate, { signal: AbortSignal.timeout(120_000) });
if (!response.ok) throw new Error(`Startup gate returned ${response.status}`);
const child = spawn(executable, args, { stdio: 'inherit' });
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => child.kill(signal));
child.on('error', error => { console.error(error); process.exitCode = 1; });
child.on('exit', code => { process.exitCode = code ?? 1; });
