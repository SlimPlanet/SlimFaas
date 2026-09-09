import type { NetworkActivityEvent } from '../types.ts';
import { eventPath, selectedEvent, type Topology } from './topology.ts';

export const ANIMATION_LIMIT = 200;
export const SPEEDS = { Fast: 450, Normal: 800, Slow: 1400 } as const;
export type Speed = keyof typeof SPEEDS;
export type MessageKind = 'request' | 'publication' | 'queue' | 'reply';
export function messageKind(event: NetworkActivityEvent): MessageKind {
  if (event.Type === 'event_publish') return 'publication';
  if (event.Type === 'enqueue' || event.Type === 'dequeue') return 'queue';
  if (event.Type === 'response' || event.Type === 'request_end') return 'reply';
  return 'request';
}
export function matchesTraffic(topology: Topology, event: NetworkActivityEvent, type: string, selected: string | null, isolate: boolean) {
  return (type === 'all' || event.Type === type) && (!isolate || selectedEvent(topology, event, selected));
}
export function displayPath(topology: Topology, event: NetworkActivityEvent, selected: string | null) {
  return eventPath(topology, event).map(id => {
    const node = topology.byId.get(id)!;
    return selected && (id === selected || node.parent === selected) ? id : node.parent ?? id;
  }).filter((id, i, path) => i === 0 || id !== path[i - 1]);
}
export function markerProgress(now: number, start: number, duration: number) {
  return Math.max(0, Math.min(0.9999, (now - start) / duration));
}
export interface Marker { path: string[]; kind: MessageKind; count: number; start: number; duration: number; highlighted: boolean }

/** Consumes the unfiltered stream. Changing the view can never replay consumed events. */
export class TrafficPlayback {
  markers = new Map<string, Marker>();
  omitted = 0;
  private seen = new Set<string>();
  private previous: readonly NetworkActivityEvent[] | null = null;
  private session: number | undefined;
  private paused = false;
  private resumeAfter = -Infinity;
  private view = '';

  update(events: readonly NetworkActivityEvent[], session: number, paused: boolean, now: number,
    topology: Topology, speed: Speed, type: string, selected: string | null, isolate: boolean) {
    if (session !== this.session) {
      this.markers.clear(); this.seen.clear(); this.previous = null; this.omitted = 0; this.resumeAfter = -Infinity; this.session = session;
    }
    const view = `${speed}/${type}/${selected}/${isolate}`;
    if (view !== this.view || paused) this.markers.clear();
    this.view = view;
    for (const [key, marker] of this.markers) if (now - marker.start >= marker.duration) this.markers.delete(key);
    const resumed = this.paused && !paused;
    this.paused = paused;
    if (resumed) this.resumeAfter = now;
    if (events === this.previous) return;
    const fresh = events.filter(event => {
      if (this.seen.has(event.Id)) return false;
      this.seen.add(event.Id); return true;
    });
    this.seen = new Set(events.map(event => event.Id));
    this.previous = events;
    if (paused) return;
    for (const event of fresh) {
      if (event.ReceivedAt === undefined ? resumed : event.ReceivedAt <= this.resumeAfter) continue;
      // Receipt time is local and monotonic. A sleeping browser returns to live.
      if (event.ReceivedAt !== undefined && now - event.ReceivedAt >= SPEEDS[speed]) continue;
      if (!matchesTraffic(topology, event, type, selected, isolate)) continue;
      const path = displayPath(topology, event, selected);
      if (path.length < 2) continue;
      const kind = messageKind(event);
      const key = JSON.stringify([path, event.Type]);
      const marker = this.markers.get(key);
      if (marker) marker.count++;
      else if (this.markers.size < ANIMATION_LIMIT) this.markers.set(key, {
        path, kind, count: 1, start: now, duration: SPEEDS[speed], highlighted: selectedEvent(topology, event, selected),
      });
      else this.omitted++;
    }
  }
}
