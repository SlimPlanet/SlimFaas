import { useEffect, useRef, useState } from 'react';
import { curveStepAfter, line, scaleLinear } from 'd3';
import type { FunctionStatusDetailed, ScaleConfig } from '../types.ts';
import { useScalingStream } from '../hooks/useScalingStream.ts';
import { createSimulationDraft, scalingLabel, scalingValue, simulationRequest,
  type ScalingDecision, type ScalingEvent, type ScalingSimulation, type SimulationDraft } from '../lib/scaling.ts';

const time = (value: number | null) => value == null ? 'Never' : new Date(value).toLocaleTimeString();

function Decision({ decision, title }: { decision: ScalingDecision; title: string }) {
  return <section className="scaling-decision" aria-label={title}>
    <div className="scaling-decision__heading"><h3>{title}</h3><span className="badge">{scalingLabel(decision.Action)}</span></div>
    <dl className="scaling-decision__steps">
      {([['Initial replicas', decision.CurrentReplicas], ['Raw metric target', decision.RawTarget],
        ['After policies', decision.PolicyTarget], ['After stabilization', decision.StabilizedTarget],
        ['Final target', decision.Target]] as const).map(([label, value]) =>
        <div className="scaling-decision__step" key={label}><dt>{label}</dt><dd>{scalingValue(value)}</dd></div>)}
    </dl>
    <p className="scaling-decision__application">{scalingLabel(decision.Application)}{decision.AcceptedReplicas !== null ? ` · ${decision.AcceptedReplicas} replicas accepted` : ''}</p>
    <ul className="scaling-decision__reasons">{decision.Reasons.map(reason => <li key={reason.Code}>{reason.Message}</li>)}</ul>
  </section>;
}

function HistoryChart({ events }: { events: ScalingEvent[] }) {
  const svg = useRef<SVGSVGElement | null>(null);
  const [width, setWidth] = useState(960);
  useEffect(() => {
    if (!svg.current) return;
    const observer = new ResizeObserver(entries => setWidth(Math.max(240, Math.round(entries[0].contentRect.width))));
    observer.observe(svg.current);
    return () => observer.disconnect();
  }, [events.length > 0]);
  if (!events.length) return null;
  const start = events[0].TimestampMs, end = Math.max(start + 1000, events[events.length - 1].TimestampMs);
  const x = scaleLinear().domain([start, end]).range([42, width - 42]);
  const top = Math.max(1, ...events.map(event => Math.max(event.CurrentReplicas, event.Target)));
  const y = scaleLinear().domain([0, top]).range([140, 20]);
  const path = (select: (event: ScalingEvent) => number) => line<ScalingEvent>()
    .x(event => x(event.TimestampMs)).y(event => y(select(event))).curve(curveStepAfter)(events) ?? '';
  return <figure className="scaling-history">
    <svg ref={svg} className="scaling-history__chart" viewBox={`0 0 ${width} 180`} role="img" aria-label="Requested replicas and final target over recent scaling decisions">
      <line className="scaling-history__axis" x1="42" y1="140" x2={width - 42} y2="140" />
      <text className="scaling-history__label" x="24" y="145">0</text><text className="scaling-history__label" x="12" y="25">{top}</text>
      <path className="scaling-history__requested" d={path(event => event.CurrentReplicas)} />
      <path className="scaling-history__target" d={path(event => event.Target)} />
      <circle className="scaling-history__point" cx={x(events[events.length - 1].TimestampMs)} cy={y(events[events.length - 1].Target)} r="3" />
      <text className="scaling-history__label" x="42" y="168">{time(start)}</text>
      <text className="scaling-history__label scaling-history__label--end" x={width - 42} y="168">{time(end)}</text>
    </svg>
    <figcaption className="scaling-history__legend"><span className="scaling-history__key scaling-history__key--target">Final target</span><span className="scaling-history__key scaling-history__key--requested">Requested replicas at decision time</span></figcaption>
  </figure>;
}

function Playground({ functionName, configuration, replicas }: { functionName: string; configuration: ScaleConfig | null; replicas: number }) {
  const [draft, setDraft] = useState(() => createSimulationDraft(configuration, replicas));
  const [result, setResult] = useState<ScalingSimulation | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [edited, setEdited] = useState(false);
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);
  const change = (update: Partial<SimulationDraft>) => { setDraft(value => ({ ...value, ...update })); setEdited(true); };
  const triggerChange = (index: number, update: Partial<SimulationDraft['Triggers'][number]>) =>
    change({ Triggers: draft.Triggers.map((trigger, i) => i === index ? { ...trigger, ...update } : trigger) });
  const simulate = async (event: React.FormEvent) => {
    event.preventDefault(); setError('');
    try {
      const request = simulationRequest(functionName, draft);
      abort.current?.abort(); const controller = new AbortController(); abort.current = controller; setBusy(true);
      const response = await fetch('/debug/scaling/simulate', { method: 'POST', signal: controller.signal,
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request) });
      const body = await response.json();
      if (!response.ok) throw new Error(body.Error || `Simulation unavailable (${response.status}).`);
      if (!controller.signal.aborted) { setResult(body); setEdited(false); }
    } catch (e) { if (!(e instanceof DOMException && e.name === 'AbortError')) setError(e instanceof Error ? e.message : 'Simulation failed.'); }
    finally { if (!abort.current?.signal.aborted) setBusy(false); }
  };
  return <section className="scaling-playground" aria-label="Scaling playground">
    <div className="scaling-explorer__section-heading"><div><h2>Playground</h2><p>Preview one decision using a copy of current observations and history.</p></div><span className="badge badge--success">Read only</span></div>
    <form onSubmit={simulate}>
      <div className="scaling-playground__fields">
        <label className="field">Initial replicas<input className="field__input" type="number" min="0" step="1" required value={draft.CurrentReplicas} onChange={e => change({ CurrentReplicas: e.target.value })} /></label>
        <label className="field">Replica maximum<input className="field__input" type="number" min="0" step="1" placeholder="No maximum" value={draft.ReplicaMax} onChange={e => change({ ReplicaMax: e.target.value })} /></label>
        <label className="scaling-playground__checkbox"><input type="checkbox" checked={draft.ScaleFromZero} onChange={e => change({ ScaleFromZero: e.target.checked })} />Allow external wake-up from zero</label>
      </div>
      {draft.Triggers.map((trigger, index) => <fieldset className="scaling-playground__trigger" key={index}>
        <legend>{trigger.MetricName || `Trigger ${index + 1}`}</legend>
        <div className="scaling-playground__fields">
          <label className="field">Source<select className="field__input" value={trigger.Source ?? ''} onChange={e => triggerChange(index, { Source: e.target.value || null })}>
            <option value="">Function / internal queues</option>
            {(configuration?.Sources ?? []).map(source => <option key={source.Name} value={source.Name}>{source.Name}</option>)}
            {trigger.Source && !configuration?.Sources?.some(source => source.Name === trigger.Source) && <option value={trigger.Source}>Unknown: {trigger.Source}</option>}
          </select></label>
          <label className="field">Metric type<select className="field__input" value={trigger.MetricType} onChange={e => triggerChange(index, { MetricType: e.target.value })}><option>AverageValue</option><option>Value</option></select></label>
          <label className="field">Threshold<input className="field__input" type="number" min="0" step="any" required value={Number.isFinite(trigger.Threshold) ? trigger.Threshold : ''} onChange={e => triggerChange(index, { Threshold: e.target.valueAsNumber })} /></label>
        </div>
        <label className="field">PromQL query<textarea className="field__input scaling-playground__code" rows={2} maxLength={2048} required value={trigger.Query} onChange={e => triggerChange(index, { Query: e.target.value })} /></label>
        <label className="field">Simulated value <span className="scaling-playground__hint">Leave empty to use collected data. Entering 0 simulates a valid zero.</span><input className="field__input" type="number" min="0" step="any" placeholder="Use collected value" value={trigger.FakeValue} onChange={e => triggerChange(index, { FakeValue: e.target.value })} /></label>
      </fieldset>)}
      {!draft.Triggers.length && <p className="notice">This function has no configured metric triggers. The preview uses its HTTP, schedule and dependency rules.</p>}
      <details className="scaling-playground__policies"><summary>Scaling policies and stabilization</summary><label className="field">Behavior JSON<textarea className="field__input scaling-playground__code" rows={12} value={draft.Behavior} onChange={e => change({ Behavior: e.target.value })} /></label></details>
      <div className="scaling-playground__actions"><button className="button button--primary" type="submit" disabled={busy}>{busy ? 'Simulating…' : 'Simulate next decision'}</button><button className="button button--quiet" type="button" disabled={busy} onClick={() => { setDraft(createSimulationDraft(configuration, replicas)); setEdited(true); setError(''); }}>Reset to current configuration</button></div>
      {error && <p className="notice notice--error" role="alert">{error}</p>}
    </form>
    {result && <div className="scaling-playground__result" aria-live="polite">
      <p className="scaling-playground__capture">Snapshot captured at {time(result.CapturedAtMs)}. {edited ? 'Inputs have changed since this preview.' : 'This result stays fixed until you simulate again.'}</p>
      <div className="scaling-playground__comparison"><Decision title="Current configuration" decision={result.Current} /><Decision title="Simulated scenario" decision={result.Simulated} /></div>
      <ul className="scaling-playground__signals">{result.Simulated.Triggers.map(trigger => <li key={trigger.Index}><strong>{trigger.Metric || `Trigger ${trigger.Index + 1}`}</strong>: {trigger.Simulated ? 'Simulated' : 'Collected'} value {scalingValue(trigger.Value)} → raw target {scalingValue(trigger.RawTarget)} · {scalingLabel(trigger.State)}{trigger.Detail ? ` — ${trigger.Detail}` : ''}</li>)}</ul>
      <ul className="scaling-playground__limits">{result.Limitations.map(limit => <li key={limit}>{limit}</li>)}</ul>
    </div>}
  </section>;
}

export default function ScalingExplorer({ functions, functionName }: { functions: FunctionStatusDetailed[]; functionName?: string }) {
  const selected = functionName ?? functions[0]?.Name ?? '';
  const workload = functions.find(f => f.Name === selected);
  const stream = useScalingStream(selected);
  const { state } = stream;
  const decision = state?.Decision;
  return <div className="scaling-explorer">
    <div className="scaling-explorer__toolbar"><label className="field field--grow">Function<select className="field__input" value={selected} onChange={e => { window.location.hash = `/live/scaling?function=${encodeURIComponent(e.target.value)}`; }}>
      {!functions.length && <option value="">No functions available</option>}
      {selected && !workload && <option value={selected}>Unavailable: {selected}</option>}
      {functions.map(fn => <option value={fn.Name} key={fn.Name}>{fn.Name}</option>)}
    </select></label><span className={`badge ${stream.status === 'Live' ? 'badge--success' : 'badge--warning'}`} role="status">{scalingLabel(stream.status)}</span><button className="button button--quiet" type="button" onClick={stream.retry}>Reconnect</button></div>
    {stream.status !== 'Live' && state && <p className="notice">Showing the last received decision. Live diagnostics are currently unavailable.</p>}
    {stream.sessionChanged && <p className="notice">The leader session changed. The recent history has restarted.</p>}
    {state?.Truncated && <p className="notice">Recent history is bounded; older or oversized details have been omitted.</p>}
    {decision && state ? <>
      <div className="metrics">{[['Ready replicas', state.ObservedReadyReplicas], ['Requested replicas', state.ObservedRequestedReplicas], ['Raw metric target', decision.RawTarget], ['Final target', decision.Target]].map(([label, value]) => <div className="metrics__card" key={String(label)}><span className="metrics__label">{label}</span><strong className="metrics__value">{scalingValue(value as number | null)}</strong></div>)}</div>
      <div className="scaling-explorer__columns"><div className="scaling-explorer__live">
        <Decision title={`Decision at ${time(decision.TimestampMs)}`} decision={decision} />
        <dl className="scaling-explorer__context"><div><dt>Last HTTP activity</dt><dd>{time(decision.LastHttpActivityMs)}</dd></div><div><dt>Last scheduled activity</dt><dd>{time(decision.LastScheduleActivityMs)}</dd></div><div><dt>Inactivity timeout remaining</dt><dd>{Math.ceil(decision.InactivityRemainingSeconds)} s</dd></div><div><dt>Dependencies</dt><dd>{decision.DependenciesReady ? 'Ready' : 'Waiting'}</dd></div></dl>
        <h2 className="scaling-explorer__heading">Signals</h2>
        {decision.Triggers.length ? decision.Triggers.map(trigger => <article className="scaling-signal" key={trigger.Index}>
          <div className="scaling-signal__heading"><h3>{trigger.Metric || `Trigger ${trigger.Index + 1}`}</h3><span className={`badge ${trigger.State === 'Valid' ? 'badge--success' : 'badge--warning'}`}>{scalingLabel(trigger.State)}</span></div>
          <p className="scaling-signal__source">{trigger.Source ?? 'Function / internal queues'} · {trigger.MetricType}</p><pre className="scaling-signal__query">{trigger.Query}</pre>
          <p>Value <strong>{scalingValue(trigger.Value)}</strong> · threshold <strong>{scalingValue(trigger.Threshold)}</strong> · raw target <strong>{scalingValue(trigger.RawTarget)}</strong></p>
          {trigger.State === 'NotEvaluated' && <p className="scaling-signal__source">This trigger was not evaluated in this cycle, including local triggers while the function sleeps.</p>}
        </article>) : <p className="empty-state">No metric triggers configured.</p>}
        {decision.Sources.length > 0 && <><h2 className="scaling-explorer__heading">External sources</h2><div className="table-wrap"><table className="table"><thead><tr><th className="table__th">Source</th><th className="table__th">State</th><th className="table__th">Last successful scrape</th></tr></thead><tbody>{decision.Sources.map(source => <tr className="table__row" key={source.Name}><td className="table__td">{source.Name}</td><td className="table__td">{scalingLabel(source.State)}</td><td className="table__td">{time(source.LastSuccessMs)}<span className="scaling-signal__source">Every {source.IntervalMs / 1000} s</span></td></tr>)}</tbody></table></div></>}
      </div><Playground key={selected} functionName={selected} configuration={state.Configuration} replicas={state.ObservedRequestedReplicas} /></div>
      <section className="scaling-explorer__history"><h2>Recent decisions</h2><HistoryChart events={state.Events} /><div className="table-wrap"><table className="table"><thead><tr><th className="table__th">Time</th><th className="table__th">Replicas</th><th className="table__th">Application</th><th className="table__th">Reasons / signals</th></tr></thead><tbody>{[...state.Events].reverse().map(event => <tr className="table__row" key={event.Id}><td className="table__td scaling-history__time">{time(event.TimestampMs)}</td><td className="table__td">{event.CurrentReplicas} → {event.Target}</td><td className="table__td">{scalingLabel(event.Application)}</td><td className="table__td">{event.Reasons.map(scalingLabel).join(' · ')}<span className="scaling-signal__source">{event.Signals.join(' · ')}</span></td></tr>)}</tbody></table></div></section>
    </> : <p className="empty-state">{selected ? 'Waiting for a scaling decision from the leader…' : 'Select a function to inspect its scaling.'}</p>}
  </div>;
}
