import React, { useRef, useEffect, useMemo, useCallback } from 'react';
import * as d3 from 'd3';
import type { FunctionStatusDetailed, QueueInfo, NetworkActivityEvent } from '../types';

interface Props {
  functions: FunctionStatusDetailed[];
  queues: QueueInfo[];
  activity: NetworkActivityEvent[];
  functionsWithQueueActivity: Set<string>;
}

// Generate a deterministic color from a name
function nameColor(name: string): string {
  const colors = [
    '#4c6ef5', '#7950f2', '#e64980', '#f76707', '#36b37e',
    '#00b8d9', '#fab005', '#e8590c', '#ae3ec9', '#0ca678',
    '#2f9e44', '#1098ad', '#f08c00', '#d6336c', '#6741d9',
  ];
  let hash = 0;
  for (let i = 0; i < name.length; i++) {
    hash = name.charCodeAt(i) + ((hash << 5) - hash);
  }
  return colors[Math.abs(hash) % colors.length];
}

// Pod status badge color
function podColor(status: string): string {
  if (status === 'Running') return '#36b37e';
  if (status === 'Starting') return '#fab005';
  if (status === 'Pending') return '#adb5bd';
  return '#ff5630';
}

// Lighten a hex color
function lightenColor(hex: string, amount: number): string {
  const num = parseInt(hex.replace('#', ''), 16);
  const r = Math.min(255, ((num >> 16) & 0xff) + Math.round(255 * amount));
  const g = Math.min(255, ((num >> 8) & 0xff) + Math.round(255 * amount));
  const b = Math.min(255, (num & 0xff) + Math.round(255 * amount));
  return `rgb(${r},${g},${b})`;
}

/** Animated message with optional multi-waypoint routing */
interface AnimatedMessage {
  id: string;
  waypoints: { x: number; y: number }[];
  color: string;
  shape: 'circle' | 'rect';
  startTime: number;
  duration: number;
}

const ANIM_DURATION = 1200;
const SVG_MIN_WIDTH = 900;
const SVG_HEIGHT = 560;
const SLIMFAAS_X = 0.5;
const SLIMFAAS_Y = 0.22;
const EXTERNAL_IN_X = 0.08;
const EXTERNAL_IN_Y = 0.22;
const EXTERNAL_OUT_X = 0.92;
const EXTERNAL_OUT_Y = 0.22;

// Queue visual dimensions (relative)
const QUEUE_W = 0.07;
const QUEUE_H_REL = 22; // px height of queue box

const NetworkMap: React.FC<Props> = ({ functions, queues, activity, functionsWithQueueActivity }) => {
  const svgRef = useRef<SVGSVGElement>(null);
  const animRef = useRef<AnimatedMessage[]>([]);
  const lastActivityLen = useRef(0);
  const frameRef = useRef(0);

  // Memoize positions for function bubbles (placed in a semicircle below slimfaas)
  const fnPositions = useMemo(() => {
    const positions: Record<string, { x: number; y: number }> = {};
    const n = functions.length;
    if (n === 0) return positions;
    const startAngle = Math.PI * 0.15;
    const endAngle = Math.PI * 0.85;
    functions.forEach((fn, i) => {
      const angle = n === 1 ? Math.PI * 0.5 : startAngle + (endAngle - startAngle) * (i / (n - 1));
      positions[fn.Name] = {
        x: SLIMFAAS_X + Math.cos(angle) * 0.35,
        y: SLIMFAAS_Y + Math.sin(angle) * 0.6,
      };
    });
    return positions;
  }, [functions]);

  // Queue center positions (midpoint between SlimFaas and function) — only for functions with queue activity
  const queuePositions = useMemo(() => {
    const positions: Record<string, { x: number; y: number }> = {};
    functions.forEach((fn) => {
      const pos = fnPositions[fn.Name];
      if (!pos) return;
      const qLen = queues.find(q => q.Name === fn.Name)?.Length ?? 0;
      if (!functionsWithQueueActivity.has(fn.Name) && qLen === 0) return;
      positions[fn.Name] = {
        x: (SLIMFAAS_X + pos.x) / 2,
        y: (SLIMFAAS_Y + pos.y) / 2,
      };
    });
    return positions;
  }, [functions, fnPositions, functionsWithQueueActivity, queues]);

  // Queue map
  const queueMap = useMemo(() => {
    const m: Record<string, number> = {};
    queues.forEach(q => { m[q.Name] = q.Length; });
    return m;
  }, [queues]);

  // Spawn new animated messages when activity changes
  useEffect(() => {
    if (activity.length <= lastActivityLen.current) {
      lastActivityLen.current = activity.length;
      return;
    }
    const newEvents = activity.slice(lastActivityLen.current);
    lastActivityLen.current = activity.length;

    for (const evt of newEvents) {
      const fnPos = (name: string) => fnPositions[name] || null;
      const qPos = (name: string) => queuePositions[name] || null;

      if (evt.Type === 'enqueue') {
        // Message goes from SlimFaas → queue entrance → queue center
        const q = qPos(evt.Target);
        if (!q) continue;
        animRef.current.push({
          id: evt.Id,
          waypoints: [
            { x: SLIMFAAS_X, y: SLIMFAAS_Y },
            { x: q.x - QUEUE_W / 2, y: q.y },
            { x: q.x, y: q.y },
          ],
          color: nameColor(evt.Target),
          shape: 'rect',
          startTime: performance.now(),
          duration: ANIM_DURATION,
        });
      } else if (evt.Type === 'dequeue') {
        // Message goes from queue center → queue exit → function
        const q = qPos(evt.Target);
        const f = fnPos(evt.Target);
        if (!q || !f) continue;
        animRef.current.push({
          id: evt.Id,
          waypoints: [
            { x: q.x, y: q.y },
            { x: q.x + QUEUE_W / 2, y: q.y },
            { x: f.x, y: f.y },
          ],
          color: nameColor(evt.Target),
          shape: 'rect',
          startTime: performance.now(),
          duration: ANIM_DURATION,
        });
      } else if (evt.Type === 'request_in') {
        // External → SlimFaas
        animRef.current.push({
          id: evt.Id,
          waypoints: [
            { x: EXTERNAL_IN_X, y: EXTERNAL_IN_Y },
            { x: SLIMFAAS_X, y: SLIMFAAS_Y },
          ],
          color: '#6b778c',
          shape: 'circle',
          startTime: performance.now(),
          duration: ANIM_DURATION,
        });
      } else if (evt.Type === 'request_out' || evt.Type === 'response') {
        // SlimFaas → function (sync, no queue)
        const f = fnPos(evt.Target);
        if (!f) continue;
        animRef.current.push({
          id: evt.Id,
          waypoints: [
            { x: SLIMFAAS_X, y: SLIMFAAS_Y },
            { x: f.x, y: f.y },
          ],
          color: nameColor(evt.Target),
          shape: 'circle',
          startTime: performance.now(),
          duration: ANIM_DURATION,
        });
      } else if (evt.Type === 'event_publish') {
        const f = fnPos(evt.Target);
        if (!f) continue;
        animRef.current.push({
          id: evt.Id,
          waypoints: [
            { x: SLIMFAAS_X, y: SLIMFAAS_Y },
            { x: f.x, y: f.y },
          ],
          color: '#fab005',
          shape: 'circle',
          startTime: performance.now(),
          duration: ANIM_DURATION,
        });
      }
    }
  }, [activity, fnPositions, queuePositions]);

  /** Interpolate position along a polyline of waypoints at parameter t in [0,1] */
  const interpolateWaypoints = useCallback((waypoints: { x: number; y: number }[], t: number) => {
    if (waypoints.length < 2) return waypoints[0];
    const segments = waypoints.length - 1;
    const segLen = 1 / segments;
    const segIdx = Math.min(Math.floor(t / segLen), segments - 1);
    const segT = (t - segIdx * segLen) / segLen;
    const ease = d3.easeCubicInOut(segT);
    const a = waypoints[segIdx];
    const b = waypoints[segIdx + 1];
    return {
      x: a.x + (b.x - a.x) * ease,
      y: a.y + (b.y - a.y) * ease,
    };
  }, []);

  // Animation loop
  const animate = useCallback(() => {
    const svg = svgRef.current;
    if (!svg) return;
    const g = d3.select(svg).select<SVGGElement>('.network-map__messages');
    const now = performance.now();
    const w = svg.clientWidth || SVG_MIN_WIDTH;
    const h = SVG_HEIGHT;

    // Remove finished
    animRef.current = animRef.current.filter(m => now - m.startTime < m.duration);

    // ── Rect messages (async / queued) ──
    const rectData = animRef.current.filter(m => m.shape === 'rect');
    const rects = g.selectAll<SVGRectElement, AnimatedMessage>('rect.msg-rect')
      .data(rectData, d => d.id);

    rects.enter()
      .append('rect')
      .attr('class', 'msg-rect')
      .attr('width', 8).attr('height', 6).attr('rx', 1.5)
      .attr('opacity', 0.95)
      .merge(rects)
      .attr('fill', d => d.color)
      .attr('x', d => {
        const t = Math.min(1, (now - d.startTime) / d.duration);
        return interpolateWaypoints(d.waypoints, t).x * w - 4;
      })
      .attr('y', d => {
        const t = Math.min(1, (now - d.startTime) / d.duration);
        return interpolateWaypoints(d.waypoints, t).y * h - 3;
      })
      .attr('opacity', d => {
        const t = (now - d.startTime) / d.duration;
        return t > 0.85 ? Math.max(0, 1 - (t - 0.85) / 0.15) : 0.95;
      });

    rects.exit().remove();

    // ── Circle messages (sync) ──
    const circleData = animRef.current.filter(m => m.shape === 'circle');
    const circles = g.selectAll<SVGCircleElement, AnimatedMessage>('circle.msg-circle')
      .data(circleData, d => d.id);

    circles.enter()
      .append('circle')
      .attr('class', 'msg-circle')
      .attr('r', 5)
      .attr('opacity', 0.9)
      .merge(circles)
      .attr('fill', d => d.color)
      .attr('cx', d => {
        const t = Math.min(1, (now - d.startTime) / d.duration);
        return interpolateWaypoints(d.waypoints, t).x * w;
      })
      .attr('cy', d => {
        const t = Math.min(1, (now - d.startTime) / d.duration);
        return interpolateWaypoints(d.waypoints, t).y * h;
      })
      .attr('opacity', d => {
        const t = (now - d.startTime) / d.duration;
        return t > 0.8 ? Math.max(0, 1 - (t - 0.8) / 0.2) : 0.9;
      });

    circles.exit().remove();

    frameRef.current = requestAnimationFrame(animate);
  }, [interpolateWaypoints]);

  // Render static elements + start animation loop
  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;

    const w = svg.clientWidth || SVG_MIN_WIDTH;
    const h = SVG_HEIGHT;
    const sel = d3.select(svg);
    sel.selectAll('*').remove();

    // Defs for gradients/filters
    const defs = sel.append('defs');
    const dropShadow = defs.append('filter').attr('id', 'shadow').attr('x', '-20%').attr('y', '-20%').attr('width', '140%').attr('height', '140%');
    dropShadow.append('feDropShadow').attr('dx', 0).attr('dy', 2).attr('stdDeviation', 3).attr('flood-opacity', 0.15);

    // Arrow marker for queue connections
    defs.append('marker')
      .attr('id', 'arrow-in').attr('viewBox', '0 0 10 10')
      .attr('refX', 5).attr('refY', 5).attr('markerWidth', 6).attr('markerHeight', 6)
      .attr('orient', 'auto-start-reverse')
      .append('path').attr('d', 'M 0 0 L 10 5 L 0 10 z').attr('fill', '#adb5bd');

    // Background
    sel.append('rect').attr('width', w).attr('height', h).attr('fill', '#f8f9fa').attr('rx', 12);

    // ── External IN shape (left arrow) ──
    const exInX = EXTERNAL_IN_X * w;
    const exInY = EXTERNAL_IN_Y * h;
    sel.append('polygon')
      .attr('points', `${exInX - 18},${exInY} ${exInX + 12},${exInY - 16} ${exInX + 12},${exInY + 16}`)
      .attr('fill', '#adb5bd').attr('opacity', 0.7);
    sel.append('text').attr('x', exInX).attr('y', exInY + 30)
      .attr('text-anchor', 'middle').attr('fill', '#6b778c').attr('font-size', 11)
      .text('IN');

    // ── External OUT shape (right arrow) ──
    const exOutX = EXTERNAL_OUT_X * w;
    const exOutY = EXTERNAL_OUT_Y * h;
    sel.append('polygon')
      .attr('points', `${exOutX + 18},${exOutY} ${exOutX - 12},${exOutY - 16} ${exOutX - 12},${exOutY + 16}`)
      .attr('fill', '#adb5bd').attr('opacity', 0.7);
    sel.append('text').attr('x', exOutX).attr('y', exOutY + 30)
      .attr('text-anchor', 'middle').attr('fill', '#6b778c').attr('font-size', 11)
      .text('OUT');

    // ── SlimFaas central node ──
    const sfX = SLIMFAAS_X * w;
    const sfY = SLIMFAAS_Y * h;
    sel.append('rect')
      .attr('x', sfX - 50).attr('y', sfY - 25).attr('width', 100).attr('height', 50)
      .attr('rx', 10).attr('fill', '#0000ff').attr('filter', 'url(#shadow)');
    sel.append('text').attr('x', sfX).attr('y', sfY + 5)
      .attr('text-anchor', 'middle').attr('fill', '#fff').attr('font-weight', 'bold').attr('font-size', 14)
      .text('SlimFaas');

    // ── Queues + Connection lines ──
    functions.forEach((fn) => {
      const pos = fnPositions[fn.Name];
      if (!pos) return;
      const qLen = queueMap[fn.Name] ?? 0;
      const color = nameColor(fn.Name);
      const qCenter = queuePositions[fn.Name];

      // If no queue activity for this function, draw a direct line from SlimFaas to the function
      if (!qCenter) {
        sel.append('line')
          .attr('x1', sfX).attr('y1', sfY + 25)
          .attr('x2', pos.x * w).attr('y2', pos.y * h - 30)
          .attr('stroke', color).attr('stroke-width', 1.5).attr('stroke-dasharray', '4 3')
          .attr('opacity', 0.35)
          .attr('marker-end', 'url(#arrow-in)');
        return;
      }

      const qCX = qCenter.x * w;
      const qCY = qCenter.y * h;
      const qW = QUEUE_W * w;
      const qH = QUEUE_H_REL;
      const qLeft = qCX - qW / 2;
      const qRight = qCX + qW / 2;

      // ── Line: SlimFaas → Queue entrance ──
      sel.append('line')
        .attr('x1', sfX).attr('y1', sfY + 25)
        .attr('x2', qLeft).attr('y2', qCY)
        .attr('stroke', color).attr('stroke-width', 1.5).attr('stroke-dasharray', '4 3')
        .attr('opacity', 0.35)
        .attr('marker-end', 'url(#arrow-in)');

      // ── Line: Queue exit → Function ──
      sel.append('line')
        .attr('x1', qRight).attr('y1', qCY)
        .attr('x2', pos.x * w).attr('y2', pos.y * h - 30)
        .attr('stroke', color).attr('stroke-width', 1.5).attr('stroke-dasharray', '4 3')
        .attr('opacity', 0.35)
        .attr('marker-end', 'url(#arrow-in)');

      // ── Queue body (pipe shape) ──
      sel.append('rect')
        .attr('x', qLeft).attr('y', qCY - qH / 2)
        .attr('width', qW).attr('height', qH)
        .attr('rx', 4).attr('ry', 4)
        .attr('fill', '#fff').attr('stroke', color).attr('stroke-width', 1.5)
        .attr('filter', 'url(#shadow)');

      // Queue fill (proportional to queue length, max fill at 20+ items)
      const fillRatio = Math.min(1, qLen / 20);
      if (qLen > 0) {
        sel.append('rect')
          .attr('x', qLeft + 1).attr('y', qCY - qH / 2 + 1)
          .attr('width', Math.max(0, (qW - 2) * fillRatio)).attr('height', qH - 2)
          .attr('rx', 3).attr('ry', 3)
          .attr('fill', lightenColor(color, 0.3))
          .attr('opacity', 0.6);

        // Draw small blocks inside the queue to represent individual messages
        const maxBlocks = Math.min(qLen, Math.floor(qW / 8));
        const blockW = 5;
        const blockH = qH - 8;
        const blockSpacing = Math.min(7, (qW - 4) / maxBlocks);
        for (let bi = 0; bi < maxBlocks; bi++) {
          sel.append('rect')
            .attr('x', qLeft + 3 + bi * blockSpacing)
            .attr('y', qCY - blockH / 2)
            .attr('width', blockW).attr('height', blockH)
            .attr('rx', 1)
            .attr('fill', color)
            .attr('opacity', 0.5 + 0.3 * (bi / maxBlocks));
        }
      }

      // Entrance arrow indicator (left side)
      sel.append('text')
        .attr('x', qLeft - 2).attr('y', qCY + 4)
        .attr('text-anchor', 'end').attr('font-size', 10).attr('fill', color)
        .text('▶');

      // Exit arrow indicator (right side)
      sel.append('text')
        .attr('x', qRight + 2).attr('y', qCY + 4)
        .attr('text-anchor', 'start').attr('font-size', 10).attr('fill', color)
        .text('▶');

      // Queue label: name + count
      sel.append('text')
        .attr('x', qCX).attr('y', qCY - qH / 2 - 5)
        .attr('text-anchor', 'middle').attr('font-size', 9).attr('fill', '#6b778c')
        .attr('font-weight', '600')
        .text(fn.Name.length > 12 ? fn.Name.slice(0, 10) + '…' : fn.Name);

      sel.append('text')
        .attr('x', qCX).attr('y', qCY + qH / 2 + 12)
        .attr('text-anchor', 'middle').attr('font-size', 10)
        .attr('fill', qLen > 0 ? color : '#adb5bd')
        .attr('font-weight', 'bold')
        .text(`📥 ${qLen} msg`);
    });

    // ── Function bubbles ──
    functions.forEach((fn) => {
      const pos = fnPositions[fn.Name];
      if (!pos) return;
      const cx = pos.x * w;
      const cy = pos.y * h;
      const color = nameColor(fn.Name);
      const isDown = (fn.NumberReady ?? 0) === 0;
      const radius = 28 + Math.min((fn.Pods ?? []).length * 4, 20);

      // Outer bubble
      sel.append('circle')
        .attr('cx', cx).attr('cy', cy).attr('r', radius)
        .attr('fill', isDown ? '#f8f9fa' : color)
        .attr('stroke', color).attr('stroke-width', 2.5)
        .attr('opacity', isDown ? 0.5 : 0.85)
        .attr('filter', 'url(#shadow)');

      // Function name
      sel.append('text').attr('x', cx).attr('y', cy - 6)
        .attr('text-anchor', 'middle').attr('fill', isDown ? '#6b778c' : '#fff')
        .attr('font-size', 10).attr('font-weight', 'bold')
        .text(fn.Name.length > 14 ? fn.Name.slice(0, 12) + '…' : fn.Name);

      // Replica count
      sel.append('text').attr('x', cx).attr('y', cy + 8)
        .attr('text-anchor', 'middle').attr('fill', isDown ? '#adb5bd' : 'rgba(255,255,255,0.8)')
        .attr('font-size', 9)
        .text(`${fn.NumberReady}/${fn.NumberRequested}`);

      // Pod dots inside the bubble
      const pods = fn.Pods ?? [];
      const podAngleStep = pods.length > 0 ? (2 * Math.PI) / pods.length : 0;
      const podRingRadius = radius * 0.55;
      pods.forEach((pod, pi) => {
        const angle = podAngleStep * pi - Math.PI / 2;
        const px = cx + Math.cos(angle) * podRingRadius;
        const py = cy + Math.sin(angle) * podRingRadius + 5;
        sel.append('circle')
          .attr('cx', px).attr('cy', py).attr('r', 4)
          .attr('fill', podColor(pod.Status))
          .attr('stroke', '#fff').attr('stroke-width', 1);
      });
    });

    // ── Messages layer (on top of everything) ──
    sel.append('g').attr('class', 'network-map__messages');

    // Start animation
    frameRef.current = requestAnimationFrame(animate);

    return () => {
      cancelAnimationFrame(frameRef.current);
    };
  }, [functions, queues, fnPositions, queueMap, queuePositions, animate, functionsWithQueueActivity]);

  return (
    <div className="network-map">
      <h2 className="network-map__title">🔄 Live Network Map</h2>
      <svg
        ref={svgRef}
        className="network-map__svg"
        viewBox={`0 0 ${SVG_MIN_WIDTH} ${SVG_HEIGHT}`}
        preserveAspectRatio="xMidYMid meet"
      />
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
        <span className="network-map__legend-item" style={{ marginLeft: 12 }}>
          <svg width="14" height="10"><circle cx="5" cy="5" r="4" fill="#6b778c" /></svg>
          Sync
        </span>
        <span className="network-map__legend-item">
          <svg width="14" height="10"><rect x="1" y="2" width="10" height="6" rx="1.5" fill="#6b778c" /></svg>
          Async (Queue)
        </span>
      </div>
    </div>
  );
};

export default NetworkMap;






