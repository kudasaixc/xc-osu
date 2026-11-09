#!/bin/bash
# xc-osu! — deploy a build to the live site (root + /dev/). xc softworks (kudasaixc)
# usage: tools/deploy.sh [dev|aot]
#   on the VPS: copies locally. Elsewhere (e.g. a dev PC): set XC_DEPLOY_SSH=user@host to rsync over SSH.
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
src="$ROOT/out/${1:-dev}/wwwroot"
[ -f "$src/index.html" ] || { echo "no build at $src"; exit 1; }
python3 "$ROOT/tools/stamp.py" "$src"
SITE=${XC_SITE_DIR:-/var/www/osu/site}
if [ -n "$XC_DEPLOY_SSH" ]; then dst="$XC_DEPLOY_SSH:$SITE"; else dst="$SITE"; fi
rsync -az --delete "$src"/ "$dst/dev/"
rsync -az --delete --exclude=/dev/ --exclude=/aot/ --exclude=/backgrounds/ "$src"/ "$dst/"
echo "deployed ${1:-dev} build to osu.xcsoftworks.com (/ and /dev/)"
