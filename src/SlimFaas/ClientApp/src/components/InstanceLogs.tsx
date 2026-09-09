import { useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useLogStream } from '../hooks/useLogStream';
import { filterLogs, logWindow, LOG_ROW_HEIGHT, type LogLine, type LogTarget } from '../lib/logs.ts';

export default function InstanceLogs({ target }: { target: LogTarget }) {
  const stream = useLogStream(target);
  return <div className="instance-logs">
    <div className="toolbar">
      {stream.sources.length > 1 && <label className="field field--grow log-view__field">Log source
        <select className="field__input" value={stream.source} onChange={event => stream.setSource(event.target.value)}>
          {stream.sources.map(source => <option key={source.Id} value={source.Id}>{source.Name} · {source.Container}</option>)}
        </select>
      </label>}
      <span className={`badge ${stream.status === 'Live' ? 'badge--success' : 'badge--warning'}`} role="status">{stream.status}</span>
      <button className="button button--quiet" type="button" onClick={stream.retry}>Reconnect</button>
    </div>
    <LogViewer key={`${stream.source}/${stream.state?.Session}`} lines={stream.lines} status={stream.status}
      discarded={Math.max(stream.discarded, stream.state?.DroppedLines ?? 0)} />
    <p className="instance-logs__note">Application output · Up to 10,000 lines / 8 MiB · Filters apply to retained lines only.</p>
  </div>;
}

export function LogViewer({ lines, status = 'Live', discarded = 0 }: { lines: LogLine[]; status?: string; discarded?: number }) {
  const [include, setInclude] = useState(''), [exclude, setExclude] = useState('');
  const [sensitive, setSensitive] = useState(false);
  const [following, setFollowing] = useState(true);
  const [scrollTop, setScrollTop] = useState(0);
  const [seen, setSeen] = useState(0);
  const viewport = useRef<HTMLDivElement>(null);
  const previousLines = useRef<LogLine[]>([]);
  const filtered = useMemo(() => filterLogs(lines, include, exclude, sensitive), [lines, include, exclude, sensitive]);
  const window = logWindow(filtered.length, scrollTop);
  const newest = lines[lines.length - 1]?.Id ?? 0;
  const pending = following ? 0 : Math.max(0, newest - seen);
  useLayoutEffect(() => {
    const element = viewport.current;
    if (!element) return;
    if (following) { element.scrollTo({ top: element.scrollHeight, behavior: 'instant' }); setSeen(newest); }
    else {
      const oldIndex = Math.floor(element.scrollTop / LOG_ROW_HEIGHT);
      const anchor = previousLines.current[oldIndex];
      if (anchor) {
        const nextIndex = filtered.findIndex(line => line.Id === anchor.Id);
        if (nextIndex >= 0) element.scrollTop = nextIndex * LOG_ROW_HEIGHT + element.scrollTop % LOG_ROW_HEIGHT;
        else if (filtered[0]?.Id > anchor.Id) element.scrollTop = 0;
      }
    }
    previousLines.current = filtered;
    setScrollTop(element.scrollTop);
  }, [filtered, following, newest, lines]);

  const pauseScrolling = () => { if (following) { setFollowing(false); setSeen(newest); } };
  const resetFilter = () => { previousLines.current = []; setScrollTop(0); if (viewport.current) viewport.current.scrollTop = 0; };
  return <div className="log-view">
    <div className="toolbar">
      <label className="field field--grow log-view__field">Find in logs<input className="field__input" type="search" placeholder="Include text…" value={include} onChange={event => { setInclude(event.target.value); resetFilter(); }} /></label>
      <label className="field field--grow log-view__field">Exclude text<input className="field__input" type="search" placeholder="Hide matching lines…" value={exclude} onChange={event => { setExclude(event.target.value); resetFilter(); }} /></label>
      <label className="log-view__case"><input type="checkbox" checked={sensitive} onChange={event => { setSensitive(event.target.checked); resetFilter(); }} /> Case sensitive</label>
    </div>
    <div className="log-view__summary"><span>{filtered.length.toLocaleString()} matching / {lines.length.toLocaleString()} retained</span>
      <button className="button button--quiet" type="button" onClick={() => setFollowing(!following)}>{following ? 'Pause scrolling' : `Follow latest${pending ? ` (${pending.toLocaleString()} new)` : ''}`}</button>
    </div>
    {!!discarded && <p className="log-view__notice">Older lines were discarded to stay within the buffer limits.</p>}
    <div className="log-view__viewport" ref={viewport} tabIndex={0} role="region" aria-label="Replica logs"
      onWheel={event => { if (event.deltaY < 0) pauseScrolling(); }} onTouchMove={pauseScrolling}
      onPointerDown={pauseScrolling} onKeyDown={event => { if (['ArrowUp', 'PageUp', 'Home'].includes(event.key)) pauseScrolling(); }}
      onScroll={() => setScrollTop(viewport.current!.scrollTop)}>
      {filtered.length ? <>
        <svg className="log-view__spacer" width="1" height={window.before} aria-hidden="true" />
        <ol className="log-view__lines" start={window.start + 1}>
          {filtered.slice(window.start, window.end).map((line, index) => <li className="log-view__line" key={line.Id} aria-posinset={window.start + index + 1} aria-setsize={filtered.length}>
            <span className="log-view__number">{line.Id}</span><time className="log-view__time">{line.TimestampMs == null ? '—' : new Date(line.TimestampMs).toLocaleTimeString()}</time>
            <span className="log-view__text">{line.Text}</span>{line.Truncated && <span className="log-view__truncated"> [line truncated]</span>}
          </li>)}
        </ol>
        <svg className="log-view__spacer" width="1" height={window.after} aria-hidden="true" />
      </> : <p className="log-view__empty">{lines.length ? 'No lines match these filters.' : status === 'Live' ? 'Waiting for log output…' : status}</p>}
    </div>
  </div>;
}
