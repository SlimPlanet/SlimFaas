import { type ReactNode } from 'react';
import type { MapNode } from '../lib/topology.ts';
import DetailPanel from './DetailPanel';

interface Props {
  node: MapNode | null;
  title: string;
  isolate: boolean;
  onIsolate: (isolate: boolean) => void;
  onClose: () => void;
  onClear: () => void;
  fallbackFocus?: () => HTMLElement | null;
  logs?: ReactNode;
}

export default function TrafficDetails({ node, title, isolate, onIsolate, onClose, onClear, fallbackFocus, logs }: Props) {
  return <DetailPanel title={title} eyebrow={node ? node.parent ? node.kind === 'job' ? 'Job execution' : node.kind === 'slimfaas' ? 'SlimFaas node' : 'Function replica' : node.kind : 'Instance unavailable'}
    onClose={onClose} fallbackFocus={fallbackFocus} fill>
    <div className="traffic-details__controls">
      <label className="traffic__isolate"><input type="checkbox" checked={isolate} onChange={event => onIsolate(event.target.checked)} /> Isolate selection</label>
      <button className="button button--quiet" type="button" onClick={onClear}>Clear selection</button>
    </div>
    <section className="traffic-details__content" aria-label="Details">
      <h3 className="traffic-details__heading">Details</h3>
      {node ? <>
        <p className="traffic-details__status">{node.status}{node.role ? ` · Raft role: ${node.role}` : ''}</p>
        <p className="traffic-details__description">{node.detail}</p>
      </> : <p className="traffic-details__status" role="status">The latest snapshot no longer contains this instance. Logs are unavailable.</p>}
    </section>
    {logs && <section className="traffic-details__logs" aria-label="Logs">
      <h3 className="traffic-details__heading">Logs</h3>
      {logs}
    </section>}
  </DetailPanel>;
}
