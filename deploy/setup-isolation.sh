#!/bin/bash
# Prepares the dedicated account the xc-osu! server runs under. Idempotent; run as root:
#   sudo deploy/setup-isolation.sh            account + runtime + data permissions (service untouched)
#   sudo deploy/setup-isolation.sh --runtime  only refresh /opt/xc-osu/dotnet (after a .NET runtime update)
#   sudo deploy/setup-isolation.sh --switch   everything, then install the unit and restart the service
set -euo pipefail

SVC_USER=xc-osu
ADMIN_USER=${ADMIN_USER:-admin}
SDK=${SDK:-/home/$ADMIN_USER/.dotnet}
RUNTIME=/opt/xc-osu/dotnet
DATA=${DATA:-/var/lib/xc-osu}
HERE=$(cd "$(dirname "$0")" && pwd)

[ "$(id -u)" = 0 ] || { echo "run as root" >&2; exit 1; }

sync_runtime() {
    # runtime only (muxer, hostfxr, shared frameworks): no SDK, root-owned so the service cannot alter it.
    install -d -o root -g root -m 755 /opt/xc-osu "$RUNTIME"
    rsync -a --delete --chown=root:root --chmod=D755,F644 "$SDK/host" "$SDK/shared" "$RUNTIME/"
    install -o root -g root -m 755 "$SDK/dotnet" "$RUNTIME/dotnet"
    echo "runtime: $(ls "$RUNTIME/shared/Microsoft.NETCore.App" | tr '\n' ' ')"
}

if [ "${1:-}" = --runtime ]; then sync_runtime; exit 0; fi

getent passwd "$SVC_USER" >/dev/null || useradd --system --user-group --home-dir "$DATA/.home" --no-create-home --shell /usr/sbin/nologin "$SVC_USER"
# admin keeps working on the data (backups, telemetry reports) through the group, never the other way round.
usermod -aG "$SVC_USER" "$ADMIN_USER"

sync_runtime

# PostgreSQL: the service connects over the local Unix socket, authenticated by peer credentials, so its role must
# carry the account name and the database must belong to it (PostgreSQL 15+ resolves "create in schema public" from
# the database owner). Existing data is never dropped here.
if command -v psql >/dev/null; then
    runuser -u postgres -- psql -Atc "SELECT 1 FROM pg_roles WHERE rolname = '$SVC_USER'" | grep -q 1 \
        || runuser -u postgres -- createuser "$SVC_USER"
    if ! runuser -u postgres -- psql -Atc "SELECT 1 FROM pg_database WHERE datname = 'xc_osu'" | grep -q 1; then
        runuser -u postgres -- createdb -O "$SVC_USER" xc_osu
    else
        runuser -u postgres -- psql -Atc "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'xc_osu'" | grep -qx "$SVC_USER" \
            || runuser -u postgres -- psql -c "ALTER DATABASE xc_osu OWNER TO \"$SVC_USER\""
    fi
fi

if [ "${1:-}" = --switch ]; then
    install -o root -g root -m 644 "$HERE/xc-osu-server.service" /etc/systemd/system/xc-osu-server.service
    systemctl daemon-reload
    systemctl stop xc-osu-server
fi

# data directory (uploads, team flags, cached beatmaps, replay archives): owned by the service, group-writable so
# admin backups keep working, closed to everyone else.
if [ "${1:-}" = --switch ] || [ "$(stat -c %U "$DATA" 2>/dev/null)" = "$SVC_USER" ]; then
    install -d -o "$SVC_USER" -g "$SVC_USER" -m 2770 "$DATA" "$DATA/.home"
    chown -R "$SVC_USER:$SVC_USER" "$DATA"
    chmod -R u+rwX,g+rwX,o-rwx "$DATA"
    find "$DATA" -type d -exec chmod g+s {} +
    # keep the existing data-protection key ring, should the server ever persist one
    if [ -d "/home/$ADMIN_USER/.aspnet/DataProtection-Keys" ] && [ ! -d "$DATA/.home/.aspnet" ]; then
        install -d -o "$SVC_USER" -g "$SVC_USER" -m 700 "$DATA/.home/.aspnet"
        cp -r "/home/$ADMIN_USER/.aspnet/DataProtection-Keys" "$DATA/.home/.aspnet/"
        chown -R "$SVC_USER:$SVC_USER" "$DATA/.home/.aspnet"; chmod -R go-rwx "$DATA/.home/.aspnet"
    fi
else
    echo "data: still owned by $(stat -c %U "$DATA" 2>/dev/null || echo '?'): left alone until --switch"
fi

if [ "${1:-}" = --switch ]; then
    systemctl start xc-osu-server
    sleep 4
    systemctl --no-pager --lines=0 status xc-osu-server
fi
