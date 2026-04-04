import { useEffect, useRef, useState, useCallback } from 'react';
import type { JobConfigurationStatus } from '../types';

const POLL_INTERVAL = 5000;

export function useJobStatus() {
  const [jobs, setJobs] = useState<JobConfigurationStatus[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const isFetching = useRef(false);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const fetchJobs = useCallback(async () => {
    if (isFetching.current) return;
    isFetching.current = true;
    try {
      const res = await fetch('/jobs/status');
      if (!res.ok) {
        setError(`HTTP ${res.status}`);
        return;
      }
      let data: JobConfigurationStatus[];
      try {
        const json = await res.json();
        data = Array.isArray(json) ? json : [];
      } catch {
        setError('Invalid JSON response');
        return;
      }
      setJobs(data);
      setError(null);
    } catch (e) {
      // Erreur réseau ou autre : on affiche un message mais on ne crash pas
      setError(e instanceof Error ? e.message : 'Unavailable');
    } finally {
      isFetching.current = false;
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchJobs();
    const tick = () => {
      timerRef.current = setTimeout(async () => {
        await fetchJobs();
        tick();
      }, POLL_INTERVAL);
    };
    tick();
    return () => {
      if (timerRef.current) clearTimeout(timerRef.current);
    };
  }, [fetchJobs]);

  return { jobs, loading, error };
}
