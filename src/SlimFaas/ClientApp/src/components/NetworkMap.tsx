import React, { useRef, useEffect, useMemo, useCallback } from 'react';
import * as d3 from 'd3';
import type { FunctionStatusDetailed, QueueInfo, NetworkActivityEvent } from '../types';

interface Props {
  functions: FunctionStatusDetailed[];
  queues: QueueInfo[];
  activity: NetworkActivityEvent[];
  functionsWithQueueActivity: Set<string>;
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

interface AnimatedMessage {
  id: string;
  waypoints: { x: number; y: number }[];
  color: string;
  shape: 'circle' | 'rect';
  startTime: number;
  duration: number;
}

interface ActiveTrace {
  functionName: string;
  type: 'sync' | 'waiting';
  startTime: number;
}

/* ── Constants ── */

const ANIM_DURATION = 1200;
const BUBBLE_R = 32;
const QUEUE_BOX_W = 60;
const QUEUE_BOX_H = 20;
const CENTER = { x: 0, y: 0 }; // SlimFaas at world origin
const EXTERNAL_OFFSET = { x: -300, y: 0 }; // "IN" well to the left
const SVG_VIEW_H = 500;

/**
 * Radial layout: place n items around (0,0).
 * Ring radius grows with n so items don't overlap.
 */
function radialPositions(n: number): { x: number; y: number }[] {
  if (n === 0) return [];
  if (n === 1) return [{ x: 0, y: 260 }];
  const baseR = 250;
  const perItem = 120; // px spacing per item around circumference
  const radius = Math.max(baseR, (n * perItem) / (2 * Math.PI));
  return Array.from({ length: n }, (_, i) => {
    const angle = (2 * Math.PI * i) / n - Math.PI / 2;
    return { x: Math.cos(angle) * radius, y: Math.sin(angle) * radius };
  });
}

/* ── Component ── */

const NetworkMap: React.FC<Props> = ({ functions, queues, activity, functionsWithQueueActivity }) => {
  const svgRef = useRef<SVGSVGElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const animRef = useRef<AnimatedMessage[]>([]);
  const tracesRef = useRef<Map<string, ActiveTrace>>(new Map());
  const seenIdsRef = useRef<Set<string>>(new Set());
  const frameRef = useRef(0);
  const zoomRef = useRef<d3.ZoomBehavior<SVGSVGElement, unknown> | null>(null);
  const worldGRef = useRef<SVGGElement | null>(null);

  /* ── Positions in world coordinates (px, center=0,0) ── */

  const fnPositions = useMemo(() => {
    const pos: Record<string, { x: number; y: number }> = {};
    const pts = radialPositions(functions.length);
    functions.forEach((fn, i) => { pos[fn.Name] = pts[i]; });
    return pos;
  }, [functions]);

  const queuePositions = useMemo(() => {
    const pos: Record<string, { x: number; y: number }> = {};
    functions.forEach((fn) => {
      const fp = fnPositions[fn.Name];
      if (!fp) return;
      const qLen = queues.find(q => q.Name === fn.Name)?.Length ?? 0;
      if (!functionsWithQueueActivity.has(fn.Name) && qLen === 0) return;
      pos[fn.Name] = { x: (CENTER.x + fp.x) / 2, y: (CENTER.y + fp.y) / 2 };
    });
    return pos;
  }, [functions, fnPositions, functionsWithQueueActivity, queues]);

  const queueMap = useMemo(() => {
    const m: Record<string, number> = {};
    queues.forEach(q => { m[q.Name] = q.Length; });
    return m;
  }, [queues]);

  /* ── Auto zoom-to-fit — only called on resize or structural changes ── */

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

  /* ── Spawn animated messages (ID-based dedup) ── */

  useEffect(() => {
    for (const evt of activity) {
      if (seenIdsRef.current.has(evt.Id)) continue;
      seenIdsRef.current.add(evt.Id);

      const fp = (name: string) => fnPositions[name] || null;
      const qp = (name: string) => queuePositions[name] || null;

      if (evt.Type === 'enqueue') {
        const q = qp(evt.Target);
        if (!q) continue;
        animRef.current.push({ id: evt.Id, waypoints: [CENTER, { x: q.x - QUEUE_BOX_W / 2, y: q.y }, q], color: nameColor(evt.Target), shape: 'rect', startTime: performance.now(), duration: ANIM_DURATION });
      } else if (evt.Type === 'dequeue') {
        const q = qp(evt.Target); const f = fp(evt.Target);
        if (!q || !f) continue;
        animRef.current.push({ id: evt.Id, waypoints: [q, { x: q.x + QUEUE_BOX_W / 2, y: q.y }, f], color: nameColor(evt.Target), shape: 'rect', startTime: performance.now(), duration: ANIM_DURATION });
      } else if (evt.Type === 'request_in') {
        animRef.current.push({ id: evt.Id, waypoints: [EXTERNAL_OFFSET, CENTER], color: '#6b778c', shape: 'circle', startTime: performance.now(), duration: ANIM_DURATION });
      } else if (evt.Type === 'request_out' || evt.Type === 'response') {
        const f = fp(evt.Target); if (!f) continue;
        animRef.current.push({ id: evt.Id, waypoints: [CENTER, f], color: nameColor(evt.Target), shape: 'circle', startTime: performance.now(), duration: ANIM_DURATION });
      } else if (evt.Type === 'request_waiting') {
        tracesRef.current.set(evt.Target, { functionName: evt.Target, type: 'waiting', startTime: performance.now() });
      } else if (evt.Type === 'request_started') {
        const ex = tracesRef.current.get(evt.Target);
        if (ex) { ex.type = 'sync'; ex.startTime = performance.now(); }
      } else if (evt.Type === 'request_end') {
        tracesRef.current.delete(evt.Source);
      } else if (evt.Type === 'event_publish') {
        const f = fp(evt.Target); if (!f) continue;
        animRef.current.push({ id: evt.Id, waypoints: [CENTER, f], color: '#fab005', shape: 'circle', startTime: performance.now(), duration: ANIM_DURATION });
      }
    }
    if (seenIdsRef.current.size > 500) {
      seenIdsRef.current = new Set(Array.from(seenIdsRef.current).slice(-300));
    }
  }, [activity, fnPositions, queuePositions]);

  /* ── Interpolate along waypoints ── */

  const interp = useCallback((wp: { x: number; y: number }[], t: number) => {
    if (wp.length < 2) return wp[0];
    const segs = wp.length - 1;
    const sl = 1 / segs;
    const si = Math.min(Math.floor(t / sl), segs - 1);
    const st = (t - si * sl) / sl;
    const e = d3.easeCubicInOut(st);
    const a = wp[si], b = wp[si + 1];
    return { x: a.x + (b.x - a.x) * e, y: a.y + (b.y - a.y) * e };
  }, []);

  /* ── Animation loop ── */

  const animate = useCallback(() => {
    const wg = worldGRef.current;
    if (!wg) { frameRef.current = requestAnimationFrame(animate); return; }
    const g = d3.select(wg).select<SVGGElement>('.nw-messages');
    const traceG = d3.select(wg).select<SVGGElement>('.nw-traces');
    const now = performance.now();

    animRef.current = animRef.current.filter(m => now - m.startTime < m.duration);

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
      .attr('cx', d => { const f = fnPositions[d.functionName]; const t = ((now - d.startTime) / 2000) % 1; return CENTER.x + ((f?.x ?? 0) - CENTER.x) * t; })
      .attr('cy', d => { const f = fnPositions[d.functionName]; const t = ((now - d.startTime) / 2000) % 1; return CENTER.y + ((f?.y ?? 0) - CENTER.y) * t; })
      .attr('opacity', d => 0.5 + 0.4 * Math.sin((now - d.startTime) / 600 * Math.PI));
    wdSel.exit().remove();

    // ── Rect messages ──
    const rd = animRef.current.filter(m => m.shape === 'rect');
    const rs = g.selectAll<SVGRectElement, AnimatedMessage>('rect.msg-rect').data(rd, d => d.id);
    rs.enter().append('rect').attr('class', 'msg-rect').attr('width', 8).attr('height', 6).attr('rx', 1.5).attr('opacity', 0.95)
      .merge(rs).attr('fill', d => d.color)
      .attr('x', d => interp(d.waypoints, Math.min(1, (now - d.startTime) / d.duration)).x - 4)
      .attr('y', d => interp(d.waypoints, Math.min(1, (now - d.startTime) / d.duration)).y - 3)
      .attr('opacity', d => { const t = (now - d.startTime) / d.duration; return t > 0.85 ? Math.max(0, 1 - (t - 0.85) / 0.15) : 0.95; });
    rs.exit().remove();

    // ── Circle messages ──
    const cd = animRef.current.filter(m => m.shape === 'circle');
    const cs = g.selectAll<SVGCircleElement, AnimatedMessage>('circle.msg-circle').data(cd, d => d.id);
    cs.enter().append('circle').attr('class', 'msg-circle').attr('r', 5).attr('opacity', 0.9)
      .merge(cs).attr('fill', d => d.color)
      .attr('cx', d => interp(d.waypoints, Math.min(1, (now - d.startTime) / d.duration)).x)
      .attr('cy', d => interp(d.waypoints, Math.min(1, (now - d.startTime) / d.duration)).y)
      .attr('opacity', d => { const t = (now - d.startTime) / d.duration; return t > 0.8 ? Math.max(0, 1 - (t - 0.8) / 0.2) : 0.9; });
    cs.exit().remove();

    frameRef.current = requestAnimationFrame(animate);
  }, [interp, fnPositions]);

  /* ── Draw static elements + setup zoom ── */

  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;
    const sel = d3.select(svg);
    sel.selectAll('*').remove();

    // Defs (outside the world group so they aren't transformed)
    const defs = sel.append('defs');
    const ds = defs.append('filter').attr('id', 'shadow').attr('x', '-20%').attr('y', '-20%').attr('width', '140%').attr('height', '140%');
    ds.append('feDropShadow').attr('dx', 0).attr('dy', 2).attr('stdDeviation', 3).attr('flood-opacity', 0.15);
    defs.append('marker').attr('id', 'arrow-in').attr('viewBox', '0 0 10 10')
      .attr('refX', 5).attr('refY', 5).attr('markerWidth', 6).attr('markerHeight', 6).attr('orient', 'auto-start-reverse')
      .append('path').attr('d', 'M 0 0 L 10 5 L 0 10 z').attr('fill', '#adb5bd');

    // Background rect (will be big enough; zoom handles viewport)
    sel.append('rect').attr('class', 'nw-bg').attr('width', '100%').attr('height', '100%').attr('fill', '#f8f9fa');

    // World group — everything zoomable
    const world = sel.append('g').attr('class', 'nw-world');
    worldGRef.current = world.node()!;

    // Setup d3-zoom
    const zoom = d3.zoom<SVGSVGElement, unknown>()
      .scaleExtent([0.15, 5])
      .on('zoom', (event) => { world.attr('transform', event.transform); });
    zoomRef.current = zoom;
    sel.call(zoom);
    // Disable double-click zoom
    sel.on('dblclick.zoom', null);

    // ── External IN ──
    const exX = EXTERNAL_OFFSET.x, exY = EXTERNAL_OFFSET.y;
    world.append('polygon')
      .attr('points', `${exX - 14},${exY} ${exX + 10},${exY - 12} ${exX + 10},${exY + 12}`)
      .attr('fill', '#adb5bd').attr('opacity', 0.7);
    world.append('text').attr('x', exX).attr('y', exY + 22)
      .attr('text-anchor', 'middle').attr('fill', '#6b778c').attr('font-size', 10).text('IN');

    // ── SlimFaas ──
    const sfW = 100, sfH = 44;
    world.append('rect').attr('x', CENTER.x - sfW / 2).attr('y', CENTER.y - sfH / 2)
      .attr('width', sfW).attr('height', sfH).attr('rx', 10)
      .attr('fill', '#0000ff').attr('filter', 'url(#shadow)');
    world.append('text').attr('x', CENTER.x).attr('y', CENTER.y + 5)
      .attr('text-anchor', 'middle').attr('fill', '#fff').attr('font-weight', 'bold').attr('font-size', 14)
      .text('SlimFaas');

    // ── Traces layer ──
    world.append('g').attr('class', 'nw-traces');

    // ── Queues + Connection lines ──
    functions.forEach((fn) => {
      const pos = fnPositions[fn.Name];
      if (!pos) return;
      const qLen = queueMap[fn.Name] ?? 0;
      const color = nameColor(fn.Name);
      const qCenter = queuePositions[fn.Name];

      if (!qCenter) {
        // Direct line
        world.append('line')
          .attr('x1', CENTER.x).attr('y1', CENTER.y + sfH / 2)
          .attr('x2', pos.x).attr('y2', pos.y - BUBBLE_R)
          .attr('stroke', color).attr('stroke-width', 1.5).attr('stroke-dasharray', '4 3')
          .attr('opacity', 0.25).attr('marker-end', 'url(#arrow-in)');
        return;
      }

      const qCX = qCenter.x, qCY = qCenter.y;
      const qW = QUEUE_BOX_W, qH = QUEUE_BOX_H;
      const qLeft = qCX - qW / 2, qRight = qCX + qW / 2;

      // SlimFaas → Queue
      world.append('line')
        .attr('x1', CENTER.x).attr('y1', CENTER.y + sfH / 2)
        .attr('x2', qLeft).attr('y2', qCY)
        .attr('stroke', color).attr('stroke-width', 1.5).attr('stroke-dasharray', '4 3')
        .attr('opacity', 0.25).attr('marker-end', 'url(#arrow-in)');
      // Queue → Function
      world.append('line')
        .attr('x1', qRight).attr('y1', qCY)
        .attr('x2', pos.x).attr('y2', pos.y - BUBBLE_R)
        .attr('stroke', color).attr('stroke-width', 1.5).attr('stroke-dasharray', '4 3')
        .attr('opacity', 0.25).attr('marker-end', 'url(#arrow-in)');

      // Queue body
      world.append('rect').attr('x', qLeft).attr('y', qCY - qH / 2)
        .attr('width', qW).attr('height', qH).attr('rx', 4).attr('ry', 4)
        .attr('fill', '#fff').attr('stroke', color).attr('stroke-width', 1.5).attr('filter', 'url(#shadow)');

      if (qLen > 0) {
        const fillRatio = Math.min(1, qLen / 20);
        world.append('rect').attr('x', qLeft + 1).attr('y', qCY - qH / 2 + 1)
          .attr('width', Math.max(0, (qW - 2) * fillRatio)).attr('height', qH - 2)
          .attr('rx', 3).attr('fill', lightenColor(color, 0.3)).attr('opacity', 0.6);

        const maxBlocks = Math.min(qLen, Math.floor(qW / 8));
        const blockW = 5, blockH = qH - 8;
        const blockSpacing = Math.min(7, (qW - 4) / maxBlocks);
        for (let bi = 0; bi < maxBlocks; bi++) {
          world.append('rect')
            .attr('x', qLeft + 3 + bi * blockSpacing).attr('y', qCY - blockH / 2)
            .attr('width', blockW).attr('height', blockH).attr('rx', 1)
            .attr('fill', color).attr('opacity', 0.5 + 0.3 * (bi / maxBlocks));
        }
      }

      world.append('text').attr('x', qLeft - 2).attr('y', qCY + 4).attr('text-anchor', 'end').attr('font-size', 10).attr('fill', color).text('▶');
      world.append('text').attr('x', qRight + 2).attr('y', qCY + 4).attr('text-anchor', 'start').attr('font-size', 10).attr('fill', color).text('▶');
      world.append('text').attr('x', qCX).attr('y', qCY - qH / 2 - 4)
        .attr('text-anchor', 'middle').attr('font-size', 9).attr('fill', '#6b778c').attr('font-weight', '600')
        .text(fn.Name.length > 14 ? fn.Name.slice(0, 12) + '…' : fn.Name);
      world.append('text').attr('x', qCX).attr('y', qCY + qH / 2 + 12)
        .attr('text-anchor', 'middle').attr('font-size', 9)
        .attr('fill', qLen > 0 ? color : '#adb5bd').attr('font-weight', 'bold')
        .text(`📥 ${qLen}`);
    });

    // ── Function bubbles ──
    functions.forEach((fn) => {
      const pos = fnPositions[fn.Name];
      if (!pos) return;
      const cx = pos.x, cy = pos.y;
      const color = nameColor(fn.Name);
      const isDown = (fn.NumberReady ?? 0) === 0;
      const r = BUBBLE_R + Math.min((fn.Pods ?? []).length * 3, 14);

      world.append('circle').attr('cx', cx).attr('cy', cy).attr('r', r)
        .attr('fill', isDown ? '#f8f9fa' : color)
        .attr('stroke', color).attr('stroke-width', 2.5)
        .attr('opacity', isDown ? 0.5 : 0.85).attr('filter', 'url(#shadow)');

      world.append('text').attr('x', cx).attr('y', cy - 5)
        .attr('text-anchor', 'middle').attr('fill', isDown ? '#6b778c' : '#fff')
        .attr('font-size', 10).attr('font-weight', 'bold')
        .text(fn.Name.length > 14 ? fn.Name.slice(0, 12) + '…' : fn.Name);
      world.append('text').attr('x', cx).attr('y', cy + 8)
        .attr('text-anchor', 'middle').attr('fill', isDown ? '#adb5bd' : 'rgba(255,255,255,0.8)')
        .attr('font-size', 9).text(`${fn.NumberReady}/${fn.NumberRequested}`);

      const pods = fn.Pods ?? [];
      if (pods.length > 0) {
        const podStep = (2 * Math.PI) / pods.length;
        const podRing = r * 0.55;
        pods.forEach((pod, pi) => {
          const a = podStep * pi - Math.PI / 2;
          world.append('circle')
            .attr('cx', cx + Math.cos(a) * podRing)
            .attr('cy', cy + Math.sin(a) * podRing + 4)
            .attr('r', 4).attr('fill', podColor(pod.Status)).attr('stroke', '#fff').attr('stroke-width', 1);
        });
      }
    });

    // ── Messages layer ──
    world.append('g').attr('class', 'nw-messages');

    // Start animation
    frameRef.current = requestAnimationFrame(animate);

    return () => { cancelAnimationFrame(frameRef.current); };
  }, [functions, queues, fnPositions, queueMap, queuePositions, animate, functionsWithQueueActivity]);

  /* ── Zoom-to-fit only on structural changes (function list) and resize ── */

  // Track function names as a stable string key
  const fnNamesKey = useMemo(() => functions.map(f => f.Name).sort().join(','), [functions]);

  // Zoom on first render and when the set of functions changes
  useEffect(() => {
    // Small delay to let the SVG render first
    const t = setTimeout(() => zoomToFit(true), 50);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fnNamesKey]);

  // Re-fit on container resize (no animation to avoid flicker)
  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const ro = new ResizeObserver(() => { zoomToFit(false); });
    ro.observe(container);
    return () => ro.disconnect();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [fnNamesKey]);

  return (
    <div className="network-map" ref={containerRef}>
      <h2 className="network-map__title">🔄 Live Network Map</h2>
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
          <svg width="14" height="10"><circle cx="5" cy="5" r="4" fill="#6b778c" /></svg> Sync
        </span>
        <span className="network-map__legend-item">
          <svg width="14" height="10"><rect x="1" y="2" width="10" height="6" rx="1.5" fill="#6b778c" /></svg> Async
        </span>
        <span className="network-map__legend-item">
          <svg width="24" height="10"><line x1="0" y1="5" x2="20" y2="5" stroke="#fab005" strokeWidth="3" strokeDasharray="8 4" /></svg> Waiting
        </span>
        <span className="network-map__legend-item">
          <svg width="24" height="10"><line x1="0" y1="5" x2="20" y2="5" stroke="#4c6ef5" strokeWidth="3" strokeDasharray="6 3" opacity="0.35" /></svg> In-flight
        </span>
      </div>
    </div>
  );
};

export default NetworkMap;




