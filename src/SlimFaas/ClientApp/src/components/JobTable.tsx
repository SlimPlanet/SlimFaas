import { useState } from 'react';
import type { JobConfigurationStatus } from '../types.ts';
import { paginate } from '../lib/live.ts';
import Pagination from './Pagination';
import WorkloadDetails from './WorkloadDetails';
export default function JobTable({ jobs }: { jobs: JobConfigurationStatus[] }) {
  const [selected, setSelected] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(0);
  const matches = jobs.filter(job => job.Name.toLowerCase().includes(search.toLowerCase()));
  const paged = paginate(matches, page);
  const detail = jobs.find(job => job.Name === selected);
  return <section className="workload-table" aria-label="Jobs">
    <div className="section-heading"><div><h2 className="section-heading__title">Jobs</h2><p className="section-heading__description">Executions and schedules at a glance.</p></div><label className="field">Find a job<input className="field__input" type="search" value={search} placeholder="Search jobs…" onChange={e => { setSearch(e.target.value); setPage(0); }} /></label></div>
    <div className="table-wrap"><table className="table"><thead className="table__head"><tr><th className="table__th">Job</th><th className="table__th">Visibility</th><th className="table__th">Running</th><th className="table__th">Retained</th><th className="table__th">Schedules</th></tr></thead><tbody>{paged.items.map(job => <tr className="table__row" key={job.Name}><td className="table__td"><button className="table__link" type="button" onClick={() => setSelected(job.Name)}>{job.Name}</button><span className="workload-table__subtitle">{job.Image}</span></td><td className="table__td"><span className="badge badge--neutral">{job.Visibility}</span></td><td className="table__td">{job.RunningJobs.filter(run => run.Status === 'Running').length} / {job.NumberParallelJob}</td><td className="table__td">{job.RunningJobs.length}</td><td className="table__td">{job.Schedules.length}</td></tr>)}</tbody></table></div>
    {matches.length === 0 && <p className="empty-state">No matching job configurations.</p>}
    <Pagination page={paged.page} pages={paged.pages} total={matches.length} onPage={setPage} />
    {detail && <WorkloadDetails workload={detail} onClose={() => setSelected(null)} />}
  </section>;
}
