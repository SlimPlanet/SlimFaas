// Observe the production renderer and real EventSource; do not replace either.
export function observeCanvas() {
  const observation = window.__traffic = { frames: [], events: [], state: null };
  const NativeEventSource = window.EventSource;
  window.EventSource = class extends NativeEventSource {
    constructor(...args) {
      super(...args);
      this.addEventListener('state', e => { observation.state = JSON.parse(e.data); });
      for (const type of ['activity', 'activity_batch']) this.addEventListener(type, e => {
        const value = JSON.parse(e.data);
        observation.events.push(...(Array.isArray(value) ? value : [value]));
        observation.events = observation.events.slice(-5000);
      });
    }
  };
  let frame, path = [];
  const proto = CanvasRenderingContext2D.prototype;
  const watched = context => context.canvas.classList.contains('traffic-canvas__surface');
  const wrap = (name, observe) => {
    const original = proto[name];
    proto[name] = function(...args) { if (watched(this)) observe.call(this, ...args); return original.apply(this, args); };
  };
  wrap('fillRect', function() {
    frame = { time: performance.now(), markers: [], waiting: false };
    observation.frames.push(frame);
    if (observation.frames.length > 10_000) observation.frames.shift();
  });
  wrap('beginPath', function() { path = []; });
  wrap('arc', function(x, y, radius) { path.push({ arc: true, x, y, radius }); });
  wrap('moveTo', function(x, y) { path.push({ x, y }); });
  wrap('lineTo', function(x, y) { path.push({ x, y }); });
  wrap('fillText', function(text) { if (frame && text === 'Waiting for a ready replica') frame.waiting = true; });
  for (const method of ['fill', 'stroke']) wrap(method, function() {
    if (!frame) return;
    const scale = this.getTransform().a / devicePixelRatio;
    const circle = path.length === 1 && path[0].arc && Math.abs(path[0].radius * scale - 4) < 0.01;
    const diamond = path.length === 4 && path.every(p => !p.arc) && Math.abs((path[1].x - path[3].x) * scale - 10) < 0.01;
    if (circle || diamond) frame.markers.push({
      x: path[0].x, y: diamond ? path[1].y : path[0].y,
      shape: diamond ? 'diamond' : 'circle', color: method === 'fill' ? this.fillStyle : this.strokeStyle, outlined: method === 'stroke',
    });
  });
}
