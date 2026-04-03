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
            <th className="function-table__th">Schedule</th>
            <th className="function-table__th">Events</th>
            <th className="function-table__th">Private Paths</th>
            <th className="function-table__th">Actions</th>
          </tr>
        </thead>
        <tbody className="function-table__body">
          {functions.map((fn) => {
            const isDown = fn.numberReady === 0;
            const isExpanded = expanded[fn.name] ?? false;

            return (
              <React.Fragment key={fn.name}>
                <tr
                  className={`function-table__row ${isDown ? 'function-table__row--down' : 'function-table__row--up'}`}
                >
                  <td className="function-table__td function-table__td--name">
                    <button
                      className="function-table__expand-btn"
                      onClick={() => toggle(fn.name)}
                      title="Show pods"
                      type="button"
                    >
                      {isExpanded ? '▾' : '▸'}
                    </button>
                    <span className="function-table__fn-icon">
                      {isDown ? '🔴' : '🟢'}
                    </span>
                    {fn.name}
                  </td>
                  <td className="function-table__td">
                    <span
                      className={`function-table__badge function-table__badge--${fn.visibility.toLowerCase()}`}
                    >
                      {fn.visibility}
                    </span>
                  </td>
                  <td className="function-table__td">{fn.podType}</td>
                  <td className="function-table__td">
                    <span className="function-table__replicas">
                      {fn.numberReady} / {fn.numberRequested}
                    </span>
                    <span className="function-table__replicas-info">
                      (min: {fn.replicasMin}, start: {fn.replicasAtStart})
                    </span>
                  </td>
                  <td className="function-table__td">
                    {fn.resources
                      ? `${fn.resources.cpuRequest ?? '-'} / ${fn.resources.cpuLimit ?? '-'}`
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {fn.resources
                      ? `${fn.resources.memoryRequest ?? '-'} / ${fn.resources.memoryLimit ?? '-'}`
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {fn.timeoutSecondBeforeSetReplicasMin}s
                  </td>
                  <td className="function-table__td">
                    {fn.schedule?.default?.wakeUp?.length
                      ? fn.schedule.default.wakeUp.join(', ')
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {fn.subscribeEvents?.length
                      ? fn.subscribeEvents.map((e) => (
                          <span
                            key={e.name}
                            className={`function-table__event function-table__event--${e.visibility.toLowerCase()}`}
                          >
                            {e.name}
                            <small>({e.visibility})</small>
                          </span>
                        ))
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {fn.pathsStartWithVisibility?.length
                      ? fn.pathsStartWithVisibility.map((p) => (
                          <span
                            key={p.path}
                            className={`function-table__path function-table__path--${p.visibility.toLowerCase()}`}
                          >
                            {p.path}
                            <small>({p.visibility})</small>
                          </span>
                        ))
                      : '-'}
                  </td>
                  <td className="function-table__td">
                    {isDown && (
                      <button
                        className="function-table__wake-btn"
                        onClick={() => onWakeUp(fn.name)}
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
                      colSpan={11}
                    >
                      <PodStatusList pods={fn.pods} />
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

