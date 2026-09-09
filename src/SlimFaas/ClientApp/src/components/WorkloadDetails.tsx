import { useState } from 'react';
import type { FunctionStatusDetailed, JobConfigurationStatus } from '../types.ts';
import { paginate } from '../lib/live.ts';
import Pagination from './Pagination';
import DetailPanel from './DetailPanel';

export default function WorkloadDetails({ workload, onClose }: { workload: FunctionStatusDetailed | JobConfigurationStatus; onClose: () => void }) {
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(0);
  const isFunction = 'Pods' in workload;
  const instances = isFunction ? workload.Pods ?? [] : workload.RunningJobs;
  const filtered = instances.filter(instance => `${instance.Name} ${'ElementId' in instance ? instance.ElementId : instance.Identity} ${instance.Status}`.toLowerCase().includes(search.toLowerCase()));
  const paged = paginate(filtered, page);
  return <DetailPanel title={workload.Name} eyebrow={isFunction ? 'Function' : 'Job configuration'} onClose={onClose}>
      <h3 className="detail-panel__heading">{isFunction ? 'Replicas' : 'Executions'}</h3>
      <label className="field">Find an instance<input className="field__input" type="search" placeholder="Name, identity or status…" value={search} onChange={e => { setSearch(e.target.value); setPage(0); }} /></label>
      <div className="table-wrap"><table className="table"><thead className="table__head"><tr><th className="table__th">Name</th><th className="table__th">Status</th><th className="table__th">{isFunction ? 'Identifier' : 'Element'}</th></tr></thead><tbody>{paged.items.map(instance => <tr key={instance.Name} className="table__row"><td className="table__td table__td--mono">{instance.Name}</td><td className="table__td">{instance.Status}</td><td className="table__td table__td--mono">{'Identity' in instance ? instance.Identity : instance.ElementId}</td></tr>)}</tbody></table></div>
      {instances.length === 0 && <p className="empty-state">No instances in the current snapshot.</p>}
      <Pagination total={filtered.length} page={paged.page} pages={paged.pages} onPage={setPage} />
      <h3 className="detail-panel__heading">Configuration</h3>
      <dl className="detail-panel__properties">{Object.entries(workload).filter(([key]) => !['Pods', 'RunningJobs', 'Name'].includes(key)).map(([key, value]) => <div className="detail-panel__property" key={key}><dt className="detail-panel__term">{key.replace(/([a-z])([A-Z])/g, '$1 $2')}</dt><dd className="detail-panel__definition">{value === null ? 'Not configured' : typeof value === 'object' ? <pre className="detail-panel__code">{JSON.stringify(value, null, 2)}</pre> : String(value)}</dd></div>)}</dl>
  </DetailPanel>;
}
