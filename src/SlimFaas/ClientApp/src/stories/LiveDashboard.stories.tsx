import { useEffect, useMemo, useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react';
import NetworkMap from '../components/NetworkMap';
import { DataInventory } from '../components/DataExplorer';
import { makeFixtures, fixtureEvents, makeReactiveFixtures } from '../lib/fixtures.ts';
import { appendActivity } from '../lib/live.ts';
import type { NetworkActivityEvent } from '../types';
const meta: Meta = { title: 'Dashboard/Live', parameters: { layout: 'padded' } };
export default meta;
export const DenseTraffic: StoryObj = {
  name: '10,000 jobs + 10,000 replicas · 1,000 events per second',
  render: () => {
    const fixture = useMemo(() => makeFixtures(10000, 10000), []);
    const [snapshot, setSnapshot] = useState(fixture);
    const [activity, setActivity] = useState<NetworkActivityEvent[]>([]);
    useEffect(() => {
      let sequence = 0;
      const timer = setInterval(() => {
        const batch = fixtureEvents(sequence, 100); sequence += 100;
        setActivity(previous => appendActivity(previous, batch));
        if (sequence % 1000 === 0) setSnapshot(makeFixtures(10000, 10000));
      }, 100);
      return () => clearInterval(timer);
    }, []);
    return <NetworkMap {...snapshot} activity={activity} />;
  },
};
export const DataFiles: StoryObj = { render: () => {
  const now = Date.now();
  return <DataInventory now={now} changed={new Set(['daily-report.pdf'])} page={{ Kind: 'files', ServerTimeMs: now, NextCursor: null, TotalCount: 3,
    Summary: { Sets: 128, Files: 3, FileBytes: 2598297, UnknownFileSizes: 1, ExpiringSoon: 1 },
    Entries: [{ Id: 'daily-report.pdf', ExpiresAtMs: now + 28000, SizeBytes: 2500000 }, { Id: 'model-cache.bin', ExpiresAtMs: null, SizeBytes: 98297 }, { Id: 'pending-metadata', ExpiresAtMs: now + 86400000, SizeBytes: null }] }} />;
} };

export const ReactiveTraffic: StoryObj = {
  name: 'Power states, FIFO queues and publication fan-out',
  render: () => {
    const [snapshot, setSnapshot] = useState(makeReactiveFixtures);
    const [activity, setActivity] = useState<NetworkActivityEvent[]>([]);
    const [live, setLive] = useState(false);
    const sequence = useMemo(() => ({ value: 0 }), []);
    const send = (type: string) => {
      const n = sequence.value++;
      const base: NetworkActivityEvent = { Id: `reactive-${n}`, Type: type, Source: 'external', Target: 'fibonacci1', SourcePod: null, TargetPod: null, QueueName: null, NodeId: 'slimfaas-0', TimestampMs: Date.now() - 10000 };
      const events = type === 'event_publish'
        ? [base, ...snapshot.functions[0].Pods!.map(pod => ({ ...base, Id: `${base.Id}-${pod.Name}`, Source: 'slimfaas', TargetPod: pod.Name }))]
        : [{ ...base, TargetPod: snapshot.functions[0].Pods![n % 3].Name, QueueName: type === 'enqueue' || type === 'dequeue' ? 'fibonacci1' : null }];
      setActivity(previous => appendActivity(previous, events));
      if (type === 'enqueue' || type === 'dequeue') setSnapshot(previous => ({ ...previous, queues: [{ Name: 'fibonacci1', Length: type === 'enqueue' ? previous.queues[0].Length + 1 : 0 }] }));
    };
    useEffect(() => {
      if (!live) return;
      const timer = setInterval(() => send(['request_out', 'event_publish', 'enqueue', 'dequeue'][sequence.value % 4]), 350);
      return () => clearInterval(timer);
    }, [live, snapshot]);
    return <><div className="toolbar" aria-label="Synthetic event controls">
      <button className="button button--quiet" onClick={() => send('request_out')}>Send sync</button>
      <button className="button button--quiet" onClick={() => send('event_publish')}>Publish event</button>
      <button className="button button--quiet" onClick={() => send('enqueue')}>Enqueue</button>
      <button className="button button--quiet" onClick={() => send('dequeue')}>Drain queue</button>
      <button className="button button--primary" onClick={() => setLive(!live)}>{live ? 'Stop synthetic traffic' : 'Start synthetic traffic'}</button>
    </div><NetworkMap {...snapshot} activity={activity} /></>;
  },
};
