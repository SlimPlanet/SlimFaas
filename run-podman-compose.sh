#!/usr/bin/env bash
set -euo pipefail

# Enable verbose mode for this script if VERBOSE=1
VERBOSE="${VERBOSE:-0}"
if [[ "$VERBOSE" == "1" ]]; then
  set -x
fi

# Podman log level (debug if VERBOSE=1, otherwise info)
PODMAN_LOG_LEVEL="${PODMAN_LOG_LEVEL:-$([[ "$VERBOSE" == "1" ]] && echo debug || echo info)}"
echo ">> Using PODMAN_LOG_LEVEL=$PODMAN_LOG_LEVEL"

# 1) In the Podman VM, the Docker-compatible socket is /run/docker.sock
DOCKER_SOCKET_PATH="/run/docker.sock"
echo ">> Using DOCKER_SOCKET_PATH=$DOCKER_SOCKET_PATH"

# (Optional) Safety: make sure the socket is readable/writable
podman machine ssh -- "sudo chmod 666 /run/user/*/podman/podman.sock /run/docker.sock 2>/dev/null || true"

# 2) Run podman compose with the right environment variables
export DOCKER_SOCKET_PATH
export DOCKER_HOST="unix:///var/run/docker.sock"
export PODMAN_LOG_LEVEL

echo ">> Running: podman --log-level=$PODMAN_LOG_LEVEL compose $*"

podman --log-level="$PODMAN_LOG_LEVEL" compose "$@"
