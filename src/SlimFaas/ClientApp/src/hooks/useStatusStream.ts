import { useEffect, useRef, useState, useCallback } from 'react';
import type { FunctionStatusDetailed, QueueInfo, NetworkActivityEvent, StatusStreamPayload } from '../types';

const COOLDOWN_MS = 3000;

export function useStatusStream() {
  const [functions, setFunctions] = useState<FunctionStatusDetailed[]>([]);
  const [queues, setQueues] = useState<QueueInfo[]>([]);
  const [activity, setActivity] = useState<NetworkActivityEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [coolingDown, setCoolingDown] = useState<Set<string>>(new Set());
  const [wakeAllCooling, setWakeAllCooling] = useState(false);
  const [functionsWithQueueActivity, setFunctionsWithQueueActivity] = useState<Set<string>>(new Set());

  const eventSourceRef = useRef<EventSource | null>(null);
  const reconnectTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const connect = useCallback(() => {
    if (eventSourceRef.current) {
      eventSourceRef.current.close();
    }

    const es = new EventSource('/status-functions-stream');
    eventSourceRef.current = es;

    es.addEventListener('state', (e: MessageEvent) => {
      try {
        const payload: StatusStreamPayload = JSON.parse(e.data);
        setFunctions(payload.Functions ?? []);
        setQueues(payload.Queues ?? []);
        setActivity(payload.RecentActivity ?? []);
        setError(null);
        setLoading(false);

        // Detect functions that have queue activity from initial recent activity or non-zero queue length
        const queueFns = new Set<string>();
        for (const evt of (payload.RecentActivity ?? [])) {
          if (evt.Type === 'enqueue' || evt.Type === 'dequeue') {
            queueFns.add(evt.Target);
          }
        }
        for (const q of (payload.Queues ?? [])) {
          if (q.Length > 0) {
            queueFns.add(q.Name);
          }
        }
        if (queueFns.size > 0) {
          setFunctionsWithQueueActivity(prev => {
            const next = new Set(prev);
            queueFns.forEach(name => next.add(name));
            return next.size !== prev.size ? next : prev;
          });
        }
      } catch {
        // ignore parse errors
      }
    });

    es.addEventListener('activity', (e: MessageEvent) => {
      try {
        const evt: NetworkActivityEvent = JSON.parse(e.data);
        setActivity(prev => {
          const next = [...prev, evt];
          // Keep only the last 200 events
          return next.length > 200 ? next.slice(-200) : next;
        });
        // Track queue activity
        if (evt.Type === 'enqueue' || evt.Type === 'dequeue') {
          setFunctionsWithQueueActivity(prev => {
            if (prev.has(evt.Target)) return prev;
            const next = new Set(prev);
            next.add(evt.Target);
            return next;
          });
        }
      } catch {
        // ignore
      }
    });

    es.onerror = () => {
      setError('Stream disconnected, reconnecting...');
      es.close();
      eventSourceRef.current = null;
      // Reconnect after a short delay
      reconnectTimer.current = setTimeout(() => connect(), 3000);
    };
  }, []);

  useEffect(() => {
    connect();
    return () => {
      if (eventSourceRef.current) eventSourceRef.current.close();
      if (reconnectTimer.current) clearTimeout(reconnectTimer.current);
    };
  }, [connect]);

  const startCooldown = useCallback((name: string) => {
    setCoolingDown(prev => new Set(prev).add(name));
    setTimeout(() => {
      setCoolingDown(prev => {
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
    } catch {
      // ignore
    }
  }, [coolingDown, startCooldown]);

  const wakeUpAll = useCallback(async () => {
    if (wakeAllCooling) return;
    setWakeAllCooling(true);
    setTimeout(() => setWakeAllCooling(false), COOLDOWN_MS);
    try {
      await fetch('/wake-functions', { method: 'POST' });
    } catch {
      // ignore
    }
  }, [wakeAllCooling]);

  return {
    functions,
    queues,
    activity,
    loading,
    error,
    wakeUp,
    wakeUpAll,
    coolingDown,
    wakeAllCooling,
    functionsWithQueueActivity,
  };
}





