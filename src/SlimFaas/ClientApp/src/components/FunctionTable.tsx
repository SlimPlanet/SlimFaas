import { useState } from 'react';
import type { FunctionStatusDetailed } from '../types.ts';
import { paginate } from '../lib/live.ts';
import Pagination from './Pagination';
import WorkloadDetails from './WorkloadDetails';
import Icon from './Icon';
interface Props { functions: FunctionStatusDetailed[]; onWakeUp: (name: string) => void; coolingDown?: Set<string> }
export default function FunctionTable({ functions, onWakeUp, coolingDown = new Set() }: Props) {
  const [selected, setSelected] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(0);
  const matches = functions.filter(fn => fn.Name.toLowerCase().includes(search.toLowerCase()));
  const paged = paginate(matches, page);
  const detail = functions.find(fn => fn.Name === selected);
  return <section className="workload-table" aria-label="Functions">
    <div className="section-heading"><div><h2 className="section-heading__title">Functions</h2><p className="section-heading__description">Your services, ready when they are needed.</p></div><label className="field">Find a function<input className="field__input" type="search" value={search} placeholder="Search functions…" onChange={e => { setSearch(e.target.value); setPage(0); }} /></label></div>
    <div className="table-wrap"><table className="table"><thead className="table__head"><tr><th className="table__th">Function</th><th className="table__th">Visibility</th><th className="table__th">Replicas ready</th><th className="table__th">Scale</th><th className="table__th">Action</th></tr></thead><tbody>{paged.items.map(fn => <tr className="table__row" key={fn.Name}>
      <td className="table__td"><button className="table__link" type="button" onClick={() => setSelected(fn.Name)}>{fn.Name}</button><span className="workload-table__subtitle">{fn.PodType}</span></td><td className="table__td"><span className="badge badge--neutral">{fn.Visibility}</span></td><td className="table__td"><span className={`badge ${fn.NumberReady ? 'badge--success' : 'badge--neutral'}`}>{fn.NumberReady} / {fn.NumberRequested}</span></td><td className="table__td">min {fn.ReplicasMin} · idle {fn.TimeoutSecondBeforeSetReplicasMin}s</td><td className="table__td"><button className="button button--quiet" type="button" disabled={fn.NumberReady > 0 || coolingDown.has(fn.Name)} onClick={() => onWakeUp(fn.Name)}><Icon name="bolt" />{coolingDown.has(fn.Name) ? 'Waking…' : fn.NumberReady ? 'Ready' : 'Wake up'}</button></td>
    </tr>)}</tbody></table></div>
    {matches.length === 0 && <p className="empty-state">No matching functions.</p>}
    <Pagination page={paged.page} pages={paged.pages} total={matches.length} onPage={setPage} />
    {detail && <WorkloadDetails workload={detail} onClose={() => setSelected(null)} />}
  </section>;
}
