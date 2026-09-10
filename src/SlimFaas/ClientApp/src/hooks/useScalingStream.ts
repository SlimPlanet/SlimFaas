import { useEffect, useState } from 'react';
import { readSse } from '../lib/live.ts';
import { mergeScalingState, type ScalingState } from '../lib/scaling.ts';

export function useScalingStream(functionName: string) {
  const [state, setState] = useState<ScalingState | null>(null);
  const [status, setStatus] = useState('Connecting');
  const [sessionChanged, setSessionChanged] = useState(false);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let failures = 0, session: string | null = null;
    setState(null); setStatus(functionName ? 'Connecting' : 'Select a function'); setSessionChanged(false);
    if (!functionName) return () => controller.abort();
    const connect = async () => {
      try {
        const response = await fetch(`/status-scaling-stream?${new URLSearchParams({ function: functionName })}`,
          { signal: controller.signal, headers: { Accept: 'text/event-stream' } });
        if ([400, 401, 403, 404].includes(response.status)) { setStatus('Scaling diagnostics are unavailable for this function or dashboard.'); return; }
        if (!response.ok || !response.body) throw new Error(response.status === 429 ? 'Too many connected viewers' : 'Scaling leader unavailable');
        await readSse(response.body, (event, data) => {
          if (controller.signal.aborted) return;
          if (event === 'scaling_error') throw new Error(JSON.parse(data).Error || 'Scaling leader changed');
          if (event !== 'scaling_state') return;
          const next: ScalingState = JSON.parse(data);
          if (next.Function !== functionName) throw new Error('Unexpected scaling function.');
          if (session && session !== next.Session) setSessionChanged(true);
          session = next.Session;
          // Validate outside the React updater so malformed streams enter the retry path.
          mergeScalingState(null, next);
          setState(previous => mergeScalingState(previous, next)); setStatus(next.Status); failures = 0;
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
  }, [functionName, attempt]);
  return { state, status, sessionChanged, retry: () => setAttempt(value => value + 1) };
}
