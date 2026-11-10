#!/usr/bin/env bash
# Read-only release status for xc-osu!; no network calls, tests or mutations.
set -euo pipefail
ROOT=${XC_OSU_ROOT:-/var/www/osu}
REPO=${XC_OSU_REPO:-/home/admin/xc-osu-repo}
echo "xc-osu! operations snapshot — $(date -u +%Y-%m-%dT%H:%M:%SZ)"
if [[ ! -d "$ROOT/site" ]]; then echo "ERROR: site directory missing"; exit 2; fi
df -h "$ROOT" | awk 'NR==2{print "Filesystem: "$1", used "$5", free "$4}'
used=$(df -P "$ROOT" | awk 'NR==2{gsub("%","",$5);print $5}')
if [[ "$used" =~ ^[0-9]+$ ]] && (( used >= 90 )); then
  echo "WARNING: disk usage is at or above 90%"
fi
if [[ -f "$ROOT/site/index.html" && -f "$ROOT/out/aot/wwwroot/index.html" ]]; then
  if cmp -s "$ROOT/site/index.html" "$ROOT/out/aot/wwwroot/index.html"; then
    echo "Current site HTML matches latest AOT build"
  else
    echo "NOTICE: site HTML differs from latest AOT build"
  fi
fi
for file in "$ROOT/site"/_framework/dotnet.native.*.wasm; do
  [[ -f "$file" ]] || continue
  printf 'Live AOT engine: %s (%s bytes)\n' "$(basename "$file")" "$(stat -c %s "$file")"
done
if [[ -f "$ROOT/backups/last-xc-osu-deployment.txt" ]]; then
  echo "Last guarded release:"
  sed -n '1,3p' "$ROOT/backups/last-xc-osu-deployment.txt"
else
  echo "Last guarded release: not yet recorded"
fi
if [[ -f "$ROOT/out/performance/history.json" ]]; then
  echo "Performance history updated: $(stat -c %y "$ROOT/out/performance/history.json")"
fi
if [[ -d "$REPO/.git" ]]; then
  echo "GitHub source commit: $(git -c safe.directory="$REPO" -C "$REPO" rev-parse --short HEAD)"
  changes=$(git -c safe.directory="$REPO" -C "$REPO" status --porcelain | wc -l)
  echo "Uncommitted files in canonical checkout: $changes"
fi
if [[ -f "$ROOT/tools/deploy.sh" && -f "$REPO/tools/deploy.sh" ]]; then
  if cmp -s "$ROOT/tools/deploy.sh" "$REPO/tools/deploy.sh"; then
    echo "Runtime deploy script matches canonical Git checkout"
  else
    echo "WARNING: runtime deploy script differs from GitHub source"
  fi
fi
