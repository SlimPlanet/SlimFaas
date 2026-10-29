export interface DeadLetterSummary {
  Function: string; CaptureEnabled: boolean; FunctionExists: boolean; Count: number;
  OldestFailedAt: string | null; NewestFailedAt: string | null;
  MaxAgeSeconds: number; MaxLength: number; CapturedAt: string; Revision: number;
}
export interface DeadLetterMessage {
  Id: string; Function: string; OriginalElementId: string; AcceptedAt: string; FailedAt: string;
  ExpiresAt: string; Attempts: number; FailureReason: string; LastHttpStatus: number | null;
  ReplayOfDeadLetterId?: string | null; RootElementId?: string | null;
  Request: { Method: string; Path: string; QueryString: string; ContentType: string; BodyLength: number | null; Headers?: unknown };
}
export interface DeadLetterPage<T> { Items: T[]; NextCursor: string | null; CapturedAt: string; Revision: number }
export interface DeadLetterReplay { DeadLetterId: string; OriginalElementId: string; ElementId: string; Function: string }
const object = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object';
export function isDeadLetterSummary(value: unknown): value is DeadLetterSummary {
  return object(value) && typeof value.Function === 'string' && typeof value.Count === 'number' &&
    typeof value.CaptureEnabled === 'boolean' && typeof value.FunctionExists === 'boolean' &&
    typeof value.CapturedAt === 'string' && typeof value.Revision === 'number' &&
    typeof value.MaxAgeSeconds === 'number' && typeof value.MaxLength === 'number' &&
    (value.OldestFailedAt === null || typeof value.OldestFailedAt === 'string') &&
    (value.NewestFailedAt === null || typeof value.NewestFailedAt === 'string');
}
export function isDeadLetterMessage(value: unknown): value is DeadLetterMessage {
  return object(value) && ['Id', 'Function', 'OriginalElementId', 'AcceptedAt', 'FailedAt', 'ExpiresAt', 'FailureReason']
    .every(key => typeof value[key] === 'string') && typeof value.Attempts === 'number' &&
    (value.LastHttpStatus === null || typeof value.LastHttpStatus === 'number') && object(value.Request) &&
    ['Method', 'Path', 'QueryString', 'ContentType'].every(key => object(value.Request) && typeof value.Request[key] === 'string') &&
    (value.Request.BodyLength === null || typeof value.Request.BodyLength === 'number');
}
export function isReplay(value: unknown): value is DeadLetterReplay {
  return object(value) && ['DeadLetterId', 'OriginalElementId', 'ElementId', 'Function'].every(key => typeof value[key] === 'string');
}
export function isPage<T>(guard: (value: unknown) => value is T) {
  return (value: unknown): value is DeadLetterPage<T> => object(value) && Array.isArray(value.Items) &&
    value.Items.every(guard) && (value.NextCursor === null || typeof value.NextCursor === 'string') &&
    typeof value.CapturedAt === 'string' && typeof value.Revision === 'number';
}
export const deadLetterPath = (name?: string, id?: string) =>
  `/data/dead-letters${name === undefined ? '' : `/${encodeURIComponent(name)}`}${id === undefined ? '' : `/messages/${encodeURIComponent(id)}`}`;
export const deadLetterLink = (name: string, id?: string) =>
  `#/dead-letters/${encodeURIComponent(name)}${id === undefined ? '' : `/messages/${encodeURIComponent(id)}`}`;
export async function deadLetterRequest<T>(path: string, guard: (value: unknown) => value is T, signal: AbortSignal, method = 'GET'): Promise<T> {
  const response = await fetch(path, { signal, method });
  if (!response.ok) throw new Error(`HTTP ${response.status}: ${response.status === 404 ? 'Absent, expired or restricted.' : response.status === 409 ? 'Function or payload unavailable.' : response.status === 429 ? 'Capacity limit reached.' : 'DLQ request unavailable.'}`);
  const value: unknown = await response.json();
  if (!guard(value)) throw new Error('Invalid DLQ response.');
  return value;
}
export const PREVIEW_BYTES = 65536;
export async function readDeadLetterPreview(response: Response) {
  if (!response.ok || !response.body) throw new Error(`Unable to preview body: HTTP ${response.status}`);
  const reader = response.body.getReader();
  const bytes = new Uint8Array(PREVIEW_BYTES + 1);
  let length = 0;
  try {
    while (length < bytes.length) {
      const { value, done } = await reader.read();
      if (done) break;
      const available = Math.min(value.length, bytes.length - length);
      bytes.set(value.subarray(0, available), length); length += available;
    }
  } finally { await reader.cancel(); reader.releaseLock(); }
  const range = response.headers.get('Content-Range');
  const total = range ? Number(range.split('/')[1]) : Number(response.headers.get('Content-Length'));
  return { text: new TextDecoder().decode(bytes.subarray(0, Math.min(length, PREVIEW_BYTES))),
    truncated: length > PREVIEW_BYTES || Number.isFinite(total) && total > PREVIEW_BYTES };
}
