#!/usr/bin/env bash
# Runs the Studio web UI and the Compute API in one container, sharing
# /data/SharpMind. Either process exiting stops the container.
set -euo pipefail

if [ -z "${SHARPMIND_TOKEN:-}" ]; then
  echo "[sharpmind] WARNING: SHARPMIND_TOKEN is not set; the Compute API returns 503 until it is."
fi

echo "[sharpmind] Studio:      ${Studio__Url:-http://0.0.0.0:5117}"
echo "[sharpmind] Compute API: ${ASPNETCORE_URLS:-http://0.0.0.0:8080}"
echo "[sharpmind] Data root:   ${SHARPMIND_DATA:-/data/SharpMind}"

dotnet /app/studio/SharpMind.Studio.dll &
studio=$!
dotnet /app/compute/SharpMind.Studio.ComputeHost.dll &
compute=$!

trap 'kill -TERM "$studio" "$compute" 2>/dev/null || true' TERM INT

status=0
wait -n "$studio" "$compute" || status=$?

kill -TERM "$studio" "$compute" 2>/dev/null || true
wait "$studio" 2>/dev/null || true
wait "$compute" 2>/dev/null || true
exit "$status"
