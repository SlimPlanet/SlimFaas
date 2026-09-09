export interface LogLine { Id: number; Text: string; TimestampMs: number | null; Truncated: boolean }
export interface LogSource { Id: string; Name: string; Container: string }
export interface LogState { Status: string; Session: string; DroppedLines: number; MaxLines: number; MaxBytes: number }
export interface LogTarget { kind: 'function' | 'job' | 'slimfaas'; name: string; replica: string }
export const LOG_LINES = 10_000;
export const LOG_BYTES = 8 * 1024 * 1024;
export const LOG_LINE_BYTES = 16 * 1024;
const encoder = new TextEncoder();

export function cleanLogText(value: string) {
  return value.replace(/\x1b(?:\][^\x07\x1b]*(?:\x07|\x1b\\)|\[[0-?]*[ -/]*[@-~]|[@-_])|[\x00-\x08\x0b-\x1f\x7f]/g, '');
}

/** The server cursor orders lines within a session; identical text is never a duplicate. */
export class LogBuffer {
  private items: { line: LogLine; bytes: number }[] = [];
  private offset = 0;
  private bytes = 0;
  private lastId = 0;
  discarded = 0;
  append(incoming: LogLine[]) {
    for (const input of incoming) {
      if (!Number.isSafeInteger(input.Id) || input.Id <= this.lastId || typeof input.Text !== 'string') continue;
      this.lastId = input.Id;
      let text = cleanLogText(input.Text.slice(0, LOG_LINE_BYTES));
      if (input.Text.length > LOG_LINE_BYTES && /[\uD800-\uDBFF]$/.test(text)) text = text.slice(0, -1);
      const encoded = encoder.encode(text);
      const truncated = input.Truncated || input.Text.length > LOG_LINE_BYTES || encoded.length > LOG_LINE_BYTES;
      if (encoded.length > LOG_LINE_BYTES) {
        let end = LOG_LINE_BYTES;
        while (end > 0 && (encoded[end] & 0xc0) === 0x80) end--;
        text = new TextDecoder().decode(encoded.subarray(0, end));
      }
      const bytes = encoder.encode(text).length;
      this.items.push({ line: { ...input, Text: text, Truncated: truncated }, bytes }); this.bytes += bytes;
      while (this.items.length - this.offset > LOG_LINES || this.bytes > LOG_BYTES) {
        this.bytes -= this.items[this.offset].bytes;
        this.items[this.offset++] = undefined!; // Release evicted text immediately, before array compaction.
        this.discarded++;
      }
      if (this.offset >= LOG_LINES) { this.items = this.items.slice(this.offset); this.offset = 0; }
    }
  }
  get lines() { return this.items.slice(this.offset).map(item => item.line); }
  get byteLength() { return this.bytes; }
}

export function filterLogs(lines: LogLine[], include: string, exclude: string, sensitive: boolean) {
  const term = sensitive ? include : include.toLowerCase(), omit = sensitive ? exclude : exclude.toLowerCase();
  return lines.filter(line => {
    const text = sensitive ? line.Text : line.Text.toLowerCase();
    return (!term || text.includes(term)) && (!omit || !text.includes(omit));
  });
}

export const LOG_ROW_HEIGHT = 24;
export function logWindow(length: number, scrollTop: number) {
  const start = Math.min(Math.max(0, length - 1), Math.max(0, Math.floor(scrollTop / LOG_ROW_HEIGHT) - 5));
  const end = Math.min(length, start + 30);
  return { start, end, before: start * LOG_ROW_HEIGHT, after: (length - end) * LOG_ROW_HEIGHT };
}
