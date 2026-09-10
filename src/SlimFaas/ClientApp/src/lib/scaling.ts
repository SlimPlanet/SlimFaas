import type { ScaleBehavior, ScaleConfig, ScaleTrigger } from '../types.ts';

export interface ScalingTriggerDiagnostic {
  Index: number; Metric: string; Source: string | null; Query: string; MetricType: string;
  Threshold: number | null; State: string; Value: number | null; RawTarget: number | null; Simulated: boolean; Detail: string | null;
}
export interface ScalingDecision {
  Function: string; TimestampMs: number; CurrentReplicas: number; ReadyReplicas: number;
  RawTarget: number | null; PolicyTarget: number | null; StabilizedTarget: number | null; Target: number;
  Action: string; Application: string; AcceptedReplicas: number | null;
  Reasons: { Code: string; Message: string }[]; Triggers: ScalingTriggerDiagnostic[];
  Sources: { Name: string; State: string; LastSuccessMs: number | null; IntervalMs: number }[];
  LastHttpActivityMs: number | null; LastScheduleActivityMs: number | null;
  InactivityRemainingSeconds: number; DependenciesReady: boolean; DependencyDemand: boolean;
}
export interface ScalingEvent {
  Id: number; TimestampMs: number; CurrentReplicas: number; Target: number; RawTarget: number | null;
  Action: string; Application: string; Reasons: string[]; Signals: string[];
}
export interface ScalingState {
  Session: string; Status: string; ServerTimeMs: number; Function: string; Decision: ScalingDecision | null;
  Configuration: ScaleConfig | null; ObservedReadyReplicas: number; ObservedRequestedReplicas: number;
  Events: ScalingEvent[]; Truncated: boolean; RefreshIntervalMs: number;
}
export interface ScalingSimulation {
  CapturedAtMs: number; Current: ScalingDecision; Simulated: ScalingDecision; Limitations: string[];
}
export interface TriggerDraft extends ScaleTrigger { FakeValue: string }
export interface SimulationDraft {
  CurrentReplicas: string; ReplicaMax: string; ScaleFromZero: boolean; Behavior: string; Triggers: TriggerDraft[];
}

const defaultBehavior: ScaleBehavior = {
  ScaleUp: { StabilizationWindowSeconds: 0, Policies: [{ Type: 'Percent', Value: 100, PeriodSeconds: 15 }, { Type: 'Pods', Value: 4, PeriodSeconds: 15 }] },
  ScaleDown: { StabilizationWindowSeconds: 300, Policies: [{ Type: 'Percent', Value: 100, PeriodSeconds: 15 }] },
};
export function createSimulationDraft(config: ScaleConfig | null, replicas: number): SimulationDraft {
  return { CurrentReplicas: String(replicas), ReplicaMax: config?.ReplicaMax == null ? '' : String(config.ReplicaMax),
    ScaleFromZero: config?.ScaleFromZero ?? false, Behavior: JSON.stringify(config?.Behavior ?? defaultBehavior, null, 2),
    Triggers: (config?.Triggers ?? []).map(t => ({ ...t, FakeValue: '' })) };
}
export function simulationRequest(functionName: string, draft: SimulationDraft) {
  const integer = (value: string, label: string) => {
    const n = Number(value);
    if (!value.trim() || !Number.isSafeInteger(n) || n < 0 || n > 2147483647) throw new Error(`${label} must be a non-negative integer.`);
    return n;
  };
  const triggers = draft.Triggers.map((t, Index) => {
    if (!t.Query.trim() || t.Query.length > 2048) throw new Error('Queries must contain 1–2048 characters.');
    if (!Number.isFinite(t.Threshold) || t.Threshold <= 0) throw new Error('Thresholds must be positive.');
    const Value = t.FakeValue.trim() === '' ? null : Number(t.FakeValue);
    if (Value !== null && (!Number.isFinite(Value) || Value < 0)) throw new Error('Simulated values must be finite and non-negative.');
    return { Index, Query: t.Query, Source: t.Source || null, MetricType: t.MetricType, Threshold: t.Threshold, Value };
  });
  let Behavior: ScaleBehavior;
  try { Behavior = JSON.parse(draft.Behavior); }
  catch { throw new Error('Policies must contain valid JSON.'); }
  if (!Behavior?.ScaleUp || !Behavior?.ScaleDown) throw new Error('Policies must define ScaleUp and ScaleDown.');
  return { Function: functionName, CurrentReplicas: integer(draft.CurrentReplicas, 'Initial replicas'),
    ReplicaMax: draft.ReplicaMax.trim() === '' ? null : integer(draft.ReplicaMax, 'Replica maximum'),
    ClearReplicaMax: draft.ReplicaMax.trim() === '', ScaleFromZero: draft.ScaleFromZero, Behavior, Triggers: triggers };
}

export function mergeScalingState(previous: ScalingState | null, next: ScalingState): ScalingState {
  if (!next.Session || !Number.isFinite(next.ServerTimeMs) || !Array.isArray(next.Events)) throw new Error('Invalid scaling frame.');
  const sameSession = previous?.Session === next.Session && previous?.Function === next.Function;
  const events = new Map<number, ScalingEvent>();
  for (const event of [...(sameSession ? previous.Events : []), ...next.Events]) {
    if (event.TimestampMs >= next.ServerTimeMs - 15 * 60 * 1000) events.set(event.Id, event);
  }
  const ordered = [...events.values()].sort((a, b) => a.Id - b.Id);
  return { ...next, Events: ordered.slice(-300), Truncated: next.Truncated || (sameSession && previous.Truncated) || ordered.length > 300 };
}

export function scalingLabel(value: string) { return value.replace(/([a-z])([A-Z])/g, '$1 $2'); }
export function scalingValue(value: number | null | undefined): string { return value == null ? '—' : value.toLocaleString(undefined, { maximumFractionDigits: 3 }); }
