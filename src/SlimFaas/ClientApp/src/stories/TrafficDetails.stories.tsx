import { useEffect, useMemo, useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react';
import TrafficDetails from '../components/TrafficDetails';
import { LogViewer } from '../components/InstanceLogs';
import { LogBuffer } from '../lib/logs.ts';
import type { MapNode } from '../lib/topology.ts';

const meta: Meta = { title: 'Dashboard/Traffic selection', parameters: { layout: 'fullscreen' } };
export default meta;

const replica: MapNode = { id: 'replica-demo', parent: 'function-demo', label: 'fibonacci1-0', kind: 'function', status: 'Running', detail: 'Ready · opaque identity replica-demo', x: 0, y: 0 };

function DrawerExample({ initialTab = 'details', unavailable = false }: { initialTab?: 'details' | 'logs'; unavailable?: boolean }) {
  const [open, setOpen] = useState(false);
  const [tab, setTab] = useState(initialTab);
  const [isolate, setIsolate] = useState(false);
  return <>
    <button className="button button--primary" onClick={() => setOpen(true)}>Open replica details</button>
    {open && <TrafficDetails node={replica} title={replica.label} tab={tab} onTab={setTab} isolate={isolate} onIsolate={setIsolate}
      onClose={() => setOpen(false)} onClear={() => { setOpen(false); setIsolate(false); }} logs={<SampleLogs unavailable={unavailable} />} />}
  </>;
}

function SampleLogs({ unavailable }: { unavailable: boolean }) {
  const buffer = useMemo(() => {
    const value = new LogBuffer();
    if (!unavailable) value.append(Array.from({ length: 10000 }, (_, i) => ({ Id: i + 1, Text: `${i % 9 ? 'Information' : 'Warning'}: request ${i + 1} · fibonacci1-0`, TimestampMs: Date.now(), Truncated: false })));
    return value;
  }, [unavailable]);
  const [lines, setLines] = useState(buffer.lines);
  useEffect(() => {
    if (unavailable) return;
    let id = 10000;
    const interval = setInterval(() => {
      buffer.append([{ Id: ++id, Text: `Information: live request ${id} · fibonacci1-0`, TimestampMs: Date.now(), Truncated: false }]);
      setLines(buffer.lines);
    }, 100);
    return () => clearInterval(interval);
  }, [buffer, unavailable]);
  return <LogViewer lines={lines} status={unavailable ? 'Logs unavailable' : 'Live'} discarded={buffer.discarded} fill />;
}

export const Details: StoryObj = { render: () => <DrawerExample /> };
export const LiveLogs: StoryObj = { name: 'Live logs · 10,000 retained lines', render: () => <DrawerExample initialTab="logs" /> };
export const Unavailable: StoryObj = { render: () => <DrawerExample initialTab="logs" unavailable /> };
