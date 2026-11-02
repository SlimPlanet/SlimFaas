#!/usr/bin/env bash
# Opt-in isolated demo. Mutations run once; only observations are polled.
set -euo pipefail
BASE_URL="${BASE_URL:-http://127.0.0.1:30020}"
BASE_URL="${BASE_URL%/}"
for tool in curl jq; do
  command -v "$tool" >/dev/null 2>&1 || {
    printf 'Missing required tool: %s. Install it and add it to PATH (Git Bash on Windows).\n' "$tool" >&2
    exit 1
  }
done
TOUR_ID="${TOUR_ID:-tour-durable-$(date +%s)-$$}"
[[ "$TOUR_ID" =~ ^[a-zA-Z0-9-]{1,100}$ ]] || { echo 'Invalid TOUR_ID' >&2; exit 1; }
TOUR_TMP=$(mktemp -d)
dependency_created=0
cleanup() {
  local result=$? status
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
  local expected="$1" method="$2" route="$3" status
  shift 3
  status=$(curl -sS --connect-timeout 5 --max-time 180 -o "$TOUR_TMP/body" -w '%{http_code}' -X "$method" "$BASE_URL$route" "$@")
  local deadline=$((SECONDS + 10))
  while [[ "$method" == GET && "$route" == /data/* && ( "$expected" == 200 || "$expected" == 404 ) && ( "$status" == 200 || "$status" == 404 ) && "$status" != "$expected" && $SECONDS -lt $deadline ]]; do
    sleep 0.2
    status=$(curl -sS --max-time 10 -o "$TOUR_TMP/body" -w '%{http_code}' "$BASE_URL$route")
  done
  HTTP_STATUS="$status"
  if [[ ",$expected," != *",$status,"* ]]; then
    echo "FAIL $method $route: expected $expected, got $status. Check optional overlays and branch images." >&2
    cat "$TOUR_TMP/body" >&2
    exit 1
  fi
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
    [[ $SECONDS -lt $deadline ]] || { echo "No controlled 422 captured for $TOUR_ID; check Tour__Enabled and logs." >&2; exit 1; }
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
request 200 GET /ready
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
# No read/reserve/ack API is called: ordinary HTTP subscribers do the processing.
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
