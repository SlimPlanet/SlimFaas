import { useEffect, useMemo, useState } from 'react';
import type { FunctionStatusDetailed, JobConfigurationStatus, QueueInfo, NetworkActivityEvent, SlimFaasNodeInfo } from '../types.ts';
import { buildTopology, eventPath, filterNodes, selectedEvent } from '../lib/topology.ts';
import { paginate } from '../lib/live.ts';
import TrafficCanvas from './TrafficCanvas';
import Pagination from './Pagination';

interface Props {
  functions: FunctionStatusDetailed[]; jobs: JobConfigurationStatus[]; queues: QueueInfo[];
  activity: NetworkActivityEvent[]; functionsWithQueueActivity: Set<string>;
  slimFaasReplicas: number; slimFaasNodes: SlimFaasNodeInfo[];
  samplingRatio?: number; maxLiveEventsPerSecond?: number;
}

export default function NetworkMap(props: Props) {
  const [frozen, setFrozen] = useState<Props | null>(null);
  const input = frozen ?? props;
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (frozen) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [frozen]);
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const [eventType, setEventType] = useState('all');
  const [page, setPage] = useState(0);
  const [journalPage, setJournalPage] = useState(0);
  const [showJournal, setShowJournal] = useState(false);
  const topology = useMemo(() => buildTopology(input.functions, input.jobs,
    input.queues.filter(q => q.Length > 0 || input.functionsWithQueueActivity.has(q.Name)), input.slimFaasNodes),
  [input.functions, input.jobs, input.queues, input.functionsWithQueueActivity, input.slimFaasNodes]);
  const node = selected ? topology.byId.get(selected) : null;
  const nodes = useMemo(() => filterNodes(topology, search), [topology, search]);
  const nodePage = paginate(nodes, page);
  const activity = useMemo(() => input.activity.filter(e => (eventType === 'all' || e.Type === eventType) && selectedEvent(topology, e, selected)),
    [input.activity, eventType, topology, selected]);
  const journal = paginate([...activity].reverse(), journalPage);
  const latest = frozen ? now : Math.max(now, Date.now());
  const observed = input.activity.filter(e => e.TimestampMs > latest - 1000).length;
  const sampled = (props.samplingRatio ?? 1) < 1 || (props.maxLiveEventsPerSecond ?? 0) > 0;
  const choose = (id: string) => { setSelected(id); setJournalPage(0); };

  return <section className="traffic" aria-label="Live traffic">
    <div className="toolbar">
      <label className="field field--grow">Find an actor
        <input className="field__input" type="search" value={search} placeholder="Function, job, replica or execution…" onChange={e => { setSearch(e.target.value); setPage(0); }} />
      </label>
      <label className="field">Event type
        <select className="field__input" value={eventType} onChange={e => { setEventType(e.target.value); setJournalPage(0); }}>
          <option value="all">All events</option>
          {['request_in', 'enqueue', 'dequeue', 'request_out', 'response', 'event_publish', 'request_waiting', 'request_started', 'request_end'].map(type => <option key={type} value={type}>{type.replace(/_/g, ' ')}</option>)}
        </select>
      </label>
      <button className={`button ${frozen ? 'button--primary' : 'button--quiet'}`} type="button" onClick={() => { setNow(Date.now()); setFrozen(frozen ? null : props); setJournalPage(0); }}>{frozen ? 'Resume live' : 'Pause'}</button>
    </div>
    <div className="traffic__caption">
      <span className={`badge ${frozen ? 'badge--warning' : 'badge--success'}`}>{frozen ? 'Paused' : 'Live'}</span>
      <span>{topology.nodes.length.toLocaleString()} actors · {observed.toLocaleString()} observed events/s</span>
      <span>{sampled ? 'Sampling / stream limits enabled' : 'Observed stream; delivery is best effort'}</span>
    </div>
    <TrafficCanvas topology={topology} events={activity} selected={selected} paused={!!frozen} onSelect={choose} />
    <div className="traffic__legend"><span className="traffic__legend-item traffic__legend-item--job">Jobs</span><span className="traffic__legend-item traffic__legend-item--function">Functions / SlimFaas</span><span className="traffic__legend-item traffic__legend-item--queue">Queues</span><span className="traffic__legend-item traffic__legend-item--external">External callers</span></div>
    {selected && <div className="traffic__selection" role="status">
      <div><strong>{node?.label ?? 'Actor no longer present'}</strong><p className="traffic__selection-detail">{node ? `${node.kind} · ${node.status} · ${node.detail}` : 'The latest snapshot no longer contains this instance.'}</p></div>
      <button className="button button--quiet" type="button" onClick={() => setSelected(null)}>Clear selection</button>
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
