import React from 'react';
import type { FunctionStatusDetailed } from '../types';
import PodStatusList from './PodStatusList';
import Tip from './Tip';
import { FN } from '../tooltips';

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
  return (
    <div className="function-table">
      <table className="function-table__table">
        <thead className="function-table__head">
          <tr>
            <th className="function-table__th">Name</th>
            <th className="function-table__th">Visibility</th>
            <th className="function-table__th">Scale</th>
            <th className="function-table__th">Resources</th>
            <th className="function-table__th">Replicas</th>
          </tr>
        </thead>
        <tbody className="function-table__body">
          {functions.map((fn) => {
            const isDown = (fn.NumberReady ?? 0) === 0;
            const podIcon = POD_TYPE_ICON[fn.PodType ?? ''] ?? '📦';

            return (
              <tr key={fn.Name} className={`function-table__row ${isDown ? 'function-table__row--down' : 'function-table__row--up'}`}>

                {/* ── Name + Type + URLs ── */}
                <td className="function-table__td function-table__td--name">
                  <span className="function-table__fn-icon">{isDown ? '🔴' : '🟢'}</span>
                  <span className="function-table__fn-name">
                    <Tip text={`${FN.name}\n\n📡 /function/${fn.Name}/…\n${FN.syncUrl}\n\n📡 /async-function/${fn.Name}/…\n${FN.asyncUrl}`}>{fn.Name}</Tip>
                    <span className="function-table__fn-type" title={fn.PodType ?? ''}>
                      <Tip text={FN.podType}>{podIcon}&nbsp;{fn.PodType}</Tip>
                    </span>
                  </span>
                </td>

                {/* ── Visibility ── */}
                <td className="function-table__td">
                  <Tip text={FN.visibility}>
                    <span className={`function-table__badge function-table__badge--${(fn.Visibility ?? '').toLowerCase()}`}>
                      {fn.Visibility ?? '-'}
                    </span>
                  </Tip>

                  {fn.Trust ? (
                    <Tip text={FN.trust}>
                      <span className={`function-table__badge function-table__badge--${fn.Trust.toLowerCase()} function-table__badge--sm`}>
                        {fn.Trust === 'Untrusted' ? '🛡️' : '✅'}&nbsp;{fn.Trust}
                      </span>
                    </Tip>
                  ) : null}

                  {fn.PathsStartWithVisibility?.length ? (
                    <div className="function-table__vis-group">
                      <Tip text={FN.pathVisibility}><span className="function-table__vis-label">Paths</span></Tip>
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
                      <Tip text={FN.subscribeEvents}><span className="function-table__vis-label">Events</span></Tip>
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

                {/* ── Scale ── */}
                <td className="function-table__td function-table__td--scale">
                  <div className="function-table__scale-row">
                    <Tip text={FN.replicas}><span className="function-table__scale-label">Replicas</span></Tip>
                    <span className="function-table__scale-val">
                      <strong>{fn.NumberReady ?? 0}</strong>/{fn.NumberRequested ?? 0}
                      <span className="function-table__replicas-info">
                        &nbsp;(<Tip text={FN.replicasMin}>min&nbsp;{fn.ReplicasMin ?? 0}</Tip>,
                        <Tip text={FN.replicasAtStart}>&nbsp;start&nbsp;{fn.ReplicasAtStart ?? 0}</Tip>)
                      </span>
                    </span>
                  </div>
                  <div className="function-table__scale-row">
                    <Tip text={FN.scaleDown}><span className="function-table__scale-label">Scale&nbsp;down</span></Tip>
                    <span className="function-table__scale-val">{fn.TimeoutSecondBeforeSetReplicasMin}s</span>
                  </div>
                  <div className="function-table__scale-row">
                    <Tip text={FN.parallelReq}><span className="function-table__scale-label">Parallel&nbsp;req</span></Tip>
                    <span className="function-table__scale-val">
                      {fn.NumberParallelRequest}&nbsp;max
                      <span className="function-table__replicas-info">
                        &nbsp;(<Tip text={FN.parallelReqPerPod}>{fn.NumberParallelRequestPerPod}/pod</Tip>)
                      </span>
                    </span>
                  </div>
                  {fn.Schedule?.Default?.WakeUp?.length ? (
                    <div className="function-table__scale-row">
                      <Tip text={FN.schedule}><span className="function-table__scale-label">Schedule</span></Tip>
                      <span className="function-table__scale-val function-table__scale-val--mono">
                        {fn.Schedule.Default.WakeUp.join(', ')}
                      </span>
                    </div>
                  ) : null}
                  {fn.DependsOn?.length ? (
                    <div className="function-table__scale-row">
                      <Tip text={FN.dependsOn}><span className="function-table__scale-label">Depends&nbsp;on</span></Tip>
                      <span className="function-table__scale-val">
                        {fn.DependsOn.map((dep) => (
                          <span key={dep} className="function-table__dep">{dep}</span>
                        ))}
                      </span>
                    </div>
                  ) : null}

                  {fn.Scale?.ReplicaMax != null ? (
                    <div className="function-table__scale-row">
                      <Tip text={FN.maxReplicas}><span className="function-table__scale-label">Max&nbsp;replicas</span></Tip>
                      <span className="function-table__scale-val">{fn.Scale.ReplicaMax}</span>
                    </div>
                  ) : null}
                  {fn.Scale?.Triggers?.length ? (
                    <div className="function-table__scale-row function-table__scale-row--top">
                      <Tip text={FN.triggers}><span className="function-table__scale-label">Triggers</span></Tip>
                      <span className="function-table__scale-val">
                        {fn.Scale.Triggers.map((t, i) => (
                          <span key={i} className="function-table__trigger-item">
                            <code className="function-table__trigger-code">{t.MetricName || t.Query}</code>
                            <span className="function-table__replicas-info">{t.MetricType}&nbsp;≥&nbsp;{t.Threshold}</span>
                          </span>
                        ))}
                      </span>
                    </div>
                  ) : null}
                  {fn.Scale?.Behavior ? (
                    <div className="function-table__scale-row function-table__scale-row--top">
                      <Tip text={FN.behavior}><span className="function-table__scale-label">Behavior</span></Tip>
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
                        <Tip text={FN.cpuResources}><span className="function-table__scale-label">CPU</span></Tip>
                        <span className="function-table__scale-val">
                          {fn.Resources.CpuRequest ?? '-'}&nbsp;/&nbsp;{fn.Resources.CpuLimit ?? '-'}
                        </span>
                      </div>
                      <div className="function-table__res-row">
                        <Tip text={FN.memResources}><span className="function-table__scale-label">Mem</span></Tip>
                        <span className="function-table__scale-val">
                          {fn.Resources.MemoryRequest ?? '-'}&nbsp;/&nbsp;{fn.Resources.MemoryLimit ?? '-'}
                        </span>
                      </div>
                    </>
                  ) : (
                    <span className="function-table__replicas-info">-</span>
                  )}
                </td>

                {/* ── Replicas: wake-up button OR pod list ── */}
                <td className="function-table__td function-table__td--replicas">
                  {isDown ? (
                    <button
                      className={`function-table__wake-btn${coolingDown.has(fn.Name) ? ' function-table__wake-btn--cooling' : ''}`}
                      disabled={coolingDown.has(fn.Name)}
                      onClick={() => onWakeUp(fn.Name)}
                      type="button"
                    >
                      {coolingDown.has(fn.Name) ? '⏳' : '⚡ Wake Up'}
                    </button>
                  ) : (
                    <PodStatusList pods={fn.Pods ?? []} />
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
};

export default FunctionTable;

