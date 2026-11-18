import { useEffect, useState } from 'react';
import { useStatusStream } from './hooks/useStatusStream';
import Navbar from './components/Navbar';
import Footer from './components/Footer';
import FunctionTable from './components/FunctionTable';
import JobTable from './components/JobTable';
import NetworkMap from './components/NetworkMap';
import DataExplorer from './components/DataExplorer';
import ScalingExplorer from './components/ScalingExplorer';
import ErrorBoundary from './components/ErrorBoundary';
import { canWake, dashboardRoute } from './lib/dashboard';
import DeadLetters from './components/DeadLetters';
import Streams from './components/Streams';

export default function App() {
  const [route, setRoute] = useState(window.location.hash);
  useEffect(() => { const change = () => setRoute(window.location.hash); window.addEventListener('hashchange', change); return () => window.removeEventListener('hashchange', change); }, []);
  const { path, params } = dashboardRoute(route);
  const live = path.startsWith('#/live');
  const deadLetterPage = path === '#/dead-letters' || path.startsWith('#/dead-letters/');
  const streamPage = path === '#/streams' || path.startsWith('#/streams/');
  const data = path === '#/live/data';
  const scaling = path === '#/live/scaling';
  const traffic = live && !data && !scaling;
  const selectedFunction = params.get('function') ?? undefined;
  const stream = useStatusStream(traffic);
  const ready = stream.functions.reduce((sum, fn) => sum + fn.NumberReady, 0);
  const running = stream.jobs.reduce((sum, job) => sum + job.RunningJobs.filter(run => run.Status === 'Running').length, 0);
  return <div className={`layout${traffic ? ' layout--traffic' : ''}`}>
    <Navbar path={path} connected={stream.loading ? 'Connecting' : stream.error ? 'Reconnecting' : 'Connected'}
      canWakeAll={stream.frontEnabled && stream.functions.some(canWake)} waking={stream.wakeAllCooling} wakeAll={stream.wakeUpAll}
      streamsEnabled={stream.streamsAvailable} deadLettersEnabled={stream.deadLettersFrontEnabled && stream.deadLettersAccessible} />
    <main id="main" className="layout__content" tabIndex={-1}>
    {!traffic && <h1 className="page-heading__title">{streamPage ? 'Streams' : deadLetterPage ? 'Dead Letter Queues' : data ? 'Data' : scaling ? 'Scaling' : 'Overview'}</h1>}
    {stream.error && <p className="notice" role="status">{stream.error} Showing the last received status.</p>}
    {stream.actionError && <p className="notice notice--error" role="alert">{stream.actionError}</p>}
    {streamPage ? <ErrorBoundary>{!stream.streamsFrontEnabled ? <p className="notice">Streams UI is disabled. Enable SlimFaas:Streams:EnableFront and SlimFaas:EnableFront.</p> :
      !stream.streamsAccessible ? <p className="notice">Stream management is restricted for this caller.</p> :
      <Streams route={route} revision={`${stream.activitySession}/${stream.streams.map(item => `${item.Name}:${item.Generation}:${item.Revision}`).join(',')}`} stale={!!stream.error} />}</ErrorBoundary> : deadLetterPage ? <ErrorBoundary>{!stream.deadLettersFrontEnabled ? <p className="notice">Dead letter UI is disabled. Enable SlimFaas:DeadLetterQueue:EnableFront and SlimFaas:EnableFront.</p>
      : !stream.deadLettersAccessible ? <p className="notice">Dead letter queues are restricted for this caller.</p>
      : <DeadLetters route={route} revision={stream.deadLetters.reduce((total, item) => total + item.Revision, 0) + stream.activitySession} stale={!!stream.error} />}</ErrorBoundary> : !live ? <>
      <div className="metrics">{[['Functions', stream.functions.length], ['Ready replicas', ready], ['Running jobs', running], ['SlimFaas nodes', stream.slimFaasReplicas]].map(([label, value]) => <div className="metrics__card" key={label}><span className="metrics__label">{label}</span><strong className="metrics__value">{Number(value).toLocaleString()}</strong></div>)}</div>
      <FunctionTable functions={stream.functions} onWakeUp={stream.wakeUp} coolingDown={stream.coolingDown} deadLetters={stream.deadLetters} />
      <JobTable jobs={stream.jobs} />
    </> : <>
      <ErrorBoundary>{!stream.frontEnabled ? <p className="notice">{stream.frontMessage}</p> : data ? <DataExplorer key={params.get('kind')} /> : scaling ? <ScalingExplorer functions={stream.functions} functionName={selectedFunction} /> : <NetworkMap {...stream} panelActor={params.get('actor')} />}</ErrorBoundary>
    </>}
  </main>{!traffic && <Footer />}</div>;
}
