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

/* ── Helpers ── */

function nameColor(name: string): string {
  const colors = [
    '#4c6ef5', '#7950f2', '#e64980', '#f76707', '#36b37e',
    '#00b8d9', '#fab005', '#e8590c', '#ae3ec9', '#0ca678',
    '#2f9e44', '#1098ad', '#f08c00', '#d6336c', '#6741d9',
  ];
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

/* ── Types ── */

/** A single leg of a chained animation. */
interface AnimSegment {
  from: { x: number; y: number };
  to: { x: number; y: number };
}

/**
 * A message that travels through chained segments.
 * Each segment must complete before the next one starts.
 */
interface AnimatedMessage {
  id: string;
  segments: AnimSegment[];
  currentSeg: number;       // index of the segment currently being animated
  color: string;
  shape: 'circle' | 'rect';
  startTime: number;         // start of the *current* segment
  segDuration: number;       // ms per segment
  label?: string;
  direction: 'forward' | 'return';
  sourceNode?: string;       // node key for counter tracking
}

interface ActiveTrace {
  functionName: string;
  type: 'sync' | 'waiting';
  startTime: number;
  podLabel?: string;
}

/* ── Constants ── */

const SEG_DURATION = 800;
const BUBBLE_R = 32;
const QUEUE_BOX_W = 60;
const QUEUE_BOX_H = 20;
const CENTER = { x: 0, y: 0 };
const EXTERNAL_OFFSET = { x: -300, y: 0 };
const SVG_VIEW_H = 500;

function radialPositions(n: number): { x: number; y: number }[] {
  if (n === 0) return [];
  if (n === 1) return [{ x: 0, y: 260 }];
  const baseR = 250;
  const perItem = 120;
  const radius = Math.max(baseR, (n * perItem) / (2 * Math.PI));
  return Array.from({ length: n }, (_, i) => {
    const angle = (2 * Math.PI * i) / n - Math.PI / 2;
    return { x: Math.cos(angle) * radius, y: Math.sin(angle) * radius };
  });
}

/* ── Component ── */

const NetworkMap: React.FC<Props> = ({ functions, queues, activity, functionsWithQueueActivity, slimFaasReplicas, slimFaasNodes }) => {
  const svgRef = useRef<SVGSVGElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const animRef = useRef<AnimatedMessage[]>([]);
  const tracesRef = useRef<Map<string, ActiveTrace>>(new Map());
  const seenIdsRef = useRef<Set<string>>(new Set());
  const frameRef = useRef(0);
  const zoomRef = useRef<d3.ZoomBehavior<SVGSVGElement, unknown> | null>(null);
  const worldGRef = useRef<SVGGElement | null>(null);
  const userHasZoomedRef = useRef(false);
  const initialFitDoneRef = useRef(false);

  // ── Active request counters per node ──
  const activeCountersRef = useRef<Record<string, number>>({});

  /* ── IP → Pod lookup ── */

  const ipToPod = useMemo(() => {
    const map: Record<string, { functionName: string; podName: string }> = {};
    for (const fn of functions) {
      for (const pod of fn.Pods ?? []) {
        if (pod.Ip) {
          map[pod.Ip] = { functionName: fn.Name, podName: pod.Name };
        }
      }
    }
    return map;
  }, [functions]);

  const resolvePodLabel = useCallback((ip: string | null | undefined): string | undefined => {
    if (!ip) return undefined;
    const entry = ipToPod[ip];
    if (entry) {
      const shortPod = entry.podName.includes('-')
        ? entry.podName.slice(entry.podName.lastIndexOf('-') + 1)
        : entry.podName;
      return `${entry.functionName}/${shortPod}`;
    }
    const clean = ip.startsWith('::ffff:') ? ip.slice(7) : ip;
    const entry2 = ipToPod[clean];
    if (entry2) {
      const shortPod = entry2.podName.includes('-')
        ? entry2.podName.slice(entry2.podName.lastIndexOf('-') + 1)
        : entry2.podName;
      return `${entry2.functionName}/${shortPod}`;
    }
    return clean;
  }, [ipToPod]);

  const resolveFunction = useCallback((ip: string | null | undefined): string | undefined => {
    if (!ip) return undefined;
    const entry = ipToPod[ip];
    if (entry) return entry.functionName;
    const clean = ip.startsWith('::ffff:') ? ip.slice(7) : ip;
    return ipToPod[clean]?.functionName;
  }, [ipToPod]);

  /* ── Positions ── */

  // Structural key: only the sorted list of function names
  const fnNamesKey = useMemo(() => functions.map(f => f.Name).sort().join(','), [functions]);

  const fnPositions = useMemo(() => {
    const names = fnNamesKey.split(',').filter(Boolean);
    const pos: Record<string, { x: number; y: number }> = {};
    const pts = radialPositions(names.length);
    names.forEach((name, i) => { pos[name] = pts[i]; });
    return pos;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fnNamesKey]);

  // Queue positions only depend on which functions HAVE had queue activity (structural)
  const queueFnKey = useMemo(() => {
    const names: string[] = [];
    for (const fn of functions) {
      const qLen = queues.find(q => q.Name === fn.Name)?.Length ?? 0;
      if (functionsWithQueueActivity.has(fn.Name) || qLen > 0) {
        names.push(fn.Name);
      }
    }
    return names.sort().join(',');
  }, [functions, queues, functionsWithQueueActivity]);

  const queuePositions = useMemo(() => {
    const pos: Record<string, { x: number; y: number }> = {};
    const names = queueFnKey.split(',').filter(Boolean);
    for (const name of names) {
      const fp = fnPositions[name];
      if (!fp) continue;
      pos[name] = { x: (CENTER.x + fp.x) / 2, y: (CENTER.y + fp.y) / 2 };
    }
    return pos;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fnNamesKey, queueFnKey]);

  const queueMap = useMemo(() => {
    const m: Record<string, number> = {};
    queues.forEach(q => { m[q.Name] = q.Length; });
    return m;
  }, [queues]);

  /* ── Counter helpers ── */

  const incCounter = useCallback((node: string) => {
    activeCountersRef.current[node] = (activeCountersRef.current[node] || 0) + 1;
  }, []);

  const decCounter = useCallback((node: string) => {
    activeCountersRef.current[node] = Math.max(0, (activeCountersRef.current[node] || 0) - 1);
  }, []);

  /* ── Auto zoom-to-fit ── */

  const zoomToFit = useCallback((animated = true) => {
    const svg = svgRef.current;
    const worldG = worldGRef.current;
    const zoom = zoomRef.current;
    if (!svg || !worldG || !zoom) return;

    const svgW = svg.clientWidth || 800;
    const svgH = svg.clientHeight || SVG_VIEW_H;

    const allPts: { x: number; y: number }[] = [
      CENTER,
      EXTERNAL_OFFSET,
      ...Object.values(fnPositions),
      ...Object.values(queuePositions),
    ];
    if (allPts.length === 0) return;

    const pad = BUBBLE_R + 50;
    let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
    for (const p of allPts) {
      if (p.x - pad < minX) minX = p.x - pad;
      if (p.x + pad > maxX) maxX = p.x + pad;
      if (p.y - pad < minY) minY = p.y - pad;
      if (p.y + pad > maxY) maxY = p.y + pad;
    }
    const bboxW = maxX - minX || 1;
    const bboxH = maxY - minY || 1;
    const scale = Math.min(svgW / bboxW, svgH / bboxH, 2.5);
    const tx = svgW / 2 - (minX + bboxW / 2) * scale;
    const ty = svgH / 2 - (minY + bboxH / 2) * scale;

    const transform = d3.zoomIdentity.translate(tx, ty).scale(scale);
    if (animated) {
      d3.select(svg).transition().duration(400).call(zoom.transform, transform);
    } else {
      d3.select(svg).call(zoom.transform, transform);
    }
  }, [fnPositions, queuePositions]);

  /* ── Spawn animated messages (chained segments) ── */

  useEffect(() => {
    for (const evt of activity) {
      if (seenIdsRef.current.has(evt.Id)) continue;
      seenIdsRef.current.add(evt.Id);

      const fp = (name: string) => fnPositions[name] || null;
      const qp = (name: string) => queuePositions[name] || null;

      const srcLabel = resolvePodLabel(evt.SourcePod);
      const tgtLabel = resolvePodLabel(evt.TargetPod);
      const srcFn = resolveFunction(evt.SourcePod);

      const now = performance.now();

      if (evt.Type === 'enqueue') {
        // External/Source → SlimFaas → Queue (2 chained segments)
        const q = qp(evt.Target);
        if (!q) continue;
        const startPos = srcFn && fp(srcFn) ? fp(srcFn)! : EXTERNAL_OFFSET;
        incCounter('slimfaas');
        animRef.current.push({
          id: evt.Id, segments: [
            { from: startPos, to: CENTER },
            { from: CENTER, to: q },
          ], currentSeg: 0, color: nameColor(evt.Target), shape: 'rect',
          startTime: now, segDuration: SEG_DURATION,
          label: srcLabel || tgtLabel, direction: 'forward', sourceNode: 'slimfaas',
        });

      } else if (evt.Type === 'dequeue') {
        // Queue → Function (1 segment) — counter on the queue (sender)
        const q = qp(evt.Target);
        const f = fp(evt.Target);
        if (!q || !f) continue;
        incCounter(`queue:${evt.Target}`);
        animRef.current.push({
          id: evt.Id, segments: [
            { from: q, to: f },
          ], currentSeg: 0, color: nameColor(evt.Target), shape: 'rect',
          startTime: now, segDuration: SEG_DURATION,
          label: tgtLabel, direction: 'forward', sourceNode: `queue:${evt.Target}`,
        });

      } else if (evt.Type === 'request_in') {
        // External → SlimFaas (1 segment)
        const startPos = srcFn && fp(srcFn) ? fp(srcFn)! : EXTERNAL_OFFSET;
        const sourceColor = srcFn ? nameColor(srcFn) : '#6b778c';
        incCounter('external');
        animRef.current.push({
          id: evt.Id, segments: [
            { from: startPos, to: CENTER },
          ], currentSeg: 0, color: sourceColor, shape: 'circle',
          startTime: now, segDuration: SEG_DURATION,
          label: srcLabel, direction: 'forward', sourceNode: 'external',
        });

      } else if (evt.Type === 'request_out') {
        // SlimFaas → Function (sync, 1 segment)
        const f = fp(evt.Target);
        if (!f) continue;
        incCounter('slimfaas');
        animRef.current.push({
          id: evt.Id, segments: [
            { from: CENTER, to: f },
          ], currentSeg: 0, color: nameColor(evt.Target), shape: 'circle',
          startTime: now, segDuration: SEG_DURATION,
          label: tgtLabel || srcLabel, direction: 'forward', sourceNode: 'slimfaas',
        });

      } else if (evt.Type === 'request_waiting') {
        tracesRef.current.set(evt.Target, { functionName: evt.Target, type: 'waiting', startTime: now, podLabel: tgtLabel || srcLabel });
        incCounter('slimfaas');

      } else if (evt.Type === 'request_started') {
        const ex = tracesRef.current.get(evt.Target);
        if (ex) { ex.type = 'sync'; ex.startTime = now; ex.podLabel = tgtLabel || srcLabel; }

      } else if (evt.Type === 'request_end') {
        tracesRef.current.delete(evt.Source);
        decCounter('slimfaas');
        decCounter(`queue:${evt.Source}`);

      } else if (evt.Type === 'event_publish') {
        const f = fp(evt.Target);
        if (!f) continue;
        incCounter('slimfaas');
        animRef.current.push({
          id: evt.Id, segments: [
            { from: CENTER, to: f },
          ], currentSeg: 0, color: '#fab005', shape: 'circle',
          startTime: now, segDuration: SEG_DURATION,
          label: tgtLabel, direction: 'forward', sourceNode: 'slimfaas',
        });
      }
    }
    if (seenIdsRef.current.size > 500) {
      seenIdsRef.current = new Set(Array.from(seenIdsRef.current).slice(-300));
    }
  }, [activity, fnPositions, queuePositions, resolvePodLabel, resolveFunction, incCounter, decCounter]);

  /* ── Animation loop ── */

  const animate = useCallback(() => {
    const wg = worldGRef.current;
    if (!wg) { frameRef.current = requestAnimationFrame(animate); return; }
    const g = d3.select(wg).select<SVGGElement>('.nw-messages');
    const traceG = d3.select(wg).select<SVGGElement>('.nw-traces');
    const counterG = d3.select(wg).select<SVGGElement>('.nw-counters');
    const now = performance.now();

    // ── Advance chained messages: advance segment or remove if done ──
    const alive: AnimatedMessage[] = [];
    for (const m of animRef.current) {
      const t = (now - m.startTime) / m.segDuration;
      if (t >= 1) {
        // Current segment complete
        if (m.currentSeg < m.segments.length - 1) {
          // Advance to next segment
          m.currentSeg++;
          m.startTime = now;
          alive.push(m);
        } else {
          // All segments done — decrement counter if tracked
          if (m.sourceNode) decCounter(m.sourceNode);
        }
      } else {
        alive.push(m);
      }
    }
    animRef.current = alive;

    // ── Compute current position for each message ──
    interface MsgRender { id: string; cx: number; cy: number; color: string; shape: string; direction: string; opacity: number; label?: string }
    const msgData: MsgRender[] = animRef.current.map(m => {
      const seg = m.segments[m.currentSeg];
      const t = Math.min(1, (now - m.startTime) / m.segDuration);
      const e = d3.easeCubicInOut(t);
      const cx = seg.from.x + (seg.to.x - seg.from.x) * e;
      const cy = seg.from.y + (seg.to.y - seg.from.y) * e;
      const isLast = m.currentSeg === m.segments.length - 1;
      const opacity = (t > 0.9 && isLast) ? Math.max(0.15, 1 - (t - 0.9) / 0.1) : 0.92;
      return { id: m.id, cx, cy, color: m.color, shape: m.shape, direction: m.direction, opacity, label: m.label };
    });

    // ── Traces ──
    const traceData = Array.from(tracesRef.current.values());
    const tl = traceG.selectAll<SVGLineElement, ActiveTrace>('line.trace-line').data(traceData, d => d.functionName);
    tl.enter().append('line').attr('class', 'trace-line').attr('stroke-width', 3).attr('stroke-linecap', 'round')
      .merge(tl)
      .attr('x1', CENTER.x).attr('y1', CENTER.y + 22)
      .attr('x2', d => fnPositions[d.functionName]?.x ?? 0)
      .attr('y2', d => fnPositions[d.functionName]?.y ?? 0)
      .attr('stroke', d => d.type === 'waiting' ? '#fab005' : nameColor(d.functionName))
      .attr('opacity', d => d.type === 'waiting' ? 0.25 + 0.25 * Math.sin((now - d.startTime) / 600 * Math.PI) : 0.35)
      .attr('stroke-dasharray', d => d.type === 'waiting' ? '8 4' : '6 3');
    tl.exit().remove();

    // Waiting dots
    const wd = traceData.filter(d => d.type === 'waiting');
    const wdSel = traceG.selectAll<SVGCircleElement, ActiveTrace>('circle.wait-dot').data(wd, d => d.functionName);
    wdSel.enter().append('circle').attr('class', 'wait-dot').attr('r', 5)
      .merge(wdSel).attr('fill', '#fab005')
      .attr('cx', d => { const f = fnPositions[d.functionName]; const tt = ((now - d.startTime) / 2000) % 1; return CENTER.x + ((f?.x ?? 0) - CENTER.x) * tt; })
      .attr('cy', d => { const f = fnPositions[d.functionName]; const tt = ((now - d.startTime) / 2000) % 1; return CENTER.y + ((f?.y ?? 0) - CENTER.y) * tt; })
      .attr('opacity', d => 0.5 + 0.4 * Math.sin((now - d.startTime) / 600 * Math.PI));
    wdSel.exit().remove();

    // Trace pod labels
    const traceLabelData = traceData.filter(d => !!d.podLabel);
    const tlLabels = traceG.selectAll<SVGTextElement, ActiveTrace>('text.trace-pod-label').data(traceLabelData, d => d.functionName);
    tlLabels.enter().append('text').attr('class', 'trace-pod-label').attr('font-size', 7).attr('fill', '#6b778c').attr('font-weight', 600).attr('pointer-events', 'none')
      .merge(tlLabels)
      .attr('x', d => { const f = fnPositions[d.functionName]; return CENTER.x + ((f?.x ?? 0) - CENTER.x) * 0.5 + 6; })
      .attr('y', d => { const f = fnPositions[d.functionName]; return CENTER.y + ((f?.y ?? 0) - CENTER.y) * 0.5 - 6; })
      .attr('opacity', d => d.type === 'waiting' ? 0.6 : 0.5)
      .text(d => d.podLabel ?? '');
    tlLabels.exit().remove();

    // ── Rect messages ──
    const rectData = msgData.filter(m => m.shape === 'rect');
    const rs = g.selectAll<SVGRectElement, MsgRender>('rect.msg-rect').data(rectData, d => d.id);
    rs.enter().append('rect').attr('class', 'msg-rect').attr('width', 8).attr('height', 6).attr('rx', 1.5)
      .merge(rs)
      .attr('fill', d => d.direction === 'return' ? lightenColor(d.color, 0.25) : d.color)
      .attr('stroke', d => d.direction === 'return' ? d.color : 'none')
      .attr('stroke-width', d => d.direction === 'return' ? 1 : 0)
      .attr('x', d => d.cx - 4).attr('y', d => d.cy - 3)
      .attr('opacity', d => d.opacity);
    rs.exit().remove();

    // ── Circle messages ──
    const circData = msgData.filter(m => m.shape === 'circle');
    const cs = g.selectAll<SVGCircleElement, MsgRender>('circle.msg-circle').data(circData, d => d.id);
    cs.enter().append('circle').attr('class', 'msg-circle').attr('r', 5)
      .merge(cs)
      .attr('fill', d => d.direction === 'return' ? lightenColor(d.color, 0.25) : d.color)
      .attr('stroke', d => d.direction === 'return' ? d.color : 'none')
      .attr('stroke-width', d => d.direction === 'return' ? 1.5 : 0)
      .attr('cx', d => d.cx).attr('cy', d => d.cy)
      .attr('opacity', d => d.opacity);
    cs.exit().remove();

    // ── Message labels ──
    const labelData = msgData.filter(m => !!m.label);
    const mls = g.selectAll<SVGTextElement, MsgRender>('text.msg-label').data(labelData, d => d.id);
    mls.enter().append('text').attr('class', 'msg-label').attr('font-size', 7).attr('fill', '#495057').attr('font-weight', 600).attr('pointer-events', 'none')
      .merge(mls)
      .attr('x', d => d.cx + 8).attr('y', d => d.cy + 2)
      .attr('opacity', d => d.opacity * 0.85)
      .text(d => d.label ?? '');
    mls.exit().remove();

    // ── Active request counter badges ──
    const counters = activeCountersRef.current;
    type CDatum = { key: string; x: number; y: number; count: number };
    const cData: CDatum[] = [];
    const sfC = counters['slimfaas'] || 0;
    if (sfC > 0) cData.push({ key: 'slimfaas', x: CENTER.x + 55, y: CENTER.y - 18, count: sfC });
    const exC = counters['external'] || 0;
    if (exC > 0) cData.push({ key: 'external', x: EXTERNAL_OFFSET.x + 20, y: EXTERNAL_OFFSET.y - 18, count: exC });
    for (const fnName of Object.keys(fnPositions)) {
      const c = counters[fnName] || 0;
      if (c > 0) {
        const p = fnPositions[fnName];
        cData.push({ key: fnName, x: p.x + BUBBLE_R + 4, y: p.y - BUBBLE_R + 2, count: c });
      }
      // Queue node counter (sender badge on queue box)
      const qc = counters[`queue:${fnName}`] || 0;
      if (qc > 0) {
        const qp = queuePositions[fnName];
        if (qp) {
          cData.push({ key: `queue:${fnName}`, x: qp.x + QUEUE_BOX_W / 2 + 4, y: qp.y - QUEUE_BOX_H / 2, count: qc });
        }
      }
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
  }, [fnPositions, queuePositions, decCounter]);

  /* ── Draw static structure ── */

  const structuralKey = `${fnNamesKey}|${queueFnKey}`;

  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;
    const sel = d3.select(svg);
    sel.selectAll('*').remove();

    userHasZoomedRef.current = false;
    initialFitDoneRef.current = false;
    activeCountersRef.current = {};

    // Defs
    const defs = sel.append('defs');
    const ds = defs.append('filter').attr('id', 'shadow').attr('x', '-20%').attr('y', '-20%').attr('width', '140%').attr('height', '140%');
    ds.append('feDropShadow').attr('dx', 0).attr('dy', 2).attr('stdDeviation', 3).attr('flood-opacity', 0.15);

    // Background
    sel.append('rect').attr('class', 'nw-bg').attr('width', '100%').attr('height', '100%').attr('fill', '#f8f9fa');

    // World group
    const world = sel.append('g').attr('class', 'nw-world');
    worldGRef.current = world.node()!;

    // Setup d3-zoom
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

    // ── External bubble ──
    const exX = EXTERNAL_OFFSET.x, exY = EXTERNAL_OFFSET.y;
    const exR = BUBBLE_R;
    world.append('circle')
      .attr('cx', exX).attr('cy', exY).attr('r', exR)
      .attr('fill', '#adb5bd').attr('stroke', '#adb5bd').attr('stroke-width', 2.5)
      .attr('opacity', 0.85).attr('filter', 'url(#shadow)');
    world.append('text').attr('x', exX).attr('y', exY + 4)
      .attr('text-anchor', 'middle').attr('fill', '#fff').attr('font-weight', 'bold').attr('font-size', 10)
      .text('External');

    // ── SlimFaas ──
    const sfW = 100, sfH = 44;
    world.append('rect').attr('x', CENTER.x - sfW / 2).attr('y', CENTER.y - sfH / 2)
      .attr('width', sfW).attr('height', sfH).attr('rx', 10)
      .attr('fill', '#0000ff').attr('filter', 'url(#shadow)');
    world.append('text').attr('x', CENTER.x).attr('y', CENTER.y - 2)
      .attr('text-anchor', 'middle').attr('fill', '#fff').attr('font-weight', 'bold').attr('font-size', 14)
      .text('SlimFaas');
    // SlimFaas replica mini-bubbles group (updated dynamically)
    world.append('g').attr('class', 'sf-replicas');

    // ── Layers ──
    world.append('g').attr('class', 'nw-traces');

    // ── Queue boxes only (NO connection lines) ──
    const fnNames = fnNamesKey.split(',').filter(Boolean);
    const queueFnNames = new Set(queueFnKey.split(',').filter(Boolean));

    fnNames.forEach((fnName) => {
      if (!queueFnNames.has(fnName)) return;
      const qCenter = queuePositions[fnName];
      if (!qCenter) return;
      const color = nameColor(fnName);
      const qCX = qCenter.x, qCY = qCenter.y;
      const qW = QUEUE_BOX_W, qH = QUEUE_BOX_H;
      const qLeft = qCX - qW / 2;

      // Queue body
      world.append('rect').attr('class', `q-box-${fnName}`).attr('x', qLeft).attr('y', qCY - qH / 2)
        .attr('width', qW).attr('height', qH).attr('rx', 4).attr('ry', 4)
        .attr('fill', '#fff').attr('stroke', color).attr('stroke-width', 1.5).attr('filter', 'url(#shadow)');
      world.append('g').attr('class', `q-fill-${fnName}`);
      world.append('text').attr('x', qCX).attr('y', qCY - qH / 2 - 4)
        .attr('text-anchor', 'middle').attr('font-size', 9).attr('fill', '#6b778c').attr('font-weight', '600')
        .text(fnName.length > 14 ? fnName.slice(0, 12) + '\u2026' : fnName);
      world.append('text').attr('class', `q-count-${fnName}`).attr('x', qCX).attr('y', qCY + qH / 2 + 12)
        .attr('text-anchor', 'middle').attr('font-size', 9).attr('font-weight', 'bold');
    });

    // ── Function bubbles (NO connection lines) ──
    fnNames.forEach((fnName) => {
      const pos = fnPositions[fnName];
      if (!pos) return;
      const color = nameColor(fnName);

      world.append('g').attr('class', `fn-group-${CSS.escape(fnName)}`);
      world.append('circle').attr('class', `fn-bubble-${CSS.escape(fnName)}`)
        .attr('cx', pos.x).attr('cy', pos.y).attr('r', BUBBLE_R)
        .attr('fill', color).attr('stroke', color).attr('stroke-width', 2.5)
        .attr('opacity', 0.85).attr('filter', 'url(#shadow)');
      world.append('text').attr('class', `fn-name-${CSS.escape(fnName)}`)
        .attr('x', pos.x).attr('y', pos.y - 5)
        .attr('text-anchor', 'middle').attr('font-size', 10).attr('font-weight', 'bold').attr('fill', '#fff')
        .text(fnName.length > 14 ? fnName.slice(0, 12) + '\u2026' : fnName);
      world.append('text').attr('class', `fn-count-${CSS.escape(fnName)}`)
        .attr('x', pos.x).attr('y', pos.y + 8)
        .attr('text-anchor', 'middle').attr('font-size', 9);
      world.append('g').attr('class', `fn-pods-${CSS.escape(fnName)}`);
    });

    // ── Messages + Counters layers ──
    world.append('g').attr('class', 'nw-messages');
    world.append('g').attr('class', 'nw-counters');

    // Start animation
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [structuralKey]);

  /* ── Update dynamic data ── */

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

      // Update bubble
      const escaped = CSS.escape(fn.Name);
      world.select(`.fn-bubble-${escaped}`)
        .attr('r', r)
        .attr('fill', isDown ? '#f8f9fa' : color)
        .attr('opacity', isDown ? 0.5 : 0.85);

      // Update name color
      world.select(`.fn-name-${escaped}`)
        .attr('fill', isDown ? '#6b778c' : '#fff');

      // Update count
      world.select(`.fn-count-${escaped}`)
        .attr('fill', isDown ? '#adb5bd' : 'rgba(255,255,255,0.8)')
        .text(`${fn.NumberReady}/${fn.NumberRequested}`);

      // Update pods
      const podGroup = world.select<SVGGElement>(`.fn-pods-${escaped}`);
      podGroup.selectAll('*').remove();

      if (pods.length > 0) {
        const podStep = (2 * Math.PI) / pods.length;
        const podRing = r * 0.55;
        pods.forEach((pod, pi) => {
          const a = podStep * pi - Math.PI / 2;
          const podG = podGroup.append('g').style('cursor', 'pointer');
          podG.append('circle')
            .attr('cx', pos.x + Math.cos(a) * podRing)
            .attr('cy', pos.y + Math.sin(a) * podRing + 4)
            .attr('r', 4).attr('fill', podColor(pod.Status)).attr('stroke', '#fff').attr('stroke-width', 1);
          if (pods.length <= 6) {
            podG.append('text')
              .attr('x', pos.x + Math.cos(a) * (podRing + 10))
              .attr('y', pos.y + Math.sin(a) * (podRing + 10) + 4 + 3)
              .attr('text-anchor', 'middle').attr('font-size', 6).attr('fill', '#6b778c').attr('pointer-events', 'none')
              .text(pod.Name.length > 20 ? '…' + pod.Name.slice(-12) : pod.Name);
          }
          podG.append('title')
            .text(`${pod.Name}\nIP: ${pod.Ip || 'N/A'}\nStatus: ${pod.Status}\nReady: ${pod.Ready}`);
        });
      }

      // Update queue count
      const qLen = queueMap[fn.Name] ?? 0;
      const qCenter = queuePositions[fn.Name];
      if (qCenter) {
        const qCX = qCenter.x, qCY = qCenter.y;
        const qW = QUEUE_BOX_W, qH = QUEUE_BOX_H;
        const qLeft = qCX - qW / 2;

        // Update count text
        world.select(`.q-count-${CSS.escape(fn.Name)}`)
          .attr('fill', qLen > 0 ? color : '#adb5bd')
          .text(`📥 ${qLen}`);

        // Update fill
        const fillGroup = world.select<SVGGElement>(`.q-fill-${CSS.escape(fn.Name)}`);
        fillGroup.selectAll('*').remove();

        if (qLen > 0) {
          const fillRatio = Math.min(1, qLen / 20);
          fillGroup.append('rect').attr('x', qLeft + 1).attr('y', qCY - qH / 2 + 1)
            .attr('width', Math.max(0, (qW - 2) * fillRatio)).attr('height', qH - 2)
            .attr('rx', 3).attr('fill', lightenColor(color, 0.3)).attr('opacity', 0.6);

          const maxBlocks = Math.min(qLen, Math.floor(qW / 8));
          const blockW = 5, blockH = qH - 8;
          const blockSpacing = Math.min(7, (qW - 4) / maxBlocks);
          for (let bi = 0; bi < maxBlocks; bi++) {
            fillGroup.append('rect')
              .attr('x', qLeft + 3 + bi * blockSpacing).attr('y', qCY - blockH / 2)
              .attr('width', blockW).attr('height', blockH).attr('rx', 1)
              .attr('fill', color).attr('opacity', 0.5 + 0.3 * (bi / maxBlocks));
          }
        }
      }
    });

    // ── Update SlimFaas replica mini-bubbles ──
    const sfGroup = world.select<SVGGElement>('.sf-replicas');
    sfGroup.selectAll('*').remove();
    const nodes = slimFaasNodes.length > 0 ? slimFaasNodes : Array.from({ length: slimFaasReplicas }, (_, i) => ({ Name: `sf-${i}`, Status: 'Running' }));
    if (nodes.length > 0) {
      const miniR = 5;
      const totalW = nodes.length * (miniR * 2 + 4) - 4;
      const startX = CENTER.x - totalW / 2 + miniR;
      const baseY = CENTER.y + 14;
      nodes.forEach((node, i) => {
        const nx = startX + i * (miniR * 2 + 4);
        const color = node.Status === 'Running' ? '#36b37e' : (node.Status === 'Starting' ? '#fab005' : '#adb5bd');
        sfGroup.append('circle')
          .attr('cx', nx).attr('cy', baseY).attr('r', miniR)
          .attr('fill', color).attr('stroke', '#fff').attr('stroke-width', 1);
        sfGroup.append('title').text(`${node.Name} — ${node.Status}`);
      });
    }
  }, [functions, fnPositions, queuePositions, queueMap, slimFaasReplicas, slimFaasNodes]);

  /* ── Zoom-to-fit ── */

  useEffect(() => {
    if (!initialFitDoneRef.current) return;
    if (userHasZoomedRef.current) return;
    const t = setTimeout(() => zoomToFit(true), 50);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [structuralKey]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const ro = new ResizeObserver(() => { if (!userHasZoomedRef.current) zoomToFit(false); });
    ro.observe(container);
    return () => ro.disconnect();
  }, [zoomToFit]);

  return (
    <div className="network-map" ref={containerRef}>
      <h2 className="network-map__title">
        🔄 Live Network Map
        <button
          className="network-map__reset-btn"
          type="button"
          title="Reset zoom to fit all elements"
          onClick={() => { userHasZoomedRef.current = false; zoomToFit(true); }}
        >
          ⟳
        </button>
      </h2>
      <svg ref={svgRef} className="network-map__svg" />
      <div className="network-map__legend">
        <span className="network-map__legend-item">
          <span className="network-map__legend-dot" style={{ backgroundColor: '#adb5bd' }} /> External
        </span>
        <span className="network-map__legend-item">
          <span className="network-map__legend-dot" style={{ backgroundColor: '#0000ff' }} /> SlimFaas
        </span>
        {functions.slice(0, 8).map(fn => (
          <span key={fn.Name} className="network-map__legend-item">
            <span className="network-map__legend-dot" style={{ backgroundColor: nameColor(fn.Name) }} />
            {fn.Name}
          </span>
        ))}
        {functions.length > 8 && (
          <span className="network-map__legend-item" style={{ fontStyle: 'italic' }}>+{functions.length - 8} more</span>
        )}
        <span className="network-map__legend-item" style={{ marginLeft: 12 }}>
          <svg width="14" height="10"><circle cx="5" cy="5" r="4" fill="#4c6ef5" /></svg> Request
        </span>
        <span className="network-map__legend-item">
          <svg width="14" height="10"><rect x="1" y="2" width="10" height="6" rx="1.5" fill="#6b778c" /></svg> Async
        </span>
        <span className="network-map__legend-item">
          <svg width="24" height="10"><line x1="0" y1="5" x2="20" y2="5" stroke="#fab005" strokeWidth="3" strokeDasharray="8 4" /></svg> Waiting
        </span>
        <span className="network-map__legend-separator" />
        <span className="network-map__legend-item" title="Active requests badge">
          <svg width="18" height="14"><rect x="1" y="0" width="16" height="14" rx="7" fill="#ff6b6b" stroke="#fff" strokeWidth="1" /><text x="9" y="10.5" textAnchor="middle" fontSize="8" fontWeight="bold" fill="#fff">3</text></svg> Active
        </span>
        <span className="network-map__legend-separator" />
        <span className="network-map__legend-item" title="Running pod">
          <svg width="10" height="10"><circle cx="5" cy="5" r="4" fill="#36b37e" stroke="#fff" strokeWidth="1" /></svg> Running
        </span>
        <span className="network-map__legend-item" title="Starting pod">
          <svg width="10" height="10"><circle cx="5" cy="5" r="4" fill="#fab005" stroke="#fff" strokeWidth="1" /></svg> Starting
        </span>
        <span className="network-map__legend-item" title="Pending pod">
          <svg width="10" height="10"><circle cx="5" cy="5" r="4" fill="#adb5bd" stroke="#fff" strokeWidth="1" /></svg> Pending
        </span>
        <span className="network-map__legend-item" style={{ fontSize: '0.7rem', color: '#868e96', fontStyle: 'italic' }}>
          Double-click to reset view
        </span>
      </div>
    </div>
  );
};

export default NetworkMap;

