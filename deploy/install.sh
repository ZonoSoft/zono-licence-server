#!/usr/bin/env bash
# ZonoSoft licence server — install or update on Ubuntu (run as root):
#   curl -fsSL https://raw.githubusercontent.com/ZonoSoft/zonosoft-licence-server/main/deploy/install.sh | bash
# Optional: DOMAIN=licence.example.com before "bash" to use another address.
set -euo pipefail
DOMAIN="${DOMAIN:-licence.zonosoft.co.uk}"
REPO="https://github.com/ZonoSoft/zonosoft-licence-server.git"
APP=/opt/zonosoft-licence
DATA=/var/lib/zonosoft-licence
ETC=/etc/zonosoft-licence
SVC=zonosoft-licence
RUNAS=zonosoft
BACKUPS=/var/backups/zonosoft-licence

[ "$(id -u)" = 0 ] || { echo "Run this as root (sudo -i first)."; exit 1; }
echo "== ZonoSoft licence server for $DOMAIN"

export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get install -y -q git curl ca-certificates gnupg sqlite3 ufw debian-keyring debian-archive-keyring apt-transport-https unattended-upgrades

# Security updates install themselves
dpkg-reconfigure -f noninteractive unattended-upgrades || true

# Firewall: SSH, web (for HTTPS certificates) and HTTPS only
ufw allow OpenSSH >/dev/null
ufw allow 80/tcp >/dev/null
ufw allow 443/tcp >/dev/null
ufw --force enable >/dev/null

# Caddy (web server with automatic HTTPS certificates)
if ! command -v caddy >/dev/null; then
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor --yes -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
  curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' > /etc/apt/sources.list.d/caddy-stable.list
  apt-get update -q
  apt-get install -y -q caddy
fi

# .NET SDK (only to build the server)
if [ ! -x /opt/dotnet/dotnet ]; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /opt/dotnet
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# The code
if [ -d "$APP/src/.git" ]; then git -C "$APP/src" fetch -q origin main && git -C "$APP/src" reset -q --hard origin/main; else rm -rf "$APP/src"; git clone -q "$REPO" "$APP/src"; fi
install -d "$APP"
/opt/dotnet/dotnet publish "$APP/src/src/ZonoSoft.Licence" -c Release -r linux-x64 --self-contained true -o "$APP/app.new" -v quiet
rm -rf "$APP/app.old"; [ -d "$APP/app" ] && mv "$APP/app" "$APP/app.old"; mv "$APP/app.new" "$APP/app"

# One-time move from the server's previous names (zono-licence). The data is copied, the new service is started, and
# only once it answers are the old service, user and folders removed. If it doesn't answer, the old one is started again.
MOVED=0
if [ -d /var/lib/zono-licence ] && [ ! -d "$DATA" ]; then
  echo "== Moving the licence server to its new name"
  systemctl disable --now zono-licence-update.timer 2>/dev/null || true
  systemctl stop zono-licence 2>/dev/null || true
  cp -a /var/lib/zono-licence "$DATA"
  cp -a /etc/zono-licence "$ETC"
  [ -d /var/backups/zono-licence ] && [ ! -d "$BACKUPS" ] && cp -a /var/backups/zono-licence "$BACKUPS"
  MOVED=1
fi
STARTED=0
undo_move() {
  trap - EXIT
  echo "!! The renamed server didn't start: going back to the previous one"
  systemctl disable --now "$SVC" 2>/dev/null || true
  rm -f "/etc/systemd/system/$SVC.service" "/etc/systemd/system/$SVC-update.service" "/etc/systemd/system/$SVC-update.timer" \
        "/usr/local/sbin/$SVC-update" "/etc/cron.daily/$SVC-backup"
  systemctl daemon-reload
  rm -rf "$DATA" "$ETC"
  systemctl start zono-licence || true
  systemctl enable --now zono-licence-update.timer || true
  exit 1
}
# Anything going wrong before the renamed server answers puts the previous one back
if [ "$MOVED" = 1 ]; then trap '[ "$STARTED" = 1 ] || undo_move' EXIT; fi

# A user for the service; its data and the admin key
id "$RUNAS" >/dev/null 2>&1 || useradd --system --home "$DATA" --shell /usr/sbin/nologin "$RUNAS"
install -d -m 0750 -o "$RUNAS" -g "$RUNAS" "$DATA"
chown -R "$RUNAS:$RUNAS" "$DATA"
install -d -m 0750 -o root -g "$RUNAS" "$ETC"
if [ ! -s "$ETC/admin.key" ]; then
  echo "zonosoft-admin-$(openssl rand -base64 24 | tr '+/' '-_' | tr -d '=')" > "$ETC/admin.key"
fi
chown root:"$RUNAS" "$ETC/admin.key"; chmod 0640 "$ETC/admin.key"
# Import key: new app builds are encrypted to it before they're published (deploy/import-downloads.sh)
if [ ! -s "$ETC/import.key" ]; then
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096 -out "$ETC/import.key" 2>/dev/null
fi
chmod 0600 "$ETC/import.key"
openssl pkey -in "$ETC/import.key" -pubout -out "$DATA/import-public.pem"
chown "$RUNAS:$RUNAS" "$DATA/import-public.pem"; chmod 0644 "$DATA/import-public.pem"

# Only Shifenza builds are offered (the previous apps are discontinued)
find "$DATA/downloads" -maxdepth 1 -name 'Zono*' -delete 2>/dev/null || true

cat > "/etc/systemd/system/$SVC.service" <<UNIT
[Unit]
Description=ZonoSoft licence server
After=network.target

[Service]
User=$RUNAS
Group=$RUNAS
Environment=ZONOSOFT_DATA=$DATA
Environment=ZONOSOFT_ADMIN_KEY_FILE=$ETC/admin.key
Environment=ZONOSOFT_URLS=http://127.0.0.1:5080
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=$APP/app/zonosoft-licence
Restart=always
RestartSec=3
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=$DATA
ReadOnlyPaths=$ETC

[Install]
WantedBy=multi-user.target
UNIT

# The website (zonosoft.co.uk) is served from this server once its DNS points here
rm -rf "$APP/site.new"; cp -r "$APP/src/site" "$APP/site.new"; chmod -R a+rX "$APP/site.new"
rm -rf "$APP/site.old"; [ -d "$APP/site" ] && mv "$APP/site" "$APP/site.old"; mv "$APP/site.new" "$APP/site"

SERVER_IP=$(hostname -I | awk '{print $1}')
points_here() { getent ahostsv4 "$1" | awk '{print $1}' | grep -qx "$SERVER_IP"; }
WEB="${WEB_DOMAIN:-zonosoft.co.uk}"

cat > /etc/caddy/Caddyfile.new <<CADDY
$DOMAIN {
    encode gzip
    reverse_proxy 127.0.0.1:5080
}
CADDY
if points_here "$WEB"; then
cat >> /etc/caddy/Caddyfile.new <<CADDY

$WEB {
    encode zstd gzip
    header {
        Strict-Transport-Security "max-age=31536000"
        X-Content-Type-Options "nosniff"
        Referrer-Policy "strict-origin-when-cross-origin"
        X-Frame-Options "DENY"
        Permissions-Policy "camera=(), microphone=(), geolocation=()"
    }
    # The app's pages moved from /workforce to /shifenza
    redir /workforce /shifenza permanent
    handle_path /workforce/* {
        redir * /shifenza{uri} permanent
    }
    @app path /portal /portal/* /api/* /downloads/*
    handle @app {
        reverse_proxy 127.0.0.1:5080
    }
    handle {
        root * $APP/site
        try_files {path} {path}.html {path}/index.html
        header /fonts/* Cache-Control "public, max-age=2592000"
        header /img/* Cache-Control "public, max-age=86400"
        file_server
    }
    handle_errors {
        root * $APP/site
        rewrite * /404.html
        file_server
    }
}
CADDY
fi
if points_here "www.$WEB"; then
cat >> /etc/caddy/Caddyfile.new <<CADDY

www.$WEB {
    redir https://$WEB{uri} permanent
}
CADDY
fi
# Only use the new web server settings if they're valid (a mistake must never take the licence server down)
if caddy validate --config /etc/caddy/Caddyfile.new --adapter caddyfile >/dev/null 2>&1; then
  mv /etc/caddy/Caddyfile.new /etc/caddy/Caddyfile
else
  echo "!! New web server settings weren't valid; keeping the previous ones."
  caddy validate --config /etc/caddy/Caddyfile.new --adapter caddyfile || true
  rm -f /etc/caddy/Caddyfile.new
fi
echo "== Web addresses: $(grep -E '^[a-z].* \{$' /etc/caddy/Caddyfile | tr -d '{' | xargs)"

# Daily backup of the licence database (kept 30 days) and the signing key
cat > "/etc/cron.daily/$SVC-backup" <<'BACKUP'
#!/bin/sh
B=/var/backups/zonosoft-licence
D=/var/lib/zonosoft-licence
mkdir -p -m 700 "$B"
sqlite3 "$D/licences.db" ".backup $B/licences-$(date +%F).db"
cp -n "$D/signing-key.pem" "$B/signing-key.pem" 2>/dev/null || true
cp -n "$D/secret.key" "$B/secret.key" 2>/dev/null || true
[ -d "$D/vault" ] && { mkdir -p -m 700 "$B/vault"; cp "$D"/vault/*.vault "$B/vault/" 2>/dev/null || true; chmod 600 "$B"/vault/* 2>/dev/null || true; }
chmod 600 "$B"/*.db "$B"/*.pem "$B"/*.key "$B"/*.tar.gz 2>/dev/null || true
find "$B" -maxdepth 1 \( -name 'licences-*.db' -o -name 'previous-*.tar.gz' \) -mtime +30 -delete
BACKUP
chmod 755 "/etc/cron.daily/$SVC-backup"

# IP addresses and free-trial device records are never kept: clear any in older backups too
for f in "$BACKUPS"/licences-*.db; do
  [ -f "$f" ] || continue
  if [ "$(sqlite3 "$f" "SELECT count(*) FROM events WHERE ip <> '';" 2>/dev/null || echo 0)" != "0" ]; then
    sqlite3 "$f" "UPDATE events SET ip = '' WHERE ip <> ''; VACUUM;" && echo "== Cleared IP addresses from $(basename "$f")"
  fi
  if [ "$(sqlite3 "$f" "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'machines';" 2>/dev/null || echo 0)" != "0" ]; then
    sqlite3 "$f" "DROP TABLE machines; VACUUM;" && echo "== Removed trial device records from $(basename "$f")"
  fi
  # Only sales and admin actions are kept: remove activity records (activations, checks, sign-ins, downloads)
  ACT="'activated','activate-failed','deactivated','released','portal-login','portal-login-failed','download','update','hello'"
  if [ "$(sqlite3 "$f" "SELECT count(*) FROM events WHERE what IN ($ACT);" 2>/dev/null || echo 0)" != "0" ]; then
    sqlite3 "$f" "DELETE FROM events WHERE what IN ($ACT); VACUUM;" && echo "== Removed activity records from $(basename "$f")"
  fi
done

# Updates itself: every 15 minutes, if the code on GitHub has changed (or the web addresses' DNS has), install again
install -m 0755 /dev/stdin "/usr/local/sbin/$SVC-update" <<'UPDATE'
#!/bin/bash
set -euo pipefail
SRC=/opt/zonosoft-licence/src
LOG=/var/log/zonosoft-licence-update.log
git -C "$SRC" fetch -q origin main
CHANGED=0
[ "$(git -C "$SRC" rev-parse HEAD)" != "$(git -C "$SRC" rev-parse origin/main)" ] && CHANGED=1
SERVER_IP=$(hostname -I | awk '{print $1}')
for d in zonosoft.co.uk www.zonosoft.co.uk; do
  if getent ahostsv4 "$d" | awk '{print $1}' | grep -qx "$SERVER_IP" && ! grep -q "^$d {" /etc/caddy/Caddyfile; then CHANGED=1; fi
done
if [ "$CHANGED" = 1 ]; then
  git -C "$SRC" reset -q --hard origin/main
  bash "$SRC/deploy/install.sh" >> "$LOG" 2>&1
fi
# New app installers for the website
bash "$SRC/deploy/import-downloads.sh" >> "$LOG" 2>&1 || true
UPDATE
cat > "/etc/systemd/system/$SVC-update.service" <<UNIT
[Unit]
Description=Update the ZonoSoft licence server from GitHub
[Service]
Type=oneshot
ExecStart=/usr/local/sbin/$SVC-update
UNIT
cat > "/etc/systemd/system/$SVC-update.timer" <<'UNIT'
[Unit]
Description=Check for ZonoSoft licence server updates every 15 minutes
[Timer]
OnBootSec=5min
OnUnitActiveSec=15min
[Install]
WantedBy=timers.target
UNIT

systemctl daemon-reload
# Bring in any new app installers now (the timer also checks every 15 minutes)
bash "$APP/src/deploy/import-downloads.sh" || true
systemctl enable -q "$SVC" caddy
systemctl restart "$SVC"
systemctl reload caddy 2>/dev/null || systemctl restart caddy

for _ in $(seq 1 20); do
  if curl -fsS http://127.0.0.1:5080/api/v1/health >/dev/null 2>&1; then STARTED=1; break; fi
  sleep 3
done
if [ "$STARTED" = 0 ] && [ "$MOVED" = 1 ]; then undo_move; fi
trap - EXIT
systemctl enable -q --now "$SVC-update.timer"

# The move worked: the previous service, user and folders are removed (a copy of the old folders is kept with the backups
# for 30 days, readable by root only)
if [ "$STARTED" = 1 ] && { [ -d /var/lib/zono-licence ] || [ -f /etc/systemd/system/zono-licence.service ]; }; then
  systemctl disable --now zono-licence-update.timer 2>/dev/null || true
  systemctl disable zono-licence 2>/dev/null || true
  rm -f /etc/systemd/system/zono-licence.service /etc/systemd/system/zono-licence-update.service \
        /etc/systemd/system/zono-licence-update.timer /usr/local/sbin/zono-licence-update /etc/cron.daily/zono-licence-backup
  systemctl daemon-reload
  install -d -m 0700 "$BACKUPS"
  tar -czf "$BACKUPS/previous-$(date +%F).tar.gz" --ignore-failed-read /var/lib/zono-licence /etc/zono-licence 2>/dev/null || true
  chmod 600 "$BACKUPS"/previous-*.tar.gz 2>/dev/null || true
  rm -rf /var/lib/zono-licence /etc/zono-licence /var/backups/zono-licence
  [ -f /var/log/zono-licence-update.log ] && cat /var/log/zono-licence-update.log >> /var/log/zonosoft-licence-update.log && rm -f /var/log/zono-licence-update.log
  id zonolic >/dev/null 2>&1 && userdel zonolic 2>/dev/null || true
  # The previous code folder last: this script may have been started from it
  rm -rf /opt/zono-licence
  echo "== The licence server now runs as $SVC; the previous service has been removed"
fi

echo
if [ "$STARTED" = 1 ]; then echo "== Licence server is running."; else echo "!! The licence server didn't start: journalctl -u $SVC -n 50"; fi
echo "== Licence server: https://$DOMAIN  (admin page: https://$DOMAIN/admin)   Website: https://$WEB"
echo "== Updates itself from GitHub every 15 minutes (log: /var/log/zonosoft-licence-update.log)"
echo "== Admin key:   sudo cat $ETC/admin.key"
echo "== Public key for the apps:"
curl -fsS http://127.0.0.1:5080/api/v1/public-key || true
