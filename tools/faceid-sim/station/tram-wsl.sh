#!/usr/bin/env bash
# Runs the station (DSAVehicle) inside WSL2 for the face-ID demo, connected to the FPT Cloud platform.
# WSL counterpart of tram-laptop.ps1, plus a receiver key for the simulator.
#   CLOUD_URL=https://85-155-21-148.sslip.io STATION_ID=CUALO-GC bash tram-wsl.sh
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../../.." && pwd)"
CLOUD="${CLOUD_URL:-https://85-155-21-148.sslip.io}"
STATION="${STATION_ID:-CUALO-GC}"
DATA="${TRAM_DATA:-$HOME/.local/share/dsa-tram}"
SRC="${TRAM_SRC:-$HOME/.cache/dsa-tram/src}"
ok()  { printf '  \e[32mOK\e[0m  %s\n' "$1"; }
bad() { printf '  \e[31mLOI\e[0m %s\n' "$1"; exit 1; }

echo "=== 1. Cong cu"
# A per-user install (dotnet-install.sh --install-dir ~/.dotnet) needs no sudo; use it when present.
if [ -x "$HOME/.dotnet/dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"; fi
INSTALL_HINT="curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir ~/.dotnet"
command -v dotnet >/dev/null || bad ".NET chua cai trong WSL: $INSTALL_HINT"
dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 8\.' || bad "Thieu ASP.NET Core 8 runtime: $INSTALL_HINT"
command -v rsync >/dev/null || bad "Thieu rsync: sudo apt install -y rsync"
ok "$(dotnet --version)"

mkdir -p "$DATA"; chmod 700 "$DATA"
# Secrets live next to the station database, created once. Letters/digits only so they paste safely into URLs and shells.
secret() { local f="$DATA/$1"; [ -s "$f" ] || (umask 077; openssl rand -base64 "$2" | tr -dc 'A-Za-z0-9' > "$f"); cat "$f"; }
ADMIN_PW="$(secret admin-password 18)"
RECEIVER_KEY="$(secret receiver-key 48)"

echo "=== 2. Duong toi Cloud ($CLOUD)"
curl -fsS --max-time 8 "$CLOUD/health" >/dev/null || bad "Khong toi duoc $CLOUD/health"
ok "Cloud tra loi"

echo "=== 3. Khoa tram $STATION"
KEY_FILE="$DATA/platform-key"
if [ ! -s "$KEY_FILE" ]; then
  echo "  Dashboard Cloud -> Tram -> $STATION -> Doi khoa -> copy, roi dan vao day (khong hien khi go)."
  read -rsp "  Khoa tram: " KEY; echo
  (umask 077; printf '%s' "$KEY" > "$KEY_FILE")
fi
PLATFORM_KEY="$(cat "$KEY_FILE")"
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 8 -H "X-Station-Key: $PLATFORM_KEY" "$CLOUD/api/v1/stations/$STATION/master")
[ "$code" = 200 ] || { rm -f "$KEY_FILE"; bad "Cloud tu choi khoa (HTTP $code). Chay lai va dan khoa moi nhat."; }
ok "Cloud chap nhan khoa"

echo "=== 4. Ma nguon -> $SRC"
# Build and run from the Linux disk: SQLite on /mnt/d (9p) locks badly, and obj/ would fight with Windows builds.
mkdir -p "$SRC"
rsync -a --delete --exclude bin --exclude obj --exclude App_Data "$REPO/DSAVehicle/" "$SRC/DSAVehicle/"
ok "da dong bo"

cat <<EOF

=== 5. Chay tram (cong 5085)
  Giao dien tram (PowerShell tren Windows):  cd $(wslpath -w "$REPO/ui" 2>/dev/null || echo "$REPO/ui"); npm run dev  -> http://localhost:5173
  Dang nhap tram : admin / $ADMIN_PW
  Cau hinh demo  : STATION_PASSWORD='$ADMIN_PW' ~/.venvs/faceid-sim/bin/python $REPO/tools/faceid-sim/station/setup_station.py
  Simulator      : FACESIM_RECEIVER_KEY=\$(cat $DATA/receiver-key) bash $REPO/tools/faceid-sim/run.sh
  Du lieu tram   : $DATA (xoa thu muc nay de bat dau lai)

EOF
export ASPNETCORE_ENVIRONMENT=Development      # Production: the station UI cookie only travels over HTTPS
export Parking__Provider=Sqlite
export ConnectionStrings__Parking="Data Source=$DATA/parking.db"
export Parking__BootstrapPassword="$ADMIN_PW"
export Parking__AlertWebhookUrl=""
export Parking__StationId="$STATION"           # must equal Platform:StationId
export Platform__Enabled=true Platform__Url="$CLOUD" Platform__StationId="$STATION" Platform__Key="$PLATFORM_KEY"
export Sync__MasterSource=Cloud
export Receiver__Key="$RECEIVER_KEY"
cd "$SRC/DSAVehicle"
# The variable name has dots, which bash cannot export; env passes it as is.
exec env "Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Warning" dotnet run --urls http://0.0.0.0:5085
