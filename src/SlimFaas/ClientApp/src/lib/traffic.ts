import type { NetworkActivityEvent } from '../types.ts';
import { eventPath, selectedEvent, type Topology } from './topology.ts';

export const ANIMATION_LIMIT = 200;
export const SPEEDS = { Fast: 450, Normal: 800, Slow: 1400 } as const;
export type Speed = keyof typeof SPEEDS;
export type MessageKind = 'request' | 'publication' | 'queue' | 'reply';
export function messageKind(event: NetworkActivityEvent): MessageKind {
  if (event.Type === 'event_publish') return 'publication';
  if (event.Type === 'enqueue' || event.Type === 'dequeue' || (event.Type === 'request_out' && event.QueueName)) return 'queue';
  if (event.Type === 'response' || event.Type === 'request_end') return 'reply';
  return 'request';
}
export function matchesTraffic(topology: Topology, event: NetworkActivityEvent, type: string, selected: string | null, isolate: boolean) {
  return (type === 'all' || event.Type === type) && (!isolate || selectedEvent(topology, event, selected));
}
/** Keep actual instance identities; the canvas alone collapses positions at overview zoom. */
export function displayPath(topology: Topology, event: NetworkActivityEvent, _selected: string | null) {
  return eventPath(topology, event);
}
export function dispatchIdentity(event: NetworkActivityEvent): string | null {
  if (event.Type === 'dequeue') return event.Id;
  return event.Type === 'request_out' && event.QueueName ? event.CorrelationId ?? event.Id : null;
}
export function markerProgress(now: number, start: number, duration: number) {
  return Math.max(0, Math.min(0.9999, (now - start) / duration));
}
export interface Marker { path: string[]; kind: MessageKind; count: number; start: number; duration: number; highlighted: boolean; waiting?: boolean }
interface Receipt { event: NetworkActivityEvent; received: number; end?: number; animated?: boolean }
const HISTORY_LIMIT = 5000;
const MISSING_PARENT_MS = 5000;
const INCOMPLETE_MS = 300_000;

/** Bounded causal playback. Technical records remain in the journal, not extra trips. */
export class TrafficPlayback {
  markers = new Map<string, Marker>();
  arrivals = new Map<string, number>();
  omitted = 0;
  private records = new Map<string, Receipt>();
  private children = new Map<string, Set<string>>();
  private pending = new Set<string>();
  private dispatches = new Map<string, { view: string; shown: boolean }>();
  private previous: readonly NetworkActivityEvent[] | null = null;
  private session: number | undefined;
  private paused = false;
  private resumeAfter = -Infinity;
  private view = '';

  private descendants(id: string): Receipt[] {
    return [...(this.children.get(id) ?? [])].flatMap(child => {
      const receipt = this.records.get(child);
      return receipt ? [receipt] : [];
    });
  }

  private publication(receipt: Receipt): boolean {
    let current: Receipt | undefined = receipt;
    // The longest supported chain is ingress/publication/delivery/send/end.
    // Bound traversal as well as retention, including malformed peer input.
    for (let depth = 0; current && depth < 8; depth++) {
      if (current.event.Type === 'event_publish') return true;
      if (current.event.Type === 'request_in' && this.descendants(current.event.Id).some(r => r.event.Type === 'event_publish')) return true;
      current = this.records.get(current.event.CorrelationId ?? '');
    }
    return false;
  }

  /** The same presentation rules apply to moving markers and their static links. */
  visualKind(event: NetworkActivityEvent): MessageKind | null {
    if (event.Type === 'request_waiting' || event.Type === 'request_started') return null;
    const receipt = this.records.get(event.Id);
    const parent = this.records.get(event.CorrelationId ?? '');
    if (event.Type === 'event_publish' && event.Target === 'slimfaas' && parent?.animated) return null;
    if (receipt && this.publication(receipt)) {
      if (event.Type === 'request_out' || messageKind(event) === 'reply') return null;
      return 'publication';
    }
    return messageKind(event);
  }

  update(events: readonly NetworkActivityEvent[], session: number, paused: boolean, now: number,
    topology: Topology, speed: Speed, type: string, selected: string | null, isolate: boolean) {
    if (session !== this.session) {
      this.markers.clear(); this.arrivals.clear(); this.records.clear(); this.children.clear(); this.pending.clear();
      this.dispatches.clear(); this.previous = null; this.omitted = 0; this.resumeAfter = -Infinity; this.session = session;
    }
    const view = `${speed}/${type}/${selected}/${isolate}`;
    if (view !== this.view || paused) {
      this.markers.clear(); this.arrivals.clear();
      for (const id of this.pending) this.records.get(id)!.end = now;
      this.pending.clear();
      for (const receipt of this.records.values()) if ((receipt.end ?? now) > now) receipt.end = now;
    }
    this.view = view;
    for (const [key, marker] of this.markers) if (now - marker.start >= marker.duration) this.markers.delete(key);
    for (const [id, until] of this.arrivals) if (now >= until) this.arrivals.delete(id);
    const resumed = this.paused && !paused;
    this.paused = paused;
    if (resumed) this.resumeAfter = now;

    if (events !== this.previous) {
      this.previous = events;
      // Register a whole batch before resolving dependencies, regardless of its order.
      for (const event of events) {
        if (this.records.has(event.Id)) continue;
        const receipt: Receipt = { event, received: event.ReceivedAt ?? now };
        this.records.set(event.Id, receipt);
        if (event.CorrelationId) {
          const children = this.children.get(event.CorrelationId) ?? new Set<string>();
          children.add(event.Id); this.children.set(event.CorrelationId, children);
        }
        const dispatch = dispatchIdentity(event);
        let duplicate = false;
        if (dispatch) {
          const previous = this.dispatches.get(dispatch);
          duplicate = !!previous && (previous.view !== view || previous.shown);
          if (!previous) this.dispatches.set(dispatch, { view, shown: paused });
          if (this.dispatches.size > HISTORY_LIMIT) this.dispatches.delete(this.dispatches.keys().next().value!);
        }
        if (paused || duplicate || (event.ReceivedAt === undefined ? resumed : event.ReceivedAt <= this.resumeAfter)
          || now - receipt.received >= SPEEDS[speed] || !matchesTraffic(topology, event, type, selected, isolate)) receipt.end = now;
        else this.pending.add(event.Id);
        if (this.records.size > HISTORY_LIMIT) {
          const oldest = this.records.keys().next().value!;
          const parent = this.records.get(oldest)!.event.CorrelationId;
          if (parent) {
            const siblings = this.children.get(parent);
            siblings?.delete(oldest);
            if (!siblings?.size) this.children.delete(parent);
          }
          this.records.delete(oldest); this.children.delete(oldest);
          if (this.pending.delete(oldest)) this.omitted++;
          this.markers.delete(`waiting:${oldest}`);
        }
      }
    }
    if (paused) return;

    // A bounded number of passes releases metadata aliases within the same frame.
    for (let pass = 0; pass < 8; pass++) {
      let changed = false;
      for (const id of this.pending) {
        const receipt = this.records.get(id)!;
        const event = receipt.event;
        const parent = this.records.get(event.CorrelationId ?? '');
        const queued = !!dispatchIdentity(event);
        const finish = (end = now) => { receipt.end = end; this.pending.delete(id); changed = true; };
        if (now - receipt.received >= INCOMPLETE_MS) { finish(); this.omitted++; continue; }
        if (event.CorrelationId && !queued) {
          if (!parent) {
            if (now - receipt.received >= MISSING_PARENT_MS) finish();
            continue;
          }
          if (parent.end === undefined || parent.end > now) continue;
        }
        if (event.Type === 'request_waiting' || event.Type === 'request_started') {
          // Readiness is metadata; only an actual dispatch can leave the node.
          if (event.Type === 'request_waiting' && parent && !this.descendants(parent.event.Id).some(r => r.event.Type === 'request_out' || r.event.Type === 'request_end')) {
            const key = `waiting:${parent.event.Id}`;
            if (this.markers.size < ANIMATION_LIMIT) this.markers.set(key, {
              path: displayPath(topology, event, selected).slice(0, 1), kind: 'request', count: 1,
              start: now, duration: INCOMPLETE_MS, highlighted: selectedEvent(topology, event, selected), waiting: true,
            });
          }
          finish(); continue;
        }
        // Incoming publication metadata and its HTTP transport describe existing hops.
        const kind = this.visualKind(event);
        if (kind === null) {
          finish(); continue;
        }
        // The caller response follows the downstream response, including when both
        // arrive before either request leg has finished its visual trip.
        if (event.Type === 'request_end' && parent?.event.Type === 'request_in') {
          const sends = this.descendants(parent.event.Id).filter(r => r.event.Type === 'request_out');
          if (sends.some(send => {
            const replies = this.descendants(send.event.Id).filter(r => messageKind(r.event) === 'reply');
            return !replies.length || replies.some(reply => reply.end === undefined || reply.end > now);
          })) {
            if (now - receipt.received >= INCOMPLETE_MS) finish();
            continue;
          }
        }
        const path = displayPath(topology, event, selected);
        // An inventory snapshot can lag the dispatch. Keep its actual destination
        // rather than drawing to a sleeping function group or an invented replica.
        if (event.TargetPod && event.CorrelationId && ['request_out', 'event_publish'].includes(event.Type)
          && !topology.pods.has(`${event.Target}/${event.TargetPod}`)) continue;
        if (path.length < 2) { finish(); continue; }
        const dispatch = dispatchIdentity(event);
        if (dispatch) {
          const previous = this.dispatches.get(dispatch)!;
          if (previous.shown) { finish(); continue; }
          previous.shown = true;
        }
        const key = JSON.stringify([path, dispatch ? 'queue' : event.Type]);
        const marker = this.markers.get(key);
        if (marker) { marker.count++; receipt.animated = true; finish(marker.start + marker.duration); }
        else if (this.markers.size < ANIMATION_LIMIT) {
          this.markers.set(key, { path, kind, count: 1, start: now, duration: SPEEDS[speed], highlighted: selectedEvent(topology, event, selected) });
          receipt.animated = true;
          finish(now + SPEEDS[speed]);
        } else { this.omitted++; finish(); }
        if (parent) this.markers.delete(`waiting:${parent.event.Id}`);
        if (['request_out', 'dequeue', 'event_publish'].includes(event.Type)) {
          this.arrivals.set(path[path.length - 1], receipt.end! + 1000);
          if (this.arrivals.size > ANIMATION_LIMIT) this.arrivals.delete(this.arrivals.keys().next().value!);
        }
      }
      if (!changed) break;
    }
  }
}
