export default function Icon({ name }: { name: 'grid' | 'activity' | 'arrow' | 'bolt' | 'close' | 'search' | 'data' | 'scaling' | 'github' }) {
  const paths = {
    grid: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
    activity: 'M2 12h5l3-8 4 16 3-8h5', arrow: 'M4 12h16 M14 6l6 6-6 6',
    bolt: 'M13 2L4 14h7l-1 8 10-13h-7z', close: 'M6 6l12 12 M18 6L6 18',
    search: 'M21 21l-6-6 M17 10a7 7 0 1 1-14 0 7 7 0 0 1 14 0',
    github: 'M9 19c-4 1-4-2-6-2 M15 22v-3.4c0-1-.3-1.8-.9-2.3 3-.3 6.2-1.5 6.2-6.6 0-1.4-.5-2.6-1.3-3.5.2-.8.2-2-.2-3.2 0 0-1.1-.3-3.6 1.3a13 13 0 0 0-6.4 0C6.3 2.7 5.2 3 5.2 3c-.4 1.2-.4 2.4-.2 3.2-.8.9-1.3 2.1-1.3 3.5 0 5.1 3.2 6.3 6.2 6.6-.6.5-.9 1.3-.9 2.3V22',
    scaling: 'M4 20h16 M5 16h4v4 M10 11h4v9 M15 5h4v15',
    data: 'M4 5c0-4 16-4 16 0s-16 4-16 0v14c0 4 16 4 16 0V5 M4 12c0 4 16 4 16 0',
  };
  return <svg className="icon" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[name]} /></svg>;
}
