#!/usr/bin/env bash
# Execute against an already running tutorial demo. Requires curl and jq.
set -euo pipefail
BASE_URL="${BASE_URL:-http://127.0.0.1:30020}"
BASE_URL="${BASE_URL%/}"
command -v jq >/dev/null
command -v curl >/dev/null
TOUR_TMP=$(mktemp -d)
TOUR_ID="tour-$(date +%s)-$$"
TOUR_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
set_id='' hash_id='' file_id='' job_id='' schedule_id=''
cleanup() {
  for id in "$set_id" "$TOUR_ID-value" "$TOUR_ID-count" "$TOUR_ID-decimal" "$TOUR_ID-ttl"; do
    [[ -z "$id" ]] || curl -s --max-time 10 -X DELETE "$BASE_URL/data/sets/$id" >/dev/null || true
  done
  [[ -z "$hash_id" ]] || curl -s --max-time 10 -X DELETE "$BASE_URL/data/hashsets/$hash_id" >/dev/null || true
  [[ -z "$file_id" ]] || curl -s --max-time 10 -X DELETE "$BASE_URL/data/files/$file_id" >/dev/null || true
  [[ -z "$schedule_id" ]] || curl -s --max-time 10 -X DELETE "$BASE_URL/job-schedules/fibonacci/$schedule_id" >/dev/null || true
  [[ -z "$job_id" ]] || curl -s --max-time 10 -X DELETE "$BASE_URL/job/fibonacci/$job_id" >/dev/null || true
  rm -rf "$TOUR_TMP"
}
trap cleanup EXIT
request() {
  local expected="$1" method="$2" route="$3"
  shift 3
  local status
  status=$(curl -sS --connect-timeout 5 --max-time 180 -o "$TOUR_TMP/body" -w '%{http_code}' -X "$method" "$BASE_URL$route" "$@")
  # The entrypoint can select a follower that has not applied a just-committed
  # write/delete yet. Poll reads only; never replay a mutation automatically.
  local deadline=$((SECONDS + 10))
  while [[ "$method" == GET && "$route" == /data/*/* && ( "$expected" == 200 || "$expected" == 404 ) && ( "$status" == 200 || "$status" == 404 ) && "$status" != "$expected" && $SECONDS -lt $deadline ]]; do
    sleep 0.2
    status=$(curl -sS --max-time 10 -o "$TOUR_TMP/body" -w '%{http_code}' "$BASE_URL$route")
  done
  if [[ ",$expected," != *",$status,"* ]]; then
    echo "FAIL $method $route: expected $expected, got $status" >&2
    cat "$TOUR_TMP/body" >&2
    exit 1
  fi
  echo "PASS $method $route ($status)"
}
wait_ready() {
  local deadline=$((SECONDS + 180))
  until curl -fsS --max-time 3 "$BASE_URL/ready" >/dev/null 2>&1; do
    [[ $SECONDS -lt $deadline ]] || { echo 'Readiness deadline exceeded' >&2; exit 1; }
    sleep 0.5
  done
}
read_state() {
  local result=0
  curl -s --max-time 2 -N "$BASE_URL/status-functions-stream" > "$TOUR_TMP/stream" || result=$?
  [[ $result == 0 || $result == 28 ]] || return "$result"
  awk '/^data: / {sub(/^data: /, ""); print; exit}' "$TOUR_TMP/stream" > "$TOUR_TMP/state"
  jq -e '.Functions and .Queues' "$TOUR_TMP/state" >/dev/null
}
wait_queue_empty() {
  local deadline=$((SECONDS + 180))
  while true; do
    read_state
    if jq -e '[.Queues[] | select(.Name == "fibonacci1")] | length > 0 and all(.Length == 0)' "$TOUR_TMP/state" >/dev/null; then break; fi
    [[ $SECONDS -lt $deadline ]] || { echo 'Async completion deadline exceeded' >&2; exit 1; }
  done
}
wait_ready
request 200 GET /health
request 200 GET /ready
request 200 GET /status-functions
jq -e '[.[].Name] | index("fibonacci1") != null and index("fibonacci4") != null' "$TOUR_TMP/body" >/dev/null
request 200 GET /status-function/fibonacci1
request 204 POST /wake-function/fibonacci1
request 204 POST /wake-functions
request 200 GET /function/fibonacci1/hello/tour
[[ $(cat "$TOUR_TMP/body") == 'Hello tour!' ]]
request 200 POST /function/fibonacci1/fibonacci -H 'Content-Type: application/json' --data '{"input":10}'
jq -e '.result == 55' "$TOUR_TMP/body" >/dev/null
request 200 GET /function/fibonacci2/download
[[ $(od -An -tx1 -N8 "$TOUR_TMP/body" | tr -d ' \n') == '89504e470d0a1a0a' ]]
request 200 GET /function/fibonacci3/hello/tour
request 200 POST /function/fibonacci3/fibonacci-recursive -H 'Content-Type: application/json' --data '{"input":5}'
request 404 GET /function/unknown-tour-function/hello/tour
request 404 GET /function/fibonacci1
request 405 POST /function/fibonacci1/hello/tour
request 500 GET /function/fibonacci1/error
request 200,404 GET /function/fibonacci4/hello/tour
echo 'INFO Private access depends on caller identity; local loopback and port-forward are not isolation tests.'
wait_queue_empty
request 202 POST /async-function/fibonacci1/fibonacci -H 'Content-Type: application/json' --data '{"input":10}'
wait_queue_empty
request 202 POST /async-function/fibonacci1/computeWithCallback -H 'Content-Type: application/json' --data '{"input":10}'
read_state
jq -e '.Queues[] | select(.Name == "fibonacci1") | .Length > 0' "$TOUR_TMP/state" >/dev/null
wait_queue_empty
echo 'PASS Deferred callback released the queue'
request 204 POST /wake-function/fibonacci3
request 204 POST /wake-function/fibonacci4
request 200 GET /function/fibonacci3/hello/subscriber
request 204 POST /publish-event/fibo-public/fibonacci -H 'Content-Type: application/json' --data '{"input":10}'
request 404 POST /publish-event/unknown-tour-event/fibonacci -H 'Content-Type: application/json' --data '{"input":10}'
request 202 POST /job/fibonacci -H 'Content-Type: application/json' --data '{"Args":["10"]}'
job_id=$(jq -er .Id "$TOUR_TMP/body")
request 200 GET /job/fibonacci
request 200 GET /jobs/status
request 200 GET /status-jobs
request 201 POST /job-schedules/fibonacci -H 'Content-Type: application/json' --data '{"Schedule":"0 0 1 1 *","Args":["10"]}'
schedule_id=$(jq -er .Id "$TOUR_TMP/body")
request 200 GET /job-schedules/fibonacci
deadline=$((SECONDS + 15))
until jq -e --arg id "$schedule_id" 'any(.[]; .Id == $id)' "$TOUR_TMP/body" >/dev/null; do
  [[ $SECONDS -lt $deadline ]] || { echo 'Schedule visibility deadline exceeded' >&2; exit 1; }
  sleep 0.2
  request 200 GET /job-schedules/fibonacci
done
request 400 POST /job-schedules/fibonacci -H 'Content-Type: application/json' --data '{"Schedule":"invalid","Args":["10"]}'
request 405 PUT /job/fibonacci
request 405 PATCH /job-schedules/fibonacci
request 200 POST /data/sets?ttl=300000 --data-binary 'tour value'
set_id=$(jq -er . "$TOUR_TMP/body")
request 200 GET "/data/sets/$set_id"
[[ $(cat "$TOUR_TMP/body") == 'tour value' ]]
request 200 POST "/data/sets/$TOUR_ID-value" --data-binary ready
request 200 GET /data/sets
request 200 POST "/data/sets/$TOUR_ID-count/incr"
[[ $(cat "$TOUR_TMP/body") == 1 ]]
request 200 POST "/data/sets/$TOUR_ID-count/incrby?by=10"
[[ $(cat "$TOUR_TMP/body") == 11 ]]
request 200 POST "/data/sets/$TOUR_ID-count/decr"
[[ $(cat "$TOUR_TMP/body") == 10 ]]
request 200 POST "/data/sets/$TOUR_ID-count/decrby?by=2"
[[ $(cat "$TOUR_TMP/body") == 8 ]]
request 200 POST "/data/sets/$TOUR_ID-decimal/incrbyfloat?by=1.25"
[[ $(cat "$TOUR_TMP/body") == 1.25 ]]
request 409 POST "/data/sets/$TOUR_ID-value/incr"
request 400 POST "/data/sets/$TOUR_ID-count/incrby"
request 400 POST "/data/sets/$TOUR_ID-count/incr?ttl=0"
request 200 POST "/data/sets/$TOUR_ID-ttl?ttl=1500" --data-binary expire
request 200 GET "/data/sets/$TOUR_ID-ttl"
deadline=$((SECONDS + 15))
until [[ $(curl -s --max-time 3 -o /dev/null -w '%{http_code}' "$BASE_URL/data/sets/$TOUR_ID-ttl") == 404 ]]; do
  [[ $SECONDS -lt $deadline ]] || { echo 'TTL expiry deadline exceeded' >&2; exit 1; }
  sleep 0.2
done
echo 'PASS TTL expired'
request 200 POST /data/hashsets?ttl=300000 --data-binary 'hashset sample'
hash_id=$(jq -er . "$TOUR_TMP/body")
request 200 GET "/data/hashsets/$hash_id"
[[ $(cat "$TOUR_TMP/body") == 'hashset sample' ]]
request 200 GET /data/hashsets
request 200 POST /data/files?ttl=300000 -H 'Content-Type: text/plain' --data-binary "@$TOUR_ROOT/bruno-slimfaas-demo/fixtures/hello.txt"
file_id=$(cat "$TOUR_TMP/body")
request 200 GET "/data/files/$file_id"
cmp "$TOUR_TMP/body" "$TOUR_ROOT/bruno-slimfaas-demo/fixtures/hello.txt"
request 200 GET /data/files
request 200 GET /metrics
request 200 POST /debug/promql/eval -H 'Content-Type: application/json' --data '{"Query":"1 + 1"}'
jq -e '.value == 2' "$TOUR_TMP/body" >/dev/null
request 200 GET /debug/store
request 400 POST /debug/promql/eval -H 'Content-Type: application/json' --data '{"Query":""}'
read_state
echo 'PASS SSE state snapshot (expected cURL timeout accepted)'
request 204 DELETE "/job-schedules/fibonacci/$schedule_id"
schedule_id=''
request 200,404 DELETE "/job/fibonacci/$job_id"
job_id=''
request 204 DELETE "/data/sets/$set_id"
request 404 GET "/data/sets/$set_id"
set_id=''
request 204 DELETE "/data/hashsets/$hash_id"
hash_id=''
request 204 DELETE "/data/files/$file_id"
request 404 GET "/data/files/$file_id"
file_id=''
echo 'PASS Core SlimFaas tour. Manual scenarios: WebSocket sessions, internal interfaces and access isolation.'
