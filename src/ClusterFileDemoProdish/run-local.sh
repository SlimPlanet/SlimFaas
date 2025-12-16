#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

PROJECT="$(find . -maxdepth 6 -name '*.csproj' | head -n 1)"
if [[ -z "${PROJECT}" ]]; then
  echo "No .csproj found."
  exit 1
fi

echo "Using project: ${PROJECT}"

PORTS=(3262 3263 3264)
DATA_DIRS=("${ROOT}/.data/node1" "${ROOT}/.data/node2" "${ROOT}/.data/node3")
for d in "${DATA_DIRS[@]}"; do mkdir -p "$d"; done

pids=()
cleanup() {
  echo "Stopping nodes..."
  for pid in "${pids[@]:-}"; do kill "$pid" 2>/dev/null || true; done
}
trap cleanup EXIT INT TERM

for i in "${!PORTS[@]}"; do
  port="${PORTS[$i]}"
  dir="${DATA_DIRS[$i]}"

  echo "Starting node $((i+1)) on http://127.0.0.1:${port}"
  echo "  -> fileStorage.RootPath: ${dir}"

  ASPNETCORE_URLS="http://127.0.0.1:${port}" \
  fileStorage__RootPath="${dir}" \
  dotnet run --project "${PROJECT}" --no-launch-profile > "${ROOT}/node-${port}.log" 2>&1 &

  pids+=("$!")
done

echo ""
echo "Nodes started:"
echo " - http://127.0.0.1:3262  (RootPath: ${DATA_DIRS[0]})"
echo " - http://127.0.0.1:3263  (RootPath: ${DATA_DIRS[1]})"
echo " - http://127.0.0.1:3264  (RootPath: ${DATA_DIRS[2]})"
echo ""
echo "Logs: node-3262.log / node-3263.log / node-3264.log"
echo "Ctrl+C to stop."

wait
