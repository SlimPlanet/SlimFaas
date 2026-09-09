import { useEffect, useMemo, useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react';
import { expect, userEvent, within } from 'storybook/test';
import TrafficDetails from '../components/TrafficDetails';
import { LogViewer } from '../components/InstanceLogs';
import { LogBuffer } from '../lib/logs.ts';
import type { MapNode } from '../lib/topology.ts';

const meta: Meta = { title: 'Dashboard/Traffic selection', parameters: { layout: 'fullscreen' } };
export default meta;

const replica: MapNode = { id: 'replica-demo', parent: 'function-demo', label: 'fibonacci1-0', kind: 'function', status: 'Running', detail: 'Ready · opaque identity replica-demo', x: 0, y: 0 };

function DrawerExample({ detailsOnly = false, status = 'Live' }: { detailsOnly?: boolean; status?: string }) {
  const [open, setOpen] = useState(false);
  const [isolate, setIsolate] = useState(false);
  return <>
    <button className="button button--primary" onClick={() => setOpen(true)}>Open replica details</button>
    {open && <TrafficDetails node={detailsOnly ? { ...replica, parent: null, label: 'fibonacci1', detail: '3 ready / 3 requested replicas' } : replica}
      title={detailsOnly ? 'fibonacci1' : replica.label} isolate={isolate} onIsolate={setIsolate}
      onClose={() => setOpen(false)} onClear={() => { setOpen(false); setIsolate(false); }}
      logs={detailsOnly ? undefined : status === 'Live' ? <SampleLogs /> : <span className="badge badge--warning" role="status">{status}</span>} />}
  </>;
}

function SampleLogs() {
  const buffer = useMemo(() => {
    const value = new LogBuffer();
    value.append(Array.from({ length: 10000 }, (_, i) => ({ Id: i + 1, Text: `${i % 9 ? 'Information' : 'Warning'}: request ${i + 1} · fibonacci1-0`, TimestampMs: Date.now(), Truncated: false })));
    return value;
  }, []);
  const [lines, setLines] = useState(buffer.lines);
  useEffect(() => {
    let id = 10000;
    const interval = setInterval(() => {
      buffer.append([{ Id: ++id, Text: `${id % 9 ? 'Information' : 'Warning'}: live request ${id} · fibonacci1-0`, TimestampMs: Date.now(), Truncated: false }]);
      setLines(buffer.lines);
    }, 100);
    return () => clearInterval(interval);
  }, [buffer]);
  return <LogViewer lines={lines} discarded={buffer.discarded} fill />;
}

export const Details: StoryObj = { name: 'Group details', render: () => <DrawerExample detailsOnly /> };
export const LiveLogs: StoryObj = { name: 'Details and live logs · 10,000 retained lines', render: () => <DrawerExample /> };
export const Unavailable: StoryObj = { render: () => <DrawerExample status="Logs unavailable" /> };
export const Disabled: StoryObj = { render: () => <DrawerExample status="Disabled" /> };
export const HighlightedSearch: StoryObj = {
  render: () => <DrawerExample />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    await userEvent.click(canvas.getByRole('button', { name: 'Open replica details' }));
    await userEvent.type(canvas.getByRole('searchbox', { name: 'Find in logs' }), 'Warning');
    await expect(canvas.getAllByRole('listitem')[0]).toHaveClass('log-view__line--match');
  },
};
