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
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data: JobConfigurationStatus[] = await res.json();
      setJobs(data);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unknown error');
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
