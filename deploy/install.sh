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

# The customer portal is also served on the main web address once its DNS points here
SITES="$DOMAIN"
SERVER_IP=$(hostname -I | awk '{print $1}')
for d in ${PORTAL_DOMAINS:-zonosoft.co.uk www.zonosoft.co.uk}; do
  if getent ahostsv4 "$d" | awk '{print $1}' | grep -qx "$SERVER_IP"; then SITES="$SITES, $d"; fi
done
cat > /etc/caddy/Caddyfile <<CADDY
$SITES {
    encode gzip
    reverse_proxy 127.0.0.1:5080
}
CADDY
echo "== Web addresses: $SITES"

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
  if getent ahostsv4 "$d" | awk '{print $1}' | grep -qx "$SERVER_IP" && ! grep -q "$d" /etc/caddy/Caddyfile; then CHANGED=1; fi
done
if [ "$CHANGED" = 1 ]; then
  git -C "$SRC" reset -q --hard origin/main
  bash "$SRC/deploy/install.sh" >> /var/log/zono-licence-update.log 2>&1
fi
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
systemctl enable -q zono-licence caddy
systemctl restart zono-licence
systemctl reload caddy 2>/dev/null || systemctl restart caddy
sleep 3

echo
if curl -fsS http://127.0.0.1:5080/api/v1/health >/dev/null; then echo "== Licence server is running."; else echo "!! The licence server didn't start: journalctl -u zono-licence -n 50"; fi
echo "== Web address: https://$DOMAIN  (admin page: https://$DOMAIN/admin, customer portal: https://$DOMAIN/portal)"
echo "== Updates itself from GitHub every 15 minutes (log: /var/log/zono-licence-update.log)"
echo "== Admin key:   sudo cat $ETC/admin.key"
echo "== Public key for the apps:"
curl -fsS http://127.0.0.1:5080/api/v1/public-key || true
