#!/bin/bash
# Independent completion runner; waits for the existing build without interrupting it.
set -euo pipefail
cd /var/www/osu
export HOME=/home/admin
export DOTNET_ROOT=/home/admin/.dotnet
export PATH=/home/admin/.dotnet:/usr/local/bin:/usr/bin:/bin
exec >> /var/www/osu/out/telemetry-finish.log 2>&1
trap 'rc=$?; echo "Finished with status $rc at $(date -u +%FT%TZ)"' EXIT
echo "Waiting for existing AOT build at $(date -u +%FT%TZ)"
while kill -0 4015185 2>/dev/null; do sleep 10; done
if ! grep -q '^aotpub done in ' /tmp/xc-tm-aot.log; then
  echo 'Existing build stopped before completion; resume incremental publish.'
  tools/aotpub.sh
fi
# Copy the final reviewed JS edits (the build may have copied static assets earlier).
cp osu/osu.Web/wwwroot/js/xc-{telemetry,launcher,i18n,log,boot,ui}.js out/aot/wwwroot/js/
cp osu/osu.Web/wwwroot/main.js out/aot/wwwroot/main.js
python3 tools/stamp.py out/aot/wwwroot
node tools/telemetry-test.mjs
python3 tools/serve.py out/aot/wwwroot 18813 &
serve_pid=$!
trap 'rc=$?; kill "$serve_pid" 2>/dev/null || true; echo "Finished with status $rc at $(date -u +%FT%TZ)"' EXIT
sleep 2
kill -0 "$serve_pid"
node tools/telemetry-game-test.mjs http://127.0.0.1:18813
mkdir -p backups/telemetry-site-20261110
rsync -a --exclude=/dev/ --exclude=/aot/ --exclude=/backgrounds/ site/ backups/telemetry-site-20261110/
tools/deploy.sh aot
curl --fail --silent http://127.0.0.1:5080/api/xc/health
printf '\nTelemetry AOT deployed successfully at %s\n' "$(date -u +%FT%TZ)"
