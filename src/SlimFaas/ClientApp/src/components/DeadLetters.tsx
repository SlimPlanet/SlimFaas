import { useEffect, useRef, useState } from 'react';
import { deadLetterLink, deadLetterPath, deadLetterRequest, isDeadLetterMessage, isDeadLetterSummary, isPage, isReplay,
  readDeadLetterPreview, PREVIEW_BYTES, type DeadLetterMessage, type DeadLetterPage, type DeadLetterReplay, type DeadLetterSummary } from '../lib/deadLetters';

interface Props { route: string; revision: number; stale: boolean }
const reasons = ['RetriesExhausted', 'NonRetryableResponse', 'DeliveryTimeout', 'CallbackError', 'CallbackTimeout', 'TransportError'];
export default function DeadLetters({ route, revision, stale }: Props) {
  const parts = route.split('?')[0].split('/');
  let name: string | undefined, id: string | undefined, invalid = false;
  try { name = parts[2] ? decodeURIComponent(parts[2]) : undefined; id = parts[4] ? decodeURIComponent(parts[4]) : undefined; }
  catch { invalid = true; }
  const [catalog, setCatalog] = useState<DeadLetterPage<DeadLetterSummary> | null>(null);
  const [messages, setMessages] = useState<DeadLetterPage<DeadLetterMessage> | null>(null);
  const [summary, setSummary] = useState<DeadLetterSummary | null>(null);
  const [detail, setDetail] = useState<DeadLetterMessage | null>(null);
  const [prefix, setPrefix] = useState(''), [reason, setReason] = useState(''), [status, setStatus] = useState('');
  const [cursors, setCursors] = useState<(string | null)[]>([null]);
  const [refresh, setRefresh] = useState(0), [loading, setLoading] = useState(false), [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false), [notice, setNotice] = useState('');
  const [preview, setPreview] = useState<{ text: string; truncated: boolean } | null>(null);
  const action = useRef<AbortController | null>(null);
  useEffect(() => { setCursors([null]); setPreview(null); setDetail(null); setSummary(null); setNotice(''); action.current?.abort(); setPending(false); }, [name, id, reason, status, prefix]);
  useEffect(() => () => action.current?.abort(), []);
  const after = cursors[cursors.length - 1];
  useEffect(() => {
    const controller = new AbortController();
    const timer = setTimeout(async () => {
      setLoading(true); setError(null);
      try {
        const query = new URLSearchParams({ limit: '100' });
        if (after) query.set('after', after);
        if (!name) {
          if (prefix) query.set('prefix', prefix);
          setCatalog(await deadLetterRequest(`${deadLetterPath()}?${query}`, isPage(isDeadLetterSummary), controller.signal));
        } else {
          if (reason) query.set('reason', reason);
          if (status) query.set('httpStatus', status);
          const [current, rows] = await Promise.all([
            deadLetterRequest(deadLetterPath(name), isDeadLetterSummary, controller.signal),
            deadLetterRequest(`${deadLetterPath(name)}/messages?${query}`, isPage(isDeadLetterMessage), controller.signal),
          ]);
          setSummary(current); setMessages(rows);
          if (id) setDetail(await deadLetterRequest(deadLetterPath(name, id), isDeadLetterMessage, controller.signal));
        }
      } catch (failure) {
        if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : String(failure)); setDetail(null); }
      } finally { if (!controller.signal.aborted) setLoading(false); }
    }, 250);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [name, id, prefix, reason, status, after, refresh, revision]);

  async function mutate(kind: 'requeue' | 'delete') {
    if (!name || !id || pending || !confirm(`${kind === 'delete' ? 'Permanently delete' : 'Requeue'} message ${id} for function ${name}?`)) return;
    action.current?.abort();
    const controller = new AbortController(); action.current = controller; setPending(true); setNotice('');
    try {
      if (kind === 'requeue') {
        const result: DeadLetterReplay = await deadLetterRequest(`${deadLetterPath(name, id)}/requeue`, isReplay, controller.signal, 'POST');
        setNotice(`Requeued: ${result.ElementId}. This confirms durable acceptance, not successful processing.`);
      } else {
        const response = await fetch(deadLetterPath(name, id), { method: 'DELETE', signal: controller.signal });
        if (response.status !== 204) throw new Error(`Deletion unavailable: HTTP ${response.status}. Refresh to see the current outcome.`);
        setNotice('Deleted.'); setPreview(null);
      }
      setRefresh(value => value + 1);
    } catch (failure) {
      if (!controller.signal.aborted) setNotice(`${failure instanceof Error ? failure.message : String(failure)} Retry requeue using this same message ID if the response was uncertain.`);
    } finally { if (!controller.signal.aborted) setPending(false); }
  }
  async function showBody() {
    if (!name || !id || pending) return;
    const controller = new AbortController(); action.current?.abort(); action.current = controller; setPending(true);
    try {
      const response = await fetch(`${deadLetterPath(name, id)}/body`, { signal: controller.signal, headers: { Range: `bytes=0-${PREVIEW_BYTES - 1}` } });
      setPreview(await readDeadLetterPreview(response));
    } catch (failure) { if (!controller.signal.aborted) setNotice(failure instanceof Error ? failure.message : String(failure)); }
    finally { if (!controller.signal.aborted) setPending(false); }
  }
  if (invalid) return <p className="notice notice--error">Invalid dead letter route.</p>;
  const page = name ? messages : catalog;
  return <section className="dead-letters" aria-label="Dead Letter Queues">
    <nav aria-label="Dead letter navigation"><a href="#/dead-letters">All dead letter queues</a>{name && <> / <a href={deadLetterLink(name)}>{name}</a></>}</nav>
    <h2>{name ?? 'Functions with dead letter queues'}</h2>
    <p>Retained failures are not dispatched automatically. Opening this page does not wake functions.</p>
    {stale && <p className="notice" role="status">Stream unavailable. The displayed snapshot may be stale.</p>}
    {error && <p className="notice notice--error" role="alert">{error}</p>}
    {notice && <p className="notice" role="status">{notice}</p>}
    {loading && <p role="status">Loading retained failures...</p>}
    <div className="toolbar">
      {!name ? <label className="field">Function prefix<input className="field__input" value={prefix} onChange={event => setPrefix(event.target.value)} /></label> : <>
        <label className="field">Failure reason<select className="field__input" value={reason} onChange={event => setReason(event.target.value)}><option value="">All reasons</option>{reasons.map(item => <option key={item}>{item}</option>)}</select></label>
        <label className="field">HTTP status<input className="field__input" type="number" min="100" max="599" value={status} onChange={event => setStatus(event.target.value)} /></label>
      </>}
      <button className="button button--quiet" disabled={loading} onClick={() => setRefresh(value => value + 1)}>Refresh</button>
    </div>
    {summary && <p>{summary.Count} retained / {summary.MaxLength} maximum; {summary.MaxAgeSeconds} seconds retention. Capture {summary.CaptureEnabled ? 'enabled' : 'disabled'}; function {summary.FunctionExists ? 'present' : 'removed'}.</p>}
    {!name && catalog && <div className="table-wrap"><table className="table"><thead><tr>{['Function', 'Capture', 'Function exists', 'Retained', 'Oldest failure', 'Latest failure'].map(label => <th className="table__th" key={label}>{label}</th>)}</tr></thead><tbody>
      {catalog.Items.map(item => <tr className="table__row" key={item.Function}><td className="table__td"><a href={deadLetterLink(item.Function)}>{item.Function}</a></td><td className="table__td">{item.CaptureEnabled ? 'Enabled' : 'Disabled'}</td><td className="table__td">{item.FunctionExists ? 'Yes' : 'Removed'}</td><td className="table__td">{item.Count}</td><td className="table__td">{item.OldestFailedAt ? new Date(item.OldestFailedAt).toLocaleString() : 'None'}</td><td className="table__td">{item.NewestFailedAt ? new Date(item.NewestFailedAt).toLocaleString() : 'None'}</td></tr>)}
    </tbody></table></div>}
    {name && messages && <div className="table-wrap"><table className="table"><thead><tr>{['Message / original ID', 'Failure / expiration', 'Reason', 'Attempts / status', 'Request / bytes'].map(label => <th className="table__th" key={label}>{label}</th>)}</tr></thead><tbody>
      {messages.Items.map(item => <tr className="table__row" key={item.Id}><td className="table__td"><a href={deadLetterLink(name, item.Id)}>{item.Id}</a><br />{item.OriginalElementId}</td><td className="table__td">{new Date(item.FailedAt).toLocaleString()}<br />{new Date(item.ExpiresAt).toLocaleString()}</td><td className="table__td">{item.FailureReason}</td><td className="table__td">{item.Attempts} / {item.LastHttpStatus ?? 'Unknown'}</td><td className="table__td">{item.Request.Method} /{item.Request.Path}<br />{item.Request.BodyLength ?? 'Unknown'} bytes</td></tr>)}
    </tbody></table></div>}
    {page && <><p className="dead-letters__snapshot">Snapshot {new Date(page.CapturedAt).toLocaleString()} · revision {page.Revision}{page.Items.length === 0 ? ' · No retained failures on this page.' : ''}</p>
      <div className="toolbar"><button className="button button--quiet" disabled={cursors.length === 1 || loading} onClick={() => setCursors(value => value.slice(0, -1))}>Previous</button><button className="button button--quiet" disabled={!page.NextCursor || loading} onClick={() => setCursors(value => [...value, page.NextCursor])}>Next</button></div></>}
    {detail && <article className="dead-letters__detail"><h3>Message {detail.Id}</h3>
      <p>Function {detail.Function}; original element {detail.OriginalElementId}; root {detail.RootElementId ?? detail.OriginalElementId}; replay of {detail.ReplayOfDeadLetterId ?? 'none'}.</p>
      <pre className="dead-letters__preview">{JSON.stringify(detail, null, 2)}</pre>
      <div className="toolbar"><button className="button button--primary" disabled={pending} onClick={() => mutate('requeue')}>Requeue</button><button className="button button--quiet" disabled={pending} onClick={() => mutate('delete')}>Delete</button><button className="button button--quiet" disabled={pending} onClick={showBody}>Preview body (64 KiB maximum)</button><a href={`${deadLetterPath(name, id)}/body`} download={id}>Download body</a></div>
      {preview && <><p>{preview.truncated ? 'Preview truncated at 64 KiB. Download for the complete body.' : 'Complete body preview.'}</p><pre className="dead-letters__preview">{preview.text}</pre></>}
    </article>}
  </section>;
}
