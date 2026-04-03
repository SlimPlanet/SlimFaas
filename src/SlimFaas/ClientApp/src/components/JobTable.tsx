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
            <th className="job-table__th">Scale</th>
            <th className="job-table__th">Resources</th>
          </tr>
        </thead>
        <tbody className="job-table__body">
          {jobs.map((job) => {
            const isExpanded = expanded[job.Name] ?? false;
            const runningCount = (job.RunningJobs ?? []).length;
            const schedules = job.Schedules ?? [];

            return (
              <React.Fragment key={job.Name}>
                <tr className={`job-table__row${runningCount > 0 ? ' job-table__row--active' : ''}`}>

                  {/* ── Name ── */}
                  <td className="job-table__td job-table__td--name">
                    <button
                      className="job-table__expand-btn"
                      onClick={() => toggle(job.Name)}
                      title="Show running jobs"
                      type="button"
                    >
                      {isExpanded ? '▾' : '▸'}
                    </button>
                    <span className="job-table__fn-name">
                      <span>📋 {job.Name}</span>
                    </span>
                  </td>

                  {/* ── Visibility + Image ── */}
                  <td className="job-table__td">
                    <span className={`job-table__badge job-table__badge--${(job.Visibility ?? '').toLowerCase()}`}>
                      {job.Visibility ?? '-'}
                    </span>
                    {job.Image ? (
                      <div className="job-table__vis-group">
                        <span className="job-table__vis-label">Image</span>
                        <code className="job-table__image-code">{job.Image}</code>
                      </div>
                    ) : null}
                    {job.ImagesWhitelist?.length ? (
                      <div className="job-table__vis-group">
                        <span className="job-table__vis-label">Whitelist</span>
                        {job.ImagesWhitelist.map((img) => (
                          <code key={img} className="job-table__image-code">{img}</code>
                        ))}
                      </div>
                    ) : null}
                  </td>

                  {/* ── Scale : parallel + schedules + depends on + running ── */}
                  <td className="job-table__td job-table__td--scale">
                    {/* Parallel */}
                    <div className="job-table__scale-row">
                      <span className="job-table__scale-label">Parallel</span>
                      <span className="job-table__scale-val">{job.NumberParallelJob}</span>
                    </div>

                    {/* Running jobs */}
                    <div className="job-table__scale-row">
                      <span className="job-table__scale-label">Running</span>
                      <span className={`job-table__scale-val${runningCount > 0 ? ' job-table__scale-val--running' : ''}`}>
                        {runningCount > 0 ? `${runningCount} job${runningCount > 1 ? 's' : ''}` : 'Idle'}
                      </span>
                    </div>

                    {/* Schedules */}
                    {schedules.length > 0 ? (
                      <div className="job-table__scale-row job-table__scale-row--top">
                        <span className="job-table__scale-label">Schedules</span>
                        <span className="job-table__scale-val">
                          {schedules.map((s) => (
                            <span key={s.Id} className="job-table__schedule-item">
                              <code className="job-table__cron">{s.Schedule}</code>
                              <span className="job-table__scale-info">
                                next: {formatTimestamp(s.NextExecutionTimestamp)}
                              </span>
                            </span>
                          ))}
                        </span>
                      </div>
                    ) : null}

                    {/* Depends on */}
                    {job.DependsOn?.length ? (
                      <div className="job-table__scale-row">
                        <span className="job-table__scale-label">Depends&nbsp;on</span>
                        <span className="job-table__scale-val">
                          {job.DependsOn.map((dep) => (
                            <span key={dep} className="job-table__dep">{dep}</span>
                          ))}
                        </span>
                      </div>
                    ) : null}
                  </td>

                  {/* ── Resources ── */}
                  <td className="job-table__td job-table__td--resources">
                    {job.Resources ? (
                      <>
                        <div className="job-table__scale-row">
                          <span className="job-table__scale-label">CPU</span>
                          <span className="job-table__scale-val">
                            {job.Resources.Requests?.['cpu'] ?? '-'}&nbsp;/&nbsp;{job.Resources.Limits?.['cpu'] ?? '-'}
                          </span>
                        </div>
                        <div className="job-table__scale-row">
                          <span className="job-table__scale-label">Mem</span>
                          <span className="job-table__scale-val">
                            {job.Resources.Requests?.['memory'] ?? '-'}&nbsp;/&nbsp;{job.Resources.Limits?.['memory'] ?? '-'}
                          </span>
                        </div>
                      </>
                    ) : (
                      <span className="job-table__scale-info">-</span>
                    )}
                  </td>
                </tr>

                {/* ── Expanded: running jobs detail ── */}
                {isExpanded && runningCount > 0 && (
                  <tr className="job-table__row job-table__row--details">
                    <td className="job-table__td job-table__td--details" colSpan={4}>
                      <div className="job-table__section">
                        <h4 className="job-table__section-title">⚙️ Running Jobs ({runningCount})</h4>
                        <table className="job-table__sub-table">
                          <thead>
                            <tr><th>Name</th><th>Status</th><th>Element</th><th>Queued</th><th>Started</th></tr>
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
                    </td>
                  </tr>
                )}
                {isExpanded && runningCount === 0 && (
                  <tr className="job-table__row job-table__row--details">
                    <td className="job-table__td job-table__td--details" colSpan={4}>
                      <span className="job-table__empty">No running jobs.</span>
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

