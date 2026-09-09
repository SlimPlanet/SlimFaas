import type { NetworkActivityEvent, FunctionStatusDetailed } from '../types.ts';

export const PAGE_SIZE = 100;
export const EVENT_LIMIT = 5000;

export function paginate<T>(items: readonly T[], page: number, size = PAGE_SIZE) {
  const pages = Math.max(1, Math.ceil(items.length / size));
  const current = Math.max(0, Math.min(page, pages - 1));
  return { items: items.slice(current * size, (current + 1) * size), page: current, pages };
}

/** Bounded live history; deduplication is also bounded and never retains payload bodies. */
export function appendActivity(previous: NetworkActivityEvent[], incoming: NetworkActivityEvent[]) {
  const ids = new Set(previous.map(e => e.Id));
  const fresh = incoming.filter(e => {
    if (ids.has(e.Id)) return false;
    ids.add(e.Id);
    return true;
  }).map(event => event.ReceivedAt === undefined ? { ...event, ReceivedAt: performance.now() } : event);
  return [...previous, ...fresh].slice(-EVENT_LIMIT);
}

export function formatBytes(value: number | null): string {
  if (value === null) return 'Unknown';
  if (value < 1024) return `${value} B`;
  const unit = Math.min(4, Math.floor(Math.log(value) / Math.log(1024)));
  return `${(value / 1024 ** unit).toFixed(1)} ${['B', 'KiB', 'MiB', 'GiB', 'TiB'][unit]}`;
}

export function ttlLabel(expiry: number | null, now: number): string {
  if (expiry === null) return 'Persistent';
  const seconds = Math.max(0, Math.ceil((expiry - now) / 1000));
  if (seconds === 0) return 'Expired';
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ${Math.floor(seconds % 3600 / 60)}m`;
  return `${Math.floor(seconds / 86400)}d ${Math.floor(seconds % 86400 / 3600)}h`;
}

/** Parses split UTF-8/chunk boundaries, CRLF, comments and multiline SSE data. */
export async function readSse(body: ReadableStream<Uint8Array>, onEvent: (event: string, data: string) => void) {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let pending = '', event = 'message', data: string[] = [], frameLength = 0;
  try {
    while (true) {
      const chunk = await reader.read();
      if (chunk.done) break;
      pending += decoder.decode(chunk.value, { stream: true });
      if (pending.length > 2_000_000) throw new Error('Stream frame is too large.');
      let end: number;
      while ((end = pending.indexOf('\n')) >= 0) {
        const line = pending.slice(0, end).replace(/\r$/, '');
        pending = pending.slice(end + 1);
        if (line === '') {
          if (data.length) onEvent(event, data.join('\n'));
          event = 'message'; data = []; frameLength = 0;
        } else if (line.startsWith('event:')) event = line.slice(6).trimStart();
        else if (line.startsWith('data:')) {
          frameLength += line.length;
          if (frameLength > 2_000_000) throw new Error('Stream frame is too large.');
          data.push(line.slice(5).replace(/^ /, ''));
        }
      }
    }
  } finally {
    await reader.cancel().catch(() => { /* Preserve the original parser or network error. */ });
    reader.releaseLock();
  }
}

export function functionState(fn: FunctionStatusDetailed) {
  if (fn.NumberReady === 0 && fn.Pods?.some(p => ['Failed', 'Error', 'CrashLoopBackOff', 'ImagePullBackOff'].includes(p.Status))) return 'Error';
  if (fn.NumberReady === 0) return fn.NumberRequested > 0 ? 'Starting' : 'Sleeping';
  return fn.NumberReady < fn.NumberRequested ? 'Scaling' : 'Ready';
}
