import React, { useState } from 'react';
import type { JobConfigurationStatus } from '../types';

function formatTimestamp(ts: number): string {
  if (ts <= 0) return '-';
  const d = new Date(ts > 1e12 ? ts / 10000 : ts * 1000);
  return d.toLocaleString();
}

function formatNextExec(ts: number | null): string {
  if (!ts || ts <= 0) return '-';
  const d = new Date(ts * 1000);
  return d.toLocaleString();
}

function timeUntil(ts: number | null): string {
  if (!ts || ts <= 0) return '';
  const now = Date.now() / 1000;
  const diff = ts - now;
  if (diff <= 0) return '(imminent)';
  const h = Math.floor(diff / 3600);
  const m = Math.floor((diff % 3600) / 60);
  if (h > 0) return `(in ${h}h ${m}m)`;
  return `(in ${m}m)`;
}

const statusIcon: Record<string, string> = {
  Running: '🟢',
  Pending: '🟡',
  Succeeded: '✅',
  Failed: '🔴',
  ImagePullBackOff: '🔴',
  Queued: '🔵',
};

interface Props {
  jobs: JobConfigurationStatus[];
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
            <th className="job-table__th">Parallel</th>
            <th className="job-table__th">CPU Req / Limit</th>
            <th className="job-table__th">Memory Req / Limit</th>
            <th className="job-table__th">Depends On</th>
            <th className="job-table__th">Schedules</th>
            <th className="job-table__th">Running</th>
          </tr>
        </thead>
        <tbody className="job-table__body">
          {jobs.map((job) => {
            const isExpanded = expanded[job.name] ?? false;
            const hasRunning = (job.runningJobs ?? []).length > 0;
            const hasSchedules = (job.schedules ?? []).length > 0;

            return (
              <React.Fragment key={job.name}>
                <tr
                  className={`job-table__row ${hasRunning ? 'job-table__row--active' : ''}`}
                >
                  <td className="job-table__td job-table__td--name">
                    <button
                      className="job-table__expand-btn"
                      onClick={() => toggle(job.name)}
                      title="Show details"
                      type="button"
                    >
                      {isExpanded ? '▾' : '▸'}
                    </button>
                    <span className="job-table__fn-icon">
                      {hasRunning ? '⚙️' : '📋'}
                    </span>
                    {job.name}
                  </td>
                  <td className="job-table__td">
                    <span
                      className={`job-table__badge job-table__badge--${(job.visibility ?? '').toLowerCase()}`}
                    >
                      {job.visibility ?? '-'}
                    </span>
                  </td>
                  <td className="job-table__td job-table__td--mono">
                    {job.image || '-'}
                  </td>
                  <td className="job-table__td">{job.numberParallelJob}</td>
                  <td className="job-table__td">
                    {job.resources
                      ? `${job.resources.requests?.cpu ?? '-'} / ${job.resources.limits?.cpu ?? '-'}`
                      : '-'}
                  </td>
                  <td className="job-table__td">
                    {job.resources
                      ? `${job.resources.requests?.memory ?? '-'} / ${job.resources.limits?.memory ?? '-'}`
                      : '-'}
                  </td>
                  <td className="job-table__td">
                    {job.dependsOn?.length
                      ? job.dependsOn.map((d) => (
                          <span key={d} className="job-table__dep">{d}</span>
                        ))
                      : '-'}
                  </td>
                  <td className="job-table__td">
                    {hasSchedules ? (
                      <span className="job-table__schedule-count">
                        🗓️ {(job.schedules ?? []).length}
                      </span>
                    ) : (
                      '-'
                    )}
                  </td>
                  <td className="job-table__td">
                    {hasRunning ? (
                      <span className="job-table__running-count">
                        ⚙️ {(job.runningJobs ?? []).length}
                      </span>
                    ) : (
                      <span className="job-table__idle">Idle</span>
                    )}
                  </td>
                </tr>
                {isExpanded && (
                  <tr className="job-table__row job-table__row--details">
                    <td className="job-table__td job-table__td--details" colSpan={9}>
                      {/* Schedules */}
                      {hasSchedules && (
                        <div className="job-table__section">
                          <h4 className="job-table__section-title">🗓️ Scheduled Jobs</h4>
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
                                  <td className="job-table__td--mono">{(s.id ?? '').substring(0, Math.min(8, (s.id ?? '').length))}…</td>
                                  <td><code>{s.schedule}</code></td>
                                  <td className="job-table__td--mono">{s.image || '-'}</td>
                                  <td>
                                    {formatNextExec(s.nextExecutionTimestamp)}{' '}
                                    <span className="job-table__time-until">
                                      {timeUntil(s.nextExecutionTimestamp)}
                                    </span>
                                  </td>
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        </div>
                      )}

                      {/* Running jobs */}
                      {hasRunning && (
                        <div className="job-table__section">
                          <h4 className="job-table__section-title">⚙️ Running Jobs</h4>
                          <table className="job-table__sub-table">
                            <thead>
                              <tr>
                                <th>Name</th>
                                <th>Status</th>
                                <th>Queued At</th>
                                <th>Started At</th>
                              </tr>
                            </thead>
                            <tbody>
                              {(job.runningJobs ?? []).map((rj) => (
                                <tr key={rj.elementId || rj.name}>
                                  <td className="job-table__td--mono">{rj.name}</td>
                                  <td>
                                    <span className="job-table__job-status">
                                      {statusIcon[rj.status] ?? '⚪'} {rj.status}
                                    </span>
                                  </td>
                                  <td>{formatTimestamp(rj.inQueueTimestamp)}</td>
                                  <td>{formatTimestamp(rj.startTimestamp)}</td>
                                </tr>
                              ))}
                            </tbody>
                          </table>
                        </div>
                      )}

                      {!hasSchedules && !hasRunning && (
                        <p className="job-table__empty">No schedules or running jobs.</p>
                      )}

                      {/* Whitelisted images */}
                      {(job.imagesWhitelist ?? []).length > 0 && (
                        <div className="job-table__section">
                          <h4 className="job-table__section-title">🐳 Whitelisted Images</h4>
                          <div className="job-table__image-list">
                            {(job.imagesWhitelist ?? []).map((img) => (
                              <span key={img} className="job-table__image-tag">{img}</span>
                            ))}
                          </div>
                        </div>
                      )}
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

