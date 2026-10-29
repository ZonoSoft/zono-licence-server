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
if [ -d "$APP/src/.git" ]; then git -C "$APP/src" pull -q; else rm -rf "$APP/src"; git clone -q "$REPO" "$APP/src"; fi
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

cat > /etc/caddy/Caddyfile <<CADDY
$DOMAIN {
    encode gzip
    reverse_proxy 127.0.0.1:5080
}
CADDY

# Daily backup of the licence database (kept 30 days) and the signing key
cat > /etc/cron.daily/zono-licence-backup <<'BACKUP'
#!/bin/sh
mkdir -p /var/backups/zono-licence
sqlite3 /var/lib/zono-licence/licences.db ".backup /var/backups/zono-licence/licences-$(date +%F).db"
cp -n /var/lib/zono-licence/signing-key.pem /var/backups/zono-licence/signing-key.pem 2>/dev/null || true
chmod 600 /var/backups/zono-licence/*
find /var/backups/zono-licence -name 'licences-*.db' -mtime +30 -delete
BACKUP
chmod 755 /etc/cron.daily/zono-licence-backup

systemctl daemon-reload
systemctl enable -q zono-licence caddy
systemctl restart zono-licence
systemctl reload caddy 2>/dev/null || systemctl restart caddy
sleep 3

echo
if curl -fsS http://127.0.0.1:5080/api/v1/health >/dev/null; then echo "== Licence server is running."; else echo "!! The licence server didn't start: journalctl -u zono-licence -n 50"; fi
echo "== Web address: https://$DOMAIN  (admin page: https://$DOMAIN/admin)"
echo "== Admin key:   sudo cat $ETC/admin.key"
echo "== Public key for the apps:"
curl -fsS http://127.0.0.1:5080/api/v1/public-key || true
