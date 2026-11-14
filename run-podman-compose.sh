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

# In the Podman VM, the Docker-compatible socket is /run/docker.sock.
# Use the active Podman connection; it need not be podman-machine-default.
DOCKER_SOCKET_PATH="${DOCKER_SOCKET_PATH:-/run/docker.sock}"
echo ">> Using DOCKER_SOCKET_PATH=$DOCKER_SOCKET_PATH"

# podman compose configures the provider's host connection itself. DOCKER_HOST
# here is interpolated into the SlimFaas container, where the socket is mounted.
export DOCKER_SOCKET_PATH
export DOCKER_HOST="unix:///var/run/docker.sock"
export PODMAN_LOG_LEVEL

echo ">> Running: podman --log-level=$PODMAN_LOG_LEVEL compose $*"

podman --log-level="$PODMAN_LOG_LEVEL" compose "$@"
