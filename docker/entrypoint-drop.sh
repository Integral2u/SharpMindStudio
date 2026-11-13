#!/usr/bin/env bash
# Runs the container command as the unprivileged sharpmind user. When the
# container starts as root (the default), fix ownership of the mounted data and
# plugin folders first, then drop privileges. This keeps named volumes and bind
# mounts working without a host-side chown step.
set -euo pipefail

if [ "$(id -u)" -eq 0 ]; then
  data="${SHARPMIND_DATA:-/data/SharpMind}"
  plugins="${SHARPMIND_PLUGINS:-/app/plugins}"

  mkdir -p "$data" "$plugins"
  if [ "$(stat -c '%u' "$data" 2>/dev/null || echo 0)" != "10001" ]; then
    chown -R sharpmind:sharpmind "$data"
  fi
  if [ "$(stat -c '%u' "$plugins" 2>/dev/null || echo 0)" != "10001" ]; then
    chown -R sharpmind:sharpmind "$plugins"
  fi

  exec setpriv --reuid=sharpmind --regid=sharpmind --init-groups "$0" "$@"
fi

exec "$@"
