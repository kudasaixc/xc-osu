#!/bin/bash
# xc-osu! — mixed-AOT release publish. Output: out/aot/wwwroot
# usage: tools/aotpub.sh [extra msbuild args]      env: XC_AOT_JOBS=n (parallel compiles), XC_SYMBOLS=1 (DWARF + names)
# The AOT steps (IL -> bitcode -> wasm objects) run in parallel. Each LLVM job on a big assembly needs ~2 GB, so the job
# count is capped by RAM: all cores from 15 GB up, 3 jobs below that (override with XC_AOT_JOBS).
# UseSharedCompilation=false: otherwise the Roslyn server keeps ~1.5 GB resident during the LLVM steps.
ROOT=$(cd "$(dirname "$0")/.." && pwd)
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet} PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$ROOT/osu/osu.Web"
start=$(date +%s)
mkdir -p "$ROOT/out"
log="$ROOT/out/aotpub.log"
memgb=$(awk '/MemTotal/ {print int($2/1048576)}' /proc/meminfo)
cores=$(nproc)
if [ -n "$XC_AOT_JOBS" ]; then jobs=$XC_AOT_JOBS
elif [ "$memgb" -ge 15 ]; then jobs=$cores
else jobs=$(( cores < 3 ? cores : 3 )); fi
# the SDK tasks size their parallel loops on Environment.ProcessorCount, which this variable overrides
export DOTNET_PROCESSOR_COUNT=$jobs
if [ "$jobs" -le 1 ]; then par="-p:DisableParallelAot=true -m:1"; else par="-p:DisableParallelAot=false -m:$jobs -nodeReuse:false"; fi
[ "$XC_SYMBOLS" = "1" ] && par="$par -p:WasmNativeDebugSymbols=true"
echo "aotpub: ${memgb} GB RAM, ${cores} cores, ${jobs} parallel AOT jobs"
python3 "$ROOT/tools/portstats.py" "$ROOT/osu/osu.Web/wwwroot/launcher/build.json" || true
dotnet publish -c Release -o "$ROOT/out/aot" -p:XcAot=true -p:CompressionEnabled=false -p:WasmEmitSymbolMap=true -p:UseSharedCompilation=false $par "$@" > "$log" 2>&1
rc=$?
grep -E " error |error :|xc-osu! AOT|AOT'ing|Build succeeded|Time Elapsed" "$log" | sed 's/\[\/.*//' | sort -u | head -40
if [ $rc -ne 0 ]; then echo "aotpub FAILED (rc=$rc) in $(( $(date +%s) - start ))s — see $log"; exit 1; fi
cp "$ROOT"/osu/osu.Web/wwwroot/manifest.webmanifest "$ROOT"/osu/osu.Web/wwwroot/icon* "$ROOT/out/aot/wwwroot/" 2>/dev/null
# drop superseded fingerprinted files from earlier publishes (they are never referenced again, only re-uploaded)
python3 "$ROOT/tools/prune-framework.py" "$ROOT/out/aot/wwwroot" || true
python3 "$ROOT/tools/stamp.py" "$ROOT/out/aot/wwwroot"
echo "aotpub done in $(( $(date +%s) - start ))s"
