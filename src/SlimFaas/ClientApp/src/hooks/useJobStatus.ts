import { useStatusStream } from './useStatusStream';

/**
 * @deprecated Prefer useStatusStream() which is the single realtime source for jobs and functions.
 */
export function useJobStatus() {
  const { jobs, loading, error } = useStatusStream();
  return { jobs, loading, error };
}
