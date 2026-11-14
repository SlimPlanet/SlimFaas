import { formatBytes, functionState } from './live.ts';
import type { DataSummary, FunctionStatusDetailed, JobConfigurationStatus, NetworkActivityEvent, QueueInfo, SlimFaasNodeInfo } from '../types.ts';
import type { DeadLetterSummary } from './deadLetters.ts';
import type { StreamSummary } from './streams.ts';

export interface MapNode {
  id: string; label: string; kind: 'function' | 'job' | 'slimfaas' | 'queue' | 'external' | 'stream' | 'sets' | 'files';
  role?: SlimFaasNodeInfo['Role'];
  state?: ReturnType<typeof functionState>; parent: string | null; x: number; y: number; status: string; detail: string;
  queueLength?: number; deadLetterCount?: number;
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

/** A job queue keeps the name SlimFaas records in activity, apart from function queues. */
export const jobQueueName = (job: string) => `job:${job}`;
export const isJobQueueName = (name: string) => name.startsWith('job:');
export function jobQueues(jobs: JobConfigurationStatus[]): QueueInfo[] {
  return jobs.map(job => ({ Name: jobQueueName(job.Name), Length: job.QueueLength }));
}
/** Job queue traffic targets the job, never a function that has the same name. */
export const isJobQueueEvent = (event: NetworkActivityEvent) => event.QueueName === jobQueueName(event.Target);

export interface ObservedFunction { name: string; replicas: string[] }
/** Event-only actors keep an honest unknown inventory state until a snapshot supplies it. */
export function observedFunctions(functions: FunctionStatusDetailed[], events: NetworkActivityEvent[]): ObservedFunction[] {
  const known = new Set(functions.map(fn => fn.Name));
  const observed = new Map<string, Set<string>>();
  for (const event of events) {
    if (event.DataKind) {
      // Peer activity can precede the caller's first inventory snapshot.
      if (event.Source && event.SourcePod && !known.has(event.Source) && !['slimfaas', 'external'].includes(event.Source) &&
          !event.SourcePod.startsWith(`${event.Source}-slimfaas-job-`)) {
        const replicas = observed.get(event.Source) ?? new Set<string>();
        replicas.add(event.SourcePod); observed.set(event.Source, replicas);
      }
      continue;
    }
    if (!event.Target || event.Target.startsWith('dlq:') || event.Target.startsWith('stream:') || known.has(event.Target) || isJobQueueEvent(event) || ['slimfaas', 'external'].includes(event.Target)) continue;
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

export function buildTopology(functions: FunctionStatusDetailed[], jobs: JobConfigurationStatus[], queues: QueueInfo[], slimNodes: SlimFaasNodeInfo[], observed: ObservedFunction[] = [], unknownQueues: string[] = [], deadLetters: DeadLetterSummary[] = [], streams: StreamSummary[] = [], dataSummary: DataSummary | null = null): Topology {
  const groups: MapGroup[] = [];
  const actors = new Map<string, string>(), pods = new Map<string, string>(), sourcePods = new Map<string, string>();
  const create = (kind: MapNode['kind'], label: string, status: string, children: Omit<MapNode, 'x' | 'y' | 'parent' | 'kind'>[], detail = '') => {
    const compact = ['queue', 'stream', 'sets', 'files'].includes(kind);
    const columns = Math.max(1, Math.ceil(Math.sqrt(children.length)));
    const group: MapGroup = { id: nodeId(kind, label), label, kind, parent: null, x: 0, y: 0, status, detail,
      width: compact ? 180 : Math.max(260, columns * CELL + 60), height: compact ? 104 : Math.max(150, Math.ceil((kind === 'function' || kind === 'job' ? Math.max(1, children.length) : children.length) / columns) * CELL + 160), children: [] };
    group.children = children.map((child, i) => ({ ...child, kind, parent: group.id,
      x: 30 + i % columns * CELL, y: 125 + Math.floor(i / columns) * CELL }));
    groups.push(group);
    if (!['queue', 'sets', 'files'].includes(kind)) actors.set(label, group.id);
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
  const queueLengths = new Map<string, number | undefined>(queues.map(queue => [queue.Name, queue.Length]));
  for (const name of unknownQueues) if (!queueLengths.has(name)) queueLengths.set(name, undefined);
  const summaries = new Map(deadLetters.map(summary => [summary.Function, summary]));
  for (const name of summaries.keys()) if (!queueLengths.has(name)) queueLengths.set(name, undefined);
  for (const [name, length] of [...queueLengths].sort(([a], [b]) => a.localeCompare(b))) {
    const dlq = summaries.get(name);
    const status = length === undefined ? 'Queue length unknown' : `${length} queued`;
    const group = create('queue', name, status + (dlq ? ` · DLQ ${dlq.Count} retained` : ''), [],
      dlq ? `Async queue · Dead Letter Queue · ${dlq.FunctionExists ? 'Function present' : 'Function removed'}` :
        length === undefined ? 'Seen in traffic; absent from this node’s inventory' : '');
    group.queueLength = length; group.deadLetterCount = dlq?.Count;
    if (dlq) actors.set(`dlq:${name}`, group.id);
  }
  for (const stream of streams) {
    const group = create('stream', stream.Name, `${stream.RetainedMessageCount} retained · ${stream.VisibleFunctionCount} visible functions`, [],
      `Stream · ${stream.Visibility} · ${stream.MaxAgeSeconds}s / ${stream.MaxLength} messages · revision ${stream.Revision}`);
    actors.set(`stream:${stream.Name}`, group.id);
  }

  for (const kind of ['sets', 'files'] as const) {
    const count = dataSummary ? kind === 'sets' ? dataSummary.Sets : dataSummary.Files : null;
    const bytes = dataSummary ? kind === 'sets' ? dataSummary.SetBytes : dataSummary.FileBytes : null;
    const unknown = kind === 'files' ? dataSummary?.UnknownFileSizes ?? 0 : 0;
    create(kind, kind === 'sets' ? 'Set' : 'Files', count === null ? 'Metadata unavailable' :
      `${count} items · ${unknown ? '≥ ' : ''}${formatBytes(bytes)}`, [],
      `Logical ${kind} store · unexpired public API entries` + (unknown ? ` · ${unknown} unknown file sizes` : ''));
  }

  // Pack each workload together with its queue, reserving the full instance rectangle.
  const pairGap = 28;
  const workloads = (kind: 'job' | 'function') => groups.filter(group => group.kind === kind);
  const sideWidth = (kind: 'job' | 'function') => Math.max(0, ...workloads(kind).map(group => group.width)) + 180 + pairGap;
  const leftWidth = sideWidth('job');
  const hub = groups.find(group => group.kind === 'slimfaas')!;
  const centerWidth = Math.max(hub.width, 2 * 180 + pairGap);
  const centerX = leftWidth + GAP;
  const rightX = centerX + centerWidth + GAP;
  const placedQueues = new Set<string>();
  const queueByName = new Map(groups.filter(group => group.kind === 'queue').map(group => [group.label, group]));
  const packSide = (kind: 'job' | 'function') => {
    let y = 0;
    for (const workload of workloads(kind)) {
      const queue = queueByName.get(kind === 'job' ? jobQueueName(workload.label) : workload.label);
      const rowHeight = Math.max(workload.height, queue?.height ?? 0);
      workload.x = kind === 'job' ? leftWidth - 180 - pairGap - workload.width : rightX + 180 + pairGap;
      workload.y = y + (rowHeight - workload.height) / 2;
      if (queue) {
        queue.x = kind === 'job' ? leftWidth - queue.width : rightX;
        queue.y = y + (rowHeight - queue.height) / 2;
        placedQueues.add(queue.id);
      }
      y += rowHeight + GAP;
    }
    for (const queue of groups.filter(group => group.kind === 'queue' && !placedQueues.has(group.id) && isJobQueueName(group.label) === (kind === 'job'))) {
      queue.x = kind === 'job' ? leftWidth - queue.width : rightX; queue.y = y;
      placedQueues.add(queue.id); y += queue.height + GAP;
    }
    return y;
  };
  const leftHeight = packSide('job'), rightHeight = packSide('function');
  const external = groups.find(group => group.kind === 'external')!;
  const sideOffset = external.height + GAP;
  for (const group of groups) if (['job', 'function', 'queue'].includes(group.kind)) {
    const sideHeight = group.kind === 'job' || group.kind === 'queue' && isJobQueueName(group.label) ? leftHeight : rightHeight;
    group.y += sideOffset + (Math.max(leftHeight, rightHeight) - sideHeight) / 2;
  }
  external.x = centerX + (centerWidth - external.width) / 2; external.y = 0;
  hub.x = centerX + (centerWidth - hub.width) / 2;
  hub.y = sideOffset + Math.max(0, (Math.max(leftHeight, rightHeight) - GAP - hub.height) / 2);
  const stores = groups.filter(group => group.kind === 'sets' || group.kind === 'files');
  stores.forEach((group, index) => { group.x = centerX + (centerWidth - (2 * 180 + pairGap)) / 2 + index * (180 + pairGap); group.y = hub.y + hub.height + GAP; });
  const streamGroups = groups.filter(group => group.kind === 'stream').sort((a, b) => a.label.localeCompare(b.label));
  const streamTop = hub.y + hub.height + GAP + 104 + GAP;
  streamGroups.forEach((group, index) => {
    const columns = Math.min(2, streamGroups.length);
    group.x = centerX + (centerWidth - (columns * 180 + (columns - 1) * pairGap)) / 2 + index % columns * (180 + pairGap);
    group.y = streamTop + Math.floor(index / columns) * (104 + pairGap);
  });
  for (const group of groups) for (const child of group.children) { child.x += group.x; child.y += group.y; }
  const width = Math.max(...groups.map(group => group.x + group.width)) + GAP;
  const height = Math.max(...groups.map(group => group.y + group.height)) + GAP;
  const nodes = groups.flatMap(g => [g, ...g.children]);
  return { groups, nodes, byId: new Map(nodes.map(n => [n.id, n])), actors, pods, sourcePods, width, height };
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

/** A job queue delivers to the execution once the snapshot lists it, otherwise to its job group. */
function jobTarget(topology: Topology, event: NetworkActivityEvent): string | undefined {
  const run = event.TargetPod ? nodeId('run', event.TargetPod) : null;
  if (run && topology.byId.has(run)) return run;
  const job = nodeId('job', event.Target);
  return topology.byId.has(job) ? job : undefined;
}

export function eventPath(topology: Topology, event: NetworkActivityEvent): string[] {
  const slim = topology.byId.has(nodeId('node', event.NodeId)) ? nodeId('node', event.NodeId) : 'slimfaas:slimfaas';
  const source = event.Source === 'slimfaas' && !event.SourcePod ? slim : resolveSource(topology, event.Source, event.SourcePod);
  const target = event.Target === 'slimfaas' ? slim : isJobQueueEvent(event) ? jobTarget(topology, event) ?? slim
    : (event.TargetPod && topology.pods.get(`${event.Target}/${event.TargetPod}`)) || topology.actors.get(event.Target) || slim;
  const queue = nodeId('queue', event.QueueName ?? event.Target);
  let path: string[];
  if (event.DataKind) {
    const store = nodeId(event.DataKind, event.DataKind === 'sets' ? 'Set' : 'Files');
    path = event.Type === 'data_request' ? [source, slim] : event.Type === 'data_access' &&
      event.DataStatusCode !== null && event.DataStatusCode !== undefined && event.DataStatusCode < 400 ? [slim, store] : [];
  }
  else if (event.StreamName) {
    const stream = nodeId('stream', event.StreamName);
    if (!topology.byId.has(stream)) return [];
    const destination = (event.TargetPod && topology.pods.get(`${event.Target}/${event.TargetPod}`)) || topology.actors.get(event.Target);
    path = event.Type === 'stream_append' ? [source, slim, stream] :
      event.Type === 'stream_delivery' ? destination ? [stream, destination] : [] :
      event.Type === 'stream_ack' ? [source, stream] : [];
  }
  else if (event.Type === 'dead_lettered' || event.Type === 'dead_letter_requeued') path = [queue];
  else if (['dead_letter_deleted', 'dead_letter_expired', 'dead_letter_evicted'].includes(event.Type)) path = [];
  else if (event.Type === 'request_in') path = [source, slim];
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
  if (event.Type.startsWith('dead_letter') &&
    (selected === nodeId('queue', event.QueueName ?? '') || selected === nodeId('function', event.QueueName ?? ''))) return true;
  if (event.StreamName) {
    const fn = event.StreamFunction ?? (event.Type === 'stream_delivery' ? event.Target : event.Type === 'stream_ack' ? event.Source : null);
    if (selected === nodeId('stream', event.StreamName) || fn && selected === nodeId('function', fn) ||
      event.Type === 'stream_delivery' && !!event.TargetPod && selected === topology.pods.get(`${event.Target}/${event.TargetPod}`)) return true;
  }
  if (event.DataKind && selected === nodeId(event.DataKind, event.DataKind === 'sets' ? 'Set' : 'Files')) return true;
  const caller = resolveSource(topology, event.Source, event.SourcePod);
  if (caller === selected || topology.byId.get(caller)?.parent === selected) return true;
  if (['request_waiting', 'request_started'].includes(event.Type) && topology.actors.get(event.Target) === selected) return true;
  return eventPath(topology, event).some(id => id === selected || topology.byId.get(id)?.parent === selected);
}

export function filterNodes(topology: Topology, search: string) {
  const query = search.trim().toLowerCase();
  return topology.nodes.filter(n => !query || `${n.label} ${n.detail} ${n.kind}`.toLowerCase().includes(query));
}
