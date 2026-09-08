import { useEffect, useMemo, useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react';
import NetworkMap from '../components/NetworkMap';
import { DataInventory } from '../components/DataExplorer';
import { makeFixtures, fixtureEvents } from '../lib/fixtures.ts';
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
