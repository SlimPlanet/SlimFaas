import { useId, type ReactNode } from 'react';
import type { MapNode } from '../lib/topology.ts';
import DetailPanel from './DetailPanel';

interface Props {
  node: MapNode | null;
  title: string;
  tab: 'details' | 'logs';
  onTab: (tab: 'details' | 'logs') => void;
  isolate: boolean;
  onIsolate: (isolate: boolean) => void;
  onClose: () => void;
  onClear: () => void;
  fallbackFocus?: () => HTMLElement | null;
  logs?: ReactNode;
}

export default function TrafficDetails({ node, title, tab, onTab, isolate, onIsolate, onClose, onClear, fallbackFocus, logs }: Props) {
  const id = useId();
  const activeTab = logs ? tab : 'details';
  return <DetailPanel title={title} eyebrow={node ? node.parent ? node.kind === 'job' ? 'Job execution' : node.kind === 'slimfaas' ? 'SlimFaas node' : 'Function replica' : node.kind : 'Instance unavailable'}
    onClose={onClose} fallbackFocus={fallbackFocus} fill>
    <div className="traffic-details__controls">
      <label className="traffic__isolate"><input type="checkbox" checked={isolate} onChange={event => onIsolate(event.target.checked)} /> Isolate selection</label>
      <button className="button button--quiet" type="button" onClick={onClear}>Clear selection</button>
    </div>
    {logs && <div className="traffic-details__tabs" role="tablist" aria-label="Instance details">
      {(['details', 'logs'] as const).map(value => <button key={value} id={`${id}-tab-${value}`} aria-controls={`${id}-panel-${value}`}
        type="button" role="tab" aria-selected={activeTab === value} tabIndex={activeTab === value ? 0 : -1}
        className={`button ${activeTab === value ? 'button--primary' : 'button--quiet'}`}
        onClick={() => onTab(value)} onKeyDown={event => {
          if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
            event.preventDefault();
            const next = event.key === 'Home' ? 'details' : event.key === 'End' ? 'logs' : value === 'details' ? 'logs' : 'details';
            onTab(next); document.getElementById(`${id}-tab-${next}`)?.focus();
          }
        }}>{value === 'details' ? 'Details' : 'Logs'}</button>)}
    </div>}
    <div className={`traffic-details__content${activeTab === 'logs' ? ' traffic-details__content--logs' : ''}`}
      role={logs ? 'tabpanel' : undefined} id={`${id}-panel-${activeTab}`} aria-labelledby={logs ? `${id}-tab-${activeTab}` : undefined}>
      {activeTab === 'logs' ? logs : node ? <>
        <p className="traffic-details__status">{node.status}{node.role ? ` · Raft role: ${node.role}` : ''}</p>
        <p className="traffic-details__description">{node.detail}</p>
        {logs && <p className="traffic__note">Traffic routes use the recorded destination. Select Logs to read this instance’s application output.</p>}
      </> : <p className="traffic-details__status" role="status">The latest snapshot no longer contains this instance. Logs are unavailable.</p>}
    </div>
  </DetailPanel>;
}
