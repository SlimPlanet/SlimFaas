import { useState, type MouseEvent } from 'react';
import type { MapNode } from '../lib/topology';
import { isJobQueueName } from '../lib/topology';
import { canWake, isOrchestrated } from '../lib/dashboard';
import { streamLink, type StreamFunction } from '../lib/streams';
import { deadLetterLink, type DeadLetterSummary } from '../lib/deadLetters';
import type { FunctionStatusDetailed, JobConfigurationStatus } from '../types';
import { WorkloadInfo } from './WorkloadDetails';
import ScalingExplorer from './ScalingExplorer';
import DataExplorer from './DataExplorer';
import Streams from './Streams';
import DeadLetters from './DeadLetters';

interface Props {
  node: MapNode; workload?: FunctionStatusDetailed | JobConfigurationStatus;
  liveFunction?: FunctionStatusDetailed; wakeUp?: (name: string) => void; cooling?: boolean;
  deadLetters?: DeadLetterSummary; subscriptions?: StreamFunction[]; streamRevision: string;
  stale: boolean; dataAvailable: boolean; actionError?: string | null;
}

export default function ActorInspector({ node, workload, liveFunction, wakeUp, cooling, deadLetters, subscriptions,
  streamRevision, stale, dataAvailable, actionError }: Props) {
  const [scale, setScale] = useState(false);
  const [page, setPage] = useState(node.kind === 'stream' ? streamLink(node.label) : '');
  // Preserve ordinary anchor destinations for copying or opening in another tab.
  function follow(event: MouseEvent<HTMLAnchorElement>) {
    if (event.defaultPrevented || event.button || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    const href = event.currentTarget.getAttribute('href');
    if (href?.startsWith('#/streams/') || href?.startsWith('#/dead-letters/')) {
      event.preventDefault(); setPage(href);
    }
  }
  const streamActor = node.kind === 'stream';
  if (node.parent) return null;
  return <div className="traffic-details__inspector">
    {workload && <>
      {liveFunction && isOrchestrated(liveFunction) && <div className="traffic-details__actions">
        <button className="button" aria-pressed={!scale} onClick={() => setScale(false)}>Info</button>
        <button className="button" aria-pressed={scale} onClick={() => setScale(true)}>Scale</button>
        {wakeUp && canWake(liveFunction) && <button className="button" disabled={cooling} onClick={() => wakeUp(liveFunction.Name)}>{cooling ? 'Waking…' : 'Wake up'}</button>}
      </div>}
      {actionError && <p className="notice notice--error" role="alert">{actionError}</p>}
      {scale && liveFunction ? <ScalingExplorer functions={[liveFunction]} functionName={liveFunction.Name} fixed /> : <WorkloadInfo workload={workload} />}
    </>}
    {node.kind === 'queue' && <p>Destination · {isJobQueueName(node.label) ? node.label.slice(4) : node.label}</p>}
    {deadLetters && <><p>DLQ · {deadLetters.Count} retained · <a onClick={follow} href={deadLetterLink(deadLetters.Function)}>Open dead letters</a></p>
      {page.startsWith('#/dead-letters/') && <DeadLetters onNavigate={follow} route={page} revision={deadLetters.Revision} stale={stale} />}</>}
    {(node.kind === 'sets' || node.kind === 'files') && (dataAvailable
      ? <DataExplorer initialKind={node.kind} /> : <p role="status">Data metadata is unavailable.</p>)}
    {(subscriptions ?? []).filter(item => streamActor ? item.Stream === node.label : node.kind === 'function' && item.Function === node.label).map(item =>
      <p key={item.Stream + '/' + item.Function}><a onClick={follow} href={streamLink(item.Stream, 'functions', item.Function)}>{item.Stream} / {item.Function}</a> · lag {item.Lag} · pending {item.PendingCount} · callbacks {item.WaitingForCallbackCount} · retries {item.RetryScheduledCount} · failed {item.FailedCount} · gaps {item.RetentionGapCount}</p>)}
    {subscriptions && page.startsWith('#/streams/') && <Streams onNavigate={follow} route={page} revision={streamRevision} stale={stale} />}
  </div>;
}
