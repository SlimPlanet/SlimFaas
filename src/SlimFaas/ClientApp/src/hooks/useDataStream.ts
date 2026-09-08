import { useEffect, useState } from 'react';
import { readSse } from '../lib/live.ts';

export interface DataEntry { Id: string; ExpiresAtMs: number | null; SizeBytes: number | null }
export interface DataPage {
  RefreshIntervalMs?: number;
  Kind: string; ServerTimeMs: number; Entries: DataEntry[]; NextCursor: string | null; TotalCount: number;
  Summary: { Sets: number; Files: number; FileBytes: number; UnknownFileSizes: number; ExpiringSoon: number };
}
export function useDataStream(kind: string, prefix: string, after: string) {
  const [page, setPage] = useState<DataPage | null>(null);
  const [status, setStatus] = useState('Connecting');
  const [receivedAt, setReceivedAt] = useState(0);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let failures = 0;
    setPage(null); setStatus('Connecting'); setReceivedAt(0);
    const connect = async () => {
      try {
        const query = new URLSearchParams({ kind, prefix, after, limit: '100' });
        const response = await fetch(`/status-data-stream?${query}`, { signal: controller.signal, headers: { Accept: 'text/event-stream' } });
        if ([400, 401, 403, 404].includes(response.status)) {
          setStatus(response.status === 400 ? 'Invalid inventory query' : 'Metadata access is restricted or the front is disabled');
          return;
        }
        if (!response.ok || !response.body) throw new Error(response.status === 429 ? 'Too many connected viewers' : 'Inventory unavailable');
        await readSse(response.body, (event, data) => {
          if (event !== 'data_state') return;
          const value: DataPage = JSON.parse(data);
          if (!Array.isArray(value.Entries) || !Number.isFinite(value.ServerTimeMs)) throw new Error('Invalid inventory frame');
          if (controller.signal.aborted) return;
          setPage(value); setReceivedAt(Date.now()); setStatus('Live'); failures = 0;
        });
        throw new Error('Disconnected');
      } catch (error) {
        if (controller.signal.aborted) return;
        failures++;
        setStatus(`${error instanceof Error ? error.message : 'Disconnected'}${failures < 6 ? ' — reconnecting' : ' — retry to reconnect'}`);
        if (failures < 6) timer = setTimeout(connect, Math.min(30_000, 1000 * 2 ** failures));
      }
    };
    void connect();
    return () => { controller.abort(); if (timer) clearTimeout(timer); };
  }, [kind, prefix, after, attempt]);
  return { page, status, receivedAt, retry: () => setAttempt(n => n + 1) };
}
