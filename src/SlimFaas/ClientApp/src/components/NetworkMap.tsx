import React, { useRef, useEffect, useMemo, useCallback } from 'react';
import * as d3 from 'd3';
import type { FunctionStatusDetailed, QueueInfo, NetworkActivityEvent, SlimFaasNodeInfo } from '../types';

interface Props {
  functions: FunctionStatusDetailed[];
  queues: QueueInfo[];
  activity: NetworkActivityEvent[];
  functionsWithQueueActivity: Set<string>;
  slimFaasReplicas: number;
  slimFaasNodes: SlimFaasNodeInfo[];
}

interface AnimSegment {
  from: { x: number; y: number };
  to: { x: number; y: number };
}

interface AnimatedMessage {
  id: string;
  segments: AnimSegment[];
  currentSeg: number;
  color: string;
  shape: 'circle' | 'rect';
  startTime: number;
  segDuration: number;
  segmentGapMs: number;
  label?: string;
}

interface ReplicaPosition {
  x: number;
  y: number;
  status: string;
  name: string;
}

const SEG_DURATION = 800;
const SEG_GAP = 120;
const CHAIN_GAP_MIN = 80;
const CHAIN_GAP_MAX = 240;
const COUNTER_STALE_MS = 60 * 1000;
const COUNTER_SWEEP_MS = 1000;
const BUBBLE_R = 34;
const REPLICA_R = 9;
const QUEUE_BOX_W = 60;
const QUEUE_BOX_H = 20;
const CENTER = { x: 0, y: 0 };
const EXTERNAL_ANGLE = -Math.PI * 0.88;
const SVG_VIEW_H = 680;

function nameColor(name: string): string {
  const colors = ['#4c6ef5', '#7950f2', '#e64980', '#f76707', '#36b37e', '#00b8d9', '#fab005', '#e8590c', '#ae3ec9', '#0ca678', '#2f9e44', '#1098ad', '#f08c00', '#d6336c', '#6741d9'];
  let hash = 0;
  for (let i = 0; i < name.length; i++) hash = name.charCodeAt(i) + ((hash << 5) - hash);
  return colors[Math.abs(hash) % colors.length];
}

function podColor(status: string): string {
  if (status === 'Running') return '#36b37e';
  if (status === 'Starting') return '#fab005';
  if (status === 'Pending') return '#adb5bd';
  return '#ff5630';
}

function lightenColor(hex: string, amount: number): string {
  const num = parseInt(hex.replace('#', ''), 16);
  const r = Math.min(255, ((num >> 16) & 0xff) + Math.round(255 * amount));
  const g = Math.min(255, ((num >> 8) & 0xff) + Math.round(255 * amount));
  const b = Math.min(255, (num & 0xff) + Math.round(255 * amount));
  return `rgb(${r},${g},${b})`;
}

function eventKey(evt: NetworkActivityEvent): string {
  return `${evt.Id}|${evt.Type}|${evt.Source}|${evt.Target}|${evt.TimestampMs}|${evt.NodeId}`;
}

function utcMs(ts: number): number {
  return Number.isFinite(ts) && ts > 0 ? Math.trunc(ts) : Date.now();
}

function radialPositions(n: number): { x: number; y: number }[] {
  if (n === 0) return [];
  if (n === 1) return [{ x: 0, y: 280 }];
  const baseR = 280;
  const perItem = 132;
  const radius = Math.max(baseR, (n * perItem) / (2 * Math.PI));
  return Array.from({ length: n }, (_, i) => {
    const angle = (2 * Math.PI * i) / n - Math.PI / 2;
    return { x: Math.cos(angle) * radius, y: Math.sin(angle) * radius };
  });
}

const NetworkMap: React.FC<Props> = ({ functions, queues, activity, functionsWithQueueActivity, slimFaasReplicas, slimFaasNodes }) => {
  const svgRef = useRef<SVGSVGElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const animRef = useRef<AnimatedMessage[]>([]);
  const seenEventsRef = useRef<Set<string>>(new Set());
  const frameRef = useRef(0);
  const zoomRef = useRef<d3.ZoomBehavior<SVGSVGElement, unknown> | null>(null);
  const worldGRef = useRef<SVGGElement | null>(null);
  const userHasZoomedRef = useRef(false);
  const initialFitDoneRef = useRef(false);
  const activeCountersRef = useRef<Record<string, number>>({});
  const activeReplicaCountersRef = useRef<Record<string, number>>({});
  const activeCounterTouchedRef = useRef<Record<string, number>>({});
  const activeReplicaCounterTouchedRef = useRef<Record<string, number>>({});
  const lastCounterSweepRef = useRef(0);
  const lastScheduledUtcRef = useRef(0);
  const lastScheduledPerfRef = useRef(0);
  const fnPositionsRef = useRef<Record<string, { x: number; y: number }>>({});
  const slimReplicaPositionsRef = useRef<Record<string, ReplicaPosition>>({});
  const functionReplicaPositionsRef = useRef<Record<string, Record<string, ReplicaPosition>>>({});
  const queuePositionsRef = useRef<Record<string, { x: number; y: number }>>({});

  const fnNamesKey = useMemo(() => functions.map(f => f.Name).sort().join(','), [functions]);

  const fnPositions = useMemo(() => {
    const names = fnNamesKey.split(',').filter(Boolean);
    const pos: Record<string, { x: number; y: number }> = {};
    const pts = radialPositions(names.length);
    names.forEach((name, i) => { pos[name] = pts[i]; });
    return pos;
  }, [fnNamesKey]);

  const externalPos = useMemo(() => {
    const pts = Object.values(fnPositions);
    const baseRadius = pts.length === 0
      ? 420
      : Math.max(...pts.map(p => Math.hypot(p.x - CENTER.x, p.y - CENTER.y))) + 190;
    return {
      x: CENTER.x + Math.cos(EXTERNAL_ANGLE) * baseRadius,
      y: CENTER.y + Math.sin(EXTERNAL_ANGLE) * baseRadius,
    };
  }, [fnPositions]);

  const queueFnKey = useMemo(() => {
    const names: string[] = [];
    for (const fn of functions) {
      if (functionsWithQueueActivity.has(fn.Name)) names.push(fn.Name);
    }
    return names.sort().join(',');
  }, [functions, functionsWithQueueActivity]);

  const queuePositions = useMemo(() => {
    const pos: Record<string, { x: number; y: number }> = {};
    const names = queueFnKey.split(',').filter(Boolean);
    for (const name of names) {
      const fp = fnPositions[name];
      if (!fp) continue;
      pos[name] = { x: (CENTER.x + fp.x) / 2, y: (CENTER.y + fp.y) / 2 };
    }
    return pos;
  }, [queueFnKey, fnPositions]);

  const queueMap = useMemo(() => {
    const m: Record<string, number> = {};
    queues.forEach(q => { m[q.Name] = q.Length; });
    return m;
  }, [queues]);

  const zoomToFit = useCallback((animated = true) => {
    const svg = svgRef.current;
    const worldG = worldGRef.current;
    const zoom = zoomRef.current;
    if (!svg || !worldG || !zoom) return;

    const svgW = svg.clientWidth || 800;
    const svgH = svg.clientHeight || SVG_VIEW_H;
    const allPts: { x: number; y: number }[] = [CENTER, externalPos, ...Object.values(fnPositions), ...Object.values(queuePositions)];
    if (allPts.length === 0) return;

    const pad = BUBBLE_R + 140;
    let minX = Infinity; let maxX = -Infinity; let minY = Infinity; let maxY = -Infinity;
    for (const p of allPts) {
      minX = Math.min(minX, p.x - pad);
      maxX = Math.max(maxX, p.x + pad);
      minY = Math.min(minY, p.y - pad);
      maxY = Math.max(maxY, p.y + pad);
    }

    const bboxW = maxX - minX || 1;
    const bboxH = maxY - minY || 1;
    const scale = Math.min(svgW / bboxW, svgH / bboxH, 2.5);
    const tx = svgW / 2 - (minX + bboxW / 2) * scale;
    const ty = svgH / 2 - (minY + bboxH / 2) * scale;
    const transform = d3.zoomIdentity.translate(tx, ty).scale(scale);

    if (animated) d3.select(svg).transition().duration(400).call(zoom.transform, transform);
    else d3.select(svg).call(zoom.transform, transform);
  }, [externalPos, fnPositions, queuePositions]);

  const slimNodeList = useMemo(() => {
    if (slimFaasNodes.length > 0) return slimFaasNodes;
    return Array.from({ length: slimFaasReplicas }, (_, i) => ({ Name: `sf-${i}`, Status: 'Running' }));
  }, [slimFaasNodes, slimFaasReplicas]);

  const slimReplicaPositions = useMemo(() => {
    const nodes = slimNodeList;
    const positions: Record<string, ReplicaPosition> = {};
    if (nodes.length === 0) return positions;
    const totalW = nodes.length * (REPLICA_R * 2 + 6) - 6;
    const startX = CENTER.x - totalW / 2 + REPLICA_R;
    const y = CENTER.y + 14;
    nodes.forEach((node, i) => {
      const x = startX + i * (REPLICA_R * 2 + 6);
      positions[node.Name] = { x, y, status: node.Status, name: node.Name };
    });
    return positions;
  }, [slimNodeList]);

  const functionReplicaPositions = useMemo(() => {
    const result: Record<string, Record<string, ReplicaPosition>> = {};
    for (const fn of functions) {
      const pods = fn.Pods ?? [];
      const center = fnPositions[fn.Name];
      if (!center || pods.length === 0) continue;
      const step = (2 * Math.PI) / pods.length;
      const ring = BUBBLE_R + 18;
      result[fn.Name] = {};
      pods.forEach((pod, idx) => {
        const a = step * idx - Math.PI / 2;
        result[fn.Name][pod.Name] = {
          x: center.x + Math.cos(a) * ring,
          y: center.y + Math.sin(a) * ring,
          status: pod.Status,
          name: pod.Name,
        };
      });
    }
    return result;
  }, [functions, fnPositions]);

  useEffect(() => {
    fnPositionsRef.current = fnPositions;
  }, [fnPositions]);

  useEffect(() => {
    slimReplicaPositionsRef.current = slimReplicaPositions;
  }, [slimReplicaPositions]);

  useEffect(() => {
    functionReplicaPositionsRef.current = functionReplicaPositions;
  }, [functionReplicaPositions]);

  useEffect(() => {
    queuePositionsRef.current = queuePositions;
  }, [queuePositions]);

  const ipToPod = useMemo(() => {
    const map: Record<string, { functionName: string; podName: string }> = {};
    for (const fn of functions) {
      for (const pod of fn.Pods ?? []) {
        if (pod.Ip) map[pod.Ip] = { functionName: fn.Name, podName: pod.Name };
      }
    }
    return map;
  }, [functions]);

  const resolvePodLink = useCallback((ip: string | null | undefined) => {
    if (!ip) return null;
    const direct = ipToPod[ip];
    if (direct) return direct;
    const clean = ip.startsWith('::ffff:') ? ip.slice(7) : ip;
    return ipToPod[clean] ?? null;
  }, [ipToPod]);

  const resolvePodLabel = useCallback((ip: string | null | undefined) => {
    const link = resolvePodLink(ip);
    if (link) {
      const shortPod = link.podName.length > 18 ? `...${link.podName.slice(-15)}` : link.podName;
      return `${link.functionName}/${shortPod}`;
    }
    if (!ip) return undefined;
    return ip.startsWith('::ffff:') ? ip.slice(7) : ip;
  }, [resolvePodLink]);

  const getFunctionTargetPosition = useCallback((functionName: string, podIpOrName: string | null | undefined) => {
    const center = fnPositions[functionName] ?? CENTER;
    if (!podIpOrName) return center;
    const link = resolvePodLink(podIpOrName);
    const resolvedFn = link?.functionName ?? functionName;
    const podName = link?.podName ?? podIpOrName;
    const replicas = functionReplicaPositions[resolvedFn];
    if (replicas && replicas[podName]) return { x: replicas[podName].x, y: replicas[podName].y };
    return center;
  }, [fnPositions, functionReplicaPositions, resolvePodLink]);

  const getSlimPosition = useCallback((nodeId: string | null | undefined) => {
    if (nodeId && slimReplicaPositions[nodeId]) {
      const p = slimReplicaPositions[nodeId];
      return { x: p.x, y: p.y };
    }
    return CENTER;
  }, [slimReplicaPositions]);

  const incCounter = useCallback((node: string) => {
    activeCounterTouchedRef.current[node] = performance.now();
    activeCountersRef.current[node] = (activeCountersRef.current[node] || 0) + 1;
  }, []);

  const decCounter = useCallback((node: string) => {
    activeCounterTouchedRef.current[node] = performance.now();
    const next = Math.max(0, (activeCountersRef.current[node] || 0) - 1);
    activeCountersRef.current[node] = next;
    if (next === 0) delete activeCounterTouchedRef.current[node];
  }, []);

  const incReplicaCounter = useCallback((key: string) => {
    activeReplicaCounterTouchedRef.current[key] = performance.now();
    activeReplicaCountersRef.current[key] = (activeReplicaCountersRef.current[key] || 0) + 1;
  }, []);

  const decReplicaCounter = useCallback((key: string) => {
    activeReplicaCounterTouchedRef.current[key] = performance.now();
    const next = Math.max(0, (activeReplicaCountersRef.current[key] || 0) - 1);
    activeReplicaCountersRef.current[key] = next;
    if (next === 0) delete activeReplicaCounterTouchedRef.current[key];
  }, []);

  const fnReplicaKey = useCallback((functionName: string, podName: string) => `fn:${functionName}:${podName}`, []);
  const sfReplicaKey = useCallback((nodeName: string) => `sf:${nodeName}`, []);

  const queueMessage = useCallback((message: AnimatedMessage) => {
    animRef.current.push(message);
  }, []);

  const spawnEvent = useCallback((evt: NetworkActivityEvent, startAt: number) => {
    const fp = (name: string) => fnPositions[name] || null;
    const qp = (name: string) => queuePositions[name] || null;
    const srcLabel = resolvePodLabel(evt.SourcePod);
    const tgtLabel = resolvePodLabel(evt.TargetPod);
    const slim = getSlimPosition(evt.NodeId);

    if (evt.Type === 'enqueue') {
      const q = qp(evt.Target);
      if (!q) return;
      // Async flow: once queued, the external in-flight request is considered completed.
      decCounter('external');
      const startPos = evt.Source === 'external' ? externalPos : slim;
      queueMessage({
        id: `${evt.Id}|enqueue`,
        segments: [{ from: startPos, to: slim }, { from: slim, to: q }],
        currentSeg: 0,
        color: nameColor(evt.Target),
        shape: 'rect',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: SEG_GAP,
        label: srcLabel || tgtLabel,
      });
      return;
    }

    if (evt.Type === 'dequeue') {
      const q = qp(evt.Target);
      const f = fp(evt.Target);
      if (!q || !f) return;
      incCounter(`queue:${evt.Target}`);
      queueMessage({
        id: `${evt.Id}|dequeue`,
        segments: [{ from: q, to: getFunctionTargetPosition(evt.Target, evt.TargetPod) || f }],
        currentSeg: 0,
        color: nameColor(evt.Target),
        shape: 'rect',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: 0,
        label: tgtLabel,
      });
      return;
    }

    if (evt.Type === 'request_in') {
      const link = resolvePodLink(evt.SourcePod);
      const startPos = link ? getFunctionTargetPosition(link.functionName, link.podName) : externalPos;
      const sourceColor = link?.functionName ? nameColor(link.functionName) : '#6b778c';
      if (link) {
        incCounter(link.functionName);
        incReplicaCounter(fnReplicaKey(link.functionName, link.podName));
      } else {
        incCounter('external');
      }
      queueMessage({
        id: `${evt.Id}|request_in`,
        segments: [{ from: startPos, to: slim }],
        currentSeg: 0,
        color: sourceColor,
        shape: 'circle',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: 0,
        label: srcLabel,
      });
      return;
    }

    if (evt.Type === 'request_out') {
      const f = fp(evt.Target);
      if (!f) return;
      incCounter('slimfaas');
      if (evt.NodeId) incReplicaCounter(sfReplicaKey(evt.NodeId));
      queueMessage({
        id: `${evt.Id}|request_out`,
        segments: [{ from: slim, to: getFunctionTargetPosition(evt.Target, evt.TargetPod) || f }],
        currentSeg: 0,
        color: nameColor(evt.Target),
        shape: 'circle',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: 0,
        label: tgtLabel || srcLabel,
      });
      return;
    }

    if (evt.Type === 'request_waiting' || evt.Type === 'request_started') {
      return;
    }

    if (evt.Type === 'request_end') {
      // Async queue flow ends here: external request closes when queue processing completes.
      if (evt.QueueName) {
        decCounter(`queue:${evt.QueueName}`);
        return;
      }

      // Convention C# (Sync + Event endpoints) :
      //   Source = "slimfaas", Target = callerIdentity,
      //   SourcePod = caller IP, TargetPod = downstream pod IP (peut être null).
      // Le sender (caller) est donc identifié par evt.SourcePod (et NON evt.TargetPod
      // qui désigne le pod aval ayant traité la requête).
      decCounter('slimfaas');
      if (evt.NodeId) decReplicaCounter(sfReplicaKey(evt.NodeId));

      const caller = resolvePodLink(evt.SourcePod);
      if (caller) {
        decCounter(caller.functionName);
        decReplicaCounter(fnReplicaKey(caller.functionName, caller.podName));
      } else {
        // Caller externe (IP inconnue ou absente, ex. chemin WebSocket / exception
        // avant sélection du pod) → toujours décrémenter "external" pour rester
        // symétrique avec l'incrément de request_in.
        decCounter('external');
      }
      return;
    }

    if (evt.Type === 'event_publish') {
      const f = fp(evt.Target);
      if (!f) return;
      queueMessage({
        id: `${evt.Id}|event_publish`,
        segments: [{ from: slim, to: getFunctionTargetPosition(evt.Target, evt.TargetPod) || f }],
        currentSeg: 0,
        color: '#fab005',
        shape: 'circle',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: 0,
        label: tgtLabel,
      });
      return;
    }

    if (evt.Type === 'response') {
      const fromFn = evt.Source !== 'slimfaas' ? evt.Source : evt.Target;
      const fromPos = getFunctionTargetPosition(fromFn, evt.SourcePod) || (fp(fromFn) ?? CENTER);
      const toPos = evt.Target === 'external' ? externalPos : slim;
      queueMessage({
        id: `${evt.Id}|response`,
        segments: [{ from: fromPos, to: toPos }],
        currentSeg: 0,
        color: fromFn ? nameColor(fromFn) : '#6b778c',
        shape: 'circle',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: 0,
        label: srcLabel,
      });
    }
  }, [decCounter, decReplicaCounter, externalPos, fnPositions, fnReplicaKey, getFunctionTargetPosition, getSlimPosition, incCounter, incReplicaCounter, queueMessage, queuePositions, resolvePodLabel, resolvePodLink, sfReplicaKey]);

  useEffect(() => {
    const fresh: NetworkActivityEvent[] = [];
    for (const evt of activity) {
      const key = eventKey(evt);
      if (seenEventsRef.current.has(key)) continue;
      seenEventsRef.current.add(key);
      fresh.push(evt);
    }

    if (fresh.length === 0) return;

    fresh.sort((a, b) => {
      const da = utcMs(a.TimestampMs);
      const db = utcMs(b.TimestampMs);
      if (da !== db) return da - db;
      return a.Id.localeCompare(b.Id);
    });

    let cursorUtc = lastScheduledUtcRef.current || utcMs(fresh[0].TimestampMs);
    let cursorPerf = Math.max(lastScheduledPerfRef.current, performance.now());

    for (const evt of fresh) {
      const evtUtc = utcMs(evt.TimestampMs);
      const gap = Math.max(CHAIN_GAP_MIN, Math.min(CHAIN_GAP_MAX, evtUtc - cursorUtc));
      cursorPerf += gap;
      cursorUtc = Math.max(cursorUtc, evtUtc);
      spawnEvent(evt, cursorPerf);
    }

    lastScheduledUtcRef.current = cursorUtc;
    lastScheduledPerfRef.current = cursorPerf;

    if (seenEventsRef.current.size > 1200) {
      seenEventsRef.current = new Set(Array.from(seenEventsRef.current).slice(-800));
    }
  }, [activity, spawnEvent]);

  const animate = useCallback(() => {
    const wg = worldGRef.current;
    if (!wg) {
      frameRef.current = requestAnimationFrame(animate);
      return;
    }

    const now = performance.now();

    // Safety net: if an end event is lost under burst traffic, clear stale in-flight counters.
    if (now - lastCounterSweepRef.current >= COUNTER_SWEEP_MS) {
      lastCounterSweepRef.current = now;
      for (const [key, count] of Object.entries(activeCountersRef.current)) {
        if (count <= 0) continue;
        const touched = activeCounterTouchedRef.current[key] ?? now;
        if (now - touched >= COUNTER_STALE_MS) {
          activeCountersRef.current[key] = 0;
          delete activeCounterTouchedRef.current[key];
        }
      }
      for (const [key, count] of Object.entries(activeReplicaCountersRef.current)) {
        if (count <= 0) continue;
        const touched = activeReplicaCounterTouchedRef.current[key] ?? now;
        if (now - touched >= COUNTER_STALE_MS) {
          activeReplicaCountersRef.current[key] = 0;
          delete activeReplicaCounterTouchedRef.current[key];
        }
      }
    }

    const alive: AnimatedMessage[] = [];
    for (const m of animRef.current) {
      if (now < m.startTime) {
        alive.push(m);
        continue;
      }

      const t = (now - m.startTime) / m.segDuration;
      if (t >= 1) {
        if (m.currentSeg < m.segments.length - 1) {
          m.currentSeg += 1;
          m.startTime = now + m.segmentGapMs;
          alive.push(m);
          }
      } else {
        alive.push(m);
      }
    }
    animRef.current = alive;

    interface MsgRender {
      id: string;
      cx: number;
      cy: number;
      color: string;
      shape: 'circle' | 'rect';
      opacity: number;
      label?: string;
    }

    const msgData: MsgRender[] = [];
    for (const m of animRef.current) {
      if (now < m.startTime) continue;
      const seg = m.segments[m.currentSeg];
      const t = Math.min(1, (now - m.startTime) / m.segDuration);
      const e = d3.easeCubicInOut(t);
      const cx = seg.from.x + (seg.to.x - seg.from.x) * e;
      const cy = seg.from.y + (seg.to.y - seg.from.y) * e;
      const isLast = m.currentSeg === m.segments.length - 1;
      const opacity = (t > 0.9 && isLast) ? Math.max(0.15, 1 - (t - 0.9) / 0.1) : 0.92;
      msgData.push({ id: m.id, cx, cy, color: m.color, shape: m.shape, opacity, label: m.label });
    }

    const g = d3.select(wg).select<SVGGElement>('.nw-messages');
    const counterG = d3.select(wg).select<SVGGElement>('.nw-counters');

    const rectData = msgData.filter(m => m.shape === 'rect');
    const rs = g.selectAll<SVGRectElement, MsgRender>('rect.msg-rect').data(rectData, d => d.id);
    rs.enter().append('rect').attr('class', 'msg-rect').attr('width', 8).attr('height', 6).attr('rx', 1.5)
      .merge(rs)
      .attr('fill', d => d.color)
      .attr('x', d => d.cx - 4)
      .attr('y', d => d.cy - 3)
      .attr('opacity', d => d.opacity);
    rs.exit().remove();

    const circData = msgData.filter(m => m.shape === 'circle');
    const cs = g.selectAll<SVGCircleElement, MsgRender>('circle.msg-circle').data(circData, d => d.id);
    cs.enter().append('circle').attr('class', 'msg-circle').attr('r', 5)
      .merge(cs)
      .attr('fill', d => lightenColor(d.color, 0))
      .attr('cx', d => d.cx)
      .attr('cy', d => d.cy)
      .attr('opacity', d => d.opacity);
    cs.exit().remove();

    const labelData = msgData.filter(m => !!m.label);
    const mls = g.selectAll<SVGTextElement, MsgRender>('text.msg-label').data(labelData, d => d.id);
    mls.enter().append('text').attr('class', 'msg-label').attr('font-size', 7).attr('fill', '#495057').attr('font-weight', 600).attr('pointer-events', 'none')
      .merge(mls)
      .attr('x', d => d.cx + 8)
      .attr('y', d => d.cy + 2)
      .attr('opacity', d => d.opacity * 0.85)
      .text(d => d.label ?? '');
    mls.exit().remove();

    const counters = activeCountersRef.current;
    const replicaCounters = activeReplicaCountersRef.current;
    const liveFnPositions = fnPositionsRef.current;
    const liveSlimReplicaPositions = slimReplicaPositionsRef.current;
    const liveFunctionReplicaPositions = functionReplicaPositionsRef.current;
    type CDatum = { key: string; x: number; y: number; count: number };
    const cData: CDatum[] = [];

    const sfC = counters.slimfaas || 0;
    if (sfC > 0) cData.push({ key: 'slimfaas', x: CENTER.x + 55, y: CENTER.y - 18, count: sfC });
    const exC = counters.external || 0;
    if (exC > 0) cData.push({ key: 'external', x: externalPos.x + BUBBLE_R + 6, y: externalPos.y - BUBBLE_R + 2, count: exC });

    for (const fnName of Object.keys(liveFnPositions)) {
      const c = counters[fnName] || 0;
      if (c > 0) {
        const p = liveFnPositions[fnName];
        cData.push({ key: fnName, x: p.x + BUBBLE_R + 4, y: p.y - BUBBLE_R + 2, count: c });
      }
      // Queue in-flight counter badge (positioned near the queue box)
      const qc = counters[`queue:${fnName}`] || 0;
      if (qc > 0) {
        const qpRef = queuePositionsRef.current[fnName];
        if (qpRef) {
          cData.push({ key: `queue:${fnName}`, x: qpRef.x + QUEUE_BOX_W / 2 + 6, y: qpRef.y - QUEUE_BOX_H / 2, count: qc });
        }
      }
    }

    const slimEntries = Object.entries(liveSlimReplicaPositions).sort((a, b) => a[0].localeCompare(b[0]));
    slimEntries.forEach(([nodeName, rp], idx) => {
      const key = sfReplicaKey(nodeName);
      const c = replicaCounters[key] || 0;
      if (c > 0) {
        const dx = 6 + (idx % 2) * 4;
        const dy = -6 - (idx % 3) * 3;
        cData.push({ key, x: rp.x + REPLICA_R + dx, y: rp.y - REPLICA_R + dy, count: c });
      }
    });

    for (const [fnName, pods] of Object.entries(liveFunctionReplicaPositions)) {
      const podEntries = Object.entries(pods).sort((a, b) => a[0].localeCompare(b[0]));
      podEntries.forEach(([podName, rp], idx) => {
        const key = fnReplicaKey(fnName, podName);
        const c = replicaCounters[key] || 0;
        if (c > 0) {
          const dx = 6 + (idx % 2) * 4;
          const dy = -6 - (idx % 3) * 3;
          cData.push({ key, x: rp.x + REPLICA_R + dx, y: rp.y - REPLICA_R + dy, count: c });
        }
      });
    }

    const cSel = counterG.selectAll<SVGGElement, CDatum>('g.counter-badge').data(cData, d => d.key);
    const cEnter = cSel.enter().append('g').attr('class', 'counter-badge');
    cEnter.append('rect').attr('rx', 7).attr('ry', 7).attr('height', 14).attr('fill', '#ff6b6b').attr('stroke', '#fff').attr('stroke-width', 1);
    cEnter.append('text').attr('text-anchor', 'middle').attr('font-size', 8).attr('font-weight', 'bold').attr('fill', '#fff').attr('dy', 10.5);
    const cMerge = cEnter.merge(cSel);
    cMerge.attr('transform', d => `translate(${d.x},${d.y})`);
    cMerge.select('text').text(d => `${d.count}`);
    cMerge.select('rect')
      .attr('width', d => Math.max(16, `${d.count}`.length * 7 + 8))
      .attr('x', d => -Math.max(16, `${d.count}`.length * 7 + 8) / 2);
    cSel.exit().remove();

    frameRef.current = requestAnimationFrame(animate);
  }, [externalPos, fnReplicaKey, sfReplicaKey]);

  const structuralKey = `${fnNamesKey}|${queueFnKey}`;

  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;

    const sel = d3.select(svg);
    sel.selectAll('*').remove();

    userHasZoomedRef.current = false;
    initialFitDoneRef.current = false;
    activeCountersRef.current = {};
    activeReplicaCountersRef.current = {};
    activeCounterTouchedRef.current = {};
    activeReplicaCounterTouchedRef.current = {};
    lastCounterSweepRef.current = 0;

    const defs = sel.append('defs');
    const ds = defs.append('filter').attr('id', 'shadow').attr('x', '-20%').attr('y', '-20%').attr('width', '140%').attr('height', '140%');
    ds.append('feDropShadow').attr('dx', 0).attr('dy', 2).attr('stdDeviation', 3).attr('flood-opacity', 0.15);

    sel.append('rect').attr('class', 'nw-bg').attr('width', '100%').attr('height', '100%').attr('fill', '#f8f9fa');
    const world = sel.append('g').attr('class', 'nw-world');
    worldGRef.current = world.node()!;

    const zoom = d3.zoom<SVGSVGElement, unknown>()
      .scaleExtent([0.15, 5])
      .on('zoom', (event) => {
        world.attr('transform', event.transform);
        if (event.sourceEvent) userHasZoomedRef.current = true;
      });
    zoomRef.current = zoom;
    sel.call(zoom);
    sel.on('dblclick.zoom', null);
    sel.on('dblclick', () => { userHasZoomedRef.current = false; zoomToFit(true); });

    world.append('circle')
      .attr('cx', externalPos.x)
      .attr('cy', externalPos.y)
      .attr('r', BUBBLE_R)
      .attr('fill', '#adb5bd')
      .attr('stroke', '#adb5bd')
      .attr('stroke-width', 2.5)
      .attr('opacity', 0.85)
      .attr('filter', 'url(#shadow)');
    world.append('text').attr('x', externalPos.x).attr('y', externalPos.y + 4)
      .attr('text-anchor', 'middle').attr('fill', '#fff').attr('font-weight', 'bold').attr('font-size', 10)
      .text('External');

    const sfW = 100;
    const sfH = 44;
    world.append('rect').attr('x', CENTER.x - sfW / 2).attr('y', CENTER.y - sfH / 2)
      .attr('width', sfW).attr('height', sfH).attr('rx', 10)
      .attr('fill', '#0000ff').attr('filter', 'url(#shadow)');
    world.append('text').attr('x', CENTER.x).attr('y', CENTER.y - 2)
      .attr('text-anchor', 'middle').attr('fill', '#fff').attr('font-weight', 'bold').attr('font-size', 14)
      .text('SlimFaas');
    world.append('g').attr('class', 'sf-replicas');

    const fnNames = fnNamesKey.split(',').filter(Boolean);
    const queueFnNames = new Set(queueFnKey.split(',').filter(Boolean));

    fnNames.forEach((fnName) => {
      if (!queueFnNames.has(fnName)) return;
      const qCenter = queuePositions[fnName];
      if (!qCenter) return;
      const color = nameColor(fnName);
      const qCX = qCenter.x;
      const qCY = qCenter.y;
      const qLeft = qCX - QUEUE_BOX_W / 2;

      world.append('rect').attr('class', `q-box-${fnName}`).attr('x', qLeft).attr('y', qCY - QUEUE_BOX_H / 2)
        .attr('width', QUEUE_BOX_W).attr('height', QUEUE_BOX_H).attr('rx', 4).attr('ry', 4)
        .attr('fill', '#fff').attr('stroke', color).attr('stroke-width', 1.5).attr('filter', 'url(#shadow)');
      world.append('g').attr('class', `q-fill-${fnName}`);
      world.append('text').attr('x', qCX).attr('y', qCY - QUEUE_BOX_H / 2 - 4)
        .attr('text-anchor', 'middle').attr('font-size', 9).attr('fill', '#6b778c').attr('font-weight', '600')
        .text(fnName.length > 14 ? `${fnName.slice(0, 12)}...` : fnName);
      world.append('text').attr('class', `q-count-${fnName}`).attr('x', qCX).attr('y', qCY + QUEUE_BOX_H / 2 + 12)
        .attr('text-anchor', 'middle').attr('font-size', 9).attr('font-weight', 'bold');
    });

    fnNames.forEach((fnName) => {
      const pos = fnPositions[fnName];
      if (!pos) return;
      const color = nameColor(fnName);
      const escaped = CSS.escape(fnName);
      world.append('g').attr('class', `fn-group-${escaped}`);
      world.append('circle').attr('class', `fn-bubble-${escaped}`)
        .attr('cx', pos.x).attr('cy', pos.y).attr('r', BUBBLE_R)
        .attr('fill', color).attr('stroke', color).attr('stroke-width', 2.5)
        .attr('opacity', 0.85).attr('filter', 'url(#shadow)');
      world.append('text').attr('class', `fn-name-${escaped}`)
        .attr('x', pos.x).attr('y', pos.y - 5)
        .attr('text-anchor', 'middle').attr('font-size', 10).attr('font-weight', 'bold').attr('fill', '#fff')
        .text(fnName.length > 14 ? `${fnName.slice(0, 12)}...` : fnName);
      world.append('text').attr('class', `fn-count-${escaped}`)
        .attr('x', pos.x).attr('y', pos.y + 8)
        .attr('text-anchor', 'middle').attr('font-size', 9);
      world.append('g').attr('class', `fn-pods-${escaped}`);
    });

    world.append('g').attr('class', 'nw-messages');
    world.append('g').attr('class', 'nw-counters');

    frameRef.current = requestAnimationFrame(animate);
    const fitTimer = setTimeout(() => {
      if (!userHasZoomedRef.current) {
        zoomToFit(false);
        initialFitDoneRef.current = true;
      }
    }, 80);

    return () => {
      cancelAnimationFrame(frameRef.current);
      clearTimeout(fitTimer);
    };
  }, [animate, externalPos, fnNamesKey, fnPositions, queueFnKey, queuePositions, structuralKey, zoomToFit]);

  useEffect(() => {
    const wg = worldGRef.current;
    if (!wg) return;
    const world = d3.select(wg);

    functions.forEach((fn) => {
      const pos = fnPositions[fn.Name];
      if (!pos) return;
      const color = nameColor(fn.Name);
      const isDown = (fn.NumberReady ?? 0) === 0;
      const pods = fn.Pods ?? [];
      const r = BUBBLE_R + Math.min(pods.length * 3, 14);
      const escaped = CSS.escape(fn.Name);

      world.select(`.fn-bubble-${escaped}`).attr('r', r).attr('fill', isDown ? '#f8f9fa' : color).attr('opacity', isDown ? 0.5 : 0.85);
      world.select(`.fn-name-${escaped}`).attr('fill', isDown ? '#6b778c' : '#fff');
      world.select(`.fn-count-${escaped}`).attr('fill', isDown ? '#adb5bd' : 'rgba(255,255,255,0.8)').text(`${fn.NumberReady}/${fn.NumberRequested}`);

      const podGroup = world.select<SVGGElement>(`.fn-pods-${escaped}`);
      podGroup.selectAll('*').remove();
      if (pods.length > 0) {
        const replicas = functionReplicaPositions[fn.Name] || {};
        for (const pod of pods) {
          const rp = replicas[pod.Name];
          if (!rp) continue;
          const podG = podGroup.append('g').style('cursor', 'pointer');
          podG.append('circle')
            .attr('cx', rp.x)
            .attr('cy', rp.y)
            .attr('r', REPLICA_R)
            .attr('fill', podColor(pod.Status))
            .attr('stroke', '#fff')
            .attr('stroke-width', 1.5);
          podG.append('text')
            .attr('x', rp.x)
            .attr('y', rp.y + REPLICA_R + 8)
            .attr('text-anchor', 'middle')
            .attr('font-size', 7)
            .attr('fill', '#495057')
            .text(pod.Name);
          podG.append('title').text(`${pod.Name}\nIP: ${pod.Ip || 'N/A'}\nStatus: ${pod.Status}\nReady: ${pod.Ready}`);
        }
      }

      const qLen = queueMap[fn.Name] ?? 0;
      const qCenter = queuePositions[fn.Name];
      if (qCenter) {
        const qCX = qCenter.x;
        const qCY = qCenter.y;
        const qLeft = qCX - QUEUE_BOX_W / 2;
        world.select(`.q-count-${CSS.escape(fn.Name)}`).attr('fill', qLen > 0 ? color : '#adb5bd').text(`in ${qLen}`);

        const fillGroup = world.select<SVGGElement>(`.q-fill-${CSS.escape(fn.Name)}`);
        fillGroup.selectAll('*').remove();
        if (qLen > 0) {
          const fillRatio = Math.min(1, qLen / 20);
          fillGroup.append('rect').attr('x', qLeft + 1).attr('y', qCY - QUEUE_BOX_H / 2 + 1)
            .attr('width', Math.max(0, (QUEUE_BOX_W - 2) * fillRatio)).attr('height', QUEUE_BOX_H - 2)
            .attr('rx', 3).attr('fill', lightenColor(color, 0.3)).attr('opacity', 0.6);
        }
      }
    });

    const sfGroup = world.select<SVGGElement>('.sf-replicas');
    sfGroup.selectAll('*').remove();
    for (const node of slimNodeList) {
      const rp = slimReplicaPositions[node.Name];
      if (!rp) continue;
      const c = podColor(node.Status);
      sfGroup.append('circle')
        .attr('cx', rp.x)
        .attr('cy', rp.y)
        .attr('r', REPLICA_R)
        .attr('fill', c)
        .attr('stroke', '#fff')
        .attr('stroke-width', 1.5);
      sfGroup.append('text')
        .attr('x', rp.x)
        .attr('y', rp.y + REPLICA_R + 8)
        .attr('text-anchor', 'middle')
        .attr('font-size', 7)
        .attr('fill', '#e9ecef')
        .text(node.Name);
      sfGroup.append('title').text(`${node.Name} - ${node.Status}`);
    }
  }, [functionReplicaPositions, functions, fnPositions, queueMap, queuePositions, slimNodeList, slimReplicaPositions]);

  useEffect(() => {
    if (!initialFitDoneRef.current) return;
    if (userHasZoomedRef.current) return;
    const t = setTimeout(() => zoomToFit(true), 50);
    return () => clearTimeout(t);
  }, [structuralKey, zoomToFit]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const ro = new ResizeObserver(() => {
      if (!userHasZoomedRef.current) zoomToFit(false);
    });
    ro.observe(container);
    return () => ro.disconnect();
  }, [zoomToFit]);

  return (
    <div className="network-map" ref={containerRef}>
      <h2 className="network-map__title">
        Live Network Map
        <button
          className="network-map__reset-btn"
          type="button"
          title="Reset zoom to fit all elements"
          onClick={() => { userHasZoomedRef.current = false; zoomToFit(true); }}
        >
          Reset
        </button>
      </h2>
      <svg ref={svgRef} className="network-map__svg" />
      <div className="network-map__legend">
        <span className="network-map__legend-item"><span className="network-map__legend-dot" style={{ backgroundColor: '#adb5bd' }} /> External</span>
        <span className="network-map__legend-item"><span className="network-map__legend-dot" style={{ backgroundColor: '#0000ff' }} /> SlimFaas</span>
        {functions.slice(0, 8).map(fn => (
          <span key={fn.Name} className="network-map__legend-item">
            <span className="network-map__legend-dot" style={{ backgroundColor: nameColor(fn.Name) }} />
            {fn.Name}
          </span>
        ))}
        {functions.length > 8 && <span className="network-map__legend-item" style={{ fontStyle: 'italic' }}>+{functions.length - 8} more</span>}
        <span className="network-map__legend-item" style={{ marginLeft: 12 }}><svg width="14" height="10"><circle cx="5" cy="5" r="4" fill="#4c6ef5" /></svg> Request</span>
        <span className="network-map__legend-item"><svg width="14" height="10"><rect x="1" y="2" width="10" height="6" rx="1.5" fill="#6b778c" /></svg> Async</span>
        <span className="network-map__legend-item"><svg width="10" height="10"><circle cx="5" cy="5" r="5" fill="#36b37e" /></svg> Replica</span>
        <span className="network-map__legend-separator" />
        <span className="network-map__legend-item" title="Active requests badge"><svg width="18" height="14"><rect x="1" y="0" width="16" height="14" rx="7" fill="#ff6b6b" stroke="#fff" strokeWidth="1" /><text x="9" y="10.5" textAnchor="middle" fontSize="8" fontWeight="bold" fill="#fff">3</text></svg> Active</span>
      </div>
    </div>
  );
};

export default NetworkMap;

