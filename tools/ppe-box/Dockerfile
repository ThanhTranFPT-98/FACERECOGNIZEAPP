# PPE AI box for the laptop lab. Build context: the DSA_Vehicles repo root.
#   docker build -f tools/ppe-box/Dockerfile -t dsa-ppe-box:lab .
# This is the image that will run on the real AI box: no torch, no CUDA, onnxruntime on the CPU.
# AiService (SCRFD weights for the face mode) is mounted read-only at run time, never baked in.
FROM python:3.11-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl libgomp1 ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY tools/ppe-box/requirements.txt tools/ppe-box/requirements.txt
RUN pip install --no-cache-dir --upgrade pip \
 && pip install --no-cache-dir -r tools/ppe-box/requirements.txt

COPY tools/ppe-box/ tools/ppe-box/
RUN install -m 0755 tools/ppe-box/docker-entrypoint.sh /usr/local/bin/ppe-box-entrypoint

# config.py reads the repo root as parents[3] of ppe_box/config.py, i.e. /app, and finds AiService there.
ENV PYTHONUNBUFFERED=1 \
    PYTHONPATH=/app/tools/ppe-box \
    PPEBOX_PORT=8096 \
    TZ=Asia/Ho_Chi_Minh
EXPOSE 8096
HEALTHCHECK --interval=15s --timeout=5s --start-period=45s --retries=5 \
  CMD curl -fsS http://localhost:8096/api/status || exit 1
ENTRYPOINT ["/usr/local/bin/ppe-box-entrypoint"]
CMD ["uvicorn", "ppe_box.server:app", "--host", "0.0.0.0", "--port", "8096"]
