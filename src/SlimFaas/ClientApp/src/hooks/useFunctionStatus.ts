import { useEffect, useRef, useState, useCallback } from 'react';
import type { FunctionStatusDetailed } from '../types';

const POLL_INTERVAL = 5000;

export function useFunctionStatus() {
  const [functions, setFunctions] = useState<FunctionStatusDetailed[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const isFetching = useRef(false);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const fetchStatus = useCallback(async () => {
    if (isFetching.current) return;
    isFetching.current = true;
    try {
      const res = await fetch('/status-functions?level=detailed');
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data: FunctionStatusDetailed[] = await res.json();
      setFunctions(data);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error');
    } finally {
      isFetching.current = false;
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchStatus();
    const tick = () => {
      timerRef.current = setTimeout(async () => {
        await fetchStatus();
        tick();
      }, POLL_INTERVAL);
    };
    tick();
    return () => {
      if (timerRef.current) clearTimeout(timerRef.current);
    };
  }, [fetchStatus]);

  const wakeUp = useCallback(async (functionName: string) => {
    try {
      await fetch(`/wake-function/${functionName}`, { method: 'POST' });
      await fetchStatus();
    } catch {
      // ignore
    }
  }, [fetchStatus]);

  const wakeUpAll = useCallback(async () => {
    const sleeping = functions.filter((f) => f.NumberReady === 0);
    await Promise.all(sleeping.map((f) => fetch(`/wake-function/${f.Name}`, { method: 'POST' })));
    await fetchStatus();
  }, [functions, fetchStatus]);

  return { functions, loading, error, wakeUp, wakeUpAll };
}
