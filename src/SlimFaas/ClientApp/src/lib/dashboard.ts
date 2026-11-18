import type { FunctionStatusDetailed } from '../types.ts';
import { functionState } from './live.ts';

export const trafficHome = '#/live/traffic';

export function dashboardRoute(hash: string) {
  const [path, query] = hash.split('?');
  const params = new URLSearchParams(query);
  return { path: !path || path === '#/' || path === '#/live' ? trafficHome : path, params };
}

export function actorLink(id: string) {
  return trafficHome + '?' + new URLSearchParams({ actor: id });
}

export function openActor(id: string) {
  // One history entry per drawer lets Back return to the same mounted canvas.
  history.pushState({ trafficActor: true }, '', actorLink(id));
  window.dispatchEvent(new Event('hashchange'));
}

export function closeActor() {
  if (history.state?.trafficActor) history.back();
  else {
    history.replaceState(null, '', trafficHome);
    window.dispatchEvent(new Event('hashchange'));
  }
}

export function isOrchestrated(fn: FunctionStatusDetailed) {
  return fn.PodType === 'Deployment' || fn.PodType === 'StatefulSet';
}

export function canWake(fn: FunctionStatusDetailed) {
  return isOrchestrated(fn) && fn.NumberRequested === 0 && functionState(fn) === 'Sleeping';
}

/** Retain both ends of a workload name when neighboring actors share a prefix. */
export function compactLabel(label: string, limit: number) {
  if (label.length <= limit) return label;
  const start = Math.floor((limit - 1) / 3);
  return label.slice(0, start) + '…' + label.slice(-(limit - 1 - start));
}
