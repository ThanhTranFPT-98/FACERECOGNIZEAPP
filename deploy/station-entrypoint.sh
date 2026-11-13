#!/bin/sh
# Container counterpart of tools/faceid-sim/station/tram-wsl.sh: create the station's secrets once,
# keep them with the database in /data, then run the station.
# Everything the station needs is passed as configuration here, so the image itself holds no secret.
set -eu

DATA="${STATION_DATA:-/data}"
STATION="${STATION_ID:-CUALO-GC}"
CLOUD="${CLOUD_URL:-https://85-155-21-148.sslip.io}"

mkdir -p "$DATA" "$DATA/evidence" "$DATA/video"
chmod 700 "$DATA"

# Letters and digits only, so the values paste safely into URLs and shells.
secret() {
  file="$DATA/$1"
  [ -s "$file" ] || (umask 077; openssl rand -base64 "$2" | tr -dc 'A-Za-z0-9' > "$file")
  cat "$file"
}
ADMIN_PW="$(secret admin-password 18)"
RECEIVER_KEY="$(secret receiver-key 48)"

# The platform key is issued by the cloud dashboard (Trạm → Đổi khoá). Passed in by compose, or dropped
# into /data/platform-key by hand. Without it the station runs offline instead of failing to start.
PLATFORM_KEY="${PLATFORM_KEY:-}"
if [ -z "$PLATFORM_KEY" ] && [ -s "$DATA/platform-key" ]; then PLATFORM_KEY="$(cat "$DATA/platform-key")"; fi
if [ -n "$PLATFORM_KEY" ]; then
  [ -s "$DATA/platform-key" ] || (umask 077; printf '%s' "$PLATFORM_KEY" > "$DATA/platform-key")
  PLATFORM_ENABLED=true
else
  PLATFORM_ENABLED=false
fi

cat <<EOF

=== Tram $STATION ===
  Giao dien      : http://localhost:5173   (admin / $ADMIN_PW)
  API            : http://localhost:5085
  Khoa receiver  : $DATA/receiver-key   (FaceID va PPE dung khoa nay)
  Len Cloud      : $([ "$PLATFORM_ENABLED" = true ] && echo "$CLOUD" || echo 'TAT - chua co PLATFORM_KEY, tram chay ngoai tuyen')
  Du lieu        : $DATA (xoa volume de bat dau lai)

EOF

# Development: the station UI cookie is allowed over plain HTTP inside the lab network.
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export Parking__Provider=Sqlite
export ConnectionStrings__Parking="Data Source=$DATA/parking.db"
export Parking__BootstrapPassword="$ADMIN_PW"
export Parking__EvidenceDirectory="$DATA/evidence"
export Parking__AlertWebhookUrl=""
export Parking__StationId="$STATION"
export Platform__Enabled="$PLATFORM_ENABLED"
export Platform__Url="$CLOUD"
export Platform__StationId="$STATION"
export Platform__Key="$PLATFORM_KEY"
# The cloud can only own the person list when the station can reach it; offline the station owns its own.
export Sync__MasterSource="${SYNC_MASTER_SOURCE:-$([ "$PLATFORM_ENABLED" = true ] && echo Cloud || echo Local)}"
export Receiver__Key="$RECEIVER_KEY"

# This setting's name contains dots, which the shell cannot export; env passes it through unchanged.
exec env "Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Warning" \
  dotnet /app/DSAVehicle.dll
