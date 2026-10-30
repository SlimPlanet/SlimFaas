import { useEffect, useMemo, useRef, useState } from 'react';
import { isPage, PREVIEW_BYTES, readDeadLetterPreview } from '../lib/deadLetters';
import { isStreamFunction, isStreamMessage, isStreamSummary, newerStreamObservation, reconcileStreamCatalog, streamLink, streamPath, streamRequest,
  type StreamFunction, type StreamMessage, type StreamPage, type StreamSummary } from '../lib/streams';

function parseRoute(route: string) {
  try {
    const parts = route.split('?')[0].split('/').slice(2).map(decodeURIComponent);
    if (parts.length > 3 || parts.length === 2 || parts.length === 3 && !['functions', 'messages'].includes(parts[1]))
      throw new Error('Unknown Streams route.');
    return { name: parts[0], kind: parts[1], key: parts[2], error: null };
  } catch (error) {
    return { name: undefined, kind: undefined, key: undefined, error: error instanceof Error ? error.message : 'Invalid route.' };
  }
}
export default function Streams({ route, revision, stale }: { route: string; revision: string; stale: boolean }) {
  const selection = useMemo(() => parseRoute(route), [route]);
  const [prefix, setPrefix] = useState('');
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined]);
  const [functionCursors, setFunctionCursors] = useState<(string | undefined)[]>([undefined]);
  const [catalog, setCatalog] = useState<StreamPage<StreamSummary> | null>(null);
  const [summary, setSummary] = useState<StreamSummary | null>(null);
  const [functions, setFunctions] = useState<StreamPage<StreamFunction> | null>(null);
  const [messages, setMessages] = useState<StreamPage<StreamMessage> | null>(null);
  const [functionDetail, setFunctionDetail] = useState<StreamFunction | null>(null);
  const [message, setMessage] = useState<StreamMessage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ text: string; truncated: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const previewRequest = useRef<AbortController | null>(null);
  const latestSummary = useRef<StreamSummary | null>(null);
  const retiredGenerations = useRef<string[]>([]);
  const after = cursors[cursors.length - 1];
  const functionAfter = functionCursors[functionCursors.length - 1];
  useEffect(() => {
    setCursors([undefined]); setFunctionCursors([undefined]); setPreview(null); previewRequest.current?.abort();
    latestSummary.current = null; retiredGenerations.current = [];
  }, [route, prefix]);
  useEffect(() => {
    setSummary(null); setCatalog(null); setFunctions(null); setMessages(null); setMessage(null); setFunctionDetail(null);
  }, [selection, after, functionAfter, prefix]);
  useEffect(() => () => previewRequest.current?.abort(), []);
  useEffect(() => {
    const controller = new AbortController();
    const signal = controller.signal;
    setError(selection.error); setBusy(true);
    if (selection.error) { setBusy(false); return () => controller.abort(); }
    const query = `?limit=100${after ? `&after=${encodeURIComponent(after)}` : ''}`;
    const path = streamPath(selection.name);
    const read = async () => {
      if (!selection.name) {
        const incoming = await streamRequest(`${path}${query}&prefix=${encodeURIComponent(prefix)}`, isPage(isStreamSummary), signal);
        if (!signal.aborted) setCatalog(previous => reconcileStreamCatalog(previous, incoming));
      }
      else {
        const metadata = await streamRequest(`${path}/metadata`, isStreamSummary, signal);
        if (signal.aborted) return;
        const known = latestSummary.current;
        if (retiredGenerations.current.includes(metadata.Generation) ||
            known && known.Generation === metadata.Generation && known.Revision > metadata.Revision) {
          setError('The responding node has an older revision. Last observed values remain visible.');
          return;
        }
        if (known && known.Generation !== metadata.Generation) {
          retiredGenerations.current = [...retiredGenerations.current.slice(-7), known.Generation];
          setFunctions(null); setMessages(null); setMessage(null); setFunctionDetail(null); setPreview(null);
        }
        latestSummary.current = metadata;
        setSummary(metadata);
        if (selection.kind === 'messages') {
          const incoming = await streamRequest(`${path}/messages/${encodeURIComponent(selection.key ?? '')}`, isStreamMessage, signal);
          if (!signal.aborted) setMessage(previous => newerStreamObservation(previous, incoming));
        }
        else if (selection.kind === 'functions') {
          const incoming = await streamRequest(`${path}/functions/${encodeURIComponent(selection.key ?? '')}`, isStreamFunction, signal);
          if (!signal.aborted) setFunctionDetail(previous => newerStreamObservation(previous, incoming));
        }
        else {
          const [subscriptions, history] = await Promise.all([
            streamRequest(`${path}/functions?limit=100${functionAfter ? `&after=${encodeURIComponent(functionAfter)}` : ''}`, isPage(isStreamFunction), signal),
            streamRequest(`${path}/messages${query}`, isPage(isStreamMessage), signal),
          ]);
          if (signal.aborted) return;
          setFunctions(previous => newerStreamObservation(previous, subscriptions));
          setMessages(previous => newerStreamObservation(previous, history));
        }
      }
    };
    const timer = window.setTimeout(() => {
      void read().catch(reason => {
        if (!signal.aborted) setError(reason instanceof Error ? reason.message : 'Stream observation failed.');
      }).finally(() => { if (!signal.aborted) setBusy(false); });
    }, 150);
    return () => { window.clearTimeout(timer); controller.abort(); };
  }, [selection, after, functionAfter, prefix, revision, refresh]);
  const inspectBody = async () => {
    if (!selection.name || !message) return;
    previewRequest.current?.abort();
    const controller = new AbortController(); previewRequest.current = controller;
    try {
      const response = await fetch(`${streamPath(selection.name)}/messages/${encodeURIComponent(message.Id)}/body`,
        { headers: { Range: `bytes=0-${PREVIEW_BYTES - 1}` }, signal: controller.signal });
      const result = await readDeadLetterPreview(response);
      if (!controller.signal.aborted) setPreview(result);
    } catch (reason) { if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Body unavailable.'); }
  };
  const next = catalog?.NextCursor ?? messages?.NextCursor;
  return <section className="streams" aria-label="Durable Streams">
    <nav className="streams__breadcrumbs" aria-label="Streams navigation">
      <a href={streamLink()}>Streams</a>{selection.name && <> / <a href={streamLink(selection.name)}>{selection.name}</a></>}
      {selection.key && <> / {selection.key}</>}
    </nav>
    <p>Observation never reserves, replays or acknowledges work. Retention is independent of function progress.</p>
    {stale && <p className="notice" role="status">Live connection is stale. Last observed values may be outdated.</p>}
    {error && <p className="notice notice--error" role="alert">{error} <button type="button" className="button" onClick={() => { setCursors([undefined]); setRefresh(value => value + 1); }}>Refresh from beginning</button></p>}
    {busy && <p role="status">Loading Stream metadata…</p>}
    {!selection.name && <label className="field">Stream name prefix<input className="field__input" value={prefix} onChange={event => setPrefix(event.target.value)} /></label>}
    {catalog && <div className="streams__table"><table><caption>Accessible Streams · captured {catalog.CapturedAt}</caption>
      <thead><tr><th>Stream</th><th>Visibility</th><th>Retained</th><th>Visible functions</th><th>Retention</th></tr></thead>
      <tbody>{catalog.Items.map(item => <tr key={item.Name}><td><a href={streamLink(item.Name)}>{item.Name}</a></td><td>{item.Visibility}</td>
        <td>{item.RetainedMessageCount}</td><td>{item.VisibleFunctionCount}</td><td>{item.MaxAgeSeconds}s / {item.MaxLength} messages</td></tr>)}</tbody></table>
      {!catalog.Items.length && <p>No accessible Streams match this prefix.</p>}</div>}
    {summary && <div className="streams__summary"><h2>{summary.Name}</h2><p>{summary.Visibility} · {summary.RetainedMessageCount} retained · {summary.MaxAgeSeconds}s / {summary.MaxLength} messages</p>
      <dl><dt>Oldest / newest append</dt><dd>{summary.OldestAppendedAt ?? 'Empty'} / {summary.NewestAppendedAt ?? 'Empty'}</dd>
        <dt>Earliest / latest retained ID</dt><dd>{summary.EarliestMessageId ?? 'Empty'} / {summary.LatestMessageId ?? 'Empty'}</dd>
        <dt>Captured / revision</dt><dd>{summary.CapturedAt} / {summary.Revision}</dd></dl></div>}
    {(functions || functionDetail) && <div className="streams__table"><table><caption>Independent function consumption</caption>
      <thead><tr>{['Function', 'Subscription', 'Lag', 'Pending', 'Callback waits', 'Retries', 'Failed', 'Runnable', 'ACK through / last ACK', 'Retention gaps', 'Last status'].map(label => <th key={label}>{label}</th>)}</tr></thead>
      <tbody>{(functionDetail ? [functionDetail] : functions?.Items ?? []).map(item => <tr key={item.Function}>
        <td><a href={streamLink(item.Stream, 'functions', item.Function)}>{item.Function}</a></td><td>{item.Active ? 'Active' : 'Unsubscribed'}</td>
        <td>{item.Lag}</td><td>{item.PendingCount}</td><td>{item.WaitingForCallbackCount}</td><td>{item.RetryScheduledCount}</td>
        <td>{item.FailedCount}</td><td>{item.RunnableCount}</td><td>{item.AcknowledgedThrough} / {item.LastAcknowledged}</td>
        <td>{item.RetentionGapCount}</td><td>{item.LastStatus ?? 'None'}</td></tr>)}</tbody></table>
      <p>Pending, callback waits, retries and failures overlap lag; do not add them together.</p>
      {functions && <nav className="streams__pagination" aria-label="Function pages"><button className="button" disabled={functionCursors.length === 1 || busy} onClick={() => setFunctionCursors(value => value.slice(0, -1))}>Previous functions</button>
        <span>Page {functionCursors.length}</span><button className="button" disabled={!functions.NextCursor || busy} onClick={() => { if (functions.NextCursor) setFunctionCursors(value => [...value, functions.NextCursor ?? undefined]); }}>Next functions</button></nav>}</div>}
    {messages && <div className="streams__table"><table><caption>Retained history · metadata only</caption><thead><tr><th>Message ID</th><th>Appended</th><th>Content type</th><th>Payload bytes</th></tr></thead>
      <tbody>{messages.Items.map(item => <tr key={item.Id}><td><a href={streamLink(selection.name, 'messages', item.Id)}>{item.Id}</a></td><td>{item.AppendedAt}</td><td>{item.ContentType}</td><td>{item.PayloadSize}</td></tr>)}</tbody></table>
      {!messages.Items.length && <p>No messages on this page.</p>}</div>}
    {message && <article><h2>Message {message.Id}</h2><p>{message.AppendedAt} · {message.ContentType} · {message.PayloadSize} bytes</p>
      <div className="streams__actions"><button className="button" type="button" disabled={stale} onClick={() => void inspectBody()}>Preview body (maximum 65,536 bytes)</button>
      {!stale && <a className="button" href={`${streamPath(selection.name)}/messages/${encodeURIComponent(message.Id)}/body`} download>Download body</a>}</div>
      {preview && <><p>{preview.truncated ? 'Preview truncated at 65,536 bytes.' : 'Complete preview.'} Untrusted content is displayed as inert text.</p><pre className="streams__preview">{preview.text}</pre></>}
    </article>}
    {(catalog || messages) && <nav className="streams__pagination" aria-label="Stream pages">
      <button className="button" disabled={cursors.length === 1 || busy} onClick={() => setCursors(value => value.slice(0, -1))}>Previous</button>
      <span>Page {cursors.length}</span><button className="button" disabled={!next || busy} onClick={() => { if (next) setCursors(value => [...value, next]); }}>Next</button>
    </nav>}
  </section>;
}
