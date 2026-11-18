#!/bin/bash
# xc-osu! mixed-AOT release publish. Output: out/aot/wwwroot
# usage: tools/aotpub.sh [extra msbuild args]      env: XC_AOT_JOBS=n (parallel compiles), XC_SYMBOLS=1 (DWARF + names),
#   XC_AOT_OUT=<dir> (output folder instead of out/aot, e.g. a test build that must not become the next deploy),
#   XC_RESPACK=0 (keep osu.Game.Resources inside its assembly instead of the separate resource pack)
# The AOT steps (IL -> bitcode -> wasm objects) run in parallel. Each LLVM job on a big assembly needs ~2 GB, so the job
# count is capped by RAM: all cores from 15 GB up, 3 jobs below that (override with XC_AOT_JOBS).
# UseSharedCompilation=false: otherwise the Roslyn server keeps ~1.5 GB resident during the LLVM steps.
ROOT=$(cd "$(dirname "$0")/.." && pwd)
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet} PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$ROOT/osu/osu.Web"
start=$(date +%s)
mkdir -p "$ROOT/out"
OUT=${XC_AOT_OUT:-$ROOT/out/aot}
log="$ROOT/out/aotpub.log"; [ -n "$XC_AOT_OUT" ] && log="$OUT.log"
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
# resource pack (tools/respack): osu.Game.Resources' resources go to a separate file the page reads on demand; the
# trimmer removes them from the assembly through the substitutions file written here
packdir="$ROOT/osu/osu.Web/obj/aot/respack"; packargs=""
# Switching the pack on or off changes osu.Game.Resources (trimmed or not) under the AOT objects of every assembly that
# references it, and the incremental trimmer / AOT steps keep the previous copy: mono then rejects osu.Game and the
# rulesets at startup ("GUID of dependent assembly osu.Game.Resources doesn't match", appdomain.c:188). Start clean.
modefile="$ROOT/osu/osu.Web/obj/aot/.respack-mode"; mode="${XC_RESPACK:-1}"
if [ -f "$modefile" ] && [ "$(cat "$modefile")" != "$mode" ]; then
  echo "aotpub: resource pack mode changed, clearing the trimmed / AOT intermediates"
  # everything: the trimmed copies, the AOT objects, and the webcil / static web assets manifests, which otherwise keep
  # publishing the previous osu.Game.Resources under its old fingerprint
  rm -rf "$ROOT/osu/osu.Web/obj/aot/net10.0"
fi
mkdir -p "$(dirname "$modefile")"; echo "$mode" > "$modefile"
if [ "${XC_RESPACK:-1}" != "0" ]; then
  resver=$(grep -oP 'ppy.osu.Game.Resources" Version="\K[^"]+' "$ROOT/osu/osu.Game/osu.Game.csproj")
  resdll="$HOME/.nuget/packages/ppy.osu.game.resources/$resver/lib/net8.0/osu.Game.Resources.dll"
  [ -f "$ROOT/tools/respack/bin/out/respack.dll" ] || dotnet build "$ROOT/tools/respack" -c Release -o "$ROOT/tools/respack/bin/out" > /dev/null
  dotnet "$ROOT/tools/respack/bin/out/respack.dll" "$resdll" "$packdir" "$packdir/substitutions.xml" || { echo "aotpub: respack failed"; exit 1; }
  packargs="-p:XcResPackSubstitutions=$packdir/substitutions.xml"
else
  packargs="-p:XcResPack=false"
fi
python3 "$ROOT/tools/portstats.py" "$ROOT/osu/osu.Web/wwwroot/launcher/build.json" || true
pub() { dotnet publish -c Release -o "$OUT" -p:XcAot=true $packargs -p:CompressionEnabled=false -p:WasmEmitSymbolMap=true -p:UseSharedCompilation=false $par "$@" > "$log" 2>&1; }
pub "$@"
rc=$?
# The SDK only recompiles an assembly's AOT object when that assembly changes, but each object also records the
# GUIDs of the assemblies it references. When a dependency changes (e.g. ImageSharp trimmed differently), the old
# object is reused, mono rejects it at startup and the runtime dies (appdomain.c:188). So if any input is newer than
# the oldest object, drop every object and publish again. xc-osu.dll (the app itself) is referenced by no object.
aotdir="$ROOT/osu/osu.Web/obj/aot/net10.0/wasm/for-publish"
oldest=$(ls -tr "$aotdir"/*.dll.o 2>/dev/null | head -1)
if [ $rc -eq 0 ] && [ -n "$oldest" ] && [ -n "$(find "$aotdir/aot-in" -name '*.dll' ! -name 'xc-osu.dll' -newer "$oldest" | head -1)" ]; then
  echo "aotpub: AOT objects are older than their inputs, recompiling all of them"
  rm -f "$aotdir"/*.dll.bc "$aotdir"/*.dll.o
  pub "$@"
  rc=$?
fi
grep -E " error |error :|xc-osu! AOT|AOT'ing|Build succeeded|Time Elapsed" "$log" | sed 's/\[\/.*//' | sort -u | head -40
if [ $rc -ne 0 ]; then echo "aotpub FAILED (rc=$rc) in $(( $(date +%s) - start ))s: see $log"; exit 1; fi
cp "$ROOT"/osu/osu.Web/wwwroot/manifest.webmanifest "$ROOT"/osu/osu.Web/wwwroot/icon* "$OUT/wwwroot/" 2>/dev/null
if [ "${XC_RESPACK:-1}" != "0" ]; then
  packfile=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["file"])' "$packdir/osu.Game.Resources.xcpack.json")
  rm -f "$OUT"/wwwroot/_framework/osu.Game.Resources.*.xcpack*
  cp "$packdir/$packfile" "$OUT/wwwroot/_framework/" || { echo "aotpub: could not copy the resource pack"; exit 1; }
  { echo "// xc-osu!: written by tools/aotpub.sh (tools/respack) for this build"; echo "export default $(cat "$packdir/osu.Game.Resources.xcpack.json");"; } > "$OUT/wwwroot/js/xc-respack-manifest.js"
  echo "aotpub: resource pack $packfile"
fi
# drop superseded fingerprinted files from earlier publishes (they are never referenced again, only re-uploaded)
python3 "$ROOT/tools/prune-framework.py" "$OUT/wwwroot" || true
python3 "$ROOT/tools/stamp.py" "$OUT/wwwroot"
echo "aotpub done in $(( $(date +%s) - start ))s"
