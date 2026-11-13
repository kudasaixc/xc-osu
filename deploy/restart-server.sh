#!/bin/bash
# Run after swapping server/publish (or server/verifier): lets the xc-osu service account read the new
# binaries, then restarts the service. The service no longer runs as admin, so a plain kill is not enough.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
# the server opens PostgreSQL on the local socket; without it, it would only restart-loop.
systemctl is-active --quiet postgresql || { echo "postgresql is not running" >&2; exit 1; }
setfacl -R -P -m u:xc-osu:rX "$ROOT/server/publish" "$ROOT/server/verifier"
sudo -n systemctl restart xc-osu-server
sleep 4
systemctl is-active xc-osu-server
