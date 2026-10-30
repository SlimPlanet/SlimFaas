export interface StreamSummary {
  Name: string; Visibility: string; RetainedMessageCount: number; VisibleFunctionCount: number;
  OldestAppendedAt: string | null; NewestAppendedAt: string | null; MaxAgeSeconds: number; MaxLength: number;
  EarliestMessageId: string | null; LatestMessageId: string | null; Generation: string; CapturedAt: string; Revision: number;
}
export interface StreamFunction {
  Stream: string; Function: string; Active: boolean; Generation: string; StartSequence: number;
  AcknowledgedThrough: number; LastAcknowledged: number; RetentionGapCount: number; Lag: number;
  PendingCount: number; WaitingForCallbackCount: number; RetryScheduledCount: number; FailedCount: number;
  RunnableCount: number; LastStatus: number | null; CapturedAt: string; Revision: number;
}
export interface StreamMessage { Id: string; AppendedAt: string; ContentType: string; PayloadSize: number; CapturedAt: string; Revision: number }
export interface StreamPage<T> { Items: T[]; NextCursor: string | null; CapturedAt: string; Revision: number }
export function newerStreamObservation<T extends { Revision: number; Generation?: string }>(previous: T | null, incoming: T): T {
  return previous && previous.Generation === incoming.Generation && previous.Revision > incoming.Revision ? previous : incoming;
}
export function reconcileStreamCatalog<T extends { Name: string; Revision: number; Generation?: string }>(
  previous: StreamPage<T> | null, incoming: StreamPage<T>): StreamPage<T> {
  return { ...incoming, Items: incoming.Items.map(item =>
    newerStreamObservation(previous?.Items.find(known => known.Name === item.Name) ?? null, item)) };
}
const object = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object';
const strings = (value: Record<string, unknown>, keys: string[]) => keys.every(key => typeof value[key] === 'string');
const numbers = (value: Record<string, unknown>, keys: string[]) => keys.every(key => typeof value[key] === 'number' && Number.isFinite(value[key]));
export const isStreamSummary = (value: unknown): value is StreamSummary => object(value) &&
  strings(value, ['Name', 'Visibility', 'Generation', 'CapturedAt']) &&
  numbers(value, ['RetainedMessageCount', 'VisibleFunctionCount', 'MaxAgeSeconds', 'MaxLength', 'Revision']) &&
  ['OldestAppendedAt', 'NewestAppendedAt', 'EarliestMessageId', 'LatestMessageId'].every(key => value[key] === null || typeof value[key] === 'string');
export const isStreamFunction = (value: unknown): value is StreamFunction => object(value) &&
  strings(value, ['Stream', 'Function', 'Generation', 'CapturedAt']) && typeof value.Active === 'boolean' &&
  (value.LastStatus === null || typeof value.LastStatus === 'number') &&
  numbers(value, ['StartSequence', 'AcknowledgedThrough', 'LastAcknowledged', 'RetentionGapCount', 'Lag', 'PendingCount',
    'WaitingForCallbackCount', 'RetryScheduledCount', 'FailedCount', 'RunnableCount', 'Revision']);
export const isStreamMessage = (value: unknown): value is StreamMessage => object(value) &&
  strings(value, ['Id', 'AppendedAt', 'ContentType', 'CapturedAt']) && numbers(value, ['PayloadSize', 'Revision']);
export const streamPath = (name?: string) => `/data/streams${name === undefined ? '' : `/${encodeURIComponent(name)}`}`;
export const streamLink = (name?: string, kind?: 'functions' | 'messages', key?: string) =>
  `#/streams${name === undefined ? '' : `/${encodeURIComponent(name)}`}${kind ? `/${kind}/${encodeURIComponent(key ?? '')}` : ''}`;
export async function streamRequest<T>(path: string, guard: (value: unknown) => value is T, signal: AbortSignal): Promise<T> {
  const response = await fetch(path, { signal });
  if (!response.ok) throw new Error(`HTTP ${response.status}: ${response.status === 404 ? 'Absent or restricted.' :
    response.status === 410 ? 'Outside retention or a previous Stream generation. Return to the beginning.' : 'Stream observation unavailable.'}`);
  const value: unknown = await response.json();
  if (!guard(value)) throw new Error('Invalid Stream response.');
  return value;
}
