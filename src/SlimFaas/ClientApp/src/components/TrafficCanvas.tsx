import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import * as d3 from 'd3';
import type { NetworkActivityEvent } from '../types.ts';
import { selectedEvent, type MapNode, type MapGroup, type Topology } from '../lib/topology.ts';

import { TrafficPlayback, displayPath, markerProgress, type Speed } from '../lib/traffic.ts';
import { drawSymbol } from '../lib/canvasSymbols.ts';

interface Props {
  topology: Topology; events: NetworkActivityEvent[]; visibleEvents: NetworkActivityEvent[];
  activitySession: number; speed: Speed; eventType: string; isolate: boolean; selected: string | null;
  onSelect: (id: string) => void; paused: boolean;
}
interface Camera { zoom: (factor: number) => void; fit: () => void; focus: (id: string) => void }

export default function TrafficCanvas(props: Props) {
  const [hover, setHover] = useState('');
  const [activityStatus, setActivityStatus] = useState('Waiting for live activity');
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const camera = useRef<Camera | null>(null);
  const current = useRef(props);
  const dirty = useRef(true);
  const [playback] = useState(() => new TrafficPlayback());
  current.current = props;
  useLayoutEffect(() => {
    playback.update(props.events, props.activitySession, props.paused || document.hidden, performance.now(), props.topology,
      props.speed, props.eventType, props.selected, props.isolate);
    dirty.current = true;
  }, [props.events, props.activitySession, props.paused, props.topology, props.speed, props.eventType, props.selected, props.isolate, props.visibleEvents]);

  useEffect(() => {
    const canvas = canvasRef.current!;
    const context = canvas.getContext('2d');
    if (!context) return;
    const selection = d3.select(canvas);
    let transform = d3.zoomIdentity;
    let width = 0, height = 0, frame = 0;
    let previousModel: Topology | null = null;
    let index = d3.quadtree<MapNode>().x(n => n.x).y(n => n.y);
    let initialized = false;
    let fittedWorkloads = false;
    let lastStatusAt = 0;
    const motion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const css = getComputedStyle(canvas);
    const colors = {
      job: css.getPropertyValue('--job').trim(), function: css.getPropertyValue('--color-primary').trim(),
      slimfaas: css.getPropertyValue('--color-primary').trim(), queue: css.getPropertyValue('--warning').trim(),
      external: css.getPropertyValue('--muted').trim(), ink: css.getPropertyValue('--ink').trim(),
      success: css.getPropertyValue('--success').trim(), danger: css.getPropertyValue('--danger').trim(), soft: css.getPropertyValue('--soft').trim(),
      line: css.getPropertyValue('--line').trim(), background: css.getPropertyValue('--surface').trim(),
    };
    const messageColor = (kind: string) => kind === 'publication' ? colors.job : kind === 'queue' ? colors.queue : kind === 'reply' ? colors.external : colors.function;
    const stateColor = (state?: string) => state === 'Sleeping' ? colors.external : state === 'Starting' || state === 'Scaling' ? colors.queue : state === 'Error' ? colors.danger : colors.success;
    const zoom = d3.zoom<HTMLCanvasElement, unknown>().scaleExtent([0.001, 12])
      .on('zoom', event => { transform = event.transform; dirty.current = true; });
    selection.call(zoom).on('dblclick.zoom', null);
    const setTransform = (x: number, y: number, k: number) => selection.call(zoom.transform, d3.zoomIdentity.translate(x, y).scale(k));
    const fit = () => {
      const model = current.current.topology;
      const k = Math.min((width - 64) / model.width, (height - 64) / model.height, 1);
      setTransform((width - model.width * k) / 2, (height - model.height * k) / 2, Math.max(0.001, k));
    };
    camera.current = {
      fit, zoom: factor => selection.call(zoom.scaleBy, factor),
      focus: id => {
        const n = current.current.topology.byId.get(id);
        if (!n) return;
        const group = n as MapGroup;
        const k = n.parent ? 2 : Math.min((width - 100) / group.width, (height - 100) / group.height, 1.5);
        const x = n.x + (n.parent ? 0 : group.width / 2), y = n.y + (n.parent ? 0 : group.height / 2);
        setTransform(width / 2 - x * k, height / 2 - y * k, k);
      },
    };
    const resize = new ResizeObserver(() => {
      width = canvas.clientWidth; height = canvas.clientHeight;
      const ratio = window.devicePixelRatio || 1;
      canvas.width = Math.round(width * ratio); canvas.height = Math.round(height * ratio);
      if (!initialized && width && height) { fit(); initialized = true; }
      dirty.current = true;
    });
    resize.observe(canvas);

    const click = (event: MouseEvent) => {
      if (event.defaultPrevented) return;
      const bounds = canvas.getBoundingClientRect();
      const [x, y] = transform.invert([event.clientX - bounds.left, event.clientY - bounds.top]);
      const node = transform.k >= 0.48 ? index.find(x, y, 14 / transform.k) : undefined;
      const group = current.current.topology.groups.find(g => x >= g.x && x <= g.x + g.width && y >= g.y && y <= g.y + g.height);
      if (node || group) canvas.focus({ preventScroll: true });
      if (node) current.current.onSelect(node.id);
      else if (group) current.current.onSelect(group.id);
    };
    canvas.addEventListener('click', click);
    const hoverNode = (event: MouseEvent) => {
      const bounds = canvas.getBoundingClientRect();
      const [x, y] = transform.invert([event.clientX - bounds.left, event.clientY - bounds.top]);
      const node = transform.k >= 0.48 ? index.find(x, y, 14 / transform.k) : undefined;
      setHover(node ? `${node.label} · ${node.status}` : '');
    };
    const clearHover = () => setHover('');
    canvas.addEventListener('mousemove', hoverNode);
    canvas.addEventListener('mouseleave', clearHover);
    const keydown = (event: KeyboardEvent) => {
      if (event.key === '+' || event.key === '=') camera.current?.zoom(1.4);
      else if (event.key === '-') camera.current?.zoom(1 / 1.4);
      else if (event.key === 'Home') fit();
      else if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
        const dx = event.key === 'ArrowLeft' ? 60 : event.key === 'ArrowRight' ? -60 : 0;
        const dy = event.key === 'ArrowUp' ? 60 : event.key === 'ArrowDown' ? -60 : 0;
        setTransform(transform.x + dx, transform.y + dy, transform.k);
      } else return;
      event.preventDefault();
    };
    canvas.addEventListener('keydown', keydown);

    const position = (id: string) => {
      const model = current.current.topology;
      let n = model.byId.get(id);
      if (!n) return null;
      if (n.parent && transform.k < 0.48) n = model.byId.get(n.parent)!;
      const group = n as MapGroup;
      return { x: n.x + (n.parent ? 0 : group.width / 2), y: n.y + (n.parent ? 0 : group.height / 2) };
    };
    const draw = (now: number) => {
      frame = requestAnimationFrame(draw);
      const { topology: model, events, visibleEvents, activitySession, speed, eventType, isolate, selected, paused } = current.current;
      if (model !== previousModel) {
        index = d3.quadtree<MapNode>().x(n => n.x).y(n => n.y).addAll(model.nodes.filter(n => n.parent));
        // The canvas can mount before the first SSE state arrives. Fit that first
        // workload inventory once, then preserve the user's camera on refreshes.
        if (!fittedWorkloads && model.groups.some(g => g.kind === 'function' || g.kind === 'job')) {
          if (initialized) fit();
          fittedWorkloads = true;
        }
        previousModel = model; dirty.current = true;
      }
      const hadMarkers = playback.markers.size > 0 || playback.arrivals.size > 0;
      playback.update(events, activitySession, paused || document.hidden, now, model, speed, eventType, selected, isolate);
      const animations = [...playback.markers.values()];
      if (hadMarkers && !animations.length) dirty.current = true;
      if (now - lastStatusAt > 250) {
        const count = animations.reduce((total, marker) => total + marker.count, 0);
        setActivityStatus(`${motion.matches ? 'Static activity · ' : ''}${count.toLocaleString()} events / ${animations.length} markers · grouped by route and type${playback.omitted ? ` · ${playback.omitted.toLocaleString()} not animated (visual limit)` : ''}`);
        lastStatusAt = now;
      }
      if (!dirty.current && !animations.length && !playback.arrivals.size) return;
      dirty.current = false;
      const ratio = window.devicePixelRatio || 1;
      context.setTransform(ratio, 0, 0, ratio, 0, 0);
      context.fillStyle = colors.background; context.fillRect(0, 0, width, height);
      context.translate(transform.x, transform.y); context.scale(transform.k, transform.k);
      const k = transform.k;
      const [left, top] = transform.invert([0, 0]), [right, bottom] = transform.invert([width, height]);
      const visible = (x: number, y: number, w = 0, h = 0) => x + w >= left && y + h >= top && x <= right && y <= bottom;

      // Keep instance destinations; collapse their screen positions only at overview zoom.
      const edges = new Map<string, { from: string; to: string; count: number; kind: string; highlighted: boolean }>();
      for (const event of visibleEvents.slice(-1500)) {
        const kind = playback.visualKind(event);
        if (kind === null) continue;
        const path = displayPath(model, event, selected);
        for (let i = 1; i < path.length; i++) {
          const key = JSON.stringify([path[i - 1], path[i], kind]);
          const edge = edges.get(key);
          if (edge) { edge.count++; edge.highlighted ||= selectedEvent(model, event, selected); }
          else edges.set(key, { from: path[i - 1], to: path[i], count: 1, kind, highlighted: selectedEvent(model, event, selected) });
        }
      }
      for (const edge of edges.values()) {
        const a = position(edge.from), b = position(edge.to);
        if (!a || !b) continue;
        context.globalAlpha = selected && !edge.highlighted ? 0.15 : 0.5;
        context.strokeStyle = messageColor(edge.kind);
        context.lineWidth = Math.min(4, 1 + Math.log10(edge.count)) / k;
        context.beginPath(); context.moveTo(a.x, a.y); context.lineTo(b.x, b.y); context.stroke();
      }
      context.globalAlpha = 1;

      for (const group of model.groups) {
        if (!visible(group.x, group.y, group.width, group.height)) continue;
        const collapsed = k < 0.48;
        const boxWidth = collapsed ? Math.min(group.width, 220 / k) : group.width;
        const boxHeight = collapsed ? Math.min(group.height, 90 / k) : group.height;
        const boxX = group.x + (group.width - boxWidth) / 2;
        const boxY = group.y + (group.height - boxHeight) / 2;
        context.fillStyle = group.state === 'Sleeping' ? colors.soft : '#fff'; context.strokeStyle = selected === group.id ? colors[group.kind] : colors.line;
        context.lineWidth = (selected === group.id ? 2 : 1) / k;
        context.beginPath(); context.roundRect(boxX, boxY, boxWidth, boxHeight, Math.min(16 / k, 24)); context.fill(); context.stroke();
        if (boxWidth * k > 80 && boxHeight * k > 45) {
          const symbol = group.kind === 'function' ? 'power' : group.kind === 'queue' || group.kind === 'external' ? group.kind : null;
          context.strokeStyle = group.state ? stateColor(group.state) : colors[group.kind];
          if (symbol) drawSymbol(context, symbol, boxX + 10 / k, boxY + 6 / k, 18 / k);
          context.fillStyle = colors[group.kind]; context.font = `600 ${11 / k}px system-ui`;
          context.fillText(group.kind.toUpperCase(), boxX + (symbol ? 35 : 12) / k, boxY + 19 / k);
          context.fillStyle = colors.ink; context.font = `600 ${13 / k}px system-ui`;
          const maxChars = Math.max(10, Math.floor(boxWidth * k / 8) - 3);
          const groupLabel = group.kind === 'external' ? 'External callers' : group.label;
          const label = groupLabel.length > maxChars ? `${groupLabel.slice(0, maxChars - 1)}…` : groupLabel;
          context.fillText(label, boxX + 12 / k, boxY + 38 / k);
          if (boxHeight * k > 65) {
            context.font = `${11 / k}px system-ui`;
            const footerY = boxY + boxHeight - 12 / k;
            if (group.state) {
              const badgeWidth = context.measureText(group.state).width + 14 / k;
              context.strokeStyle = stateColor(group.state); context.lineWidth = 1 / k;
              context.fillStyle = colors.background;
              context.beginPath(); context.roundRect(boxX + 10 / k, footerY - 13 / k, badgeWidth, 18 / k, 9 / k); context.fill(); context.stroke();
              context.fillStyle = stateColor(group.state); context.fillText(group.state, boxX + 17 / k, footerY);
              const remaining = boxWidth - badgeWidth - 30 / k;
              if (remaining * k > 45) {
                context.fillStyle = colors.external;
                context.fillText(group.status.slice(group.state.length + 3), boxX + badgeWidth + 17 / k, footerY, remaining);
              }
            } else {
              context.fillStyle = colors.external;
              context.fillText(group.status, boxX + 12 / k, footerY, boxWidth - 24 / k);
            }
          }
        }
        if (boxWidth * k > 140 && boxHeight * k > 115 && (group.kind === 'queue' || group.kind === 'external')) {
          context.strokeStyle = colors[group.kind];
          const size = Math.min(48, boxHeight * k - 70);
          drawSymbol(context, group.kind, boxX + boxWidth / 2 - size / 2 / k, boxY + (44 + (boxHeight * k - 70 - size) / 2) / k, size / k);
        }
        if (k < 0.48) continue;
        for (const child of group.children) {
          if (!visible(child.x - 10, child.y - 10, 20, 20)) continue;
          context.fillStyle = child.role === 'Leader' ? colors.success : child.status === 'Failed' || child.status === 'Error' ? '#bd303e' : colors[group.kind];
          context.globalAlpha = child.status === 'Pending' ? 0.45 : 1;
          context.beginPath();
          if (group.kind === 'job') context.roundRect(child.x - 5, child.y - 5, 10, 10, 2);
          else context.arc(child.x, child.y, 5, 0, Math.PI * 2);
          context.fill(); context.globalAlpha = 1;
          if (child.role === 'Leader') {
            context.strokeStyle = colors.success; context.lineWidth = 1.5 / k;
            context.beginPath(); context.moveTo(child.x - 6, child.y - 10); context.lineTo(child.x - 6, child.y - 17);
            context.lineTo(child.x - 2, child.y - 13); context.lineTo(child.x, child.y - 19);
            context.lineTo(child.x + 2, child.y - 13); context.lineTo(child.x + 6, child.y - 17);
            context.lineTo(child.x + 6, child.y - 10); context.closePath(); context.stroke();
            context.font = `600 ${10 / k}px system-ui`; context.textAlign = 'center';
            context.fillText('Leader', child.x, child.y + 15 / k); context.textAlign = 'left';
          }
          const arrival = playback.arrivals.get(child.id);
          if (arrival && now >= arrival - 1000) {
            context.strokeStyle = colors.success; context.lineWidth = 2 / k;
            context.beginPath(); context.arc(child.x, child.y, 10, 0, Math.PI * 2); context.stroke();
          }
          if (k >= 3 && child.role !== 'Leader') {
            context.fillStyle = colors.ink; context.font = `${10 / k}px system-ui`; context.textAlign = 'center';
            context.fillText(child.label.length > 14 ? `…${child.label.slice(-13)}` : child.label, child.x, child.y + 14);
            context.textAlign = 'left';
          }
          if (selected === child.id) {
            context.strokeStyle = colors.ink; context.lineWidth = 2 / k;
            context.beginPath(); context.arc(child.x, child.y, 10, 0, Math.PI * 2); context.stroke();
          }
        }
      }
      for (const animation of animations) {
        const positions = animation.path.map(position).filter(p => p !== null)
          .filter((p, i, path) => i === 0 || p.x !== path[i - 1].x || p.y !== path[i - 1].y);
        if (animation.waiting && positions.length) {
          const point = positions[0];
          context.strokeStyle = colors.queue; context.lineWidth = 2 / k;
          context.beginPath(); context.arc(point.x, point.y, 9 / k, 0, Math.PI * 2); context.stroke();
          context.fillStyle = colors.ink; context.font = `${11 / k}px system-ui`;
          context.fillText('Waiting for a ready replica', point.x + 14 / k, point.y - 10 / k);
          continue;
        }
        if (positions.length < 2) continue;
        const progress = markerProgress(now, animation.start, animation.duration) * (positions.length - 1);
        const i = Math.min(positions.length - 2, Math.floor(progress)), fraction = progress - i;
        const a = positions[i], b = positions[i + 1];
        context.globalAlpha = selected && !animation.highlighted ? 0.25 : 1;
        context.fillStyle = context.strokeStyle = messageColor(animation.kind);
        if (motion.matches) {
          context.lineWidth = 3 / k; context.beginPath();
          positions.forEach((point, index) => index === 0 ? context.moveTo(point.x, point.y) : context.lineTo(point.x, point.y));
          context.stroke();
        }
        const destination = positions[positions.length - 1];
        const x = motion.matches ? destination.x : a.x + (b.x - a.x) * fraction;
        const y = motion.matches ? destination.y : a.y + (b.y - a.y) * fraction;
        context.beginPath();
        if (animation.kind === 'queue') context.rect(x - 4 / k, y - 4 / k, 8 / k, 8 / k);
        else if (animation.kind === 'publication') { context.moveTo(x, y - 6 / k); context.lineTo(x + 5 / k, y); context.lineTo(x, y + 6 / k); context.lineTo(x - 5 / k, y); context.closePath(); }
        else context.arc(x, y, 4 / k, 0, Math.PI * 2);
        if (animation.kind === 'reply') { context.lineWidth = 2 / k; context.stroke(); } else context.fill();
        if (animation.count > 1) { context.font = `600 ${11 / k}px system-ui`; context.fillText(`×${animation.count}`, x + 8 / k, y - 6 / k); }
      }
      context.globalAlpha = 1;

    };
    const visibility = () => {
      const p = current.current;
      playback.update(p.events, p.activitySession, p.paused || document.hidden, performance.now(), p.topology, p.speed, p.eventType, p.selected, p.isolate);
      dirty.current = true;
    };
    document.addEventListener('visibilitychange', visibility);
    frame = requestAnimationFrame(draw);
    return () => {
      document.removeEventListener('visibilitychange', visibility);
      cancelAnimationFrame(frame); resize.disconnect(); selection.on('.zoom', null);
      canvas.removeEventListener('mousemove', hoverNode); canvas.removeEventListener('mouseleave', clearHover);
      canvas.removeEventListener('click', click); canvas.removeEventListener('keydown', keydown); camera.current = null;
    };
  }, []);

  useEffect(() => { if (props.selected) camera.current?.focus(props.selected); }, [props.selected]);

  return <div className="traffic-canvas">
    <canvas ref={canvasRef} className="traffic-canvas__surface" tabIndex={0} aria-label="Live traffic map. Use arrow keys to pan, plus and minus to zoom, Home to fit. Select instances in the list below." />
    <div className="traffic-canvas__controls">
      <button type="button" className="button button--quiet" aria-label="Zoom in" onClick={() => camera.current?.zoom(1.4)}>+</button>
      <button type="button" className="button button--quiet" aria-label="Zoom out" onClick={() => camera.current?.zoom(1 / 1.4)}>−</button>
      <button type="button" className="button button--quiet" onClick={() => camera.current?.fit()}>Fit map</button>
      {props.selected && props.topology.byId.get(props.selected)?.kind === 'function' && <button type="button" className="button button--quiet" onClick={() => camera.current?.focus(props.selected!)}>Show replicas</button>}
    </div>
    <output className="traffic-canvas__activity" aria-live="off">{activityStatus}</output>
    {hover && <output className="traffic-canvas__hover">{hover}</output>}
    <p className="traffic-canvas__hint">Scroll to zoom · Drag to explore · Select an instance below</p>
  </div>;
}
