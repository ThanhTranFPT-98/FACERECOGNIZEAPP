#!/usr/bin/env bash
# Starts the simulator (WSL2 / Linux). First run creates a virtualenv outside the repo and installs the packages.
#   FACESIM_RECEIVER_KEY=<Receiver:Key of the station> ./run.sh
set -euo pipefail
cd "$(dirname "$0")"
VENV="${FACESIM_VENV:-$HOME/.venvs/faceid-sim}"
if [ ! -x "$VENV/bin/uvicorn" ]; then
  python3 -m venv "$VENV" || { echo "Install venv first: sudo apt install -y python3-venv"; exit 1; }
  "$VENV/bin/pip" install --upgrade pip
  "$VENV/bin/pip" install torch --index-url https://download.pytorch.org/whl/cpu
  "$VENV/bin/pip" install -r requirements.txt
fi
if [ "${1:-}" = "test" ]; then exec "$VENV/bin/python" -m unittest discover -s tests -v; fi
echo "Open http://localhost:${FACESIM_PORT:-8095} in a browser on Windows (allow the camera)."
exec "$VENV/bin/uvicorn" faceid_sim.server:app --host 0.0.0.0 --port "${FACESIM_PORT:-8095}"
