#!/usr/bin/env bash
set -euo pipefail

# 1. Récupère le chemin du socket Podman côté host
raw_socket_path=$(podman info --format '{{.Host.RemoteSocket.Path}}' 2>/dev/null || echo "")

if [ -z "$raw_socket_path" ]; then
  echo "Impossible de récupérer .Host.RemoteSocket.Path via 'podman info'."
  echo "Vérifie que 'podman machine' est bien démarrée."
  exit 1
fi

# Enlève éventuellement le prefix 'unix://'
host_socket_path="${raw_socket_path#unix://}"

echo "Utilisation du socket Podman host : $host_socket_path"

# 2. Lancer podman compose en surchargant uniquement les variables nécessaires
DOCKER_SOCKET_PATH="$host_socket_path" \
DOCKER_HOST="unix:///var/run/docker.sock" \
podman compose "$@"
