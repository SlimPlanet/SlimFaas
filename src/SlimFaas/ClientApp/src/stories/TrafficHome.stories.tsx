import { useMemo, useState } from 'react';
import type { Meta, StoryObj } from '@storybook/react';
import { expect, userEvent, within } from 'storybook/test';
import Navbar from '../components/Navbar';
import NetworkMap from '../components/NetworkMap';
import { canWake, trafficHome } from '../lib/dashboard';
import { makeObservationFixture, makeReactiveFixtures, makeSpeechFunctions } from '../lib/fixtures';

const meta: Meta = { title: 'Dashboard/Traffic home', parameters: { layout: 'fullscreen' } };
export default meta;

function Home({ dense = false }: { dense?: boolean }) {
  const fixture = useMemo(makeReactiveFixtures, []);
  const [functions, setFunctions] = useState(() => {
    const samples = makeSpeechFunctions();
    if (!dense) return [...samples, ...fixture.functions];
    return [...samples, ...Array.from({ length: 1000 }, (_, index) => ({
      ...fixture.functions[1], Name: 'worker-' + String(index).padStart(4, '0'),
    }))];
  });
  const wake = (name?: string) => setFunctions(current => current.map(fn =>
    canWake(fn) && (!name || fn.Name === name) ? { ...fn, NumberRequested: 1 } : fn));
  return <div className="layout layout--traffic">
    <Navbar path={trafficHome} connected="Connected" canWakeAll={functions.some(canWake)} waking={false}
      wakeAll={() => wake()} deadLettersEnabled={false} streamsEnabled={false} />
    <main className="layout__content" id="main"><NetworkMap {...fixture} functions={functions} activity={[]} wakeUp={wake} /></main>
  </div>;
}
export const Desktop: StoryObj = { render: () => <Home /> };
export const ManyWorkloads: StoryObj = { name: '1,003 independently selectable workloads', render: () => <Home dense /> };
export const WakeSleeping: StoryObj = {
  render: () => <Home />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    await userEvent.click(canvas.getByRole('button', { name: 'Actors' }));
    await userEvent.click(canvas.getByRole('button', { name: 'image-resizer' }));
    const dialog = within(canvas.getByRole('dialog'));
    await userEvent.click(dialog.getByRole('button', { name: 'Wake up' }));
    await expect(dialog.getByText('Starting · 0 / 1 ready')).toBeVisible();
  },
};
export const MobileInfo: StoryObj = {
  globals: { viewport: { value: 'mobile1', isRotated: false } }, render: () => <Home />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    await userEvent.click(canvas.getByRole('button', { name: 'Actors' }));
    await userEvent.click(canvas.getByRole('button', { name: 'smartguide-api-text-to-speech-piper' }));
    await expect(canvas.getByRole('dialog')).toHaveTextContent('smartguide-api-text-to-speech-piper');
  },
};

function RollingDeployment() {
  const fixture = useMemo(() => makeObservationFixture(), []);
  return <div className="layout layout--traffic">
    <Navbar path={trafficHome} connected="Connected" canWakeAll={false} waking={false} wakeAll={() => {}} streamsEnabled={false} deadLettersEnabled={false} />
    <main className="layout__content"><NetworkMap {...fixture} activity={[]} /></main>
  </div>;
}
export const LifecycleAndRequests: StoryObj = {
  name: 'Rolling deployment and scheduled jobs',
  render: () => <RollingDeployment />,
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement);
    await expect(canvas.getByText('Requests · 2.8 CPU · 1.4 GiB RAM')).toBeVisible();
    await userEvent.click(canvas.getByRole('button', { name: 'Actors' }));
    await userEvent.click(canvas.getByRole('button', { name: /^daily-report$/ }));
    await expect(within(canvas.getByRole('dialog')).getByText(/^Next run · /)).toBeVisible();
  },
};
