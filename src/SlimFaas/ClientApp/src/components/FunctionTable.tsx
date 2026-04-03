import React, { useState } from 'react';
import type { FunctionStatusDetailed } from '../types';
import PodStatusList from './PodStatusList';

interface Props {
  functions: FunctionStatusDetailed[];
  onWakeUp: (name: string) => void;
}

const FunctionTable: React.FC<Props> = ({ functions, onWakeUp }) => {
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
            <th className="function-table__th">Type</th>
            <th className="function-table__th">Replicas</th>
            <th className="function-table__th">CPU Req / Limit</th>
            <th className="function-table__th">Memory Req / Limit</th>
            <th className="function-table__th">Scale Down (s)</th>
            <th className="function-table__th">Parallel Req</th>
            <th className="function-table__th">Schedule</th>
            <th className="function-table__th">Events</th>
            <th className="function-table__th">Private Paths</th>
            <th className="function-table__th">Depends On</th>
            <th className="function-table__th">Actions</th>
          </tr>
        </thead>
        <tbody className="function-table__body">
          {functions.map((fn) => {
            const isDown = fn.NumberReady === 0;
            const isExpanded = expanded[fn.Name] ?? false;

            return (
              <React.Fragment key={fn.Name}>
                <tr
                  className={`function-table__row ${isDown ? 'function-table__row--down' : 'function-table__row--up'}`}
                >
                  <td className="function-table__td function-table__td--name">
                    <button
                      className="function-table__expand-btn"
                      onClick={() => toggle(fn.Name)}
                      title="Show pods"
                      type="button"
                    >
                      {isExpanded ? '▾' : '▸'}
                    </button>
                    <span className="function-table__fn-icon">
                      {isDown ? '🔴' : '🟢'}
                    </span>
                    {fn.Name}
                  </td>
                  <td className="function-table__td">
                    <span
                      className={`function-table__badge function-table__badge--${(fn.Visibility ?? '').toLowerCase()}`}
                    >
                      {fn.Visibility ?? '-'}
                    </span>
                  </td>
                  <td className="function-table__td">{fn.PodType}</td>
                  <td className="function-table__td">
                    <span className="function-table__replicas">
                      {fn.NumberReady} / {fn.NumberRequested}
                    </span>
                    <span className="function-table__replicas-info">
                      (min: {fn.ReplicasMin}, start: {fn.ReplicasAtStart})
                    </span>
                  </td>
                  <td className="function-table__td">
                    {fn.Resources
                      ? `${fn.Resources.CpuRequest ?? '-'} / ${fn.Resources.CpuLimit ?? '-'}`
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {fn.Resources
                      ? `${fn.Resources.MemoryRequest ?? '-'} / ${fn.Resources.MemoryLimit ?? '-'}`
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {fn.TimeoutSecondBeforeSetReplicasMin}s
                  </td>
                  <td className="function-table__td">
                    <span className="function-table__replicas">
                      {fn.NumberParallelRequest}
                    </span>
                    <span className="function-table__replicas-info">
                      ({fn.NumberParallelRequestPerPod}/pod)
                    </span>
                  </td>
                  <td className="function-table__td">
                    {fn.Schedule?.Default?.WakeUp?.length
                      ? fn.Schedule.Default.WakeUp.join(', ')
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {fn.SubscribeEvents?.length
                      ? fn.SubscribeEvents.map((e) => (
                          <span
                            key={e.Name}
                            className={`function-table__event function-table__event--${(e.Visibility ?? '').toLowerCase()}`}
                          >
                            {e.Name}
                            <small>({e.Visibility ?? '-'})</small>
                          </span>
                        ))
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {fn.PathsStartWithVisibility?.length
                      ? fn.PathsStartWithVisibility.map((p) => (
                          <span
                            key={p.Path}
                            className={`function-table__path function-table__path--${(p.Visibility ?? '').toLowerCase()}`}
                          >
                            {p.Path}
                            <small>({p.Visibility ?? '-'})</small>
                          </span>
                        ))
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {fn.DependsOn?.length
                      ? fn.DependsOn.map((dep) => (
                          <span key={dep} className="function-table__dep">
                            {dep}
                          </span>
                        ))
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {isDown && (
                      <button
                        className="function-table__wake-btn"
                        onClick={() => onWakeUp(fn.Name)}
                        type="button"
                      >
                        ⚡ Wake Up
                      </button>
                    )}
                  </td>
                </tr>
                {isExpanded && (
                  <tr className="function-table__row function-table__row--pods">
                    <td
                      className="function-table__td function-table__td--pods"
                      colSpan={13}
                    >
                      <PodStatusList pods={fn.Pods} />
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

