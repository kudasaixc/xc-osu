#!/usr/bin/env bash
# xc-osu! local production deployment: locking, backup, delayed rsync, rollback.
# Usage: tools/deploy-guarded.sh dev|aot
# No app tests or builds are run by this script.
set -Eeuo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
MODE=${1:-dev}
case "$MODE" in dev|aot) ;; *) echo "Usage: $0 dev|aot" >&2; exit 2;; esac
SITE=${XC_SITE_DIR:-/var/www/osu/site}
BACKUPS=${XC_BACKUP_DIR:-/var/www/osu/backups}
SRC="$ROOT/out/$MODE/wwwroot"

# Avoid a typo in the target path turning rsync --delete into a disaster.
if [[ "$SITE" != /var/www/osu/site && "${XC_ALLOW_CUSTOM_SITE:-0}" != 1 ]]; then
    echo "Refusing nonstandard site: $SITE (set XC_ALLOW_CUSTOM_SITE=1 to override)" >&2
    exit 2
fi
[[ -d "$SITE" && -f "$SITE/index.html" && -f "$SRC/index.html" && -d "$SRC/_framework" ]] || {
    echo "Missing existing site or complete $MODE build" >&2; exit 2;
}
[[ -d "$ROOT/out" && -d "$BACKUPS" ]] || { echo "Missing out/ or backups/ directory" >&2; exit 2; }
command -v flock >/dev/null && command -v rsync >/dev/null && command -v sha256sum >/dev/null || {
    echo "Missing flock, rsync or sha256sum" >&2; exit 2;
}
exec 9>"$ROOT/out/.xc-osu-deploy.lock"
flock -n 9 || { echo "Another deployment is already running" >&2; exit 75; }

# Require free room for a complete backup and rsync temporary files.
FREE_KIB=$(df -Pk "$SITE" | awk 'NR==2 {print $4}')
MIN_FREE_KIB=${XC_MIN_FREE_KIB:-1048576}
[[ "$FREE_KIB" =~ ^[0-9]+$ && "$MIN_FREE_KIB" =~ ^[0-9]+$ ]] || exit 2
(( FREE_KIB >= MIN_FREE_KIB )) || {
    echo "Insufficient disk space: ${FREE_KIB} KiB (minimum ${MIN_FREE_KIB} KiB)" >&2
    exit 28
}
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
SNAPSHOT="$BACKUPS/site-pre-$MODE-$STAMP-$$"
mkdir "$SNAPSHOT"
BACKUP_READY=0
RESTORE_ARGS=(--exclude=/dev/ --exclude=/aot/ --exclude=/backgrounds/ --exclude=/launcher/performance.json)

rollback() {
    code=${1:-$?}
    trap - ERR
    if [[ "$BACKUP_READY" == 1 ]]; then
        echo "Deployment failed (exit $code); restoring $SNAPSHOT" >&2
        rsync -a --delete-delay "${RESTORE_ARGS[@]}" "$SNAPSHOT/" "$SITE/" || {
            echo "AUTOMATIC ROLLBACK FAILED. Restore manually from: $SNAPSHOT" >&2
            exit 90
        }
        echo "Previous site files restored." >&2
    fi
    exit "$code"
}
trap 'rollback $?' ERR

rsync -a --delete "${RESTORE_ARGS[@]}" "$SITE/" "$SNAPSHOT/"
[[ -s "$SNAPSHOT/index.html" && -s "$SNAPSHOT/main.js" ]] || {
    echo "Backup incomplete" >&2; exit 22;
}
BACKUP_READY=1
echo "Backup ready: $SNAPSHOT"

python3 "$ROOT/tools/stamp.py" "$SRC"
rsync -a --delete-delay --delay-updates "${RESTORE_ARGS[@]}" "$SRC/" "$SITE/"

# Only file-integrity verification, not a runtime test.
for filename in index.html main.js; do
    a=$(sha256sum "$SRC/$filename" | cut -d' ' -f1)
    b=$(sha256sum "$SITE/$filename" | cut -d' ' -f1)
    [[ "$a" == "$b" ]] || { echo "Hash mismatch: $filename" >&2; rollback 23; }
done
printf 'mode=%s\nsnapshot=%s\ncompleted=%s\n' "$MODE" "$SNAPSHOT" "$(date -u --iso-8601=seconds)" > "$BACKUPS/last-xc-osu-deployment.txt"
trap - ERR
echo "Published xc-osu! $MODE; previous release: $SNAPSHOT"
