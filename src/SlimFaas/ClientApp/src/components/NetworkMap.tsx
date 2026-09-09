import { useEffect, useMemo, useRef, useState } from 'react';
import type { FunctionStatusDetailed, JobConfigurationStatus, QueueInfo, NetworkActivityEvent, SlimFaasNodeInfo } from '../types.ts';
import { buildTopology, eventPath, filterNodes, observedFunctions, observedQueues, type ObservedFunction } from '../lib/topology.ts';
import { paginate } from '../lib/live.ts';
import { matchesTraffic, SPEEDS, type Speed } from '../lib/traffic.ts';
import TrafficCanvas from './TrafficCanvas';
import Pagination from './Pagination';
import InstanceLogs from './InstanceLogs';

interface Props {
  functions: FunctionStatusDetailed[]; jobs: JobConfigurationStatus[]; queues: QueueInfo[];
  activity: NetworkActivityEvent[]; functionsWithQueueActivity: Set<string>;
  slimFaasReplicas: number; slimFaasNodes: SlimFaasNodeInfo[];
  activitySession?: number; samplingRatio?: number; maxLiveEventsPerSecond?: number;
}

export default function NetworkMap(props: Props) {
  const [frozen, setFrozen] = useState<Props | null>(null);
  const input = frozen ?? props;
  const [now, setNow] = useState(performance.now());
  useEffect(() => {
    if (frozen) return;
    const timer = setInterval(() => setNow(performance.now()), 1000);
    return () => clearInterval(timer);
  }, [frozen]);
  const [search, setSearch] = useState('');
  const [detailTab, setDetailTab] = useState<'details' | 'logs'>('details');
  const [selected, setSelected] = useState<string | null>(null);
  const [eventType, setEventType] = useState('all');
  const [isolate, setIsolate] = useState(false);
  const [speed, setSpeed] = useState<Speed>(() => {
    try { const saved = localStorage.getItem('slimfaas.traffic.speed'); return saved && Object.keys(SPEEDS).includes(saved) ? saved as Speed : 'Fast'; }
    catch { return 'Fast'; }
  });
  const changeSpeed = (value: Speed) => { setSpeed(value); try { localStorage.setItem('slimfaas.traffic.speed', value); } catch { /* Storage can be disabled. */ } };
  const [page, setPage] = useState(0);
  const [journalPage, setJournalPage] = useState(0);
  const [showJournal, setShowJournal] = useState(false);
  const observedRef = useRef<ObservedFunction[]>([]);
  const nextObserved = observedFunctions(input.functions, input.activity);
  // Most receipts concern existing inventory: preserve the expensive topology in that case.
  if (JSON.stringify(nextObserved) !== JSON.stringify(observedRef.current)) observedRef.current = nextObserved;
  const observedActors = observedRef.current;
  const observedQueuesRef = useRef<string[]>([]);
  const nextObservedQueues = observedQueues(input.queues, input.activity);
  if (JSON.stringify(nextObservedQueues) !== JSON.stringify(observedQueuesRef.current)) observedQueuesRef.current = nextObservedQueues;
  const unknownQueues = observedQueuesRef.current;
  const topology = useMemo(() => buildTopology(input.functions, input.jobs,
    input.queues.filter(q => q.Length > 0 || input.functionsWithQueueActivity.has(q.Name)), input.slimFaasNodes, observedActors, unknownQueues),
  [input.functions, input.jobs, input.queues, input.functionsWithQueueActivity, input.slimFaasNodes, observedActors, unknownQueues]);
  const node = selected ? topology.byId.get(selected) : null;
  const nodes = useMemo(() => filterNodes(topology, search), [topology, search]);
  const nodePage = paginate(nodes, page);
  const activity = useMemo(() => input.activity.filter(e => matchesTraffic(topology, e, eventType, selected, isolate)),
    [input.activity, eventType, topology, selected, isolate]);
  const journal = paginate([...activity].reverse(), journalPage);
  const observed = input.activity.filter(e => e.ReceivedAt !== undefined && e.ReceivedAt > now - 1000).length;

  const choose = (id: string) => { setSelected(id); setDetailTab('details'); setJournalPage(0); };
  const logTarget = node?.parent && ['function', 'job', 'slimfaas'].includes(node.kind) ? {
    kind: node.kind as 'function' | 'job' | 'slimfaas', name: topology.byId.get(node.parent)!.label, replica: node.label,
  } : null;

  return <section className="traffic" aria-label="Live traffic">
    <div className="toolbar">
      <label className="field field--grow traffic__search">Find an actor
        <input className="field__input" type="search" value={search} placeholder="Function, job, replica or execution…" onChange={e => { setSearch(e.target.value); setPage(0); }} />
      </label>
      <label className="field">Event type
        <select className="field__input" value={eventType} onChange={e => { setEventType(e.target.value); setJournalPage(0); }}>
          <option value="all">All events</option>
          {['request_in', 'enqueue', 'dequeue', 'request_out', 'response', 'event_publish', 'request_waiting', 'request_started', 'request_end'].map(type => <option key={type} value={type}>{type.replace(/_/g, ' ')}</option>)}
        </select>
      </label>
      <label className="field">Animation speed
        <select className="field__input" value={speed} onChange={e => changeSpeed(e.target.value as Speed)}>
          {Object.entries(SPEEDS).map(([label, duration]) => <option key={label} value={label}>{label} · {duration} ms</option>)}
        </select>
      </label>
      <button className={`button ${frozen ? 'button--primary' : 'button--quiet'}`} type="button" onClick={() => { setNow(performance.now()); setFrozen(frozen ? null : props); setJournalPage(0); }}>{frozen ? 'Resume live' : 'Pause'}</button>
    </div>
    <div className="traffic__caption">
      <span className={`badge ${frozen ? 'badge--warning' : 'badge--success'}`}>{frozen ? 'Paused' : 'Live'}</span>
      <span>{topology.nodes.length.toLocaleString()} actors · {observed.toLocaleString()} observed events/s</span>
      <span>Sampling: {((props.samplingRatio ?? 1) * 100).toLocaleString()}% · Stream limit: {props.maxLiveEventsPerSecond ? `${props.maxLiveEventsPerSecond.toLocaleString()} events/s/node` : 'none'}</span>
    </div>
    {(eventType !== 'all' || (selected && isolate)) && <p className="traffic__filters" role="status">Filtered view: {eventType !== 'all' ? eventType.replace(/_/g, ' ') : 'all event types'}{selected && isolate ? ' · selected actor only' : ''}. <button className="table__link" onClick={() => { setEventType('all'); setIsolate(false); }}>Clear filters</button></p>}
    <TrafficCanvas topology={topology} events={props.activity} visibleEvents={activity} activitySession={props.activitySession ?? 0} selected={selected} paused={!!frozen} onSelect={choose} speed={speed} eventType={eventType} isolate={isolate} />
    <div className="traffic__legend" aria-label="Message legend"><span className="traffic__legend-item traffic__legend-item--function">Requests</span><span className="traffic__legend-item traffic__legend-item--publication">Publications</span><span className="traffic__legend-item traffic__legend-item--queue">Queue messages</span><span className="traffic__legend-item traffic__legend-item--external">Replies</span></div>
    <p className="traffic__note">Animation speed is independent of request latency. Delivery is best effort; the journal retains up to 5,000 received events.</p>
    {selected && <div className="traffic__selection">
      <div><strong>{node?.label ?? 'Actor no longer present'}</strong><p className="traffic__selection-detail">{node ? `${node.kind} · ${node.status} · ${node.detail}` : 'The latest snapshot no longer contains this instance.'}</p></div>
      <label className="traffic__isolate"><input type="checkbox" checked={isolate} onChange={event => { setIsolate(event.target.checked); setJournalPage(0); }} /> Isolate selection</label>
      <button className="button button--quiet" type="button" onClick={() => { setSelected(null); setIsolate(false); }}>Clear selection</button>
      {logTarget && <>
        <div className="traffic__detail-tabs" role="tablist" aria-label="Instance details">
          {(['details', 'logs'] as const).map(tab => <button key={tab} id={`instance-tab-${tab}`} aria-controls={`instance-panel-${tab}`}
            type="button" role="tab" aria-selected={detailTab === tab} tabIndex={detailTab === tab ? 0 : -1}
            className={`button ${detailTab === tab ? 'button--primary' : 'button--quiet'}`}
            onClick={() => setDetailTab(tab)} onKeyDown={event => {
              if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
                event.preventDefault(); const next = event.key === 'Home' ? 'details' : event.key === 'End' ? 'logs' : tab === 'details' ? 'logs' : 'details';
                setDetailTab(next); document.getElementById(`instance-tab-${next}`)?.focus();
              }
            }}>{tab === 'details' ? 'Details' : 'Logs'}</button>)}
        </div>
        <div className="traffic__detail-content" role="tabpanel" id={`instance-panel-${detailTab}`} aria-labelledby={`instance-tab-${detailTab}`}>
          {detailTab === 'logs' ? <InstanceLogs key={selected} target={logTarget} /> : <p className="traffic__note">{node?.role ? `Raft role: ${node.role}. ` : ''}Traffic routes use the recorded destination. Select Logs to read this instance’s application output.</p>}
        </div>
      </>}
    </div>}
    <div className="section-heading"><h2 className="section-heading__title">{showJournal ? 'Event journal' : 'Actors'}</h2><button className="button button--quiet" type="button" onClick={() => setShowJournal(!showJournal)}>{showJournal ? 'Show actors' : `Event journal (${activity.length.toLocaleString()})`}</button></div>
    {showJournal ? <>
      <p className="traffic__note">Up to 5,000 received events. Pause to inspect a stable view. Select an actor to follow its connections.</p>
      <div className="table-wrap"><table className="table"><thead className="table__head"><tr><th className="table__th">Time</th><th className="table__th">Event</th><th className="table__th">Path</th><th className="table__th">Node</th></tr></thead>
        <tbody>{journal.items.map(e => <tr className="table__row" key={e.Id}><td className="table__td table__td--mono">{new Date(e.TimestampMs).toLocaleTimeString()}</td><td className="table__td">{e.Type.replace(/_/g, ' ')}</td><td className="table__td table__td--path">{eventPath(topology, e).map(id => topology.byId.get(id)?.label ?? id).join(' → ')}</td><td className="table__td">{e.NodeId}</td></tr>)}</tbody></table></div>
      {activity.length === 0 && <p className="empty-state">Waiting for matching traffic.</p>}
      <Pagination page={journal.page} pages={journal.pages} total={activity.length} onPage={setJournalPage} />
    </> : <>
      <div className="table-wrap"><table className="table"><thead className="table__head"><tr><th className="table__th">Actor</th><th className="table__th">Kind</th><th className="table__th">Status</th><th className="table__th">Details</th></tr></thead>
        <tbody>{nodePage.items.map(n => <tr className={`table__row ${selected === n.id ? 'table__row--selected' : ''}`} key={n.id}>
          <td className="table__td"><button className="table__link" type="button" onClick={() => choose(n.id)}>{n.label}</button></td><td className="table__td">{n.parent ? n.kind === 'job' ? 'Job execution' : 'Replica' : n.kind}</td><td className="table__td">{n.status}</td><td className="table__td table__td--mono">{n.detail}</td></tr>)}</tbody></table></div>
      {nodes.length === 0 && <p className="empty-state">No actors match your search.</p>}
      <Pagination page={nodePage.page} pages={nodePage.pages} total={nodes.length} onPage={setPage} />
    </>}
  </section>;
}
