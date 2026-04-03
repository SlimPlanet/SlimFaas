import React, { useState } from 'react';
import type { JobConfigurationStatus } from '../types';

interface Props {
  jobs: JobConfigurationStatus[];
}

function formatTimestamp(ts: number | null | undefined): string {
  if (!ts) return '-';
  return new Date(ts * 1000).toLocaleString();
}

const JobTable: React.FC<Props> = ({ jobs }) => {
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});

  const toggle = (name: string) =>
    setExpanded((prev) => ({ ...prev, [name]: !prev[name] }));

  return (
    <div className="job-table">
      <table className="job-table__table">
        <thead className="job-table__head">
          <tr>
            <th className="job-table__th">Name</th>
            <th className="job-table__th">Visibility</th>
            <th className="job-table__th">Image</th>
            <th className="job-table__th">Parallel Jobs</th>
            <th className="job-table__th">Running</th>
            <th className="job-table__th">Schedules</th>
            <th className="job-table__th">Depends On</th>
          </tr>
        </thead>
        <tbody className="job-table__body">
          {jobs.map((job) => {
            const isExpanded = expanded[job.name] ?? false;
            const runningCount = (job.runningJobs ?? []).length;
            const schedulesCount = (job.schedules ?? []).length;

            return (
              <React.Fragment key={job.name}>
                <tr className="job-table__row">
                  <td className="job-table__td job-table__td--name">
                    <button
                      className="job-table__expand-btn"
                      onClick={() => toggle(job.name)}
                      title="Show details"
                      type="button"
                    >
                      {isExpanded ? '▾' : '▸'}
                    </button>
                    <span className="job-table__icon">📋</span>
                    {job.name}
                  </td>
                  <td className="job-table__td">
                    <span
                      className={`job-table__badge job-table__badge--${(job.visibility ?? '').toLowerCase()}`}
                    >
                      {job.visibility ?? '-'}
                    </span>
                  </td>
                  <td className="job-table__td job-table__td--image">
                    {job.image ?? '-'}
                  </td>
                  <td className="job-table__td">{job.numberParallelJob}</td>
                  <td className="job-table__td">
                    <span
                      className={`job-table__running ${runningCount > 0 ? 'job-table__running--active' : ''}`}
                    >
                      {runningCount}
                    </span>
                  </td>
                  <td className="job-table__td">{schedulesCount}</td>
                  <td className="job-table__td">
                    {job.dependsOn?.length
                      ? job.dependsOn.map((dep) => (
                          <span key={dep} className="job-table__dep">
                            {dep}
                          </span>
                        ))
                      : '-'}
                  </td>
                </tr>
                {isExpanded && (
                  <tr className="job-table__row job-table__row--details">
                    <td className="job-table__td" colSpan={7}>
                      <div className="job-table__details">
                        {/* Running jobs */}
                        {runningCount > 0 && (
                          <div className="job-table__section">
                            <h4 className="job-table__section-title">
                              ⚙️ Running Jobs ({runningCount})
                            </h4>
                            <table className="job-table__sub-table">
                              <thead>
                                <tr>
                                  <th>Name</th>
                                  <th>Status</th>
                                  <th>Element</th>
                                  <th>Queued</th>
                                  <th>Started</th>
                                </tr>
                              </thead>
                              <tbody>
                                {(job.runningJobs ?? []).map((rj) => (
                                  <tr key={rj.elementId}>
                                    <td>{rj.name}</td>
                                    <td>{rj.status}</td>
                                    <td>{rj.elementId}</td>
                                    <td>{formatTimestamp(rj.inQueueTimestamp)}</td>
                                    <td>{formatTimestamp(rj.startTimestamp)}</td>
                                  </tr>
                                ))}
                              </tbody>
                            </table>
                          </div>
                        )}

                        {/* Schedules */}
                        {schedulesCount > 0 && (
                          <div className="job-table__section">
                            <h4 className="job-table__section-title">
                              🗓️ Schedules ({schedulesCount})
                            </h4>
                            <table className="job-table__sub-table">
                              <thead>
                                <tr>
                                  <th>ID</th>
                                  <th>Cron</th>
                                  <th>Image</th>
                                  <th>Next Execution</th>
                                </tr>
                              </thead>
                              <tbody>
                                {(job.schedules ?? []).map((s) => (
                                  <tr key={s.id}>
                                    <td>{s.id}</td>
                                    <td>
                                      <code>{s.schedule}</code>
                                    </td>
                                    <td>{s.image ?? '-'}</td>
                                    <td>{formatTimestamp(s.nextExecutionTimestamp)}</td>
                                  </tr>
                                ))}
                              </tbody>
                            </table>
                          </div>
                        )}

                        {runningCount === 0 && schedulesCount === 0 && (
                          <span className="job-table__empty">
                            No running jobs or schedules.
                          </span>
                        )}
                      </div>
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

export default JobTable;

