import assert from 'node:assert/strict';
import { test } from 'node:test';
import { LogBuffer, filterLogs, cleanLogText, logWindow, LOG_BYTES, LOG_LINE_BYTES } from '../src/lib/logs.ts';
const line = (Id, Text = `Message ${Id}`) => ({ Id, Text, TimestampMs: null, Truncated: false });
test('log retention is bounded by count, identical text is retained and duplicate cursors are ignored', () => {
  const buffer = new LogBuffer();
  buffer.append(Array.from({ length: 12000 }, (_, i) => line(i + 1, 'same text')));
  assert.equal(buffer.lines.length, 10000); assert.equal(buffer.lines[0].Id, 2001); assert.equal(buffer.discarded, 2000);
  buffer.append([line(12000), line(1)]);
  assert.equal(buffer.lines.length, 10000);
});
test('UTF-8 byte and line limits preserve valid Unicode and report truncation', () => {
  const buffer = new LogBuffer();
  buffer.append(Array.from({ length: 1000 }, (_, i) => line(i + 1, '🍋'.repeat(5000))));
  assert.ok(buffer.byteLength <= LOG_BYTES); assert.ok(buffer.lines.length < 10000);
  for (const row of buffer.lines) { assert.ok(new TextEncoder().encode(row.Text).length <= LOG_LINE_BYTES); assert.ok(row.Truncated); assert.ok(!row.Text.includes('�')); }
});
test('filters operate on retained text, with explicit case sensitivity and exclusion', () => {
  const lines = [line(1, 'Info request'), line(2, 'INFO probe'), line(3, 'Error')];
  assert.deepEqual(filterLogs(lines, 'info', 'probe', false).map(l => l.Id), [1]);
  assert.deepEqual(filterLogs(lines, 'INFO', '', true).map(l => l.Id), [2]);
  assert.equal(filterLogs(lines, '', '', false).length, 3);
});
test('ANSI color, OSC links and terminal controls cannot change the viewer', () => {
  assert.equal(cleanLogText('\x1b[31mhello\x1b[0m\r\x00'), 'hello');
  assert.equal(cleanLogText('\x1b]8;;https://invalid\x07link\x1b]8;;\x07'), 'link');
  assert.equal(cleanLogText('<script>alert(1)</script>'), '<script>alert(1)</script>');
});
test('virtual windows keep rendered rows bounded at the start, middle, end and after filtering', () => {
  for (const length of [0, 1, 10000]) for (const position of [0, 10000, 10000000]) {
    const window = logWindow(length, position);
    assert.ok(window.end - window.start <= 30);
    assert.equal(window.before + window.after + (window.end - window.start) * 24, length * 24);
  }
});
test('a resized drawer fills its log viewport with bounded overscan, including partial rows', () => {
  for (const height of [120, 361, 900, 2160]) for (const position of [0, 241, 120001, 239999]) {
    const window = logWindow(10000, position, height);
    const first = Math.min(Math.floor(position / 24), 10000 - Math.ceil(height / 24));
    assert.ok(window.start <= first);
    assert.ok(window.end >= Math.min(10000, first + Math.ceil(height / 24)));
    assert.ok(window.end - window.start <= Math.ceil(height / 24) + 11);
    assert.equal(window.before + window.after + (window.end - window.start) * 24, 240000);
  }
  assert.deepEqual(logWindow(0, 100000, 900), { start: 0, end: 0, before: 0, after: 0 });
  assert.deepEqual(logWindow(3, 100000, 900), { start: 0, end: 3, before: 0, after: 0 });
});
