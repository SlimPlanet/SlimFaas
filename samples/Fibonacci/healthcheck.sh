#!/usr/bin/env bash
# The Ubuntu ASP.NET image includes Bash, but neither curl nor wget.
set -euo pipefail

exec 3<>/dev/tcp/127.0.0.1/"${1:-5000}"
printf 'GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3
read -r -t 2 protocol status reason <&3
[[ "$protocol" == HTTP/1.* && "$status" == 200 ]]
