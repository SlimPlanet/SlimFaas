import React, { useState } from 'react';
import type { FunctionStatusDetailed } from '../types';
import PodStatusList from './PodStatusList';

interface Props {
  functions: FunctionStatusDetailed[];
  onWakeUp: (name: string) => void;
  coolingDown?: Set<string>;
}

// Icône selon le type de pod
const POD_TYPE_ICON: Record<string, string> = {
  Deployment:  '🚀',
  StatefulSet: '🗄️',
  DaemonSet:   '⚙️',
};

const FunctionTable: React.FC<Props> = ({ functions, onWakeUp, coolingDown = new Set() }) => {
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const toggle = (name: string) =>
    setExpanded((prev) => ({ ...prev, [name]: !prev[name] }));

  return (
    <div className="function-table">
      <table className="function-table__table">
        <thead className="function-table__head">
          <tr>
            <th className="function-table__th">Name</th>
            <th className="function-table__th">Visibility</th>
            <th className="function-table__th">Scale</th>
            <th className="function-table__th">Resources</th>
            <th className="function-table__th">Actions</th>
          </tr>
        </thead>
        <tbody className="function-table__body">
          {functions.map((fn) => {
            const isDown = (fn.NumberReady ?? 0) === 0;
            const isExpanded = expanded[fn.Name] ?? false;
            const podIcon = POD_TYPE_ICON[fn.PodType ?? ''] ?? '📦';

            return (
              <React.Fragment key={fn.Name}>
                <tr className={`function-table__row ${isDown ? 'function-table__row--down' : 'function-table__row--up'}`}>

                  {/* ── Name + Type ── */}
                  <td className="function-table__td function-table__td--name">
                    <button
                      className="function-table__expand-btn"
                      onClick={() => toggle(fn.Name)}
                      title="Show pods"
                      type="button"
                    >
                      {isExpanded ? '▾' : '▸'}
                    </button>
                    <span className="function-table__fn-icon">{isDown ? '🔴' : '🟢'}</span>
                    <span className="function-table__fn-name">
                      {fn.Name}
                      <span className="function-table__fn-type" title={fn.PodType ?? ''}>
                        {podIcon}&nbsp;<span className="function-table__fn-type-label">{fn.PodType}</span>
                      </span>
                    </span>
                  </td>

                  {/* ── Visibility (global + trust + paths + events) ── */}
                  <td className="function-table__td">
                    {/* Global visibility badge */}
                    <span className={`function-table__badge function-table__badge--${(fn.Visibility ?? '').toLowerCase()}`}>
                      {fn.Visibility ?? '-'}
                    </span>

                    {/* Trust level */}
                    {fn.Trust ? (
                      <span className={`function-table__badge function-table__badge--${fn.Trust.toLowerCase()} function-table__badge--sm`}>
                        {fn.Trust === 'Untrusted' ? '🛡️' : '✅'}&nbsp;{fn.Trust}
                      </span>
                    ) : null}

                    {fn.PathsStartWithVisibility?.length ? (
                      <div className="function-table__vis-group">
                        <span className="function-table__vis-label">Paths</span>
                        {fn.PathsStartWithVisibility.map((p) => (
                          <span key={p.Path} className="function-table__vis-item">
                            <span className={`function-table__badge function-table__badge--${(p.Visibility ?? '').toLowerCase()} function-table__badge--sm`}>
                              {p.Visibility ?? '-'}
                            </span>
                            <code className="function-table__path-code">{p.Path}</code>
                          </span>
                        ))}
                      </div>
                    ) : null}

                    {fn.SubscribeEvents?.length ? (
                      <div className="function-table__vis-group">
                        <span className="function-table__vis-label">Events</span>
                        {fn.SubscribeEvents.map((e) => (
                          <span key={e.Name} className="function-table__vis-item">
                            <span className={`function-table__badge function-table__badge--${String(e.Visibility ?? '').toLowerCase()} function-table__badge--sm`}>
                              {e.Visibility === 0 ? 'Public' : (e.Visibility ?? '-')}
                            </span>
                            <span className="function-table__event-name">{e.Name}</span>
                          </span>
                        ))}
                      </div>
                    ) : null}
                  </td>

                  {/* ── Scale + Depends On ── */}
                  <td className="function-table__td function-table__td--scale">
                    <div className="function-table__scale-row">
                      <span className="function-table__scale-label">Replicas</span>
                      <span className="function-table__scale-val">
                        <strong>{fn.NumberReady ?? 0}</strong>/{fn.NumberRequested ?? 0}
                        <span className="function-table__replicas-info">
                          &nbsp;(min&nbsp;{fn.ReplicasMin ?? 0}, start&nbsp;{fn.ReplicasAtStart ?? 0})
                        </span>
                      </span>
                    </div>
                    <div className="function-table__scale-row">
                      <span className="function-table__scale-label">Scale&nbsp;down</span>
                      <span className="function-table__scale-val">{fn.TimeoutSecondBeforeSetReplicasMin}s</span>
                    </div>
                    <div className="function-table__scale-row">
                      <span className="function-table__scale-label">Parallel&nbsp;req</span>
                      <span className="function-table__scale-val">
                        {fn.NumberParallelRequest}&nbsp;max
                        <span className="function-table__replicas-info">
                          &nbsp;({fn.NumberParallelRequestPerPod}/pod)
                        </span>
                      </span>
                    </div>
                    {fn.Schedule?.Default?.WakeUp?.length ? (
                      <div className="function-table__scale-row">
                        <span className="function-table__scale-label">Schedule</span>
                        <span className="function-table__scale-val function-table__scale-val--mono">
                          {fn.Schedule.Default.WakeUp.join(', ')}
                        </span>
                      </div>
                    ) : null}
                    {fn.DependsOn?.length ? (
                      <div className="function-table__scale-row">
                        <span className="function-table__scale-label">Depends&nbsp;on</span>
                        <span className="function-table__scale-val">
                          {fn.DependsOn.map((dep) => (
                            <span key={dep} className="function-table__dep">{dep}</span>
                          ))}
                        </span>
                      </div>
                    ) : null}

                    {/* SlimFaas/Scale config */}
                    {fn.Scale?.ReplicaMax != null ? (
                      <div className="function-table__scale-row">
                        <span className="function-table__scale-label">Max&nbsp;replicas</span>
                        <span className="function-table__scale-val">{fn.Scale.ReplicaMax}</span>
                      </div>
                    ) : null}
                    {fn.Scale?.Triggers?.length ? (
                      <div className="function-table__scale-row function-table__scale-row--top">
                        <span className="function-table__scale-label">Triggers</span>
                        <span className="function-table__scale-val">
                          {fn.Scale.Triggers.map((t, i) => (
                            <span key={i} className="function-table__trigger-item">
                              <code className="function-table__trigger-code">{t.MetricName || t.Query}</code>
                              <span className="function-table__replicas-info">
                                {t.MetricType}&nbsp;≥&nbsp;{t.Threshold}
                              </span>
                            </span>
                          ))}
                        </span>
                      </div>
                    ) : null}
                    {fn.Scale?.Behavior ? (
                      <div className="function-table__scale-row function-table__scale-row--top">
                        <span className="function-table__scale-label">Behavior</span>
                        <span className="function-table__scale-val">
                          <span className="function-table__behavior-item">
                            <span className="function-table__replicas-info">▲ up stabilize {fn.Scale.Behavior.ScaleUp?.StabilizationWindowSeconds ?? 0}s</span>
                            {fn.Scale.Behavior.ScaleUp?.Policies?.map((p, i) => (
                              <span key={i} className="function-table__replicas-info">
                                {p.Type === 'Percent' ? `${p.Value}%` : `${p.Value} pods`}/{p.PeriodSeconds}s
                              </span>
                            ))}
                          </span>
                          <span className="function-table__behavior-item">
                            <span className="function-table__replicas-info">▼ down stabilize {fn.Scale.Behavior.ScaleDown?.StabilizationWindowSeconds ?? 0}s</span>
                            {fn.Scale.Behavior.ScaleDown?.Policies?.map((p, i) => (
                              <span key={i} className="function-table__replicas-info">
                                {p.Type === 'Percent' ? `${p.Value}%` : `${p.Value} pods`}/{p.PeriodSeconds}s
                              </span>
                            ))}
                          </span>
                        </span>
                      </div>
                    ) : null}
                  </td>

                  {/* ── Resources ── */}
                  <td className="function-table__td function-table__td--resources">
                    {fn.Resources ? (
                      <>
                        <div className="function-table__res-row">
                          <span className="function-table__scale-label">CPU</span>
                          <span className="function-table__scale-val">
                            {fn.Resources.CpuRequest ?? '-'}&nbsp;/&nbsp;{fn.Resources.CpuLimit ?? '-'}
                          </span>
                        </div>
                        <div className="function-table__res-row">
                          <span className="function-table__scale-label">Mem</span>
                          <span className="function-table__scale-val">
                            {fn.Resources.MemoryRequest ?? '-'}&nbsp;/&nbsp;{fn.Resources.MemoryLimit ?? '-'}
                          </span>
                        </div>
                      </>
                    ) : (
                      <span className="function-table__replicas-info">-</span>
                    )}
                  </td>

                  {/* ── Actions ── */}
                  <td className="function-table__td">
                    {isDown && (
                      <button
                        className={`function-table__wake-btn${coolingDown.has(fn.Name) ? ' function-table__wake-btn--cooling' : ''}`}
                        disabled={coolingDown.has(fn.Name)}
                        onClick={() => onWakeUp(fn.Name)}
                        type="button"
                      >
                        {coolingDown.has(fn.Name) ? '⏳' : '⚡ Wake Up'}
                      </button>
                    )}
                  </td>
                </tr>

                {isExpanded && (
                  <tr className="function-table__row function-table__row--pods">
                    <td className="function-table__td function-table__td--pods" colSpan={5}>
                      <PodStatusList pods={fn.Pods ?? []} />
                    </td>
                  </tr>
                )}
              </React.Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
};

export default FunctionTable;

