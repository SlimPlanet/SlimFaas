import { useEffect, useState } from 'react';
import { useStatusStream } from './hooks/useStatusStream';
import Navbar from './components/Navbar';
import Footer from './components/Footer';
import FunctionTable from './components/FunctionTable';
import JobTable from './components/JobTable';
import NetworkMap from './components/NetworkMap';
import DataExplorer from './components/DataExplorer';
import ErrorBoundary from './components/ErrorBoundary';
import Icon from './components/Icon';

export default function App() {
  const [route, setRoute] = useState(window.location.hash || '#/overview');
  useEffect(() => { const change = () => setRoute(window.location.hash || '#/overview'); window.addEventListener('hashchange', change); return () => window.removeEventListener('hashchange', change); }, []);
  const live = route.startsWith('#/live');
  const data = route === '#/live/data';
  const stream = useStatusStream(live && !data);
  const ready = stream.functions.reduce((sum, fn) => sum + fn.NumberReady, 0);
  const running = stream.jobs.reduce((sum, job) => sum + job.RunningJobs.filter(run => run.Status === 'Running').length, 0);
  const allReady = stream.functions.length > 0 && stream.functions.every(fn => fn.NumberReady > 0);
  return <div className="layout"><Navbar live={live} /><main id="main" className="layout__content" tabIndex={-1}>
    <div className="page-heading"><div><span className="page-heading__eyebrow">YOUR INFRASTRUCTURE, IN FOCUS</span><h1 className="page-heading__title">{live ? 'Live Stream' : 'Overview'}</h1><p className="page-heading__description">{live ? 'Follow the traffic. Explore what is alive.' : 'A clear view of your functions, replicas and jobs.'}</p></div><span className={`badge ${stream.error ? 'badge--warning' : 'badge--success'}`}>{stream.loading ? 'Connecting' : stream.error ? 'Reconnecting' : 'Connected'}</span></div>
    {stream.error && <p className="notice" role="status">{stream.error} Showing the last received status.</p>}
    {stream.actionError && <p className="notice notice--error" role="alert">{stream.actionError}</p>}
    {!live ? <>
      <div className="metrics">{[['Functions', stream.functions.length], ['Ready replicas', ready], ['Running jobs', running], ['SlimFaas nodes', stream.slimFaasReplicas]].map(([label, value]) => <div className="metrics__card" key={label}><span className="metrics__label">{label}</span><strong className="metrics__value">{Number(value).toLocaleString()}</strong></div>)}</div>
      <div className="overview-actions"><a className="overview-actions__link" href="#/live/traffic">Explore live traffic <Icon name="arrow" /></a><button className="button button--primary" type="button" disabled={!stream.functions.length || allReady || stream.wakeAllCooling} onClick={stream.wakeUpAll}><Icon name="bolt" />{stream.wakeAllCooling ? 'Waking functions…' : 'Wake all functions'}</button></div>
      <FunctionTable functions={stream.functions} onWakeUp={stream.wakeUp} coolingDown={stream.coolingDown} />
      <JobTable jobs={stream.jobs} />
    </> : <>
      <nav className="live-tabs" aria-label="Live Stream views"><a className={`live-tabs__tab ${!data ? 'live-tabs__tab--active' : ''}`} href="#/live/traffic" aria-current={!data ? 'page' : undefined}><Icon name="activity" />Traffic</a><a className={`live-tabs__tab ${data ? 'live-tabs__tab--active' : ''}`} href="#/live/data" aria-current={data ? 'page' : undefined}><Icon name="data" />Data</a></nav>
      <ErrorBoundary>{!stream.frontEnabled ? <p className="notice">{stream.frontMessage}</p> : data ? <DataExplorer /> : <NetworkMap {...stream} activity={stream.activity} samplingRatio={stream.samplingRatio} maxLiveEventsPerSecond={stream.maxLiveEventsPerSecond} />}</ErrorBoundary>
    </>}
  </main><Footer /></div>;
}
