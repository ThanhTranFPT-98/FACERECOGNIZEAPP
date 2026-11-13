"""
HTTP side of the PPE AI box. The browser owns the webcam (a container has none) and posts JPEG frames;
this process judges them and reports to the station exactly as the box at the mine will.

Run: uvicorn ppe_box.server:app --host 0.0.0.0 --port 8096   (from tools/ppe-box)
"""
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import FileResponse, Response

from .config import Settings
from .pipeline import Pipeline
from .rois import RoiStore
from .station import StationLink
from .vision import build, decode_jpeg

STATIC = Path(__file__).parent / "static"
MAX_FRAME_BYTES = 5_000_000

settings = Settings()
vision = build(settings)
rois = RoiStore(settings)
link = StationLink(settings)
pipeline = Pipeline(settings, vision, rois, link)


@asynccontextmanager
async def lifespan(_: FastAPI):
    rois.start()
    pipeline.start()
    yield
    pipeline.stop()
    rois.stop()


app = FastAPI(title="DSA PPE AI Box", lifespan=lifespan)


def _process(data: bytes) -> dict:
    frame = decode_jpeg(data)
    if frame is None:
        raise HTTPException(400, "Not a JPEG image.")
    return pipeline.observe(frame)


@app.post("/api/frame")
async def frame(request: Request):
    data = await request.body()
    if len(data) > MAX_FRAME_BYTES:
        raise HTTPException(413, "Frame too large.")
    return await run_in_threadpool(_process, data)


@app.post("/api/panel")
async def panel(request: Request):
    body = await request.json()
    if not isinstance(body, dict):
        raise HTTPException(400, "Expected an object.")
    items = body.get("items")
    if items is not None and not isinstance(items, dict):
        raise HTTPException(400, "items must be an object of name -> true/false.")
    return pipeline.set_panel(items, body.get("people"), body.get("roiId")).public()


@app.get("/api/rois")
def regions():
    return {"source": rois.source, "error": rois.last_error, "rois": [r.public() for r in rois.rois]}


@app.post("/api/rois/refresh")
async def refresh():
    ok = await run_in_threadpool(rois.refresh)
    return {"ok": ok, "source": rois.source, "error": rois.last_error, "rois": [r.public() for r in rois.rois]}


@app.get("/api/reports")
def reports():
    return link.log()


@app.get("/api/status")
def status():
    return {
        "deviceId": settings.device_id, "channel": settings.channel, "mode": vision.name,
        "items": list(settings.items), "receiver": settings.receiver_endpoint(),
        "receiverKeySet": len(settings.receiver_key) >= 32, "sendInterval": settings.send_interval,
        "frames": pipeline.frames, "secondsSinceFrame": pipeline.seconds_since_frame,
        "sent": link.sent, "failed": link.failed, "last": link.last(),
        "roiSource": rois.source, "roiError": rois.last_error,
        "rois": [r.public() for r in rois.rois], "panel": pipeline.panel.public(),
    }


@app.get("/snapshot")
def snapshot():
    """The still the station's ROI editor draws on, so the operator marks up what this camera really sees."""
    image = pipeline.snapshot
    if not image:
        raise HTTPException(404, "Chưa có khung hình nào. Mở http://localhost:8096 và cho phép dùng camera.")
    return Response(image, media_type="image/jpeg", headers={"Cache-Control": "no-store"})


@app.get("/")
def index():
    return FileResponse(STATIC / "index.html")
