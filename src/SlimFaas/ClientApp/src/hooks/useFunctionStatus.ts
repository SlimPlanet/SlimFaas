import { useEffect, useRef, useState, useCallback } from 'react';
import type { FunctionStatusDetailed } from '../types';

const POLL_INTERVAL = 5000;
const COOLDOWN_MS = 3000;

export function useFunctionStatus() {
  const [functions, setFunctions] = useState<FunctionStatusDetailed[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // Ensemble des noms de fonctions dont le bouton wake est en cooldown
  const [coolingDown, setCoolingDown] = useState<Set<string>>(new Set());
  // Cooldown du bouton "Wake All"
  const [wakeAllCooling, setWakeAllCooling] = useState(false);

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

  const startCooldown = useCallback((name: string) => {
    setCoolingDown((prev) => new Set(prev).add(name));
    setTimeout(() => {
      setCoolingDown((prev) => {
        const next = new Set(prev);
        next.delete(name);
        return next;
      });
    }, COOLDOWN_MS);
  }, []);

  const wakeUp = useCallback(async (functionName: string) => {
    if (coolingDown.has(functionName)) return;
    startCooldown(functionName);
    try {
      await fetch(`/wake-function/${functionName}`, { method: 'POST' });
      await fetchStatus();
    } catch {
      // ignore
    }
  }, [coolingDown, startCooldown, fetchStatus]);

  const wakeUpAll = useCallback(async () => {
    if (wakeAllCooling) return;
    setWakeAllCooling(true);
    setTimeout(() => setWakeAllCooling(false), COOLDOWN_MS);
    try {
      await fetch('/wake-functions', { method: 'POST' });
      await fetchStatus();
    } catch {
      // ignore
    }
  }, [wakeAllCooling, fetchStatus]);

  return { functions, loading, error, wakeUp, wakeUpAll, coolingDown, wakeAllCooling };
}
