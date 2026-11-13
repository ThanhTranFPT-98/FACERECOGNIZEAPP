#!/usr/bin/env bash
# Runs the PPE box outside Docker (WSL2 / Linux). First run creates a virtualenv outside the repo.
#   PPEBOX_RECEIVER_KEY=<Receiver:Key of the station> ./run.sh
# No torch here: the box only needs onnxruntime (see ppe_box/vision.py for why SCRFD still imports).
set -euo pipefail
cd "$(dirname "$0")"
VENV="${PPEBOX_VENV:-$HOME/.venvs/ppe-box}"
if [ ! -x "$VENV/bin/uvicorn" ]; then
  python3 -m venv "$VENV" || { echo "Install venv first: sudo apt install -y python3-venv"; exit 1; }
  "$VENV/bin/pip" install --upgrade pip
  "$VENV/bin/pip" install -r requirements.txt
fi
if [ "${1:-}" = "test" ]; then exec "$VENV/bin/python" -m unittest discover -s tests -v; fi
echo "Open http://localhost:${PPEBOX_PORT:-8096} in a browser on Windows (allow the camera)."
exec "$VENV/bin/uvicorn" ppe_box.server:app --host 0.0.0.0 --port "${PPEBOX_PORT:-8096}"
