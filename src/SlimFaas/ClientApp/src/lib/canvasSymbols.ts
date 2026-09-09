/** Small canvas-native symbols in the same 24-unit style as the dashboard SVG icons. */
export function drawSymbol(context: CanvasRenderingContext2D, kind: 'queue' | 'external' | 'power', x: number, y: number, size: number) {
  context.save(); context.translate(x, y); context.scale(size / 24, size / 24);
  context.lineWidth = 1.8; context.lineCap = 'round'; context.lineJoin = 'round';
  context.beginPath();
  if (kind === 'power') {
    context.arc(12, 13, 8, -Math.PI / 3, Math.PI * 4 / 3);
    context.moveTo(12, 2); context.lineTo(12, 12);
  } else if (kind === 'external') {
    context.arc(9, 6, 3, 0, Math.PI * 2);
    context.moveTo(3, 21); context.lineTo(3, 17); context.bezierCurveTo(3, 10, 15, 10, 15, 17); context.lineTo(15, 21);
    context.moveTo(16, 9); context.lineTo(23, 9); context.moveTo(20, 6); context.lineTo(23, 9); context.lineTo(20, 12);
  } else {
    for (let index = 0; index < 3; index++) context.roundRect(5 + index * 5, 7, 3, 10, 1);
    context.moveTo(0, 12); context.lineTo(4, 12); context.moveTo(2, 10); context.lineTo(4, 12); context.lineTo(2, 14);
    context.moveTo(20, 12); context.lineTo(24, 12); context.moveTo(22, 10); context.lineTo(24, 12); context.lineTo(22, 14);
  }
  context.stroke(); context.restore();
}
