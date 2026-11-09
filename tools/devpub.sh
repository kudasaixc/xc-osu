#!/bin/bash
# xc-osu! — dev publish (interpreter, no trimming, no compression). Output: out/dev/wwwroot
ROOT=$(cd "$(dirname "$0")/.." && pwd)
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet} PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1
cd "$ROOT/osu/osu.Web"
start=$(date +%s)
log=$(mktemp)
dotnet publish -c Release -o "$ROOT/out/dev" -p:PublishTrimmed=false -p:CompressionEnabled=false -p:WasmEmitSymbolMap=true > "$log" 2>&1
rc=$?
grep -E " error |error :|Build succeeded|failed" "$log" | sed 's/\[\/.*//' | sort -u | head -40
rm -f "$log"
if [ $rc -ne 0 ]; then echo "devpub FAILED (rc=$rc) in $(( $(date +%s) - start ))s"; exit 1; fi
cp "$ROOT"/osu/osu.Web/wwwroot/manifest.webmanifest "$ROOT"/osu/osu.Web/wwwroot/icon* "$ROOT/out/dev/wwwroot/" 2>/dev/null
python3 "$ROOT/tools/stamp.py" "$ROOT/out/dev/wwwroot"
echo "devpub done in $(( $(date +%s) - start ))s"
