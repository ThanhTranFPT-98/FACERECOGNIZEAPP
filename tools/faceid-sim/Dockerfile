# FaceID simulator for the laptop lab. Build context: the DSA_Vehicles repo root.
#   docker build -f tools/faceid-sim/Dockerfile -t dsa-faceid-sim:lab .
# AiService (code + model weights, ~180 MB) is mounted read-only at run time instead of being copied in:
# the weights are not in git and have no business inside an image.
FROM python:3.11-slim

# libgomp is what torch and onnxruntime need; opencv-python-headless needs no X libraries.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl libgomp1 ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
# Torch from the CPU index first, so pip never pulls the CUDA build (several GB).
COPY tools/faceid-sim/requirements.txt tools/faceid-sim/requirements.txt
RUN pip install --no-cache-dir --upgrade pip \
 && pip install --no-cache-dir torch --index-url https://download.pytorch.org/whl/cpu \
 && pip install --no-cache-dir -r tools/faceid-sim/requirements.txt

COPY tools/faceid-sim/ tools/faceid-sim/
RUN install -m 0755 tools/faceid-sim/docker-entrypoint.sh /usr/local/bin/faceid-sim-entrypoint

# config.py reads the repo root as parents[3] of faceid_sim/config.py, i.e. /app, and finds AiService there.
ENV PYTHONUNBUFFERED=1 \
    PYTHONPATH=/app/tools/faceid-sim \
    FACESIM_DATA=/data \
    FACESIM_PORT=8095 \
    TZ=Asia/Ho_Chi_Minh
VOLUME ["/data"]
EXPOSE 8095
HEALTHCHECK --interval=15s --timeout=5s --start-period=60s --retries=5 \
  CMD curl -fsS http://localhost:8095/api/status || exit 1
ENTRYPOINT ["/usr/local/bin/faceid-sim-entrypoint"]
CMD ["uvicorn", "faceid_sim.server:app", "--host", "0.0.0.0", "--port", "8095"]
