#!/usr/bin/env bash
# Brings new app installers onto the server for the website's Download page.
# Builds are published on the repository's "dist" branch encrypted to this server's import key (RSA-OAEP wrapped
# AES-256), so only this server can read them; GitHub never holds a readable copy. Run as root (the updater does).
set -euo pipefail
REPO="https://github.com/ZonoSoft/zono-licence-server.git"
DIST=/opt/zono-licence/dist
KEY=/etc/zono-licence/import.key
DL=/var/lib/zono-licence/downloads
[ -f "$KEY" ] || exit 0
git ls-remote --exit-code --heads "$REPO" dist >/dev/null 2>&1 || exit 0
if [ -d "$DIST/.git" ]; then
  git -C "$DIST" fetch -q --depth 1 origin dist && git -C "$DIST" reset -q --hard origin/dist
else
  rm -rf "$DIST"; git clone -q --depth 1 --branch dist --single-branch "$REPO" "$DIST"
fi
[ -f "$DIST/dist/manifest.txt" ] || exit 0
install -d -o zonolic -g zonolic -m 0750 "$DL"
while read -r sha name; do
  [[ "$name" =~ ^Zono[A-Za-z0-9._-]+$ ]] || continue
  [[ "$sha" =~ ^[0-9a-f]{64}$ ]] || continue
  if [ -f "$DL/$name" ] && [ "$(cat "$DL/$name.sha256" 2>/dev/null)" = "$sha" ]; then continue; fi
  tmp=$(mktemp -d)
  if openssl pkeyutl -decrypt -inkey "$KEY" -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 \
       -in "$DIST/dist/$name.key.enc" -out "$tmp/k" 2>/dev/null \
     && openssl enc -d -aes-256-cbc -pbkdf2 -iter 100000 -in "$DIST/dist/$name.enc" -out "$tmp/f" -pass "file:$tmp/k" 2>/dev/null \
     && [ "$(sha256sum "$tmp/f" | cut -d' ' -f1)" = "$sha" ]; then
    install -o zonolic -g zonolic -m 0640 "$tmp/f" "$DL/$name"
    echo "$sha" > "$DL/$name.sha256"; chown zonolic:zonolic "$DL/$name.sha256"
    echo "== Imported $name"
  else
    echo "!! $name couldn't be imported (wrong key or damaged file)"
  fi
  rm -rf "$tmp"
done < "$DIST/dist/manifest.txt"
