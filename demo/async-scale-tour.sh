#!/usr/bin/env bash
# A bounded workload for the supplied demo; requires Bash, curl and jq.
set -euo pipefail
BASE_URL="${BASE_URL:-http://127.0.0.1:30020}"
BASE_URL="${BASE_URL%/}"
REQUESTS="${REQUESTS:-800}"
CONCURRENCY="${CONCURRENCY:-16}"
for tool in curl jq; do command -v "$tool" >/dev/null; done
[[ $REQUESTS =~ ^[0-9]+$ && $CONCURRENCY =~ ^[0-9]+$ ]] || { echo 'REQUESTS and CONCURRENCY must be integers.' >&2; exit 1; }
REQUESTS=$((10#$REQUESTS))
CONCURRENCY=$((10#$CONCURRENCY))
(( REQUESTS >= 100 && REQUESTS <= 5000 && CONCURRENCY >= 1 && CONCURRENCY <= 32 )) || {
  echo 'Use 100–5000 REQUESTS and 1–32 CONCURRENCY.' >&2; exit 1;
}
SCALE_TMP=$(mktemp -d)
workers=()
cleanup() {
  for worker in "${workers[@]-}"; do [[ -n $worker ]] || continue; kill "$worker" 2>/dev/null || true; done
  for worker in "${workers[@]-}"; do [[ -n $worker ]] || continue; wait "$worker" 2>/dev/null || true; done
  rm -rf -- "$SCALE_TMP"
}
trap cleanup EXIT
trap 'echo "Stopped submitting. Already accepted requests remain queued; let them drain in the UI." >&2; exit 130' INT TERM

read_state() {
  local status=0
  curl -s --max-time 2 -N "$BASE_URL/status-functions-stream" > "$SCALE_TMP/stream" || status=$?
  [[ $status == 0 || $status == 28 ]] || return "$status"
  awk '/^data: / {sub(/^data: /, ""); print; exit}' "$SCALE_TMP/stream" > "$SCALE_TMP/state"
  jq -e '.Functions and .Queues' "$SCALE_TMP/state" >/dev/null
  requested=$(jq -er '.Functions[] | select(.Name == "fibonacci1") | .NumberRequested' "$SCALE_TMP/state")
  ready=$(jq -er '.Functions[] | select(.Name == "fibonacci1") | .NumberReady' "$SCALE_TMP/state")
  queued=$(jq -er '.Queues[] | select(.Name == "fibonacci1") | .Length' "$SCALE_TMP/state")
}

echo 'Use a dedicated tutorial demo with no other producers. Open its dashboard now.'
deadline=$((SECONDS + 120))
until curl -fsS --max-time 2 "$BASE_URL/ready" >/dev/null 2>&1; do
  (( SECONDS < deadline )) || { echo 'SlimFaas did not become ready within 120 seconds.' >&2; exit 1; }
  sleep 0.5
done
read_state
jq '.Functions[] | select(.Name == "fibonacci1")' "$SCALE_TMP/state" > "$SCALE_TMP/config"
jq -e '.ReplicasAtStart == 1 and .Scale.ReplicaMax > 1 and
  any(.Scale.Triggers[]; .Query | contains("slimfaas_function_queue_ready_items"))' "$SCALE_TMP/config" >/dev/null || {
  echo 'This exercise requires the tutorial fibonacci1 queue-based scaling configuration.' >&2; exit 1;
}
down_window=$(jq -r '.Scale.Behavior.ScaleDown.StabilizationWindowSeconds // 300' "$SCALE_TMP/config")
settle_timeout=$((down_window + 120))
echo "Waiting for an idle queue and exactly one requested/ready replica (up to $settle_timeout seconds)…"
deadline=$((SECONDS + settle_timeout))
while true; do
  # Keep one replica awake while old metric samples and scale-down policies expire.
  curl -fsS --max-time 15 -X POST "$BASE_URL/wake-function/fibonacci1" >/dev/null
  read_state
  [[ $requested == 1 && $ready == 1 && $queued == 0 ]] && break
  (( SECONDS < deadline )) || { echo 'Baseline not reached; stop other producers and inspect the UI.' >&2; exit 1; }
done
echo "Baseline N=1. Submitting $REQUESTS async /compute requests with $CONCURRENCY producers."

send_work() {
  local index=$1 code
  trap - EXIT
  trap 'exit 130' INT TERM
  while (( index < REQUESTS )); do
    if ! code=$(curl -sS --max-time 15 -o /dev/null -w '%{http_code}' -X POST \
      "$BASE_URL/async-function/fibonacci1/compute" \
      -H 'Content-Type: application/json' --data '{"input":10}'); then
      printf '%s\n' "Transport error for submission $index; acceptance is unknown. No automatic retry." > "$SCALE_TMP/error-$1"
      return 1
    fi
    printf '%s\n' "$code" >> "$SCALE_TMP/codes-$1"
    if [[ $code != 202 ]]; then
      printf 'Submission %s returned HTTP %s.\n' "$index" "$code" > "$SCALE_TMP/error-$1"
      return 1
    fi
    index=$((index + CONCURRENCY))
  done
}
for (( worker=0; worker<CONCURRENCY; worker++ )); do
  : > "$SCALE_TMP/codes-$worker"
  send_work "$worker" &
  workers+=("$!")
done
peak_requested=1
peak_ready=1
peak_queue=0
empty_samples=0
deadline=$((SECONDS + 300))
printf '%-8s %-12s %-10s %-10s %-10s\n' 'Seconds' 'Submitted' 'Requested' 'Ready' 'Queue'
started=$SECONDS
while true; do
  read_state
  submitted=$(cat "$SCALE_TMP"/codes-* | wc -l | tr -d ' ')
  printf '%-8s %-12s %-10s %-10s %-10s\n' "$((SECONDS - started))" "$submitted" "$requested" "$ready" "$queued"
  (( requested <= peak_requested )) || peak_requested=$requested
  (( ready <= peak_ready )) || peak_ready=$ready
  (( queued <= peak_queue )) || peak_queue=$queued
  if compgen -G "$SCALE_TMP/error-*" >/dev/null; then
    cat "$SCALE_TMP"/error-* >&2
    echo 'Accepted work is still queued. Inspect the UI before running another burst.' >&2
    exit 1
  fi
  if [[ $submitted == "$REQUESTS" && $queued == 0 && $peak_ready -gt 1 ]]; then
    empty_samples=$((empty_samples + 1))
    (( empty_samples < 3 )) || break
  else
    empty_samples=0
  fi
  (( SECONDS < deadline )) || { echo 'Queue did not drain within 300 seconds. Leave SlimFaas running and inspect its logs.' >&2; exit 1; }
done
for worker in "${workers[@]-}"; do [[ -n $worker ]] || continue; wait "$worker"; done
workers=()
(( peak_requested > 1 && peak_ready > 1 && peak_queue > 0 )) || {
  echo 'No complete scale-out observed. Check the trigger, metrics, capacity and sampling interval before repeating.' >&2; exit 1;
}
echo "PASS: all $REQUESTS requests accepted; N=1 -> M=$peak_ready ready replicas (peak requested=$peak_requested, queue=$peak_queue). Queue drained."
echo "Waiting for requested and ready replicas to return to 1 or 0 (up to $settle_timeout seconds)…"
deadline=$((SECONDS + settle_timeout))
while true; do
  read_state
  printf 'Requested=%s Ready=%s Queue=%s\n' "$requested" "$ready" "$queued"
  [[ $queued == 0 && $requested -le 1 && $ready -le 1 ]] && break
  (( SECONDS < deadline )) || { echo 'Scale-down deadline exceeded. Inspect recent metrics, dependencies and running jobs.' >&2; exit 1; }
done
echo 'PASS: scale-down observed. No persistent tutorial data or job was created.'
