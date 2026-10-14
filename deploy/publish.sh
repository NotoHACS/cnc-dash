#!/bin/bash
# Server deploy/publish — run on the LXC as root (or via sudo).
set -euo pipefail

APP=/opt/cncdash
SRC=${1:-/opt/cncdash/src}   # checkout of the repo (deploy step clones it there)

echo "== prerequisites =="
export DOTNET_ROOT=${DOTNET_ROOT:-/usr/lib/dotnet}
test -x /usr/bin/dotnet || test -x "$DOTNET_ROOT/dotnet" || {
  echo "install .NET 8 runtime first"; exit 1; }
apt-get install -y tzdata >/dev/null || true

echo "== publish server =="
dotnet publish "$SRC/server" -c Release -o "$APP/server"

echo "== app dir =="
mkdir -p "$APP/data"
chown -R cncdash:cncdash "$APP"

echo "== secrets =="
if [ ! -f "$APP/secrets.env" ]; then
  PW=$(tr -dc 'A-Za-z0-9' < /dev/urandom | head -c 24)
  cat > "$APP/secrets.env" <<EOF
CNC_DASH_ADMIN_PASSWORD=$PW
EOF
  chmod 600 "$APP/secrets.env"
  echo "admin password generated: $PW"
else
  echo "secrets.env exists, keeping"
fi

echo "== systemd =="
cp "$SRC/deploy/cncdash.service" /etc/systemd/system/cncdash.service
systemctl daemon-reload
systemctl enable --now cncdash
systemctl status cncdash --no-pager | head -5

echo "== done: http://127.0.0.1:5154 (tunnel: cnc-dash.example.com) =="
echo "seed a machine: CNC_DASH_SEED_MACHINE=cnc12 or add it via /dashboard/admin"