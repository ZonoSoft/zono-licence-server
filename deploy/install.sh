#!/usr/bin/env bash
# Zono licence server — install or update on Ubuntu (run as root):
#   curl -fsSL https://raw.githubusercontent.com/ZonoSoft/zono-licence-server/main/deploy/install.sh | bash
# Optional: DOMAIN=licence.example.com before "bash" to use another address.
set -euo pipefail
DOMAIN="${DOMAIN:-licence.zonosoft.co.uk}"
REPO="https://github.com/ZonoSoft/zono-licence-server.git"
APP=/opt/zono-licence
DATA=/var/lib/zono-licence
ETC=/etc/zono-licence

[ "$(id -u)" = 0 ] || { echo "Run this as root (sudo -i first)."; exit 1; }
echo "== Zono licence server for $DOMAIN"

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
/opt/dotnet/dotnet publish "$APP/src/src/ZonoLicence" -c Release -r linux-x64 --self-contained true -o "$APP/app.new" -v quiet
rm -rf "$APP/app.old"; [ -d "$APP/app" ] && mv "$APP/app" "$APP/app.old"; mv "$APP/app.new" "$APP/app"

# A user for the service; its data and the admin key
id zonolic >/dev/null 2>&1 || useradd --system --home "$DATA" --shell /usr/sbin/nologin zonolic
install -d -m 0750 -o zonolic -g zonolic "$DATA"
install -d -m 0750 -o root -g zonolic "$ETC"
if [ ! -s "$ETC/admin.key" ]; then
  echo "zono-admin-$(openssl rand -base64 24 | tr '+/' '-_' | tr -d '=')" > "$ETC/admin.key"
fi
chown root:zonolic "$ETC/admin.key"; chmod 0640 "$ETC/admin.key"
# Import key: new app builds are encrypted to it before they're published (deploy/import-downloads.sh)
if [ ! -s "$ETC/import.key" ]; then
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096 -out "$ETC/import.key" 2>/dev/null
fi
chmod 0600 "$ETC/import.key"
openssl pkey -in "$ETC/import.key" -pubout -out "$DATA/import-public.pem"
chown zonolic:zonolic "$DATA/import-public.pem"; chmod 0644 "$DATA/import-public.pem"

cat > /etc/systemd/system/zono-licence.service <<UNIT
[Unit]
Description=Zono licence server
After=network.target

[Service]
User=zonolic
Group=zonolic
Environment=ZONO_DATA=$DATA
Environment=ZONO_ADMIN_KEY_FILE=$ETC/admin.key
Environment=ZONO_URLS=http://127.0.0.1:5080
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart=$APP/app/zono-licence
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
cat > /etc/cron.daily/zono-licence-backup <<'BACKUP'
#!/bin/sh
mkdir -p /var/backups/zono-licence
sqlite3 /var/lib/zono-licence/licences.db ".backup /var/backups/zono-licence/licences-$(date +%F).db"
cp -n /var/lib/zono-licence/signing-key.pem /var/backups/zono-licence/signing-key.pem 2>/dev/null || true
cp -n /var/lib/zono-licence/secret.key /var/backups/zono-licence/secret.key 2>/dev/null || true
chmod 600 /var/backups/zono-licence/*
find /var/backups/zono-licence -name 'licences-*.db' -mtime +30 -delete
BACKUP
chmod 755 /etc/cron.daily/zono-licence-backup

# IP addresses and free-trial device records are never kept: clear any in older backups too
for f in /var/backups/zono-licence/licences-*.db; do
  [ -f "$f" ] || continue
  if [ "$(sqlite3 "$f" "SELECT count(*) FROM events WHERE ip <> '';" 2>/dev/null || echo 0)" != "0" ]; then
    sqlite3 "$f" "UPDATE events SET ip = '' WHERE ip <> ''; VACUUM;" && echo "== Cleared IP addresses from $(basename "$f")"
  fi
  if [ "$(sqlite3 "$f" "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'machines';" 2>/dev/null || echo 0)" != "0" ]; then
    sqlite3 "$f" "DROP TABLE machines; VACUUM;" && echo "== Removed trial device records from $(basename "$f")"
  fi
done

# Updates itself: every 15 minutes, if the code on GitHub has changed (or the web addresses' DNS has), install again
install -m 0755 /dev/stdin /usr/local/sbin/zono-licence-update <<'UPDATE'
#!/bin/bash
set -euo pipefail
SRC=/opt/zono-licence/src
git -C "$SRC" fetch -q origin main
CHANGED=0
[ "$(git -C "$SRC" rev-parse HEAD)" != "$(git -C "$SRC" rev-parse origin/main)" ] && CHANGED=1
SERVER_IP=$(hostname -I | awk '{print $1}')
for d in zonosoft.co.uk www.zonosoft.co.uk; do
  if getent ahostsv4 "$d" | awk '{print $1}' | grep -qx "$SERVER_IP" && ! grep -q "^$d {" /etc/caddy/Caddyfile; then CHANGED=1; fi
done
if [ "$CHANGED" = 1 ]; then
  git -C "$SRC" reset -q --hard origin/main
  bash "$SRC/deploy/install.sh" >> /var/log/zono-licence-update.log 2>&1
fi
# New app installers for the website
bash "$SRC/deploy/import-downloads.sh" >> /var/log/zono-licence-update.log 2>&1 || true
UPDATE
cat > /etc/systemd/system/zono-licence-update.service <<'UNIT'
[Unit]
Description=Update the Zono licence server from GitHub
[Service]
Type=oneshot
ExecStart=/usr/local/sbin/zono-licence-update
UNIT
cat > /etc/systemd/system/zono-licence-update.timer <<'UNIT'
[Unit]
Description=Check for Zono licence server updates every 15 minutes
[Timer]
OnBootSec=5min
OnUnitActiveSec=15min
[Install]
WantedBy=timers.target
UNIT

systemctl daemon-reload
systemctl enable -q --now zono-licence-update.timer
# Bring in any new app installers now (the timer also checks every 15 minutes)
bash "$(dirname "$0")/import-downloads.sh" || true
systemctl enable -q zono-licence caddy
systemctl restart zono-licence
systemctl reload caddy 2>/dev/null || systemctl restart caddy
sleep 3

echo
if curl -fsS http://127.0.0.1:5080/api/v1/health >/dev/null; then echo "== Licence server is running."; else echo "!! The licence server didn't start: journalctl -u zono-licence -n 50"; fi
echo "== Licence server: https://$DOMAIN  (admin page: https://$DOMAIN/admin)   Website: https://$WEB"
echo "== Updates itself from GitHub every 15 minutes (log: /var/log/zono-licence-update.log)"
echo "== Admin key:   sudo cat $ETC/admin.key"
echo "== Public key for the apps:"
curl -fsS http://127.0.0.1:5080/api/v1/public-key || true
