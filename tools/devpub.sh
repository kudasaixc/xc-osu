#!/bin/bash
# fast dev publish of xc-osu! (no trimming, no compression)
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH DOTNET_NOLOGO=1
cd /var/www/osu/osu/osu.Web
start=$(date +%s)
log=$(mktemp)
dotnet publish -c Release -o /var/www/osu/out/dev -p:PublishTrimmed=false -p:CompressionEnabled=false -p:WasmEmitSymbolMap=true > "$log" 2>&1
rc=$?
grep -E " error |error :|Build succeeded|failed" "$log" | sed 's/\[\/var.*//' | sort -u | head -40
rm -f "$log"
if [ $rc -ne 0 ]; then echo "devpub FAILED (rc=$rc) in $(( $(date +%s) - start ))s"; exit 1; fi
echo "devpub done in $(( $(date +%s) - start ))s"
