#!/bin/bash
# Finish the already-running pacing build, validate it, then deploy. Never restarts compilation.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"
exec >> out/stutter-finish.log 2>&1
serve_pid=''
trap 'rc=$?; [ -z "$serve_pid" ] || kill "$serve_pid" 2>/dev/null || true; echo "Finished status=$rc $(date -u +%FT%TZ)"' EXIT
while systemctl is-active --quiet xc-osu-stutter-build.service; do sleep 10; done
grep -q '^aotpub done in ' out/stutter-build.log || { echo 'Build did not succeed; deployment stopped.'; exit 1; }
cp osu/osu.Web/wwwroot/js/*.js out/aot/wwwroot/js/
cp osu/osu.Web/wwwroot/main.js out/aot/wwwroot/main.js
python3 tools/performance-history.py --public out/aot/wwwroot/launcher/performance.json
python3 tools/stamp.py out/aot/wwwroot
node tools/telemetry-test.mjs
node tools/telemetry-local-test.mjs
node tools/ios-preset-test.mjs
python3 tools/performance-history-test.py
python3 tools/serve.py out/aot/wwwroot 18813 &
serve_pid=$!
sleep 2
kill -0 "$serve_pid"
node tools/telemetry-game-test.mjs http://127.0.0.1:18813 out/stutter-engine-validation.json
backup="backups/pacing1-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$backup"
rsync -a --exclude=/dev/ --exclude=/aot/ --exclude=/backgrounds/ site/ "$backup/"
tools/deploy.sh aot
python3 tools/performance-history.py --public site/launcher/performance.json
curl --fail --silent http://127.0.0.1:5080/api/xc/health
printf '\nPacing1 deployed successfully %s\n' "$(date -u +%FT%TZ)"
