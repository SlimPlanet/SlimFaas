import { functionState } from './live.ts';
import type { FunctionStatusDetailed, JobConfigurationStatus, NetworkActivityEvent, QueueInfo, SlimFaasNodeInfo } from '../types.ts';

export interface MapNode {
  id: string; label: string; kind: 'function' | 'job' | 'slimfaas' | 'queue' | 'external';
  role?: SlimFaasNodeInfo['Role'];
  state?: ReturnType<typeof functionState>; parent: string | null; x: number; y: number; status: string; detail: string;
}
export interface MapGroup extends MapNode { width: number; height: number; children: MapNode[] }
export interface Topology {
  groups: MapGroup[]; nodes: MapNode[]; byId: Map<string, MapNode>;
  actors: Map<string, string>; pods: Map<string, string>; sourcePods: Map<string, string>;
  width: number; height: number;
}
const CELL = 34;
const GAP = 90;
export const nodeId = (kind: string, name: string) => `${kind}:${name}`;

export interface ObservedFunction { name: string; replicas: string[] }
/** Event-only actors keep an honest unknown inventory state until a snapshot supplies it. */
export function observedFunctions(functions: FunctionStatusDetailed[], events: NetworkActivityEvent[]): ObservedFunction[] {
  const known = new Set(functions.map(fn => fn.Name));
  const observed = new Map<string, Set<string>>();
  for (const event of events) {
    if (!event.Target || known.has(event.Target) || ['slimfaas', 'external'].includes(event.Target)) continue;
    const replicas = observed.get(event.Target) ?? new Set<string>();
    if (event.TargetPod) replicas.add(event.TargetPod);
    observed.set(event.Target, replicas);
  }
  return [...observed].sort(([a], [b]) => a.localeCompare(b)).map(([name, replicas]) => ({ name, replicas: [...replicas].sort() }));
}

export function observedQueues(queues: QueueInfo[], events: NetworkActivityEvent[]): string[] {
  const known = new Set(queues.map(queue => queue.Name));
  return [...new Set(events.flatMap(event => event.QueueName && !known.has(event.QueueName) ? [event.QueueName] : []))].sort();
}

export function buildTopology(functions: FunctionStatusDetailed[], jobs: JobConfigurationStatus[], queues: QueueInfo[], slimNodes: SlimFaasNodeInfo[], observed: ObservedFunction[] = [], unknownQueues: string[] = []): Topology {
  const groups: MapGroup[] = [];
  const actors = new Map<string, string>(), pods = new Map<string, string>(), sourcePods = new Map<string, string>();
  const create = (kind: MapNode['kind'], label: string, status: string, children: Omit<MapNode, 'x' | 'y' | 'parent' | 'kind'>[], detail = '') => {
    const columns = Math.max(1, Math.ceil(Math.sqrt(children.length)));
    const group: MapGroup = { id: nodeId(kind, label), label, kind, parent: null, x: 0, y: 0, status, detail,
      width: Math.max(260, columns * CELL + 60), height: Math.max(150, Math.ceil(children.length / columns) * CELL + 160), children: [] };
    group.children = children.map((child, i) => ({ ...child, kind, parent: group.id,
      x: 30 + i % columns * CELL, y: 125 + Math.floor(i / columns) * CELL }));
    groups.push(group);
    if (kind !== 'queue') actors.set(label, group.id);
    return group;
  };
  for (const job of [...jobs].sort((a, b) => a.Name.localeCompare(b.Name))) {
    const runs = [...job.RunningJobs].sort((a, b) => a.Name.localeCompare(b.Name));
    const group = create('job', job.Name, `${runs.filter(r => r.Status === 'Running').length} running`, runs.map(r => ({
      id: nodeId('run', r.Name), label: r.Name, status: r.Status, detail: r.ElementId,
    })), job.Image);
    for (const child of group.children) sourcePods.set(child.label, child.id);
  }
  create('external', 'external', 'People and external systems', [], 'External callers');
  create('slimfaas', 'slimfaas', `${slimNodes.length} nodes · ${slimNodes.find(n => n.Role === 'Leader')?.Name ?? 'Unknown'} leader`, slimNodes.map(n => ({
    id: nodeId('node', n.Name), label: n.Name, status: `${n.Status} · ${n.Role ?? 'Unknown'}`, detail: 'SlimFaas node', role: n.Role,
  })));
  for (const fn of [...functions].sort((a, b) => a.Name.localeCompare(b.Name))) {
    const fnPods = [...(fn.Pods ?? [])].sort((a, b) => a.Name.localeCompare(b.Name));
    const group = create('function', fn.Name, `${functionState(fn)} · ${fn.NumberReady} / ${fn.NumberRequested} ready`, fnPods.map(p => {
      const id = nodeId('pod', `${fn.Name}/${p.Name}`);
      pods.set(`${fn.Name}/${p.Identity}`, id); pods.set(`${fn.Name}/${p.Name}`, id);
      sourcePods.set(p.Name, id);
      if (p.Identity) sourcePods.set(p.Identity, id);
      return { id, label: p.Name, status: p.Ready ? 'Running' : p.Status, detail: p.Identity };
    }), fn.PodType);
    group.state = functionState(fn);
  }
  for (const fn of observed) {
    const group = create('function', fn.name, 'Observed traffic · inventory unknown', fn.replicas.map(identity => {
      const id = nodeId('observed', `${fn.name}/${identity}`);
      pods.set(`${fn.name}/${identity}`, id); sourcePods.set(identity, id);
      return { id, label: `Replica ${identity.slice(-8)}`, status: 'Observed traffic', detail: identity };
    }), 'Seen in activity; absent from this node’s current inventory');
    actors.set(fn.name, group.id);
  }
  for (const q of [...queues].sort((a, b) => a.Name.localeCompare(b.Name))) create('queue', q.Name, `${q.Length} queued`, []);
  for (const name of unknownQueues) create('queue', name, 'Queue length unknown', [], 'Seen in traffic; absent from this node’s inventory');

  // Reserve readable hub space even when neighboring workloads have thousands of instances.
  const hubWidth = Math.max(260, Math.min(1200, Math.max(...groups.map(g => g.width)) / 3));
  for (const g of groups) if (['external', 'slimfaas', 'queue'].includes(g.kind)) {
    g.width = Math.max(g.width, hubWidth); g.height = Math.max(g.height, hubWidth * 0.65);
  }
  const regions: { groups: MapGroup[]; height: number }[] = [];
  // Each kind has its own packed region. Group rectangles reserve all instance space.
  let regionX = 0, height = 0;
  for (const kind of ['job', 'external', 'slimfaas', 'queue', 'function']) {
    const region = groups.filter(g => g.kind === kind);
    if (!region.length) continue;
    const cols = Math.ceil(Math.sqrt(region.length));
    const cellW = Math.max(...region.map(g => g.width)) + GAP;
    const cellH = Math.max(...region.map(g => g.height)) + GAP;
    region.forEach((g, i) => {
      g.x = regionX + i % cols * cellW; g.y = Math.floor(i / cols) * cellH;
      for (const child of g.children) { child.x += g.x; child.y += g.y; }
    });
    regionX += cols * cellW;
    const regionHeight = Math.ceil(region.length / cols) * cellH;
    height = Math.max(height, regionHeight);
    regions.push({ groups: region, height: regionHeight });
  }
  height += hubWidth * 0.65 + GAP;
  for (const region of regions) for (const group of region.groups) {
    const offset = group.kind === 'external' ? 0 : group.kind === 'queue' ? height - region.height : (height - region.height) / 2;
    group.y += offset;
    for (const child of group.children) child.y += offset;
  }
  const nodes = groups.flatMap(g => [g, ...g.children]);
  return { groups, nodes, byId: new Map(nodes.map(n => [n.id, n])), actors, pods, sourcePods, width: regionX, height };
}

export function resolveSource(topology: Topology, actor: string, pod: string | null): string {
  if (pod) {
    const resolved = topology.sourcePods.get(pod);
    if (resolved) return resolved;
    const separator = pod.lastIndexOf('-slimfaas-job-');
    const jobId = nodeId('job', pod.slice(0, separator));
    if (separator > 0 && topology.byId.has(jobId)) return jobId;
  }
  return topology.actors.get(actor) ?? 'external:external';
}

export function eventPath(topology: Topology, event: NetworkActivityEvent): string[] {
  const slim = topology.byId.has(nodeId('node', event.NodeId)) ? nodeId('node', event.NodeId) : 'slimfaas:slimfaas';
  const source = event.Source === 'slimfaas' && !event.SourcePod ? slim : resolveSource(topology, event.Source, event.SourcePod);
  const target = event.Target === 'slimfaas' ? slim : (event.TargetPod && topology.pods.get(`${event.Target}/${event.TargetPod}`)) || topology.actors.get(event.Target) || slim;
  const queue = nodeId('queue', event.QueueName ?? event.Target);
  let path: string[];
  if (event.Type === 'request_in') path = [source, slim];
  else if (event.Type === 'enqueue') path = [source, slim, queue];
  else if (event.Type === 'dequeue' || (event.Type === 'request_out' && event.QueueName)) path = [queue, target];
  else if (event.Type === 'response' || event.Type === 'request_end') path = event.QueueName
    ? [event.Target === 'slimfaas' ? source : target, queue]
    : event.Target === 'slimfaas' ? [slim, source] : [target, slim];
  else if (event.Type === 'request_waiting' || event.Type === 'request_started') path = [slim];
  else if (event.Type === 'request_out' || event.Type === 'event_publish') path = event.Target === 'slimfaas' ? [source, slim] : [slim, target];
  else path = [source, slim, target];
  return path.filter((id, i) => topology.byId.has(id) && (i === 0 || id !== path[i - 1]));
}

export function selectedEvent(topology: Topology, event: NetworkActivityEvent, selected: string | null): boolean {
  if (!selected) return true;
  const caller = resolveSource(topology, event.Source, event.SourcePod);
  if (caller === selected || topology.byId.get(caller)?.parent === selected) return true;
  if (['request_waiting', 'request_started'].includes(event.Type) && topology.actors.get(event.Target) === selected) return true;
  return eventPath(topology, event).some(id => id === selected || topology.byId.get(id)?.parent === selected);
}

export function filterNodes(topology: Topology, search: string) {
  const query = search.trim().toLowerCase();
  return topology.nodes.filter(n => !query || `${n.label} ${n.detail} ${n.kind}`.toLowerCase().includes(query));
}
