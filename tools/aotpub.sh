#!/bin/bash
# xc-osu! — mixed-AOT release publish. Output: out/aot/wwwroot
# Parallel AOT when the machine has >= 16 GB RAM (one assembly at a time below that).
ROOT=$(cd "$(dirname "$0")/.." && pwd)
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet} PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1
cd "$ROOT/osu/osu.Web"
start=$(date +%s)
mkdir -p "$ROOT/out"
log="$ROOT/out/aotpub.log"
memgb=$(awk '/MemTotal/ {print int($2/1048576)}' /proc/meminfo)
if [ "$memgb" -ge 15 ]; then par="-p:DisableParallelAot=false"; else par="-p:DisableParallelAot=true -m:1"; fi
echo "aotpub: ${memgb} GB RAM, $(nproc) cores ($par)"
python3 "$ROOT/tools/portstats.py" "$ROOT/osu/osu.Web/wwwroot/launcher/build.json" || true
dotnet publish -c Release -o "$ROOT/out/aot" -p:XcAot=true -p:CompressionEnabled=false -p:WasmEmitSymbolMap=true $par "$@" > "$log" 2>&1
rc=$?
grep -E " error |error :|xc-osu! AOT|AOT'ing|Build succeeded" "$log" | sed 's/\[\/.*//' | sort -u | head -40
if [ $rc -ne 0 ]; then echo "aotpub FAILED (rc=$rc) in $(( $(date +%s) - start ))s"; exit 1; fi
cp "$ROOT"/osu/osu.Web/wwwroot/manifest.webmanifest "$ROOT"/osu/osu.Web/wwwroot/icon* "$ROOT/out/aot/wwwroot/" 2>/dev/null
python3 "$ROOT/tools/stamp.py" "$ROOT/out/aot/wwwroot"
echo "aotpub done in $(( $(date +%s) - start ))s"
