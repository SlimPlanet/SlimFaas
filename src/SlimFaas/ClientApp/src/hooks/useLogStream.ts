import { useEffect, useState } from 'react';
import { readSse } from '../lib/live.ts';
import { LogBuffer, type LogLine, type LogSource, type LogState, type LogTarget } from '../lib/logs.ts';

export function useLogStream(target: LogTarget) {
  const [sources, setSources] = useState<LogSource[]>([]);
  const [source, setSource] = useState('');
  const [status, setStatus] = useState('Connecting');
  const [lines, setLines] = useState<LogLine[]>([]);
  const [state, setState] = useState<LogState | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [discarded, setDiscarded] = useState(0);
  const targetKey = JSON.stringify(target);
  useEffect(() => {
    const controller = new AbortController();
    setStatus('Connecting'); setSources([]); setSource(''); setLines([]); setState(null);
    void (async () => {
      try {
        const query = new URLSearchParams(JSON.parse(targetKey));
        const response = await fetch(`/status-log-sources?${query}`, { signal: controller.signal });
        if (response.status === 403) { const error = await response.json().catch(() => null); if (!controller.signal.aborted) setStatus(error?.Status === 'Disabled' ? 'Disabled' : 'Access denied'); return; }
        if (!response.ok) throw new Error('Log sources unavailable');
        const result: { Status: string; Sources: LogSource[] } = await response.json();
        if (controller.signal.aborted) return;
        setSources(result.Sources); setSource(result.Sources[0]?.Id ?? '');
        setStatus(result.Sources.length ? 'Connecting' : 'Logs unavailable');
      } catch (error) {
        if (!controller.signal.aborted) setStatus(error instanceof Error ? error.message : 'Disconnected');
      }
    })();
    return () => controller.abort();
  }, [targetKey, attempt]);

  useEffect(() => {
    if (!source) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let failures = 0, session = '';
    let buffer = new LogBuffer();
    setLines([]); setState(null); setDiscarded(0);
    const connect = async () => {
      let retryAfter = 0;
      let terminal = false;
      try {
        setStatus('Connecting');
        const query = new URLSearchParams({ source, tail: '10000' });
        const response = await fetch(`/status-logs-stream?${query}`, { signal: controller.signal, headers: { Accept: 'text/event-stream' } });
        if ([400, 401, 403, 404].includes(response.status)) {
          const error = response.status === 403 ? await response.json().catch(() => null) : null;
          if (!controller.signal.aborted) setStatus(response.status === 404 ? 'Source removed or restarted' : response.status === 400 ? 'Invalid log source' : error?.Status === 'Disabled' ? 'Disabled' : 'Access denied'); return;
        }
        retryAfter = Math.min(30000, Math.max(0, Number(response.headers.get('Retry-After')) * 1000 || 0));
        if (!response.ok || !response.body) throw new Error(response.status === 429 ? 'Too many log viewers' : 'Logs unavailable');
        await readSse(response.body, (event, data) => {
          if (controller.signal.aborted) return;
          if (event === 'log_state') {
            const next: LogState = JSON.parse(data);
            if (session !== next.Session) { buffer = new LogBuffer(); setLines([]); setDiscarded(0); session = next.Session; }
            setState(next); setStatus(next.Status);
            terminal = ['Ended', 'Source removed', 'Access denied'].includes(next.Status);
          } else if (event === 'log_batch') {
            const batch: { Lines: LogLine[] } = JSON.parse(data);
            buffer.append(batch.Lines); setLines(buffer.lines); setDiscarded(buffer.discarded);
          }
        });
        if (!terminal) throw new Error('Disconnected');
      } catch (error) {
        if (controller.signal.aborted) return;
        const message = error instanceof Error ? error.message : 'Disconnected';
        setStatus(`${message} · ${failures < 3 ? 'reconnecting' : 'retry to reconnect'}`);
        if (failures < 3) timer = setTimeout(connect, Math.max(retryAfter, 1000 * 2 ** failures++));
      }
    };
    void connect();
    return () => { controller.abort(); if (timer) clearTimeout(timer); };
  }, [source]);
  return { sources, source, setSource, lines, status, state, discarded, retry: () => setAttempt(n => n + 1) };
}
