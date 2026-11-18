#!/bin/bash
# xc-osu!: publish a test build to https://osu.xcsoftworks.com/wasmtest/ (behind the forks' password, see the Apache
# vhost). Never touches the live root or the other forks; the production deploy scripts exclude /wasmtest/.
# usage: tools/deploy-wasmtest.sh [build dir, default out/wasmtest-c]
# The build loads its engine files from this site (no CDN): .br copies are made for Apache's pre-compressed rewrite.
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
SRC=${1:-$ROOT/out/wasmtest-c}
[ -f "$SRC/wwwroot/index.html" ] && [ -d "$SRC/wwwroot/_framework" ] || { echo "no complete build at $SRC" >&2; exit 2; }
DST="$ROOT/site/wasmtest"
mkdir -p "$DST"
# brotli copies of what is worth compressing (engine wasm, scripts, the resource pack); kept next to the build
command -v brotli >/dev/null || { echo "brotli CLI missing (apt install brotli)" >&2; exit 2; }
find "$SRC/wwwroot" -type f \( -name '*.wasm' -o -name '*.js' -o -name '*.mjs' -o -name '*.xcpack' -o -name '*.json' -o -name '*.dat' \) -size +8k | while read -r f; do
  [ -f "$f.br" ] && [ "$f.br" -nt "$f" ] && continue
  brotli -q 9 -f -o "$f.br" "$f"
done
# files copied from the SDK packs (icudt_*.dat, dotnet.runtime.js) are 0760: Apache got 403 on them
rsync -a --delete --chmod=D2775,F664 "$SRC/wwwroot/" "$DST/"
echo "deployed $SRC to https://osu.xcsoftworks.com/wasmtest/ ($(du -sh "$DST" | cut -f1))"
