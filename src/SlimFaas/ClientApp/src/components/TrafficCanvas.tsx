import { useEffect, useRef } from 'react';
import * as d3 from 'd3';
import type { NetworkActivityEvent } from '../types.ts';
import { eventPath, type MapNode, type MapGroup, type Topology } from '../lib/topology.ts';

interface Props {
  topology: Topology; events: NetworkActivityEvent[]; selected: string | null;
  onSelect: (id: string) => void; paused: boolean;
}
interface Camera { zoom: (factor: number) => void; fit: () => void; focus: (id: string) => void }

export default function TrafficCanvas(props: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const camera = useRef<Camera | null>(null);
  const current = useRef(props);
  const dirty = useRef(true);
  current.current = props;
  useEffect(() => { dirty.current = true; }, [props]);

  useEffect(() => {
    const canvas = canvasRef.current!;
    const context = canvas.getContext('2d');
    if (!context) return;
    const selection = d3.select(canvas);
    let transform = d3.zoomIdentity;
    let width = 0, height = 0, frame = 0;
    let previousModel: Topology | null = null;
    let index = d3.quadtree<MapNode>().x(n => n.x).y(n => n.y);
    let lastEvent: string | undefined;
    let initialized = false;
    let fittedWorkloads = false;
    let wasPaused = false;
    let animations: { path: string[]; start: number }[] = [];
    const motion = window.matchMedia('(prefers-reduced-motion: reduce)');
    const css = getComputedStyle(canvas);
    const colors = {
      job: css.getPropertyValue('--job').trim(), function: css.getPropertyValue('--color-primary').trim(),
      slimfaas: css.getPropertyValue('--color-primary').trim(), queue: css.getPropertyValue('--warning').trim(),
      external: css.getPropertyValue('--muted').trim(), ink: css.getPropertyValue('--ink').trim(),
      line: css.getPropertyValue('--line').trim(), background: css.getPropertyValue('--surface').trim(),
    };
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
      if (node) current.current.onSelect(node.id);
      else if (group) current.current.onSelect(group.id);
    };
    canvas.addEventListener('click', click);
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
      const { topology: model, events, selected, paused } = current.current;
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
      const newest = events[events.length - 1]?.Id;
      if (wasPaused && !paused) { lastEvent = newest; animations = []; dirty.current = true; }
      wasPaused = paused;
      if (newest !== lastEvent) {
        const offset = lastEvent ? events.findIndex(e => e.Id === lastEvent) + 1 : events.length;
        if (!paused && !motion.matches) {
          const recent = events.slice(Math.max(offset, events.length - 160)).filter(e => Date.now() - e.TimestampMs < 2500);
          animations = [...animations, ...recent.map(e => ({ path: eventPath(model, e), start: now }))].slice(-200);
        }
        lastEvent = newest; dirty.current = true;
      }
      if (paused || motion.matches) animations = [];
      const hadAnimations = animations.length > 0;
      animations = animations.filter(a => now - a.start < 1400);
      if (hadAnimations && !animations.length) dirty.current = true;
      if (!dirty.current && !animations.length) return;
      dirty.current = false;
      const ratio = window.devicePixelRatio || 1;
      context.setTransform(ratio, 0, 0, ratio, 0, 0);
      context.fillStyle = colors.background; context.fillRect(0, 0, width, height);
      context.translate(transform.x, transform.y); context.scale(transform.k, transform.k);
      const k = transform.k;
      const [left, top] = transform.invert([0, 0]), [right, bottom] = transform.invert([width, height]);
      const visible = (x: number, y: number, w = 0, h = 0) => x + w >= left && y + h >= top && x <= right && y <= bottom;

      // Aggregate identical edges. Instance edges are expanded only for the selected actor.
      const edges = new Map<string, { from: string; to: string; count: number }>();
      for (const event of events.slice(-1500)) {
        const path = eventPath(model, event).map(id => {
          const node = model.byId.get(id)!;
          return k >= 0.48 && selected && (id === selected || node.parent === selected) ? id : node.parent ?? id;
        });
        for (let i = 1; i < path.length; i++) {
          if (path[i - 1] === path[i]) continue;
          const key = `${path[i - 1]}\0${path[i]}`;
          const edge = edges.get(key);
          if (edge) edge.count++;
          else edges.set(key, { from: path[i - 1], to: path[i], count: 1 });
        }
      }
      context.strokeStyle = colors.line;
      for (const edge of edges.values()) {
        const a = position(edge.from), b = position(edge.to);
        if (!a || !b) continue;
        context.lineWidth = Math.min(4, 1 + Math.log10(edge.count)) / k;
        context.beginPath(); context.moveTo(a.x, a.y); context.lineTo(b.x, b.y); context.stroke();
      }

      for (const group of model.groups) {
        if (!visible(group.x, group.y, group.width, group.height)) continue;
        const collapsed = k < 0.48;
        const boxWidth = collapsed ? Math.min(group.width, 220 / k) : group.width;
        const boxHeight = collapsed ? Math.min(group.height, 90 / k) : group.height;
        const boxX = group.x + (group.width - boxWidth) / 2;
        const boxY = group.y + (group.height - boxHeight) / 2;
        context.fillStyle = '#fff'; context.strokeStyle = selected === group.id ? colors[group.kind] : colors.line;
        context.lineWidth = (selected === group.id ? 2 : 1) / k;
        context.beginPath(); context.roundRect(boxX, boxY, boxWidth, boxHeight, Math.min(16 / k, 24)); context.fill(); context.stroke();
        if (boxWidth * k > 80 && boxHeight * k > 45) {
          context.fillStyle = colors[group.kind]; context.font = `600 ${11 / k}px system-ui`;
          context.fillText(group.kind.toUpperCase(), boxX + 12 / k, boxY + 19 / k);
          context.fillStyle = colors.ink; context.font = `600 ${13 / k}px system-ui`;
          const maxChars = Math.max(10, Math.floor(boxWidth * k / 8) - 3);
          const label = group.label.length > maxChars ? `${group.label.slice(0, maxChars - 1)}…` : group.label;
          context.fillText(label, boxX + 12 / k, boxY + 38 / k);
          if (boxHeight * k > 65) {
            context.fillStyle = colors.external; context.font = `${11 / k}px system-ui`;
            context.fillText(group.status, boxX + 12 / k, boxY + boxHeight - 12 / k);
          }
        }
        if (k < 0.48) continue;
        for (const child of group.children) {
          if (!visible(child.x - 10, child.y - 10, 20, 20)) continue;
          context.fillStyle = child.status === 'Failed' || child.status === 'Error' ? '#bd303e' : colors[group.kind];
          context.globalAlpha = child.status === 'Pending' ? 0.45 : 1;
          context.beginPath();
          if (group.kind === 'job') context.roundRect(child.x - 5, child.y - 5, 10, 10, 2);
          else context.arc(child.x, child.y, 5, 0, Math.PI * 2);
          context.fill(); context.globalAlpha = 1;
          if (k >= 3) {
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
        const positions = animation.path.map(position).filter(p => p !== null);
        if (positions.length < 2) continue;
        const progress = (now - animation.start) / 1400 * (positions.length - 1);
        const i = Math.min(positions.length - 2, Math.floor(progress)), fraction = progress - i;
        const a = positions[i], b = positions[i + 1];
        context.fillStyle = colors.function;
        context.beginPath(); context.arc(a.x + (b.x - a.x) * fraction, a.y + (b.y - a.y) * fraction, 3 / k, 0, Math.PI * 2); context.fill();
      }
    };
    frame = requestAnimationFrame(draw);
    return () => {
      cancelAnimationFrame(frame); resize.disconnect(); selection.on('.zoom', null);
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
    </div>
    <p className="traffic-canvas__hint">Scroll to zoom · Drag to explore · Select an instance below</p>
  </div>;
}
