import { useEffect, useMemo, useRef, useState } from 'react';
import type { DataSummary, FunctionStatusDetailed, JobConfigurationStatus, QueueInfo, NetworkActivityEvent, SlimFaasNodeInfo } from '../types.ts';
import { buildTopology, eventPath, filterNodes, isJobQueueName, jobQueues, observedFunctions, observedQueues, type ObservedFunction } from '../lib/topology.ts';
import { paginate } from '../lib/live.ts';
import { matchesTraffic, SPEEDS, type Speed } from '../lib/traffic.ts';
import TrafficCanvas from './TrafficCanvas';
import Pagination from './Pagination';
import InstanceLogs from './InstanceLogs';
import TrafficDetails from './TrafficDetails';
import ActorInspector from './ActorInspector';
import { canWake, closeActor, openActor } from '../lib/dashboard';
import { deadLetterLink, type DeadLetterSummary } from '../lib/deadLetters';
import { type StreamSummary, type StreamFunction } from '../lib/streams';

interface Props {
  loading?: boolean; panelActor?: string | null; wakeUp?: (name: string) => void; coolingDown?: Set<string>;
  deadLettersAccessible?: boolean; deadLettersFrontEnabled?: boolean; actionError?: string | null; error?: string | null;
  dataSummary?: DataSummary | null;
  deadLetters?: DeadLetterSummary[];
  streams?: StreamSummary[]; streamSubscriptions?: StreamFunction[]; streamsTruncated?: boolean; streamsAvailable?: boolean;
  functions: FunctionStatusDetailed[]; jobs: JobConfigurationStatus[]; queues: QueueInfo[];
  activity: NetworkActivityEvent[]; queuesWithActivity: Set<string>;
  slimFaasReplicas: number; slimFaasNodes: SlimFaasNodeInfo[];
  activitySession?: number; samplingRatio?: number; maxLiveEventsPerSecond?: number;
}

const noDeadLetters: DeadLetterSummary[] = [];
const noStreams: StreamSummary[] = [];

export default function NetworkMap(props: Props) {
  const [frozen, setFrozen] = useState<Props | null>(null);
  const input = useMemo(() => {
    const saved = frozen ?? props;
    const snapshot = props.dataSummary !== null ? saved : { ...saved, dataSummary: null, activity: saved.activity.filter(event => !event.DataKind) };
    const visible = props.deadLettersAccessible === false || props.deadLettersFrontEnabled === false
      ? { ...snapshot, deadLetters: noDeadLetters } : snapshot;
    return props.streamsAvailable !== false ? visible : {
      ...visible, streams: noStreams, streamSubscriptions: [], streamsTruncated: false,
      activity: visible.activity.filter(event => !event.StreamName),
    };
  }, [frozen, props]);
  const [now, setNow] = useState(performance.now());
  useEffect(() => {
    if (frozen) return;
    const timer = setInterval(() => setNow(performance.now()), 1000);
    return () => clearInterval(timer);
  }, [frozen]);
  const [search, setSearch] = useState('');
  const [lastSelected, setSelected] = useState<string | null>(null);
  const [selectedLabel, setSelectedLabel] = useState('');
  const [storyPanel, setStoryPanel] = useState<string | null>(null);
  const panel = props.panelActor === undefined ? storyPanel : props.panelActor;
  const selected = panel ?? lastSelected;
  const close = () => { if (props.panelActor === undefined) setStoryPanel(null); else closeActor(); };
  const tray = useRef<HTMLDivElement>(null);
  const toolTrigger = useRef<HTMLButtonElement | null>(null);
  const [tools, setTools] = useState<'actors' | 'journal' | 'filters' | null>(null);
  const showJournal = tools === 'journal';
  const toggle = (view: 'actors' | 'journal' | 'filters', trigger: HTMLButtonElement) => {
    toolTrigger.current = trigger;
    setTools(current => current === view ? null : view);
  };
  const closeTools = () => { setTools(null); toolTrigger.current?.focus(); };
  useEffect(() => {
    if (tools) tray.current?.querySelector<HTMLElement>(tools === 'actors' ? 'input' : tools === 'filters' ? 'select' : 'button')?.focus();
  }, [tools]);

  const section = useRef<HTMLElement>(null);
  const [eventType, setEventType] = useState('all');
  useEffect(() => {
    if (props.loading || props.streamsAvailable !== false) return;
    setEventType(value => value.startsWith('stream_') ? 'all' : value);
    if (selected?.startsWith('stream:')) {
      setSelected(null); setSelectedLabel('');
      if (panel) close();
    }
  }, [props.loading, props.streamsAvailable, selected]);
  const [functionFilter, setFunctionFilter] = useState('');
  const [isolate, setIsolate] = useState(false);
  const [speed, setSpeed] = useState<Speed>(() => {
    try { const saved = localStorage.getItem('slimfaas.traffic.speed'); return saved && Object.keys(SPEEDS).includes(saved) ? saved as Speed : 'Fast'; }
    catch { return 'Fast'; }
  });
  const changeSpeed = (value: Speed) => { setSpeed(value); try { localStorage.setItem('slimfaas.traffic.speed', value); } catch { /* Storage can be disabled. */ } };
  const [page, setPage] = useState(0);
  const [journalPage, setJournalPage] = useState(0);
  const observedRef = useRef<ObservedFunction[]>([]);
  const nextObserved = observedFunctions(input.functions, input.activity);
  // Most receipts concern existing inventory: preserve the expensive topology in that case.
  if (JSON.stringify(nextObserved) !== JSON.stringify(observedRef.current)) observedRef.current = nextObserved;
  const observedActors = observedRef.current;
  const queues = useMemo(() => [...input.queues, ...jobQueues(input.jobs)], [input.queues, input.jobs]);
  const observedQueuesRef = useRef<string[]>([]);
  const nextObservedQueues = observedQueues(queues, input.activity);
  if (JSON.stringify(nextObservedQueues) !== JSON.stringify(observedQueuesRef.current)) observedQueuesRef.current = nextObservedQueues;
  const unknownQueues = observedQueuesRef.current;
  const topology = useMemo(() => {
    const withDeadLetters = new Set(input.deadLetters?.map(summary => summary.Function));
    return buildTopology(input.functions, input.jobs,
      queues.filter(q => isJobQueueName(q.Name) || q.Length > 0 || input.queuesWithActivity.has(q.Name) || withDeadLetters.has(q.Name)),
      input.slimFaasNodes, observedActors, unknownQueues, input.deadLetters, input.streams, input.dataSummary);
  },
  [input.functions, input.jobs, queues, input.queuesWithActivity, input.slimFaasNodes, observedActors, unknownQueues, input.deadLetters, input.streams, input.dataSummary]);
  const node = selected ? topology.byId.get(selected) : null;
  const nodes = useMemo(() => tools === 'actors' ? filterNodes(topology, search) : [], [topology, search, tools]);
  const nodePage = paginate(nodes, page);
  const activity = useMemo(() => input.activity.filter(e => matchesTraffic(topology, e, eventType, selected, isolate) &&
    (!functionFilter || e.QueueName === functionFilter || e.Source === functionFilter || e.Target === functionFilter)),
    [input.activity, eventType, topology, selected, isolate, functionFilter]);
  const selectedDeadLetters = props.deadLettersFrontEnabled !== false && props.deadLettersAccessible !== false && (node?.kind === 'queue' || node?.kind === 'function')
    ? props.deadLetters?.find(item => item.Function === node.label) : undefined;
  const liveFunction = node?.kind === 'function' && !node.parent ? props.functions.find(fn => fn.Name === node.label) : undefined;
  const workload = node?.kind === 'job' && !node.parent ? input.jobs.find(job => job.Name === node.label) : liveFunction;
  const wakeable = useMemo(() => new Set(props.functions.filter(canWake).map(fn => fn.Name)), [props.functions]);
  const journal = paginate(showJournal ? [...activity].reverse() : [], journalPage);
  const observed = input.activity.filter(e => e.ReceivedAt !== undefined && e.ReceivedAt > now - 1000).length;

  const choose = (id: string) => {
    setSelected(id); setSelectedLabel(topology.byId.get(id)?.label ?? id); setJournalPage(0);
    if (props.panelActor === undefined) setStoryPanel(id); else openActor(id);
  };
  const clearSelection = () => { if (panel) close(); setSelected(null); setIsolate(false); };
  const instancePresent = !node?.parent || (node.kind === 'function'
    ? props.functions.some(fn => fn.Name === topology.byId.get(node.parent!)?.label && fn.Pods?.some(pod => pod.Name === node.label))
    : node.kind === 'job'
      ? props.jobs.some(job => job.Name === topology.byId.get(node.parent!)?.label && job.RunningJobs.some(run => run.Name === node.label))
      : props.slimFaasNodes.some(replica => replica.Name === node.label));
  const logTarget = instancePresent && node?.parent && ['function', 'job', 'slimfaas'].includes(node.kind) ? {
    kind: node.kind as 'function' | 'job' | 'slimfaas', name: topology.byId.get(node.parent)!.label, replica: node.label,
  } : null;

  return <section ref={section} className="traffic" aria-label="Live traffic">
    <div className="traffic__bar">
      <h1 className="traffic__title">Live Stream</h1>
      <button className="button" aria-expanded={tools === 'actors'} onClick={event => toggle('actors', event.currentTarget)}>{showJournal ? 'Show actors' : 'Actors'}</button>
      <button className="button" aria-expanded={showJournal} onClick={event => toggle('journal', event.currentTarget)}>Event journal ({activity.length.toLocaleString()})</button>
      <button className="button" aria-expanded={tools === 'filters'} onClick={event => toggle('filters', event.currentTarget)}>Filters</button>
      <button className={`button ${frozen ? 'button--primary' : 'button--quiet'}`} type="button" onClick={() => { setNow(performance.now()); setFrozen(frozen ? null : props); setJournalPage(0); }}>{frozen ? 'Resume live' : 'Pause'}</button>
      <span className="traffic__stats">{topology.nodes.length.toLocaleString()} actors · {observed.toLocaleString()} events/s</span>
    </div>
    {tools === 'filters' && <div ref={tray} onKeyDown={event => { if (event.key === 'Escape') closeTools(); }} className="traffic__tray traffic__tray--filters" aria-label="Traffic filters">
    <div className="toolbar">
      <label className="field">Event type
        <select className="field__input" value={eventType} onChange={e => { setEventType(e.target.value); setJournalPage(0); }}>
          <option value="all">All events</option>
          {['data_request', 'data_access', 'request_in', 'enqueue', 'dequeue', 'request_out', 'response', 'event_publish', 'request_waiting', 'request_started', 'request_end', 'dead_lettered', 'dead_letter_requeued', 'dead_letter_deleted', 'dead_letter_expired', 'dead_letter_evicted',
            ...(props.streamsAvailable !== false ? ['stream_append', 'stream_delivery', 'stream_ack', 'stream_waiting', 'stream_retry', 'stream_failed', 'stream_deleted', 'stream_retention'] : [])].map(type => <option key={type} value={type}>{type.replace(/_/g, ' ')}</option>)}
        </select>
      </label>
      <label className="field">Function
        <select className="field__input" value={functionFilter} onChange={event => { setFunctionFilter(event.target.value); setJournalPage(0); }}>
          <option value="">All functions</option>{[...new Set([...input.functions.map(item => item.Name), ...(input.deadLetters ?? []).map(item => item.Function)])].sort().map(name => <option key={name}>{name}</option>)}
        </select>
      </label>
      <label className="field">Animation speed
        <select className="field__input" value={speed} onChange={e => changeSpeed(e.target.value as Speed)}>
          {Object.entries(SPEEDS).map(([label, duration]) => <option key={label} value={label}>{label} · {duration} ms</option>)}
        </select>
      </label>

    </div>
    <div className="traffic__caption">
      <span className={`badge ${frozen ? 'badge--warning' : 'badge--success'}`}>{frozen ? 'Paused' : 'Live'}</span>
      <span>{topology.nodes.length.toLocaleString()} actors · {observed.toLocaleString()} observed events/s</span>
      <span>Sampling: {((props.samplingRatio ?? 1) * 100).toLocaleString()}% · Stream limit: {props.maxLiveEventsPerSecond ? `${props.maxLiveEventsPerSecond.toLocaleString()} events/s/node` : 'none'}</span>
    </div>
    <div className="traffic__legend" aria-label="Message legend"><span className="traffic__legend-item traffic__legend-item--function">Requests</span><span className="traffic__legend-item traffic__legend-item--publication">Publications</span><span className="traffic__legend-item traffic__legend-item--queue">Queue messages</span><span className="traffic__legend-item traffic__legend-item--reply">Replies</span><span className="traffic__legend-item traffic__legend-item--data">Data access</span></div>
    <p className="traffic__note">Animation speed is independent of request latency. Delivery is best effort; the journal retains up to 5,000 received events.</p>
    {props.streamsTruncated && <p className="traffic__note">Stream snapshot is bounded. Open Streams in Views for the full inventory.</p>}
    </div>}
    {(eventType !== 'all' || functionFilter || (selected && isolate)) && <p className="traffic__filters" role="status">Filtered view: {eventType !== 'all' ? eventType.replace(/_/g, ' ') : 'all event types'}{functionFilter && ` · ${functionFilter}`}{selected && isolate ? ' · selected actor only' : ''}. <button className="table__link" onClick={() => { setEventType('all'); setFunctionFilter(''); setIsolate(false); }}>Clear filters</button></p>}
    <TrafficCanvas wakeable={wakeable} onWake={props.wakeUp} cooling={props.coolingDown} dataAvailable={props.dataSummary !== null} topology={topology} events={props.activity} visibleEvents={activity} activitySession={props.activitySession ?? 0} selected={selected} paused={!!frozen} onSelect={choose} speed={speed} eventType={eventType} isolate={isolate} />
    {selected && !props.loading && <div className="traffic__selection">
      <button className="table__link traffic__selected-name" type="button" aria-haspopup="dialog" onClick={() => choose(selected)}>{node?.label ?? selectedLabel} · Open details</button>
      <button className="button button--quiet" type="button" onClick={clearSelection}>Clear selection</button>
    </div>}
    {panel && !props.loading && !(props.streamsAvailable === false && panel.startsWith('stream:')) &&
      <TrafficDetails node={instancePresent ? node ?? null : null} title={node?.label ?? (panel === lastSelected ? selectedLabel : panel)}
        isolate={isolate} onIsolate={value => { setIsolate(value); setJournalPage(0); }} onClose={close} onClear={clearSelection}
        fallbackFocus={() => section.current?.querySelector('canvas') ?? null}
        logs={logTarget ? <InstanceLogs key={selected} target={logTarget} fill /> : undefined}>
        {node && <ActorInspector key={node.id} node={node} workload={workload} liveFunction={liveFunction}
          wakeUp={props.wakeUp} cooling={props.coolingDown?.has(node.label)} deadLetters={selectedDeadLetters}
          subscriptions={props.streamsAvailable === false ? undefined : props.streamSubscriptions ?? []}
          streamRevision={`${props.activitySession}/${props.streams?.map(item => `${item.Name}:${item.Generation}:${item.Revision}`).join(',')}`}
          stale={!!props.error} dataAvailable={props.dataSummary != null} actionError={props.actionError} />}
      </TrafficDetails>}
    {(tools === 'actors' || showJournal) && <div ref={tray} onKeyDown={event => { if (event.key === 'Escape') closeTools(); }} className="traffic__tray" aria-label={showJournal ? 'Event journal' : 'Actors'}>
      <div className="section-heading"><h2 className="section-heading__title">{showJournal ? 'Event journal' : 'Actors'}</h2>
        <button className="button" onClick={closeTools}>Close list</button></div>
    {showJournal ? <>
      <p className="traffic__note">Up to 5,000 received events. Pause to inspect a stable view. Select an actor to follow its connections.</p>
      <div className="table-wrap"><table className="table"><thead className="table__head"><tr><th className="table__th">Time</th><th className="table__th">Event</th><th className="table__th">Path</th><th className="table__th">Node</th></tr></thead>
        <tbody>{journal.items.map(e => <tr className="table__row" key={e.Id}><td className="table__td table__td--mono">{new Date(e.TimestampMs).toLocaleTimeString()}</td><td className="table__td">{e.DeadLetterId && e.QueueName ? <a href={deadLetterLink(e.QueueName, e.DeadLetterId)}>{e.Type.replace(/_/g, ' ')} · {e.DeadLetterId}</a> : e.Type.replace(/_/g, ' ')}{e.DataKind && ` · ${e.DataKind} / ${e.DataOperation}${e.DataStatusCode ? ` · HTTP ${e.DataStatusCode}` : ''}`}</td><td className="table__td table__td--path">{eventPath(topology, e).map(id => topology.byId.get(id)?.label ?? id).join(' → ')}</td><td className="table__td">{e.NodeId}</td></tr>)}</tbody></table></div>
      {activity.length === 0 && <p className="empty-state">Waiting for matching traffic.</p>}
      <Pagination page={journal.page} pages={journal.pages} total={activity.length} onPage={setJournalPage} />
    </> : <>
      <label className="field field--grow traffic__search">Find an actor
        <input className="field__input" type="search" value={search} placeholder="Function, job, replica or execution…" onChange={e => { setSearch(e.target.value); setPage(0); }} />
      </label>
      <div className="table-wrap"><table className="table"><thead className="table__head"><tr><th className="table__th">Actor</th><th className="table__th">Kind</th><th className="table__th">Status</th><th className="table__th">Details</th></tr></thead>
        <tbody>{nodePage.items.map(n => <tr className={`table__row ${selected === n.id ? 'table__row--selected' : ''}`} key={n.id}>
          <td className="table__td"><button className="table__link" type="button" onClick={() => choose(n.id)}>{n.label}</button></td><td className="table__td">{n.parent ? n.kind === 'job' ? 'Job execution' : 'Replica' : n.kind}</td><td className="table__td">{n.status}</td><td className="table__td table__td--mono">{n.detail}</td></tr>)}</tbody></table></div>
      {nodes.length === 0 && <p className="empty-state">No actors match your search.</p>}
      <Pagination page={nodePage.page} pages={nodePage.pages} total={nodes.length} onPage={setPage} />
    </>}
    </div>}
  </section>;
}
