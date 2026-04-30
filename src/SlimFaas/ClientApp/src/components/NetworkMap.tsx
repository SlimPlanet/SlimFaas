import React, { useRef, useEffect, useMemo, useCallback } from 'react';
import * as d3 from 'd3';
import type { FunctionStatusDetailed, JobConfigurationStatus, QueueInfo, NetworkActivityEvent, SlimFaasNodeInfo } from '../types';

interface Props {
  functions: FunctionStatusDetailed[];
  jobs: JobConfigurationStatus[];
  queues: QueueInfo[];
  activity: NetworkActivityEvent[];
  functionsWithQueueActivity: Set<string>;
  slimFaasReplicas: number;
  slimFaasNodes: SlimFaasNodeInfo[];
}

interface AnimSegment {
  from: { x: number; y: number };
  to: { x: number; y: number };
}

interface AnimatedMessage {
  id: string;
  segments: AnimSegment[];
  currentSeg: number;
  color: string;
  shape: 'circle' | 'rect';
  startTime: number;
  segDuration: number;
  segmentGapMs: number;
  label?: string;
}

interface ReplicaPosition {
  x: number;
  y: number;
  status: string;
  name: string;
}

const SEG_DURATION = 800;
const SEG_GAP = 120;
const CHAIN_GAP_MIN = 80;
const CHAIN_GAP_MAX = 240;
const MAX_VISUAL_LAG_MS = 2500;
const MAX_ANIMATED_EVENTS_PER_BATCH = 120;
const MAX_IN_FLIGHT_ANIMATIONS = 260;
const OVERLOAD_GAP_MS = 16;
const COUNTER_STALE_MS = 2 * 60 * 1000;
const COUNTER_SWEEP_MS = 1000;
const BUBBLE_R = 34;
const JOB_BUBBLE_R = 32;
const SLIMFAAS_R = 42;
const REPLICA_R = 9;
const JOB_RUN_R = 8;
const SLIM_REPLICA_MIN_RING = SLIMFAAS_R + 34;
const SLIM_REPLICA_ARC_SPACING = 58;
const QUEUE_BOX_W = 60;
const QUEUE_BOX_H = 20;
const CENTER = { x: 0, y: 0 };
const EXTERNAL_ANGLE = -Math.PI * 0.88;
const JOBS_ARC_START = -Math.PI * 0.58;
const JOBS_ARC_END = Math.PI * 0.58;
const SVG_VIEW_H = 680;
const REPLICA_SLOT_COUNT = 16;
const REPLICA_SLOT_ORDER = [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];

function nameColor(name: string): string {
  const colors = ['#4c6ef5', '#7950f2', '#e64980', '#f76707', '#36b37e', '#00b8d9', '#fab005', '#e8590c', '#ae3ec9', '#0ca678', '#2f9e44', '#1098ad', '#f08c00', '#d6336c', '#6741d9'];
  let hash = 0;
  for (let i = 0; i < name.length; i++) hash = name.charCodeAt(i) + ((hash << 5) - hash);
  return colors[Math.abs(hash) % colors.length];
}

function podColor(status: string): string {
  if (status === 'Running') return '#36b37e';
  if (status === 'Starting') return '#fab005';
  if (status === 'Pending') return '#adb5bd';
  return '#ff5630';
}

function jobRunColor(status: string): string {
  if (status === 'Running') return '#36b37e';
  if (status === 'Succeeded') return '#2f9e44';
  if (status === 'Pending') return '#fab005';
  if (status === 'ImagePullBackOff') return '#f76707';
  if (status === 'Failed') return '#ff5630';
  return '#adb5bd';
}

function lightenColor(hex: string, amount: number): string {
  const num = parseInt(hex.replace('#', ''), 16);
  const r = Math.min(255, ((num >> 16) & 0xff) + Math.round(255 * amount));
  const g = Math.min(255, ((num >> 8) & 0xff) + Math.round(255 * amount));
  const b = Math.min(255, (num & 0xff) + Math.round(255 * amount));
  return `rgb(${r},${g},${b})`;
}

function eventKey(evt: NetworkActivityEvent): string {
  return `${evt.Id}|${evt.Type}|${evt.Source}|${evt.Target}|${evt.TimestampMs}|${evt.NodeId}`;
}

function activityCorrelationKey(evt: NetworkActivityEvent): string {
  return evt.CorrelationId || evt.Id;
}

function utcMs(ts: number): number {
  return Number.isFinite(ts) && ts > 0 ? Math.trunc(ts) : Date.now();
}

function radialPositions(n: number): { x: number; y: number }[] {
  if (n === 0) return [];
  if (n === 1) return [{ x: 0, y: 280 }];
  const baseR = 280;
  const perItem = 132;
  const radius = Math.max(baseR, (n * perItem) / (2 * Math.PI));
  return Array.from({ length: n }, (_, i) => {
    const angle = (2 * Math.PI * i) / n - Math.PI / 2;
    return { x: Math.cos(angle) * radius, y: Math.sin(angle) * radius };
  });
}

function jobAndExternalRadius(fnPositions: Record<string, { x: number; y: number }>, jobCount: number): number {
  const functionRadius = Math.max(280, ...Object.values(fnPositions).map(p => Math.hypot(p.x - CENTER.x, p.y - CENTER.y)));
  return Math.max(functionRadius + 190, 420, (Math.max(1, jobCount) * 120) / Math.PI);
}

function replicaSlotAngle(slotIndex: number): number {
  const ringSlot = REPLICA_SLOT_ORDER[slotIndex % REPLICA_SLOT_COUNT] ?? (slotIndex % REPLICA_SLOT_COUNT);
  return (2 * Math.PI * ringSlot) / REPLICA_SLOT_COUNT - Math.PI / 2;
}

function splitLabelLines(label: string, maxCharsPerLine: number): string[] {
  const clean = (label || '').trim();
  if (!clean) return [''];

  const words = clean.split(/([\s_.-]+)/).filter(Boolean);
  const lines: string[] = [];
  let current = '';

  const pushChunked = (text: string) => {
    let rest = text;
    while (rest.length > maxCharsPerLine) {
      lines.push(rest.slice(0, maxCharsPerLine));
      rest = rest.slice(maxCharsPerLine);
    }
    if (rest.length > 0) {
      if (current.length > 0) lines.push(current.trim());
      current = rest;
    }
  };

  for (const part of words) {
    const candidate = `${current}${part}`;
    if (candidate.length <= maxCharsPerLine) {
      current = candidate;
      continue;
    }

    if (current.trim().length > 0) {
      lines.push(current.trim());
      current = '';
    }

    if (part.length > maxCharsPerLine && !/^[\s_.-]+$/.test(part)) {
      pushChunked(part);
    } else {
      current = part;
    }
  }

  if (current.trim().length > 0) {
    lines.push(current.trim());
  }

  return lines.length > 0 ? lines : [clean];
}

function applyMultilineSvgText(
  textSelection: d3.Selection<SVGTextElement, unknown, any, any>,
  lines: string[],
  x: number,
  y: number,
  lineHeight: number,
) {
  textSelection.attr('x', x).attr('y', y).text('');
  const offset = ((lines.length - 1) * lineHeight) / 2;
  lines.forEach((line, index) => {
    textSelection
      .append('tspan')
      .attr('x', x)
      .attr('dy', index === 0 ? -offset : lineHeight)
      .text(line);
  });
}

const NetworkMap: React.FC<Props> = ({ functions, jobs, queues, activity, functionsWithQueueActivity, slimFaasReplicas, slimFaasNodes }) => {
  const svgRef = useRef<SVGSVGElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const animRef = useRef<AnimatedMessage[]>([]);
  const seenEventsRef = useRef<Set<string>>(new Set());
  const frameRef = useRef(0);
  const zoomRef = useRef<d3.ZoomBehavior<SVGSVGElement, unknown> | null>(null);
  const worldGRef = useRef<SVGGElement | null>(null);
  const userHasZoomedRef = useRef(false);
  const initialFitDoneRef = useRef(false);
  const activeReplicaCountersRef = useRef<Record<string, number>>({});
  const activeReplicaCounterTouchedRef = useRef<Record<string, number>>({});
  const activeQueueCountersRef = useRef<Record<string, number>>({});
  const activeQueueCounterTouchedRef = useRef<Record<string, number>>({});
  const activeExternalCounterRef = useRef(0);
  const activeExternalCounterTouchedRef = useRef(0);
  const requestInSenderRef = useRef<Record<string, { senderReplicaKey?: string; isExternal: boolean; touchedAt: number }>>({});
  const requestOutSenderRef = useRef<Record<string, { senderReplicaKey: string; touchedAt: number }>>({});
  const ignoredRequestOutRef = useRef<Set<string>>(new Set());
  const lastPublishByNodeAndTargetRef = useRef<Record<string, number>>({});
  const lastCounterSweepRef = useRef(0);
  const lastScheduledUtcRef = useRef(0);
  const lastScheduledPerfRef = useRef(0);
  const slimReplicaPositionsRef = useRef<Record<string, ReplicaPosition>>({});
  const functionReplicaPositionsRef = useRef<Record<string, Record<string, ReplicaPosition>>>({});
  const functionReplicaSlotsRef = useRef<Record<string, Record<string, number>>>({});
  const jobRunPositionsRef = useRef<Record<string, Record<string, ReplicaPosition>>>({});
  const jobRunSlotsRef = useRef<Record<string, Record<string, number>>>({});

  const fnNamesKey = useMemo(() => functions.map(f => f.Name).sort().join(','), [functions]);
  const jobNamesKey = useMemo(() => jobs.map(j => j.Name).sort().join(','), [jobs]);

  const fnPositions = useMemo(() => {
    const names = fnNamesKey.split(',').filter(Boolean);
    const pos: Record<string, { x: number; y: number }> = {};
    const pts = radialPositions(names.length);
    names.forEach((name, i) => { pos[name] = pts[i]; });
    return pos;
  }, [fnNamesKey]);

  const jobPositions = useMemo(() => {
    const names = jobNamesKey.split(',').filter(Boolean);
    const pos: Record<string, { x: number; y: number }> = {};
    if (names.length === 0) return pos;

    const radius = jobAndExternalRadius(fnPositions, names.length);
    names.forEach((name, i) => {
      const angle = names.length === 1
        ? 0
        : JOBS_ARC_START + ((JOBS_ARC_END - JOBS_ARC_START) * i) / (names.length - 1);
      pos[name] = { x: Math.cos(angle) * radius, y: Math.sin(angle) * radius };
    });
    return pos;
  }, [fnPositions, jobNamesKey]);

  const externalPos = useMemo(() => {
    const baseRadius = jobAndExternalRadius(fnPositions, jobNamesKey.split(',').filter(Boolean).length);
    return {
      x: CENTER.x + Math.cos(EXTERNAL_ANGLE) * baseRadius,
      y: CENTER.y + Math.sin(EXTERNAL_ANGLE) * baseRadius,
    };
  }, [fnPositions, jobNamesKey]);

  const queueFnKey = useMemo(() => {
    const names: string[] = [];
    for (const fn of functions) {
      if (functionsWithQueueActivity.has(fn.Name)) names.push(fn.Name);
    }
    return names.sort().join(',');
  }, [functions, functionsWithQueueActivity]);

  const queuePositions = useMemo(() => {
    const pos: Record<string, { x: number; y: number }> = {};
    const names = queueFnKey.split(',').filter(Boolean);
    for (const name of names) {
      const fp = fnPositions[name];
      if (!fp) continue;
      pos[name] = { x: (CENTER.x + fp.x) / 2, y: (CENTER.y + fp.y) / 2 };
    }
    return pos;
  }, [queueFnKey, fnPositions]);

  const queueMap = useMemo(() => {
    const m: Record<string, number> = {};
    queues.forEach(q => { m[q.Name] = q.Length; });
    return m;
  }, [queues]);

  const zoomToFit = useCallback((animated = true) => {
    const svg = svgRef.current;
    const worldG = worldGRef.current;
    const zoom = zoomRef.current;
    if (!svg || !worldG || !zoom) return;

    const svgW = svg.clientWidth || 800;
    const svgH = svg.clientHeight || SVG_VIEW_H;
    const allPts: { x: number; y: number }[] = [CENTER, externalPos, ...Object.values(slimReplicaPositionsRef.current), ...Object.values(fnPositions), ...Object.values(jobPositions), ...Object.values(queuePositions)];
    if (allPts.length === 0) return;

    const pad = BUBBLE_R + 140;
    let minX = Infinity; let maxX = -Infinity; let minY = Infinity; let maxY = -Infinity;
    for (const p of allPts) {
      minX = Math.min(minX, p.x - pad);
      maxX = Math.max(maxX, p.x + pad);
      minY = Math.min(minY, p.y - pad);
      maxY = Math.max(maxY, p.y + pad);
    }

    const bboxW = maxX - minX || 1;
    const bboxH = maxY - minY || 1;
    const scale = Math.min(svgW / bboxW, svgH / bboxH, 2.5);
    const tx = svgW / 2 - (minX + bboxW / 2) * scale;
    const ty = svgH / 2 - (minY + bboxH / 2) * scale;
    const transform = d3.zoomIdentity.translate(tx, ty).scale(scale);

    if (animated) d3.select(svg).transition().duration(400).call(zoom.transform, transform);
    else d3.select(svg).call(zoom.transform, transform);
  }, [externalPos, fnPositions, jobPositions, queuePositions]);

  const slimNodeList = useMemo(() => {
    if (slimFaasNodes.length > 0) return slimFaasNodes;
    return Array.from({ length: slimFaasReplicas }, (_, i) => ({ Name: `sf-${i}`, Status: 'Running' }));
  }, [slimFaasNodes, slimFaasReplicas]);

  const slimReplicaPositions = useMemo(() => {
    const nodes = slimNodeList;
    const positions: Record<string, ReplicaPosition> = {};
    if (nodes.length === 0) return positions;
    const ring = Math.max(SLIM_REPLICA_MIN_RING, (nodes.length * SLIM_REPLICA_ARC_SPACING) / (2 * Math.PI));
    nodes.forEach((node, i) => {
      const angle = (2 * Math.PI * i) / nodes.length - Math.PI / 2;
      positions[node.Name] = {
        x: CENTER.x + Math.cos(angle) * ring,
        y: CENTER.y + Math.sin(angle) * ring,
        status: node.Status,
        name: node.Name,
      };
    });
    return positions;
  }, [slimNodeList]);

  const functionReplicaPositions = useMemo(() => {
    const result: Record<string, Record<string, ReplicaPosition>> = {};
    const liveFunctionNames = new Set(functions.map(fn => fn.Name));
    for (const functionName of Object.keys(functionReplicaSlotsRef.current)) {
      if (!liveFunctionNames.has(functionName)) delete functionReplicaSlotsRef.current[functionName];
    }

    for (const fn of functions) {
      const pods = fn.Pods ?? [];
      const center = fnPositions[fn.Name];
      if (!center || pods.length === 0) continue;
      const slots = functionReplicaSlotsRef.current[fn.Name] ?? {};
      functionReplicaSlotsRef.current[fn.Name] = slots;
      const livePodNames = new Set(pods.map(pod => pod.Name));
      for (const podName of Object.keys(slots)) {
        if (!livePodNames.has(podName)) delete slots[podName];
      }

      const usedSlots = new Set(Object.values(slots));
      const newPods = pods.filter(pod => slots[pod.Name] === undefined).sort((a, b) => a.Name.localeCompare(b.Name));
      for (const pod of newPods) {
        let slot = 0;
        while (usedSlots.has(slot)) slot += 1;
        slots[pod.Name] = slot;
        usedSlots.add(slot);
      }

      result[fn.Name] = {};
      pods.forEach((pod) => {
        const slot = slots[pod.Name] ?? 0;
        const a = replicaSlotAngle(slot);
        const ring = BUBBLE_R + 18 + Math.floor(slot / REPLICA_SLOT_COUNT) * (REPLICA_R * 2 + 8);
        result[fn.Name][pod.Name] = {
          x: center.x + Math.cos(a) * ring,
          y: center.y + Math.sin(a) * ring,
          status: pod.Status,
          name: pod.Name,
        };
      });
    }
    return result;
  }, [functions, fnPositions]);

  const jobRunPositions = useMemo(() => {
    const result: Record<string, Record<string, ReplicaPosition>> = {};
    const liveJobNames = new Set(jobs.map(job => job.Name));
    for (const jobName of Object.keys(jobRunSlotsRef.current)) {
      if (!liveJobNames.has(jobName)) delete jobRunSlotsRef.current[jobName];
    }

    for (const job of jobs) {
      const runs = job.RunningJobs ?? [];
      const center = jobPositions[job.Name];
      if (!center || runs.length === 0) continue;
      const slots = jobRunSlotsRef.current[job.Name] ?? {};
      jobRunSlotsRef.current[job.Name] = slots;
      const liveRunNames = new Set(runs.map(run => run.Name || run.ElementId));
      for (const runName of Object.keys(slots)) {
        if (!liveRunNames.has(runName)) delete slots[runName];
      }

      const usedSlots = new Set(Object.values(slots));
      const newRuns = runs
        .filter(run => slots[run.Name || run.ElementId] === undefined)
        .sort((a, b) => (a.StartTimestamp || 0) - (b.StartTimestamp || 0) || (a.Name || a.ElementId).localeCompare(b.Name || b.ElementId));
      for (const run of newRuns) {
        let slot = 0;
        while (usedSlots.has(slot)) slot += 1;
        slots[run.Name || run.ElementId] = slot;
        usedSlots.add(slot);
      }

      result[job.Name] = {};
      runs.forEach((run) => {
        const runName = run.Name || run.ElementId;
        const slot = slots[runName] ?? 0;
        const a = replicaSlotAngle(slot);
        const ring = JOB_BUBBLE_R + 17 + Math.floor(slot / REPLICA_SLOT_COUNT) * (JOB_RUN_R * 2 + 8);
        result[job.Name][runName] = {
          x: center.x + Math.cos(a) * ring,
          y: center.y + Math.sin(a) * ring,
          status: run.Status,
          name: runName,
        };
      });
    }
    return result;
  }, [jobs, jobPositions]);

  useEffect(() => {
    slimReplicaPositionsRef.current = slimReplicaPositions;
  }, [slimReplicaPositions]);

  useEffect(() => {
    functionReplicaPositionsRef.current = functionReplicaPositions;
  }, [functionReplicaPositions]);

  useEffect(() => {
    jobRunPositionsRef.current = jobRunPositions;
  }, [jobRunPositions]);

  const ipToPod = useMemo(() => {
    const map: Record<string, { functionName: string; podName: string }> = {};
    for (const fn of functions) {
      for (const pod of fn.Pods ?? []) {
        if (pod.Ip) map[pod.Ip] = { functionName: fn.Name, podName: pod.Name };
      }
    }
    return map;
  }, [functions]);

  const resolvePodLink = useCallback((ip: string | null | undefined) => {
    if (!ip) return null;
    const direct = ipToPod[ip];
    if (direct) return direct;
    const clean = ip.startsWith('::ffff:') ? ip.slice(7) : ip;
    return ipToPod[clean] ?? null;
  }, [ipToPod]);

  const resolvePodLabel = useCallback((ip: string | null | undefined) => {
    const link = resolvePodLink(ip);
    if (link) {
      const shortPod = link.podName.length > 18 ? `...${link.podName.slice(-15)}` : link.podName;
      return `${link.functionName}/${shortPod}`;
    }
    if (!ip) return undefined;
    return ip.startsWith('::ffff:') ? ip.slice(7) : ip;
  }, [resolvePodLink]);

  const getFunctionReplicaPosition = useCallback((functionName: string, podIpOrName: string | null | undefined) => {
    const link = resolvePodLink(podIpOrName);
    const resolvedFn = link?.functionName ?? functionName;
    const podName = link?.podName ?? podIpOrName;
    const replicas = functionReplicaPositions[resolvedFn];
    if (!replicas || Object.keys(replicas).length === 0) return null;
    if (podName && replicas[podName]) return { x: replicas[podName].x, y: replicas[podName].y };
    const firstReplica = Object.values(replicas)[0];
    return firstReplica ? { x: firstReplica.x, y: firstReplica.y } : null;
  }, [functionReplicaPositions, resolvePodLink]);

  const getJobPosition = useCallback((jobName: string, runNameOrElementId: string | null | undefined) => {
    const runs = jobRunPositions[jobName];
    if (runs && Object.keys(runs).length > 0) {
      if (runNameOrElementId && runs[runNameOrElementId]) return { x: runs[runNameOrElementId].x, y: runs[runNameOrElementId].y };
      const firstRun = Object.values(runs)[0];
      if (firstRun) return { x: firstRun.x, y: firstRun.y };
    }

    return jobPositions[jobName] ?? null;
  }, [jobPositions, jobRunPositions]);

  const getActorPosition = useCallback((actorName: string, podIpOrName: string | null | undefined) => {
    const jobPos = getJobPosition(actorName, podIpOrName);
    if (jobPos) return jobPos;
    return getFunctionReplicaPosition(actorName, podIpOrName);
  }, [getFunctionReplicaPosition, getJobPosition]);

  const getSlimPosition = useCallback((nodeId: string | null | undefined) => {
    if (nodeId && slimReplicaPositions[nodeId]) {
      const p = slimReplicaPositions[nodeId];
      return { x: p.x, y: p.y };
    }
    const firstReplica = Object.values(slimReplicaPositions)[0];
    return firstReplica ? { x: firstReplica.x, y: firstReplica.y } : null;
  }, [slimReplicaPositions]);

  const incReplicaCounter = useCallback((key: string) => {
    activeReplicaCounterTouchedRef.current[key] = performance.now();
    activeReplicaCountersRef.current[key] = (activeReplicaCountersRef.current[key] || 0) + 1;
  }, []);

  const decReplicaCounter = useCallback((key: string) => {
    activeReplicaCounterTouchedRef.current[key] = performance.now();
    const next = Math.max(0, (activeReplicaCountersRef.current[key] || 0) - 1);
    activeReplicaCountersRef.current[key] = next;
    if (next === 0) delete activeReplicaCounterTouchedRef.current[key];
  }, []);

  const incQueueCounter = useCallback((key: string) => {
    activeQueueCounterTouchedRef.current[key] = performance.now();
    activeQueueCountersRef.current[key] = (activeQueueCountersRef.current[key] || 0) + 1;
  }, []);

  const decQueueCounter = useCallback((key: string) => {
    activeQueueCounterTouchedRef.current[key] = performance.now();
    const next = Math.max(0, (activeQueueCountersRef.current[key] || 0) - 1);
    activeQueueCountersRef.current[key] = next;
    if (next === 0) delete activeQueueCounterTouchedRef.current[key];
  }, []);

  const incExternalCounter = useCallback(() => {
    activeExternalCounterTouchedRef.current = performance.now();
    activeExternalCounterRef.current += 1;
  }, []);

  const decExternalCounter = useCallback(() => {
    activeExternalCounterTouchedRef.current = performance.now();
    activeExternalCounterRef.current = Math.max(0, activeExternalCounterRef.current - 1);
    if (activeExternalCounterRef.current === 0) {
      activeExternalCounterTouchedRef.current = 0;
    }
  }, []);

  const fnReplicaKey = useCallback((functionName: string, podName: string) => `fn:${functionName}:${podName}`, []);
  const sfReplicaKey = useCallback((nodeName: string) => `sf:${nodeName}`, []);

  const resolveFunctionReplicaCounterKey = useCallback((functionName: string | null | undefined, podIpOrName: string | null | undefined): string | null => {
    if (!functionName) return null;

    const link = resolvePodLink(podIpOrName);
    if (link) {
      return fnReplicaKey(link.functionName, link.podName);
    }

    const replicas = functionReplicaPositions[functionName];
    if (!replicas || Object.keys(replicas).length === 0) return null;
    if (podIpOrName && replicas[podIpOrName]) {
      return fnReplicaKey(functionName, podIpOrName);
    }

    const firstReplica = Object.keys(replicas)[0];
    return firstReplica ? fnReplicaKey(functionName, firstReplica) : null;
  }, [fnReplicaKey, functionReplicaPositions, resolvePodLink]);

  const resolveSenderReplicaCounterKey = useCallback((evt: NetworkActivityEvent): string | null => {
    const sourceActor = (evt.Source || 'slimfaas').toLowerCase();
    if ((sourceActor === 'external' || sourceActor === 'slimfaas') && evt.NodeId) {
      return sfReplicaKey(evt.NodeId);
    }

    if (sourceActor !== 'external' && sourceActor !== 'slimfaas') {
      const functionReplica = resolveFunctionReplicaCounterKey(evt.Source, evt.SourcePod);
      if (functionReplica) return functionReplica;
    }

    const srcLink = resolvePodLink(evt.SourcePod);
    if (srcLink) {
      return fnReplicaKey(srcLink.functionName, srcLink.podName);
    }

    // Async worker emits source=functionName from a SlimFaas replica.
    if (evt.NodeId) {
      return sfReplicaKey(evt.NodeId);
    }

    return null;
  }, [fnReplicaKey, resolveFunctionReplicaCounterKey, resolvePodLink, sfReplicaKey]);


  const queueMessage = useCallback((message: AnimatedMessage) => {
    animRef.current.push(message);
    if (animRef.current.length > MAX_IN_FLIGHT_ANIMATIONS) {
      animRef.current.splice(0, animRef.current.length - MAX_IN_FLIGHT_ANIMATIONS);
    }
  }, []);

  const spawnEvent = useCallback((evt: NetworkActivityEvent, startAt: number, shouldAnimate = true) => {
    const enqueueVisualMessage = (message: AnimatedMessage) => {
      if (shouldAnimate) queueMessage(message);
    };
    const replicaOf = (name: string, pod: string | null | undefined) => getActorPosition(name, pod);
    const srcLabel = resolvePodLabel(evt.SourcePod);
    const tgtLabel = resolvePodLabel(evt.TargetPod);
    const slim = getSlimPosition(evt.NodeId);
    if (!slim) return;

    if (evt.Type === 'enqueue') {
      const queueName = evt.QueueName || evt.Target;
      const queuePos = queuePositions[queueName];
      if (!queuePos) return;

      // Async ingress from unknown source is considered done once persisted in queue.
      const sourceIsKnownInternal = !!resolvePodLink(evt.SourcePod);
      if (!sourceIsKnownInternal) {
        decExternalCounter();
      }

      enqueueVisualMessage({
        id: `${evt.Id}|enqueue`,
        segments: [{ from: slim, to: queuePos }],
        currentSeg: 0,
        color: nameColor(evt.Target),
        shape: 'rect',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: SEG_GAP,
        label: srcLabel || tgtLabel,
      });
      return;
    }

    if (evt.Type === 'dequeue') {
      const queueName = evt.QueueName || evt.Target;
      const queuePos = queuePositions[queueName];
      const targetPos = replicaOf(evt.Target, evt.TargetPod);
      if (!queuePos || !targetPos) return;
      incQueueCounter(`queue:${queueName}`);
      enqueueVisualMessage({
        id: `${evt.Id}|dequeue`,
        segments: [{ from: queuePos, to: targetPos }],
        currentSeg: 0,
        color: nameColor(evt.Target),
        shape: 'rect',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: 0,
        label: tgtLabel,
      });
      return;
    }

    if (evt.Type === 'request_in') {
      const link = resolvePodLink(evt.SourcePod);
      const sourceName = (evt.Source || '').trim();
      const sourceActor = sourceName.toLowerCase();
      const sourceIsFunction = sourceActor.length > 0 && sourceActor !== 'external' && sourceActor !== 'slimfaas';

      const functionStartPos = sourceIsFunction
        ? replicaOf(sourceName, evt.SourcePod)
        : (link ? replicaOf(link.functionName, link.podName) : null);
      const senderReplicaKey = sourceIsFunction
        ? resolveFunctionReplicaCounterKey(sourceName, evt.SourcePod)
        : (link ? fnReplicaKey(link.functionName, link.podName) : null);

      if (senderReplicaKey) {
        incReplicaCounter(senderReplicaKey);
      } else {
        incExternalCounter();
      }

      requestInSenderRef.current[activityCorrelationKey(evt)] = {
        senderReplicaKey: senderReplicaKey ?? undefined,
        isExternal: !senderReplicaKey,
        touchedAt: performance.now(),
      };

      const startPos = functionStartPos ?? externalPos;
      if (!startPos) return;
      const sourceColor = link?.functionName ? nameColor(link.functionName) : '#6b778c';
      enqueueVisualMessage({
        id: `${evt.Id}|request_in`,
        segments: [{ from: startPos, to: slim }],
        currentSeg: 0,
        color: sourceColor,
        shape: 'circle',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: 0,
        label: srcLabel,
      });
      return;
    }

    if (evt.Type === 'request_out') {
      const isAsyncQueueHop = evt.Source === evt.Target && !evt.SourcePod;
      if (isAsyncQueueHop) {
        ignoredRequestOutRef.current.add(activityCorrelationKey(evt));
        return;
      }

      const publishKey = `${evt.NodeId}|${evt.Target}`;
      const lastPublishTs = lastPublishByNodeAndTargetRef.current[publishKey] || 0;
      const isTransportDuplicateOfPublish =
        (evt.Source || '').toLowerCase() === 'slimfaas'
        && !evt.SourcePod
        && !evt.TargetPod
        && !evt.QueueName
        && lastPublishTs > 0
        && Math.abs(utcMs(evt.TimestampMs) - lastPublishTs) <= 3000;

      if (isTransportDuplicateOfPublish) {
        ignoredRequestOutRef.current.add(activityCorrelationKey(evt));
        return;
      }

      const targetPos = replicaOf(evt.Target, evt.TargetPod);
      if (!targetPos) {
        ignoredRequestOutRef.current.add(activityCorrelationKey(evt));
        return;
      }
      const sourceActor = evt.Source || 'slimfaas';
      const senderReplicaKey = resolveSenderReplicaCounterKey(evt);
      if (senderReplicaKey) {
        incReplicaCounter(senderReplicaKey);
        requestOutSenderRef.current[activityCorrelationKey(evt)] = {
          senderReplicaKey,
          touchedAt: performance.now(),
        };
      }

      const sourceReplicaPos = sourceActor === 'external'
        ? null
        : sourceActor === 'slimfaas'
          ? slim
          : replicaOf(sourceActor, evt.SourcePod);

      const sourcePos = sourceActor === 'external'
        ? externalPos
        : (sourceReplicaPos ?? slim);
      enqueueVisualMessage({
        id: `${evt.Id}|request_out`,
        segments: [{ from: sourcePos, to: targetPos }],
        currentSeg: 0,
        color: nameColor(evt.Target),
        shape: 'circle',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: 0,
        label: tgtLabel || srcLabel,
      });
      return;
    }

    if (evt.Type === 'request_waiting' || evt.Type === 'request_started') {
      return;
    }

    if (evt.Type === 'request_end') {
      if (evt.QueueName) {
        decQueueCounter(`queue:${evt.QueueName}`);
        return;
      }

      const correlationKey = activityCorrelationKey(evt);
      const trackedSender = requestInSenderRef.current[correlationKey];
      delete requestInSenderRef.current[correlationKey];

      if (trackedSender) {
        if (trackedSender.isExternal) {
          decExternalCounter();
        }

        if (trackedSender.senderReplicaKey) {
          decReplicaCounter(trackedSender.senderReplicaKey);
        }

        return;
      }

      const trackedRequestOut = requestOutSenderRef.current[correlationKey];
      delete requestOutSenderRef.current[correlationKey];

      if (trackedRequestOut) {
        decReplicaCounter(trackedRequestOut.senderReplicaKey);
        return;
      }

      if (ignoredRequestOutRef.current.delete(correlationKey)) {
        return;
      }

      const sourceActor = (evt.Source || '').toLowerCase();
      const targetActor = (evt.Target || '').toLowerCase();
      if (sourceActor === 'external' && targetActor === 'slimfaas') {
        return;
      }

      const fallbackSenderReplicaKey = resolveSenderReplicaCounterKey(evt);
      if (fallbackSenderReplicaKey) {
        decReplicaCounter(fallbackSenderReplicaKey);
      }
      return;
    }

    if (evt.Type === 'event_publish') {
      const targetPos = replicaOf(evt.Target, evt.TargetPod);
      if (!targetPos) return;
      const publishKey = `${evt.NodeId}|${evt.Target}`;
      lastPublishByNodeAndTargetRef.current[publishKey] = utcMs(evt.TimestampMs);
      enqueueVisualMessage({
        id: `${evt.Id}|event_publish`,
        segments: [{ from: slim, to: targetPos }],
        currentSeg: 0,
        color: '#fab005',
        shape: 'circle',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: 0,
        label: tgtLabel,
      });
      return;
    }

    if (evt.Type === 'response') {
      const fromFn = evt.Source !== 'slimfaas' ? evt.Source : evt.Target;
      const fromPos = replicaOf(fromFn, evt.SourcePod);
      if (!fromPos) return;
      const toPos = evt.Target === 'external' ? externalPos : slim;
      enqueueVisualMessage({
        id: `${evt.Id}|response`,
        segments: [{ from: fromPos, to: toPos }],
        currentSeg: 0,
        color: fromFn ? nameColor(fromFn) : '#6b778c',
        shape: 'circle',
        startTime: startAt,
        segDuration: SEG_DURATION,
        segmentGapMs: 0,
        label: srcLabel,
      });
    }
  }, [decExternalCounter, decQueueCounter, decReplicaCounter, externalPos, fnReplicaKey, getActorPosition, getSlimPosition, incExternalCounter, incQueueCounter, incReplicaCounter, queueMessage, queuePositions, resolveFunctionReplicaCounterKey, resolvePodLabel, resolvePodLink, resolveSenderReplicaCounterKey]);

  useEffect(() => {
    const fresh: NetworkActivityEvent[] = [];
    for (const evt of activity) {
      const key = eventKey(evt);
      if (seenEventsRef.current.has(key)) continue;
      seenEventsRef.current.add(key);
      fresh.push(evt);
    }

    if (fresh.length === 0) return;

    fresh.sort((a, b) => {
      const da = utcMs(a.TimestampMs);
      const db = utcMs(b.TimestampMs);
      if (da !== db) return da - db;
      return a.Id.localeCompare(b.Id);
    });

    const now = performance.now();
    const scheduleLimit = now + MAX_VISUAL_LAG_MS;
    let cursorUtc = lastScheduledUtcRef.current || utcMs(fresh[0].TimestampMs);
    let cursorPerf = Math.min(Math.max(lastScheduledPerfRef.current, now), scheduleLimit);
    const animationStride = Math.max(1, Math.ceil(fresh.length / MAX_ANIMATED_EVENTS_PER_BATCH));
    let animatedCount = 0;

    fresh.forEach((evt, index) => {
      const evtUtc = utcMs(evt.TimestampMs);
      const rawGap = Math.max(CHAIN_GAP_MIN, Math.min(CHAIN_GAP_MAX, evtUtc - cursorUtc));
      const backlogMs = Math.max(0, cursorPerf - now);
      const gap = backlogMs > MAX_VISUAL_LAG_MS / 2 ? Math.min(rawGap, OVERLOAD_GAP_MS) : rawGap;
      cursorPerf = Math.min(cursorPerf + gap, scheduleLimit);
      cursorUtc = Math.max(cursorUtc, evtUtc);
      const shouldAnimate = fresh.length <= MAX_ANIMATED_EVENTS_PER_BATCH
        || (index % animationStride === 0 && animatedCount < MAX_ANIMATED_EVENTS_PER_BATCH);
      if (shouldAnimate) animatedCount += 1;
      spawnEvent(evt, cursorPerf, shouldAnimate);
    });

    lastScheduledUtcRef.current = cursorUtc;
    lastScheduledPerfRef.current = Math.min(cursorPerf, performance.now() + MAX_VISUAL_LAG_MS);

    if (seenEventsRef.current.size > 10000) {
      seenEventsRef.current = new Set(Array.from(seenEventsRef.current).slice(-8000));
    }
  }, [activity, spawnEvent]);

  const animate = useCallback(() => {
    const wg = worldGRef.current;
    if (!wg) {
      frameRef.current = requestAnimationFrame(animate);
      return;
    }

    const now = performance.now();

    // Safety net: if an end event is lost under burst traffic, clear stale in-flight counters.
    if (now - lastCounterSweepRef.current >= COUNTER_SWEEP_MS) {
      lastCounterSweepRef.current = now;
      for (const [key, count] of Object.entries(activeReplicaCountersRef.current)) {
        if (count <= 0) continue;
        const touched = activeReplicaCounterTouchedRef.current[key] ?? now;
        if (now - touched >= COUNTER_STALE_MS) {
          activeReplicaCountersRef.current[key] = 0;
          delete activeReplicaCounterTouchedRef.current[key];
        }
      }
      for (const [key, count] of Object.entries(activeQueueCountersRef.current)) {
        if (count <= 0) continue;
        const touched = activeQueueCounterTouchedRef.current[key] ?? now;
        if (now - touched >= COUNTER_STALE_MS) {
          activeQueueCountersRef.current[key] = 0;
          delete activeQueueCounterTouchedRef.current[key];
        }
      }
      if (activeExternalCounterRef.current > 0) {
        const touched = activeExternalCounterTouchedRef.current || now;
        if (now - touched >= COUNTER_STALE_MS) {
          activeExternalCounterRef.current = 0;
          activeExternalCounterTouchedRef.current = 0;
        }
      }
      for (const [key, tracked] of Object.entries(requestInSenderRef.current)) {
        if (now - tracked.touchedAt >= COUNTER_STALE_MS) {
          delete requestInSenderRef.current[key];
        }
      }
      for (const [key, tracked] of Object.entries(requestOutSenderRef.current)) {
        if (now - tracked.touchedAt >= COUNTER_STALE_MS) {
          delete requestOutSenderRef.current[key];
        }
      }
      if (ignoredRequestOutRef.current.size > 10000) {
        ignoredRequestOutRef.current = new Set(Array.from(ignoredRequestOutRef.current).slice(-8000));
      }
    }

    const alive: AnimatedMessage[] = [];
    for (const m of animRef.current) {
      if (now < m.startTime) {
        alive.push(m);
        continue;
      }

      const t = (now - m.startTime) / m.segDuration;
      if (t >= 1) {
        if (m.currentSeg < m.segments.length - 1) {
          m.currentSeg += 1;
          m.startTime = now + m.segmentGapMs;
          alive.push(m);
          }
      } else {
        alive.push(m);
      }
    }
    animRef.current = alive;

    interface MsgRender {
      id: string;
      cx: number;
      cy: number;
      color: string;
      shape: 'circle' | 'rect';
      opacity: number;
      label?: string;
    }

    const msgData: MsgRender[] = [];
    for (const m of animRef.current) {
      if (now < m.startTime) continue;
      const seg = m.segments[m.currentSeg];
      const t = Math.min(1, (now - m.startTime) / m.segDuration);
      const e = d3.easeCubicInOut(t);
      const cx = seg.from.x + (seg.to.x - seg.from.x) * e;
      const cy = seg.from.y + (seg.to.y - seg.from.y) * e;
      const isLast = m.currentSeg === m.segments.length - 1;
      const opacity = (t > 0.9 && isLast) ? Math.max(0.15, 1 - (t - 0.9) / 0.1) : 0.92;
      msgData.push({ id: m.id, cx, cy, color: m.color, shape: m.shape, opacity, label: m.label });
    }

    const g = d3.select(wg).select<SVGGElement>('.nw-messages');
    const counterG = d3.select(wg).select<SVGGElement>('.nw-counters');

    const rectData = msgData.filter(m => m.shape === 'rect');
    const rs = g.selectAll<SVGRectElement, MsgRender>('rect.msg-rect').data(rectData, d => d.id);
    rs.enter().append('rect').attr('class', 'msg-rect').attr('width', 8).attr('height', 6).attr('rx', 1.5)
      .merge(rs)
      .attr('fill', d => d.color)
      .attr('x', d => d.cx - 4)
      .attr('y', d => d.cy - 3)
      .attr('opacity', d => d.opacity);
    rs.exit().remove();

    const circData = msgData.filter(m => m.shape === 'circle');
    const cs = g.selectAll<SVGCircleElement, MsgRender>('circle.msg-circle').data(circData, d => d.id);
    cs.enter().append('circle').attr('class', 'msg-circle').attr('r', 5)
      .merge(cs)
      .attr('fill', d => lightenColor(d.color, 0))
      .attr('cx', d => d.cx)
      .attr('cy', d => d.cy)
      .attr('opacity', d => d.opacity);
    cs.exit().remove();

    const labelData = msgData.filter(m => !!m.label);
    const mls = g.selectAll<SVGTextElement, MsgRender>('text.msg-label').data(labelData, d => d.id);
    mls.enter().append('text').attr('class', 'msg-label').attr('font-size', 7).attr('fill', '#495057').attr('font-weight', 600).attr('pointer-events', 'none')
      .merge(mls)
      .attr('x', d => d.cx + 8)
      .attr('y', d => d.cy + 2)
      .attr('opacity', d => d.opacity * 0.85)
      .text(d => d.label ?? '');
    mls.exit().remove();

    const replicaCounters = activeReplicaCountersRef.current;
    const queueCounters = activeQueueCountersRef.current;
    const externalCounter = activeExternalCounterRef.current;
    const liveSlimReplicaPositions = slimReplicaPositionsRef.current;
    const liveFunctionReplicaPositions = functionReplicaPositionsRef.current;
    const liveJobRunPositions = jobRunPositionsRef.current;
    type CDatum = { key: string; x: number; y: number; count: number };
    const cData: CDatum[] = [];


    const slimEntries = Object.entries(liveSlimReplicaPositions).sort((a, b) => a[0].localeCompare(b[0]));
    slimEntries.forEach(([nodeName, rp], idx) => {
      const key = sfReplicaKey(nodeName);
      const c = replicaCounters[key] || 0;
      if (c > 0) {
        const dx = 6 + (idx % 2) * 4;
        const dy = -6 - (idx % 3) * 3;
        cData.push({ key, x: rp.x + REPLICA_R + dx, y: rp.y - REPLICA_R + dy, count: c });
      }
    });

    for (const [fnName, pods] of Object.entries(liveFunctionReplicaPositions)) {
      const podEntries = Object.entries(pods).sort((a, b) => a[0].localeCompare(b[0]));
      podEntries.forEach(([podName, rp], idx) => {
        const key = fnReplicaKey(fnName, podName);
        const c = replicaCounters[key] || 0;
        if (c > 0) {
          const dx = 6 + (idx % 2) * 4;
          const dy = -6 - (idx % 3) * 3;
          cData.push({ key, x: rp.x + REPLICA_R + dx, y: rp.y - REPLICA_R + dy, count: c });
        }
      });
    }

    for (const [jobName, runs] of Object.entries(liveJobRunPositions)) {
      const runEntries = Object.entries(runs).sort((a, b) => a[0].localeCompare(b[0]));
      runEntries.forEach(([runName, rp], idx) => {
        const key = fnReplicaKey(`job:${jobName}`, runName);
        const c = replicaCounters[key] || 0;
        if (c > 0) {
          const dx = 6 + (idx % 2) * 4;
          const dy = -6 - (idx % 3) * 3;
          cData.push({ key, x: rp.x + JOB_RUN_R + dx, y: rp.y - JOB_RUN_R + dy, count: c });
        }
      });
    }

    for (const [queueName, qp] of Object.entries(queuePositions)) {
      const key = `queue:${queueName}`;
      const c = queueCounters[key] || 0;
      if (c > 0) {
        cData.push({ key, x: qp.x + QUEUE_BOX_W / 2 + 6, y: qp.y - QUEUE_BOX_H / 2, count: c });
      }
    }

    if (externalCounter > 0) {
      cData.push({
        key: 'external',
        x: externalPos.x + BUBBLE_R + 6,
        y: externalPos.y - BUBBLE_R + 2,
        count: externalCounter,
      });
    }

    const cSel = counterG.selectAll<SVGGElement, CDatum>('g.counter-badge').data(cData, d => d.key);
    const cEnter = cSel.enter().append('g').attr('class', 'counter-badge');
    cEnter.append('rect').attr('rx', 7).attr('ry', 7).attr('height', 14).attr('fill', '#f08c00').attr('stroke', '#fff').attr('stroke-width', 1);
    cEnter.append('text').attr('text-anchor', 'middle').attr('font-size', 8).attr('font-weight', 'bold').attr('fill', '#fff').attr('dy', 10.5);
    const cMerge = cEnter.merge(cSel);
    cMerge.attr('transform', d => `translate(${d.x},${d.y})`);
    cMerge.select('text').text(d => `${d.count}`);
    cMerge.select('rect')
      .attr('width', d => Math.max(16, `${d.count}`.length * 7 + 8))
      .attr('x', d => -Math.max(16, `${d.count}`.length * 7 + 8) / 2);
    cSel.exit().remove();

    frameRef.current = requestAnimationFrame(animate);
  }, [externalPos, fnReplicaKey, queuePositions, sfReplicaKey]);

  const structuralKey = `${fnNamesKey}|${jobNamesKey}|${queueFnKey}`;

  useEffect(() => {
    const svg = svgRef.current;
    if (!svg) return;

    const sel = d3.select(svg);
    sel.selectAll('*').remove();

    userHasZoomedRef.current = false;
    initialFitDoneRef.current = false;
    activeReplicaCountersRef.current = {};
    activeReplicaCounterTouchedRef.current = {};
    activeQueueCountersRef.current = {};
    activeQueueCounterTouchedRef.current = {};
    activeExternalCounterRef.current = 0;
    activeExternalCounterTouchedRef.current = 0;
    requestInSenderRef.current = {};
    requestOutSenderRef.current = {};
    ignoredRequestOutRef.current = new Set();
    lastPublishByNodeAndTargetRef.current = {};
    lastCounterSweepRef.current = 0;

    const defs = sel.append('defs');
    const ds = defs.append('filter').attr('id', 'shadow').attr('x', '-20%').attr('y', '-20%').attr('width', '140%').attr('height', '140%');
    ds.append('feDropShadow').attr('dx', 0).attr('dy', 2).attr('stdDeviation', 3).attr('flood-opacity', 0.15);

    sel.append('rect').attr('class', 'nw-bg').attr('width', '100%').attr('height', '100%').attr('fill', '#f8f9fa');
    const world = sel.append('g').attr('class', 'nw-world');
    worldGRef.current = world.node()!;

    const zoom = d3.zoom<SVGSVGElement, unknown>()
      .scaleExtent([0.15, 5])
      .on('zoom', (event) => {
        world.attr('transform', event.transform);
        if (event.sourceEvent) userHasZoomedRef.current = true;
      });
    zoomRef.current = zoom;
    sel.call(zoom);
    sel.on('dblclick.zoom', null);
    sel.on('dblclick', () => { userHasZoomedRef.current = false; zoomToFit(true); });

    world.append('circle')
      .attr('cx', externalPos.x)
      .attr('cy', externalPos.y)
      .attr('r', BUBBLE_R)
      .attr('fill', '#adb5bd')
      .attr('stroke', '#adb5bd')
      .attr('stroke-width', 2.5)
      .attr('opacity', 0.85)
      .attr('filter', 'url(#shadow)');
    world.append('text').attr('x', externalPos.x).attr('y', externalPos.y + 4)
      .attr('text-anchor', 'middle').attr('fill', '#fff').attr('font-weight', 'bold').attr('font-size', 10)
      .attr('paint-order', 'stroke')
      .attr('stroke', 'rgba(0,0,0,0.8)')
      .attr('stroke-width', 1.3)
      .attr('stroke-linejoin', 'round')
      .text('External');

    world.append('circle')
      .attr('cx', CENTER.x)
      .attr('cy', CENTER.y)
      .attr('r', SLIMFAAS_R)
      .attr('fill', '#0000ff')
      .attr('filter', 'url(#shadow)');
    world.append('text').attr('x', CENTER.x).attr('y', CENTER.y - 2)
      .attr('text-anchor', 'middle').attr('fill', '#fff').attr('font-weight', 'bold').attr('font-size', 14)
      .attr('paint-order', 'stroke')
      .attr('stroke', 'rgba(0,0,0,0.8)')
      .attr('stroke-width', 1.3)
      .attr('stroke-linejoin', 'round')
      .text('SlimFaas');
    world.append('g').attr('class', 'sf-replicas');

    const fnNames = fnNamesKey.split(',').filter(Boolean);
    const queueFnNames = new Set(queueFnKey.split(',').filter(Boolean));

    fnNames.forEach((fnName) => {
      if (!queueFnNames.has(fnName)) return;
      const qCenter = queuePositions[fnName];
      if (!qCenter) return;
      const color = nameColor(fnName);
      const qCX = qCenter.x;
      const qCY = qCenter.y;
      const qLeft = qCX - QUEUE_BOX_W / 2;

      world.append('rect').attr('class', `q-box-${fnName}`).attr('x', qLeft).attr('y', qCY - QUEUE_BOX_H / 2)
        .attr('width', QUEUE_BOX_W).attr('height', QUEUE_BOX_H).attr('rx', 4).attr('ry', 4)
        .attr('fill', '#fff').attr('stroke', color).attr('stroke-width', 1.5).attr('filter', 'url(#shadow)');
      world.append('g').attr('class', `q-fill-${fnName}`);
      const queueLabel = world.append('text').attr('class', `q-name-${fnName}`).attr('x', qCX).attr('y', qCY - QUEUE_BOX_H / 2 - 4)
        .attr('text-anchor', 'middle').attr('font-size', 8.5).attr('fill', '#6b778c').attr('font-weight', '700');
      applyMultilineSvgText(queueLabel, splitLabelLines(fnName, 12), qCX, qCY - QUEUE_BOX_H / 2 - 6, 9);
      world.append('text').attr('class', `q-count-${fnName}`).attr('x', qCX).attr('y', qCY + QUEUE_BOX_H / 2 + 12)
        .attr('text-anchor', 'middle').attr('font-size', 9).attr('font-weight', 'bold');
    });

    fnNames.forEach((fnName) => {
      const pos = fnPositions[fnName];
      if (!pos) return;
      const color = nameColor(fnName);
      const escaped = CSS.escape(fnName);
      world.append('g').attr('class', `fn-group-${escaped}`);
      world.append('circle').attr('class', `fn-bubble-${escaped}`)
        .attr('cx', pos.x).attr('cy', pos.y).attr('r', BUBBLE_R)
        .attr('fill', color).attr('stroke', color).attr('stroke-width', 2.5)
        .attr('opacity', 0.85).attr('filter', 'url(#shadow)');
      world.append('text').attr('class', `fn-name-${escaped}`)
        .attr('x', pos.x).attr('y', pos.y - 5)
        .attr('text-anchor', 'middle').attr('font-size', 9).attr('font-weight', 'bold').attr('fill', '#fff')
        .attr('paint-order', 'stroke')
        .attr('stroke', 'rgba(0,0,0,0.85)')
        .attr('stroke-width', 1.5)
        .attr('stroke-linejoin', 'round');
      world.append('text').attr('class', `fn-count-${escaped}`)
        .attr('x', pos.x).attr('y', pos.y + 8)
        .attr('text-anchor', 'middle').attr('font-size', 9);
      world.append('g').attr('class', `fn-pods-${escaped}`);
    });

    const jobNames = jobNamesKey.split(',').filter(Boolean);
    jobNames.forEach((jobName) => {
      const pos = jobPositions[jobName];
      if (!pos) return;
      const color = nameColor(`job:${jobName}`);
      const escaped = CSS.escape(jobName);
      world.append('g').attr('class', `job-group-${escaped}`);
      world.append('circle').attr('class', `job-bubble-${escaped}`)
        .attr('cx', pos.x)
        .attr('cy', pos.y)
        .attr('r', JOB_BUBBLE_R)
        .attr('fill', color)
        .attr('stroke', '#fff')
        .attr('stroke-width', 2.5)
        .attr('opacity', 0.88)
        .attr('filter', 'url(#shadow)');
      world.append('text').attr('class', `job-kind-${escaped}`)
        .attr('x', pos.x).attr('y', pos.y - 14)
        .attr('text-anchor', 'middle').attr('font-size', 10).attr('font-weight', 900).attr('fill', 'rgba(255,255,255,0.95)')
        .attr('letter-spacing', 0.8)
        .attr('paint-order', 'stroke')
        .attr('stroke', 'rgba(0,0,0,0.72)')
        .attr('stroke-width', 1.2)
        .attr('stroke-linejoin', 'round')
        .text('JOB');
      world.append('text').attr('class', `job-name-${escaped}`)
        .attr('x', pos.x).attr('y', pos.y)
        .attr('text-anchor', 'middle').attr('font-size', 8).attr('font-weight', 'bold').attr('fill', '#fff')
        .attr('paint-order', 'stroke')
        .attr('stroke', 'rgba(0,0,0,0.72)')
        .attr('stroke-width', 1.2)
        .attr('stroke-linejoin', 'round');
      world.append('text').attr('class', `job-count-${escaped}`)
        .attr('x', pos.x).attr('y', pos.y + 20)
        .attr('text-anchor', 'middle').attr('font-size', 11).attr('font-weight', 900);
      world.append('g').attr('class', `job-runs-${escaped}`);
    });

    world.append('g').attr('class', 'nw-messages');
    world.append('g').attr('class', 'nw-counters');

    frameRef.current = requestAnimationFrame(animate);
    const fitTimer = setTimeout(() => {
      if (!userHasZoomedRef.current) {
        zoomToFit(false);
        initialFitDoneRef.current = true;
      }
    }, 80);

    return () => {
      cancelAnimationFrame(frameRef.current);
      clearTimeout(fitTimer);
    };
  }, [animate, externalPos, fnNamesKey, fnPositions, jobNamesKey, jobPositions, queueFnKey, queuePositions, structuralKey, zoomToFit]);

  useEffect(() => {
    const wg = worldGRef.current;
    if (!wg) return;
    const world = d3.select(wg);

    functions.forEach((fn) => {
      const pos = fnPositions[fn.Name];
      if (!pos) return;
      const color = nameColor(fn.Name);
      const isDown = (fn.NumberReady ?? 0) === 0;
      const pods = fn.Pods ?? [];
      const r = BUBBLE_R + Math.min(pods.length * 3, 14);
      const escaped = CSS.escape(fn.Name);

      world.select(`.fn-bubble-${escaped}`).attr('r', r).attr('fill', isDown ? '#f8f9fa' : color).attr('opacity', isDown ? 0.5 : 0.85);
      const fnNameSelection = world.select<SVGTextElement>(`.fn-name-${escaped}`)
        .attr('fill', isDown ? '#6b778c' : '#fff')
        .attr('stroke', isDown ? 'rgba(255,255,255,0)' : 'rgba(0,0,0,0.85)')
        .attr('stroke-width', isDown ? 0 : 1.5);
      applyMultilineSvgText(fnNameSelection, splitLabelLines(fn.Name, 12), pos.x, pos.y - 7, 10);
      world.select(`.fn-count-${escaped}`).attr('fill', isDown ? '#adb5bd' : 'rgba(255,255,255,0.8)').text(`${fn.NumberReady}/${fn.NumberRequested}`);

      const podGroup = world.select<SVGGElement>(`.fn-pods-${escaped}`);
      podGroup.selectAll('*').remove();
      if (pods.length > 0) {
        const replicas = functionReplicaPositions[fn.Name] || {};
        for (const pod of pods) {
          const rp = replicas[pod.Name];
          if (!rp) continue;
          const podG = podGroup.append('g').style('cursor', 'pointer');
          podG.append('circle')
            .attr('cx', rp.x)
            .attr('cy', rp.y)
            .attr('r', REPLICA_R)
            .attr('fill', podColor(pod.Status))
            .attr('stroke', '#fff')
            .attr('stroke-width', 1.5);
          podG.append('text')
            .attr('x', rp.x)
            .attr('y', rp.y + REPLICA_R + 8)
            .attr('text-anchor', 'middle')
            .attr('font-size', 7)
            .attr('fill', '#212529')
            .attr('font-weight', 700)
            .attr('paint-order', 'stroke')
            .attr('stroke', 'rgba(255,255,255,0.9)')
            .attr('stroke-width', 1.8)
            .attr('stroke-linejoin', 'round')
            .text(pod.Name);
          podG.append('title').text(`${pod.Name}\nIP: ${pod.Ip || 'N/A'}\nStatus: ${pod.Status}\nReady: ${pod.Ready}`);
        }
      }

      const qLen = queueMap[fn.Name] ?? 0;
      const qCenter = queuePositions[fn.Name];
      if (qCenter) {
        const qCX = qCenter.x;
        const qCY = qCenter.y;
        const qLeft = qCX - QUEUE_BOX_W / 2;
        world.select(`.q-count-${CSS.escape(fn.Name)}`).attr('fill', qLen > 0 ? color : '#adb5bd').text(`in ${qLen}`);

        const fillGroup = world.select<SVGGElement>(`.q-fill-${CSS.escape(fn.Name)}`);
        fillGroup.selectAll('*').remove();
        if (qLen > 0) {
          const fillRatio = Math.min(1, qLen / 20);
          fillGroup.append('rect').attr('x', qLeft + 1).attr('y', qCY - QUEUE_BOX_H / 2 + 1)
            .attr('width', Math.max(0, (QUEUE_BOX_W - 2) * fillRatio)).attr('height', QUEUE_BOX_H - 2)
            .attr('rx', 3).attr('fill', lightenColor(color, 0.3)).attr('opacity', 0.6);
        }
      }
    });

    jobs.forEach((job) => {
      const pos = jobPositions[job.Name];
      if (!pos) return;
      const escaped = CSS.escape(job.Name);
      const color = nameColor(`job:${job.Name}`);
      const runs = job.RunningJobs ?? [];
      const r = JOB_BUBBLE_R + Math.min(runs.length * 3, 14);

      world.select(`.job-bubble-${escaped}`)
        .attr('cx', pos.x)
        .attr('cy', pos.y)
        .attr('r', r)
        .attr('fill', runs.length > 0 ? color : '#fff7e6')
        .attr('stroke', color)
        .attr('opacity', runs.length > 0 ? 0.9 : 0.72);

      world.select(`.job-kind-${escaped}`)
        .attr('x', pos.x)
        .attr('y', pos.y - Math.min(14, r * 0.45))
        .attr('fill', runs.length > 0 ? 'rgba(255,255,255,0.92)' : '#f08c00')
        .attr('stroke', runs.length > 0 ? 'rgba(0,0,0,0.72)' : 'rgba(255,255,255,0.85)')
        .text('JOB');

      const jobNameSelection = world.select<SVGTextElement>(`.job-name-${escaped}`)
        .attr('fill', '#7a4f01')
        .attr('stroke', runs.length > 0 ? 'rgba(0,0,0,0.72)' : 'rgba(255,255,255,0.9)')
        .attr('stroke-width', 1.2);
      applyMultilineSvgText(jobNameSelection, splitLabelLines(job.Name, 10).slice(0, 2), pos.x, pos.y + 1, 8);

      world.select(`.job-count-${escaped}`)
        .attr('x', pos.x)
        .attr('y', pos.y + Math.min(20, r * 0.62))
        .attr('fill', runs.length > 0 ? 'rgba(255,255,255,0.95)' : '#f08c00')
        .attr('paint-order', 'stroke')
        .attr('stroke', runs.length > 0 ? 'rgba(0,0,0,0.72)' : 'rgba(255,255,255,0.85)')
        .attr('stroke-width', 1.2)
        .attr('stroke-linejoin', 'round')
        .text(`${runs.length}`);

      const runGroup = world.select<SVGGElement>(`.job-runs-${escaped}`);
      runGroup.selectAll('*').remove();
      if (runs.length > 0) {
        const replicas = jobRunPositions[job.Name] || {};
        for (const run of runs) {
          const runName = run.Name || run.ElementId;
          const rp = replicas[runName];
          if (!rp) continue;
          const runG = runGroup.append('g').style('cursor', 'pointer');
          runG.append('circle')
            .attr('cx', rp.x)
            .attr('cy', rp.y)
            .attr('r', JOB_RUN_R)
            .attr('fill', jobRunColor(run.Status))
            .attr('stroke', '#fff')
            .attr('stroke-width', 1.5);
          runG.append('text')
            .attr('x', rp.x)
            .attr('y', rp.y + JOB_RUN_R + 8)
            .attr('text-anchor', 'middle')
            .attr('font-size', 7)
            .attr('fill', '#212529')
            .attr('font-weight', 700)
            .attr('paint-order', 'stroke')
            .attr('stroke', 'rgba(255,255,255,0.9)')
            .attr('stroke-width', 1.8)
            .attr('stroke-linejoin', 'round')
            .text(runName);
          runG.append('title').text(`${run.Name || 'Job run'}\nType: Job\nStatus: ${run.Status}\nElementId: ${run.ElementId || 'N/A'}\nStarted: ${run.StartTimestamp || 'N/A'}`);
        }
      }
    });

    const sfGroup = world.select<SVGGElement>('.sf-replicas');
    sfGroup.selectAll('*').remove();
    for (const node of slimNodeList) {
      const rp = slimReplicaPositions[node.Name];
      if (!rp) continue;
      const c = podColor(node.Status);
      const vx = rp.x - CENTER.x;
      const vy = rp.y - CENTER.y;
      const distance = Math.max(1, Math.hypot(vx, vy));
      const labelX = rp.x + (vx / distance) * 12;
      const labelY = rp.y + (vy / distance) * 12 + 2;
      const labelAnchor = Math.abs(vx / distance) < 0.25 ? 'middle' : (vx > 0 ? 'start' : 'end');
      sfGroup.append('circle')
        .attr('cx', rp.x)
        .attr('cy', rp.y)
        .attr('r', REPLICA_R)
        .attr('fill', c)
        .attr('stroke', '#fff')
        .attr('stroke-width', 1.5);
      sfGroup.append('text')
        .attr('x', labelX)
        .attr('y', labelY)
        .attr('text-anchor', labelAnchor)
        .attr('font-size', 7)
        .attr('fill', '#212529')
        .attr('font-weight', 600)
        .attr('paint-order', 'stroke')
        .attr('stroke', 'rgba(255,255,255,0.9)')
        .attr('stroke-width', 1.8)
        .attr('stroke-linejoin', 'round')
        .text(node.Name);
      sfGroup.append('title').text(`${node.Name} - ${node.Status}`);
    }
  }, [functionReplicaPositions, functions, fnPositions, jobPositions, jobRunPositions, jobs, queueMap, queuePositions, slimNodeList, slimReplicaPositions]);

  useEffect(() => {
    if (!initialFitDoneRef.current) return;
    if (userHasZoomedRef.current) return;
    const t = setTimeout(() => zoomToFit(true), 50);
    return () => clearTimeout(t);
  }, [structuralKey, zoomToFit]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;
    const ro = new ResizeObserver(() => {
      if (!userHasZoomedRef.current) zoomToFit(false);
    });
    ro.observe(container);
    return () => ro.disconnect();
  }, [zoomToFit]);

  return (
    <div className="network-map" ref={containerRef}>
      <h2 className="network-map__title">
        Live Network Map
        <button
          className="network-map__reset-btn"
          type="button"
          title="Reset zoom to fit all elements"
          onClick={() => { userHasZoomedRef.current = false; zoomToFit(true); }}
        >
          Reset
        </button>
      </h2>
      <svg ref={svgRef} className="network-map__svg" />
      <div className="network-map__legend">
        <span className="network-map__legend-item"><span className="network-map__legend-dot" style={{ backgroundColor: '#adb5bd' }} /> External</span>
        <span className="network-map__legend-item"><span className="network-map__legend-dot" style={{ backgroundColor: '#0000ff' }} /> SlimFaas</span>
        {functions.slice(0, 8).map(fn => (
          <span key={fn.Name} className="network-map__legend-item">
            <span className="network-map__legend-dot" style={{ backgroundColor: nameColor(fn.Name) }} />
            {fn.Name}
          </span>
        ))}
        {functions.length > 8 && <span className="network-map__legend-item" style={{ fontStyle: 'italic' }}>+{functions.length - 8} more</span>}
        {jobs.slice(0, 6).map(job => (
          <span key={`job-${job.Name}`} className="network-map__legend-item">
            <span className="network-map__legend-dot" style={{ backgroundColor: nameColor(`job:${job.Name}`) }} />
            Job: {job.Name}
          </span>
        ))}
        {jobs.length > 6 && <span className="network-map__legend-item" style={{ fontStyle: 'italic' }}>+{jobs.length - 6} jobs</span>}
        <span className="network-map__legend-item" style={{ marginLeft: 12 }}><svg width="14" height="10"><circle cx="5" cy="5" r="4" fill="#4c6ef5" /></svg> Request</span>
        <span className="network-map__legend-item"><svg width="14" height="10"><rect x="1" y="2" width="10" height="6" rx="1.5" fill="#6b778c" /></svg> Async</span>
        <span className="network-map__legend-item"><svg width="10" height="10"><circle cx="5" cy="5" r="5" fill="#36b37e" /></svg> Replica</span>
        <span className="network-map__legend-item"><svg width="12" height="12"><circle cx="6" cy="6" r="5" fill="#f08c00" /></svg> Job</span>
        <span className="network-map__legend-separator" />
        <span className="network-map__legend-item" title="Active requests badge"><svg width="18" height="14"><rect x="1" y="0" width="16" height="14" rx="7" fill="#f08c00" stroke="#fff" strokeWidth="1" /><text x="9" y="10.5" textAnchor="middle" fontSize="8" fontWeight="bold" fill="#fff">3</text></svg> Active</span>
      </div>
    </div>
  );
};

export default NetworkMap;
