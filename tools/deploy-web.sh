#!/usr/bin/env bash
# xc-osu! web (/web/) deploy. xc softworks (kudasaixc)
# Copies web/ to site/web/a/<content hash>/ and points site/web/index.html at it.
# Assets get a new folder whenever anything changes, so they can be cached forever; older folders are kept a week for open tabs.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/web"; dst="${XC_WEB_DST:-$root/site/web}"

for f in "$src"/js/*.js; do node --check "$f"; done
hash="$(cd "$src" && find js data fonts web.css -type f | LC_ALL=C sort | xargs sha256sum | sha256sum | cut -c1-12)"

mkdir -p "$dst/a/$hash"
rsync -a --delete "$src/js" "$src/data" "$src/fonts" "$src/web.css" "$dst/a/$hash/"
sed "s#__A__#a/$hash#g" "$src/index.html" > "$dst/index.html.new"
if grep -q '__A__' "$dst/index.html.new"; then echo "placeholder left in index.html" >&2; exit 1; fi
mv "$dst/index.html.new" "$dst/index.html"

find "$dst/a" -mindepth 1 -maxdepth 1 -type d ! -name "$hash" -mtime +7 -exec rm -rf {} +
echo "web deployed: $dst (assets a/$hash)"
