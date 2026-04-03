import React from 'react';
import type { JobConfigurationStatus } from '../types';
import Tip from './Tip';
import { JOB } from '../tooltips';

interface Props {
  jobs: JobConfigurationStatus[];
}

function formatTimestamp(ts: number | null | undefined): string {
  if (!ts) return '-';
  return new Date(ts * 1000).toLocaleString();
}

const JobTable: React.FC<Props> = ({ jobs }) => {
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
            const runningJobs = job.RunningJobs ?? [];
            const runningCount = runningJobs.length;
            const schedules = job.Schedules ?? [];

            return (
              <React.Fragment key={job.Name}>
                <tr className={`job-table__row${runningCount > 0 ? ' job-table__row--active' : ''}`}>

                  {/* ── Name ── */}
                  <td className="job-table__td job-table__td--name">
                    <span className="job-table__fn-name">
                      <Tip text={JOB.name}><span>📋 {job.Name}</span></Tip>
                    </span>
                  </td>

                  {/* ── Visibility + Image ── */}
                  <td className="job-table__td">
                    <Tip text={JOB.visibility}>
                      <span className={`job-table__badge job-table__badge--${(job.Visibility ?? '').toLowerCase()}`}>
                        {job.Visibility ?? '-'}
                      </span>
                    </Tip>
                    {job.Image ? (
                      <div className="job-table__vis-group">
                        <Tip text={JOB.image}><span className="job-table__vis-label">Image</span></Tip>
                        <code className="job-table__image-code">{job.Image}</code>
                      </div>
                    ) : null}
                    {job.ImagesWhitelist?.length ? (
                      <div className="job-table__vis-group">
                        <Tip text={JOB.whitelist}><span className="job-table__vis-label">Whitelist</span></Tip>
                        {job.ImagesWhitelist.map((img) => (
                          <code key={img} className="job-table__image-code">{img}</code>
                        ))}
                      </div>
                    ) : null}
                  </td>

                  {/* ── Scale ── */}
                  <td className="job-table__td job-table__td--scale">
                    <div className="job-table__scale-row">
                      <Tip text={JOB.parallel}><span className="job-table__scale-label">Parallel</span></Tip>
                      <span className="job-table__scale-val">{job.NumberParallelJob}</span>
                    </div>

                    <div className="job-table__scale-row">
                      <Tip text={JOB.running}><span className="job-table__scale-label">Running</span></Tip>
                      <span className={`job-table__scale-val${runningCount > 0 ? ' job-table__scale-val--running' : ''}`}>
                        {runningCount > 0 ? `${runningCount} job${runningCount > 1 ? 's' : ''}` : 'Idle'}
                      </span>
                    </div>

                    {schedules.length > 0 ? (
                      <div className="job-table__scale-row job-table__scale-row--top">
                        <Tip text={JOB.schedules}><span className="job-table__scale-label">Schedules</span></Tip>
                        <span className="job-table__scale-val">
                          {schedules.map((s) => (
                            <span key={s.Id} className="job-table__schedule-item">
                              <code className="job-table__cron">{s.Schedule}</code>
                              <span className="job-table__scale-info">next: {formatTimestamp(s.NextExecutionTimestamp)}</span>
                            </span>
                          ))}
                        </span>
                      </div>
                    ) : null}

                    {job.DependsOn?.length ? (
                      <div className="job-table__scale-row">
                        <Tip text={JOB.dependsOn}><span className="job-table__scale-label">Depends&nbsp;on</span></Tip>
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
                          <Tip text={JOB.cpuResources}><span className="job-table__scale-label">CPU</span></Tip>
                          <span className="job-table__scale-val">
                            {job.Resources.Requests?.['cpu'] ?? '-'}&nbsp;/&nbsp;{job.Resources.Limits?.['cpu'] ?? '-'}
                          </span>
                        </div>
                        <div className="job-table__scale-row">
                          <Tip text={JOB.memResources}><span className="job-table__scale-label">Mem</span></Tip>
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

                {/* ── Running jobs always visible ── */}
                {runningCount > 0 && (
                  <tr className="job-table__row job-table__row--details">
                    <td className="job-table__td job-table__td--details" colSpan={4}>
                      <table className="job-table__sub-table">
                        <thead>
                          <tr><th>Name</th><th>Status</th><th>Element</th><th>Queued</th><th>Started</th></tr>
                        </thead>
                        <tbody>
                          {runningJobs.map((rj) => (
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

