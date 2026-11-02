#!/usr/bin/env bash
# Execute against an already running tutorial demo. Requires curl and jq.
set -euo pipefail
BASE_URL="${BASE_URL:-http://127.0.0.1:30020}"
BASE_URL="${BASE_URL%/}"
for tool in curl jq; do
  command -v "$tool" >/dev/null 2>&1 || {
    printf 'Missing required tool: %s. Install it and add it to PATH (Git Bash on Windows).\n' "$tool" >&2
    exit 1
  }
done
TOUR_TMP=$(mktemp -d)
TOUR_ID="tour-$(date +%s)-$$"
TOUR_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
set_id='' hash_id='' file_id='' job_id='' schedule_id=''
dependency_created=0
cleanup() {
  local result=$? status
  for id in "$set_id" "$TOUR_ID-value" "$TOUR_ID-count" "$TOUR_ID-decimal" "$TOUR_ID-ttl"; do
    [[ -z "$id" ]] || curl -s --max-time 10 -X DELETE "$BASE_URL/data/sets/$id" >/dev/null || true
  done
  [[ -z "$hash_id" ]] || curl -s --max-time 10 -X DELETE "$BASE_URL/data/hashsets/$hash_id" >/dev/null || true
  [[ -z "$file_id" ]] || curl -s --max-time 10 -X DELETE "$BASE_URL/data/files/$file_id" >/dev/null || true
  [[ -z "$schedule_id" ]] || curl -s --max-time 10 -X DELETE "$BASE_URL/job-schedules/fibonacci/$schedule_id" >/dev/null || true
  [[ -z "$job_id" ]] || curl -s --max-time 10 -X DELETE "$BASE_URL/job/fibonacci/$job_id" >/dev/null || true
  if (( dependency_created )); then
    for key in "$TOUR_ID-dlq-ready" "$TOUR_ID-dlq-completed"; do
      status=$(curl -sS --max-time 10 -o "$TOUR_TMP/cleanup" -w '%{http_code}' -X DELETE "$BASE_URL/data/sets/$key") || status=000
      if [[ "$status" != 204 && "$status" != 404 ]]; then
        echo "FAIL cleanup $key ($status); its TTL is ten minutes." >&2
        result=1
      fi
    done
  fi
  rm -rf "$TOUR_TMP"
  exit "$result"
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
  HTTP_STATUS="$status"
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
  local result deadline=$((SECONDS + 15))
  while (( SECONDS < deadline )); do
    result=0
    curl -s --max-time 2 -N "$BASE_URL/status-functions-stream?activity=false" > "$TOUR_TMP/stream" || result=$?
    if [[ $result == 0 || $result == 28 ]]; then
      awk '/^event: / {event=$2; sub(/\r$/, "", event)} event == "state" && /^data: / {sub(/^data: /, ""); print; exit}' "$TOUR_TMP/stream" > "$TOUR_TMP/state"
      if jq -e '.Functions and .Queues' "$TOUR_TMP/state" >/dev/null 2>&1; then return 0; fi
    fi
    sleep 0.2
  done
  echo 'No complete SSE state snapshot received within 15 seconds. Check readiness and server load.' >&2
  return 1
}
wait_queue_empty() {
  local deadline=$((SECONDS + 180))
  while true; do
    read_state
    if jq -e '[.Queues[] | select(.Name == "fibonacci1")] | length > 0 and all(.Length == 0)' "$TOUR_TMP/state" >/dev/null; then break; fi
    [[ $SECONDS -lt $deadline ]] || { echo 'Async completion deadline exceeded' >&2; exit 1; }
  done
}
wait_queue_nonempty() {
  local deadline=$((SECONDS + 15))
  while true; do
    read_state
    if jq -e 'any(.Queues[]; .Name == "fibonacci1" and .Length > 0)' "$TOUR_TMP/state" >/dev/null; then return 0; fi
    [[ $SECONDS -lt $deadline ]] || {
      echo 'Deferred callback was not observed in the fibonacci1 queue within 15 seconds. Check status propagation and callback logs.' >&2
      return 1
    }
  done
}
wait_subscribers() {
  local deadline=$((SECONDS + 180))
  while true; do
    request 200,404 GET /data/streams/orders/functions
    if jq -e '[.Items[]? | select(.Active and (.Function == "fibonacci1" or .Function == "fibonacci2"))] | length == 2' "$TOUR_TMP/body" >/dev/null; then return; fi
    [[ $SECONDS -lt $deadline ]] || { echo 'Both orders subscribers must exist before publishing.' >&2; exit 1; }
    sleep 0.5
  done
}
find_failure() {
  local deadline=$((SECONDS + 180)) cursor pages route
  while true; do
    cursor='' pages=0
    while true; do
      route='/data/dead-letters/fibonacci1/messages?httpStatus=422&limit=500'
      [[ -z "$cursor" ]] || route="$route&after=$(jq -rn --arg value "$cursor" '$value|@uri')"
      request 200 GET "$route"
      DLQ_ID=$(jq -r --arg id "$TOUR_ID" '.Items[] | select((.Request.Path | ltrimstr("/")) == ("tour/dlq/" + $id)) | .Id' "$TOUR_TMP/body")
      if [[ -n "$DLQ_ID" ]]; then
        [[ "$DLQ_ID" != *$'\n'* ]] || { echo 'Multiple matching failures; do not replay automatically.' >&2; exit 1; }
        jq -e --arg id "$DLQ_ID" '.Items[] | select(.Id == $id) | .LastHttpStatus == 422 and .FailureReason == "NonRetryableResponse"' "$TOUR_TMP/body" >/dev/null
        return
      fi
      cursor=$(jq -r '.NextCursor // empty' "$TOUR_TMP/body")
      [[ -n "$cursor" ]] || break
      pages=$((pages + 1))
      [[ $pages -lt 20 ]] || { echo 'DLQ inspection exceeded 20 pages; inspect this run manually.' >&2; exit 1; }
    done
    [[ $SECONDS -lt $deadline ]] || { echo "No controlled 422 captured for $TOUR_ID; check sample image, DLQ capture and logs." >&2; exit 1; }
    sleep 0.5
  done
}
wait_marker() {
  local deadline=$((SECONDS + 180))
  while true; do
    request 200,404 GET "/data/sets/$TOUR_ID-dlq-completed"
    [[ $(cat "$TOUR_TMP/body") != "$REPLAY_ID" ]] || return 0
    [[ $SECONDS -lt $deadline ]] || { echo "Replay $REPLAY_ID did not complete; inspect DLQ and function logs." >&2; exit 1; }
    sleep 0.5
  done
}
wait_acknowledgements() {
  local deadline=$((SECONDS + 180))
  while true; do
    request 200 GET /data/streams/orders/functions
    if jq -e --argjson target "$SEQUENCE" '[.Items[] | select(.Function == "fibonacci1" or .Function == "fibonacci2")] | length == 2 and all(.Active and .AcknowledgedThrough >= $target)' "$TOUR_TMP/body" >/dev/null; then return; fi
    [[ $SECONDS -lt $deadline ]] || { echo "Both subscribers did not ACK $MESSAGE_ID." >&2; cat "$TOUR_TMP/body" >&2; exit 1; }
    sleep 0.5
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
wait_queue_nonempty
wait_queue_empty
echo 'PASS Deferred callback released the queue'
request 204 POST /wake-function/fibonacci3
request 204 POST /wake-function/fibonacci4
request 200 GET /function/fibonacci3/hello/subscriber
request 204 POST /publish-event/fibo-public/fibonacci -H 'Content-Type: application/json' --data '{"input":10}'
request 404 POST /publish-event/unknown-tour-event/fibonacci -H 'Content-Type: application/json' --data '{"input":10}'
request 202 POST /job/fibonacci -H 'Content-Type: application/json' --data '{"Args":["10"],"TtlSecondsAfterFinished":60}'
job_id=$(jq -er .Id "$TOUR_TMP/body")
request 200 GET /job/fibonacci
deadline=$((SECONDS + 180))
until jq -e --arg id "$job_id" 'any(.[]; .Id == $id and .Status == "Succeeded")' "$TOUR_TMP/body" >/dev/null; do
  if jq -e --arg id "$job_id" 'any(.[]; .Id == $id and .Status == "Failed")' "$TOUR_TMP/body" >/dev/null; then
    echo 'FAIL Fibonacci job execution' >&2
    cat "$TOUR_TMP/body" >&2
    exit 1
  fi
  [[ $SECONDS -lt $deadline ]] || { echo 'Job completion deadline exceeded' >&2; cat "$TOUR_TMP/body" >&2; exit 1; }
  sleep 0.5
  request 200 GET /job/fibonacci
done
echo 'PASS Fibonacci job succeeded'
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
request 200 GET /data/dead-letters
wait_subscribers
# Refuse reused IDs instead of accidentally succeeding with a previous ready flag.
request 404 GET "/data/sets/$TOUR_ID-dlq-ready"
request 404 GET "/data/sets/$TOUR_ID-dlq-completed"
request 202 POST "/async-function/fibonacci1/tour/dlq/$TOUR_ID" -H 'Content-Type: application/json' --data "{\"orderId\":\"$TOUR_ID\"}"
find_failure
echo "PASS Captured controlled 422: $DLQ_ID ($TOUR_ID)"
request 200 GET "/data/dead-letters/fibonacci1/messages/$DLQ_ID/body"
jq -e --arg id "$TOUR_ID" '.orderId == $id' "$TOUR_TMP/body" >/dev/null
dependency_created=1
request 200 POST "/data/sets/$TOUR_ID-dlq-ready?ttl=600000" -H 'Content-Type: text/plain' --data-binary ready
deadline=$((SECONDS + 10))
while true; do
  request 200,404 GET "/data/sets/$TOUR_ID-dlq-ready"
  [[ $(cat "$TOUR_TMP/body") != ready ]] || break
  [[ $SECONDS -lt $deadline ]] || { echo 'Repair visibility deadline exceeded' >&2; exit 1; }
  sleep 0.2
done
request 202 POST "/data/dead-letters/fibonacci1/messages/$DLQ_ID/requeue"
REPLAY_ID=$(jq -er --arg id "$DLQ_ID" 'select(.DeadLetterId == $id and .ElementId != .OriginalElementId) | .ElementId' "$TOUR_TMP/body")
wait_marker
deadline=$((SECONDS + 10))
while true; do
  request 200,404 GET "/data/dead-letters/fibonacci1/messages/$DLQ_ID"
  [[ "$HTTP_STATUS" != 404 ]] || break
  [[ $SECONDS -lt $deadline ]] || { echo 'Replayed entry still visible' >&2; exit 1; }
  sleep 0.2
done
echo "PASS Replayed original body as fresh successful execution $REPLAY_ID"
# Ordinary HTTP subscribers process the message, not a manual read/reserve/ack API.
request 201 POST /publish-stream/orders/compute -H 'Content-Type: application/json' --data "{\"orderId\":\"$TOUR_ID\",\"amount\":42}"
MESSAGE_ID=$(jq -er '.MessageId | select(test("^[a-f0-9]+_[0-9]{20}$"))' "$TOUR_TMP/body")
SEQUENCE=$(jq -nr --arg id "$MESSAGE_ID" '$id|split("_")|last|tonumber')
wait_acknowledgements
echo "PASS fibonacci1 and fibonacci2 independently ACKed $MESSAGE_ID"
request 200 GET "/data/streams/orders/messages/$MESSAGE_ID"
jq -e --arg id "$MESSAGE_ID" '.Id == $id' "$TOUR_TMP/body" >/dev/null
request 200 GET "/data/streams/orders/messages/$MESSAGE_ID/body"
jq -e --arg id "$TOUR_ID" '.orderId == $id and .amount == 42' "$TOUR_TMP/body" >/dev/null
echo "PASS ACK preserved this run's retained history; orders and its subscriptions were not deleted."
echo 'PASS SlimFaas tour including DLQ repair and durable Streams. Manual scenarios: WebSocket sessions, internal interfaces and access isolation.'
