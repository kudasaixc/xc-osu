#!/bin/bash
# xc-osu! — deploy a build to the live site (root + /dev/). xc softworks (kudasaixc)
# usage: tools/deploy.sh [dev|aot] [test]
#   "test" only updates /aot/ (https://osu.xcsoftworks.com/aot/) and leaves the live site alone.
#   on the VPS: copies locally. Elsewhere (e.g. a dev PC): set XC_DEPLOY_SSH=user@host to rsync over SSH.
set -e
ROOT=$(cd "$(dirname "$0")/.." && pwd)
# Local live releases go through the guarded, recoverable deployment path.
if [ -z "${XC_DEPLOY_SSH:-}" ] && [ "${2:-}" != "test" ]; then
  exec /bin/bash "$ROOT/tools/deploy-guarded.sh" "${1:-dev}"
fi
src="$ROOT/out/${1:-dev}/wwwroot"
[ -f "$src/index.html" ] || { echo "no build at $src"; exit 1; }
python3 "$ROOT/tools/stamp.py" "$src"
SITE=${XC_SITE_DIR:-/var/www/osu/site}
if [ -n "$XC_DEPLOY_SSH" ]; then dst="$XC_DEPLOY_SSH:$SITE"; else dst="$SITE"; fi
if [ "$2" = "test" ]; then
  rsync -az --delete "$src"/ "$dst/aot/"
  echo "deployed ${1:-dev} build to https://osu.xcsoftworks.com/aot/ (test only)"; exit 0
fi
# /dev/ keeps the interpreter build as a fallback once AOT is live
[ "${1:-dev}" = "dev" ] && rsync -az --delete "$src"/ "$dst/dev/"
rsync -az --delete --exclude=/dev/ --exclude=/aot/ --exclude=/backgrounds/ --exclude=/web/ --exclude=/launcher/performance.json "$src"/ "$dst/"
echo "deployed ${1:-dev} build to https://osu.xcsoftworks.com/"
