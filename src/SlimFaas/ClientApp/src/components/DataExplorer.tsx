import { useEffect, useRef, useState } from 'react';
import { useDataStream, type DataPage } from '../hooks/useDataStream';
import { formatBytes, ttlLabel } from '../lib/live.ts';

export function DataInventory({ page, now, changed }: { page: DataPage; now: number; changed: Set<string> }) {
  const [copied, setCopied] = useState<string | null>(null);
  const [copyError, setCopyError] = useState(false);
  async function copy(id: string) {
    try { await navigator.clipboard.writeText(id); setCopied(id); setCopyError(false); }
    catch { setCopyError(true); }
  }
  return <>
    <div className="metrics">
      {[['Keys', page.Summary.Sets.toLocaleString()], ['Files', page.Summary.Files.toLocaleString()], ['File volume', formatBytes(page.Summary.FileBytes)], ['Expire in 60s', page.Summary.ExpiringSoon.toLocaleString()]].map(([label, value]) => <div className="metrics__card" key={label}><span className="metrics__label">{label}</span><strong className="metrics__value">{value}</strong></div>)}
    </div>
    {page.Summary.UnknownFileSizes > 0 && <p className="notice">Volume excludes {page.Summary.UnknownFileSizes} file(s) with unknown size.</p>}
    <div className="table-wrap"><table className="table"><thead className="table__head"><tr><th className="table__th">Key</th><th className="table__th">Time to live</th><th className="table__th">Expires at</th>{page.Kind === 'files' && <th className="table__th">Size</th>}<th className="table__th">Copy</th></tr></thead>
      <tbody>{page.Entries.map(entry => <tr key={entry.Id} className={`table__row ${changed.has(entry.Id) ? 'table__row--changed' : ''}`}>
        <td className="table__td table__td--mono">{entry.Id}</td>
        <td className="table__td"><span className={`badge ${entry.ExpiresAtMs !== null && entry.ExpiresAtMs - now <= 60_000 ? 'badge--warning' : 'badge--neutral'}`}>{ttlLabel(entry.ExpiresAtMs, now)}</span>{entry.ExpiresAtMs !== null && entry.ExpiresAtMs > now && entry.ExpiresAtMs - now <= 60_000 && <span className="data-explorer__expiry-label">Expires soon</span>}</td>
        <td className="table__td">{entry.ExpiresAtMs === null ? 'No expiration' : new Date(entry.ExpiresAtMs).toLocaleString()}</td>
        {page.Kind === 'files' && <td className="table__td"><strong>{formatBytes(entry.SizeBytes)}</strong>{entry.SizeBytes !== null && <span className="data-explorer__bytes">{entry.SizeBytes.toLocaleString()} bytes</span>}</td>}
        <td className="table__td"><button className="button button--quiet" type="button" aria-label={`Copy ${entry.Id}`} onClick={() => void copy(entry.Id)}>{copied === entry.Id ? 'Copied' : 'Copy key'}</button></td>
      </tr>)}</tbody></table></div>
    {copyError && <p className="notice" role="status">Clipboard unavailable. Select the key text to copy it.</p>}
    {page.Entries.length === 0 && <p className="empty-state">No matching {page.Kind === 'sets' ? 'keys' : 'files'} on this page.</p>}
  </>;
}

export default function DataExplorer() {
  const [kind, setKind] = useState('sets');
  const [search, setSearch] = useState('');
  const [prefix, setPrefix] = useState('');
  const [cursors, setCursors] = useState(['']);
  const [now, setNow] = useState(Date.now());
  const { page, status, receivedAt, retry } = useDataStream(kind, prefix, cursors[cursors.length - 1]);
  const previous = useRef<Map<string, string> | null>(null);
  const [highlights, setHighlights] = useState<Map<string, number>>(new Map());
  useEffect(() => { const timer = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(timer); }, []);
  useEffect(() => { const timer = setTimeout(() => { setPrefix(search); setCursors(['']); previous.current = null; setHighlights(new Map()); }, 250); return () => clearTimeout(timer); }, [search]);
  useEffect(() => {
    if (!page) return;
    const next = new Map(page.Entries.map(e => [e.Id, `${e.ExpiresAtMs}/${e.SizeBytes}`]));
    const old = previous.current;
    const timestamp = Date.now();
    setHighlights(highlights => {
      const active = new Map([...highlights].filter(([id, until]) => until > timestamp && next.has(id)));
      if (old) for (const [id, signature] of next) if (old.get(id) !== signature) active.set(id, timestamp + 2500);
      return active;
    });
    previous.current = next;
  }, [page]);
  const changed = new Set([...highlights].filter(([, until]) => until > now).map(([id]) => id));
  const stale = status !== 'Live' || !!receivedAt && now - receivedAt > Math.max(10_000, (page?.RefreshIntervalMs ?? 1000) * 3);
  const serverNow = page ? page.ServerTimeMs + Math.max(0, now - receivedAt) : now;
  const resetPage = () => { setCursors(['']); previous.current = null; setHighlights(new Map()); };
  return <section className="data-explorer" aria-label="Live data inventory">
    <div className="toolbar">
      <div className="segmented" aria-label="Data kind">{['sets', 'files'].map(value => <button className={`segmented__button ${kind === value ? 'segmented__button--active' : ''}`} type="button" key={value} aria-pressed={kind === value} onClick={() => { setKind(value); resetPage(); }}>{value === 'sets' ? 'Sets' : 'Files'}</button>)}</div>
      <label className="field field--grow">Key prefix<input className="field__input" type="search" placeholder="Filter keys by prefix…" value={search} onChange={e => setSearch(e.target.value)} /></label>
      <span className={`badge ${stale ? 'badge--warning' : 'badge--success'}`}>{stale ? page ? 'Stale' : status.startsWith('Metadata access') ? 'Restricted' : 'Connecting' : 'Live'}</span>
    </div>
    <p className="data-explorer__description">A live inventory of keys, expiry and file sizes. Values and documents stay in your applications.</p>
    {status !== 'Live' && <div className="notice" role="status">{status}. {page && 'Showing the last received inventory.'} <button className="table__link" type="button" onClick={retry}>Retry</button></div>}
    {page && <>
      <DataInventory page={page} now={serverNow} changed={changed} />
      <nav className="pagination" aria-label="Inventory pages"><span className="pagination__count">{page.TotalCount.toLocaleString()} matching items · Page {cursors.length}</span>
        <button className="button button--quiet" type="button" disabled={cursors.length === 1} onClick={() => { setCursors(cursors.slice(0, -1)); previous.current = null; }}>Previous</button>
        <button className="button button--quiet" type="button" disabled={!page.NextCursor || stale} onClick={() => { setCursors([...cursors, page.NextCursor!]); previous.current = null; }}>Next</button>
        <button className="button button--quiet" type="button" onClick={resetPage}>First page</button>
      </nav>
      <p className="data-explorer__updated">Last received {receivedAt ? new Date(receivedAt).toLocaleTimeString() : '—'} · Expiration times use the server clock.</p>
    </>}
  </section>;
}
