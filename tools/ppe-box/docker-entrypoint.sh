#!/bin/sh
# The station generates its receiver key on first start; the box reads it from the station's data volume
# instead of it being pasted into compose. Without a key the receiver answers 401, so wait for it.
set -eu

KEY_FILE="${RECEIVER_KEY_FILE:-/station-data/receiver-key}"
if [ -z "${PPEBOX_RECEIVER_KEY:-}" ]; then
  waited=0
  while [ ! -s "$KEY_FILE" ] && [ "$waited" -lt 120 ]; do sleep 2; waited=$((waited + 2)); done
  if [ -s "$KEY_FILE" ]; then
    PPEBOX_RECEIVER_KEY="$(cat "$KEY_FILE")"
    export PPEBOX_RECEIVER_KEY
    echo "Receiver key doc tu $KEY_FILE sau ${waited}s."
  else
    echo "CANH BAO: khong thay $KEY_FILE sau ${waited}s. Tram se tu choi (401) cho den khi co khoa."
  fi
fi

exec "$@"
