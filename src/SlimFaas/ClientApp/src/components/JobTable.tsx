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
            const isExpanded = expanded[job.Name] ?? false;
            const runningCount = (job.RunningJobs ?? []).length;
            const schedulesCount = (job.Schedules ?? []).length;

            return (
              <React.Fragment key={job.Name}>
                <tr className="job-table__row">
                  <td className="job-table__td job-table__td--name">
                    <button
                      className="job-table__expand-btn"
                      onClick={() => toggle(job.Name)}
                      title="Show details"
                      type="button"
                    >
                      {isExpanded ? '▾' : '▸'}
                    </button>
                    <span className="job-table__icon">📋</span>
                    {job.Name}
                  </td>
                  <td className="job-table__td">
                    <span
                      className={`job-table__badge job-table__badge--${(job.Visibility ?? '').toLowerCase()}`}
                    >
                      {job.Visibility ?? '-'}
                    </span>
                  </td>
                  <td className="job-table__td job-table__td--image">
                    {job.Image ?? '-'}
                  </td>
                  <td className="job-table__td">{job.NumberParallelJob}</td>
                  <td className="job-table__td">
                    <span
                      className={`job-table__running ${runningCount > 0 ? 'job-table__running--active' : ''}`}
                    >
                      {runningCount}
                    </span>
                  </td>
                  <td className="job-table__td">{schedulesCount}</td>
                  <td className="job-table__td">
                    {job.DependsOn?.length
                      ? job.DependsOn.map((dep) => (
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
                                {(job.RunningJobs ?? []).map((rj) => (
                                  <tr key={rj.ElementId}>
                                    <td>{rj.Name}</td>
                                    <td>{rj.Status}</td>
                                    <td>{rj.ElementId}</td>
                                    <td>{formatTimestamp(rj.InQueueTimestamp)}</td>
                                    <td>{formatTimestamp(rj.StartTimestamp)}</td>
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
                                {(job.Schedules ?? []).map((s) => (
                                  <tr key={s.Id}>
                                    <td>{s.Id}</td>
                                    <td>
                                      <code>{s.Schedule}</code>
                                    </td>
                                    <td>{s.Image ?? '-'}</td>
                                    <td>{formatTimestamp(s.NextExecutionTimestamp)}</td>
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

