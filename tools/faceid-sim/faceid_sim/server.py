"""
HTTP side of the simulated terminal. The browser owns the webcam (works the same on Windows and in WSL2, which has no
camera of its own) and posts JPEG frames; this process recognises them and talks to the station like a device would.

Run: uvicorn faceid_sim.server:app --host 0.0.0.0 --port 8095   (from tools/faceid-sim)
"""
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, File, Form, HTTPException, Request, UploadFile
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import FileResponse

from .config import Settings
from .debounce import NO_FACE, PassDetector
from .device import StationLink, verify_message
from .engine import Engine, decode_jpeg, face_jpeg
from .gallery import STRANGER, Gallery

STATIC = Path(__file__).parent / "static"
MAX_FRAME_BYTES = 5_000_000

settings = Settings()
engine = Engine(settings)
gallery = Gallery(settings.data_dir)
link = StationLink(settings)
passes = PassDetector(settings.stable_frames, settings.cooldown_seconds, settings.stranger_cooldown_seconds, STRANGER)


@asynccontextmanager
async def lifespan(_: FastAPI):
    link.start()
    yield
    link.stop()


app = FastAPI(title="DSA FaceID Simulator", lifespan=lifespan)


def _process(data: bytes) -> dict:
    frame = decode_jpeg(data)
    if frame is None:
        raise HTTPException(400, "Not a JPEG image.")
    faces = engine.detect(frame)
    # Like a door terminal: only the nearest face is verified; others are shown but ignored.
    primary = faces[0] if faces and faces[0].size >= settings.min_face_px else None
    match = None
    if primary is not None:
        match = gallery.match(engine.embed(frame, primary), settings.match_threshold)
    label = NO_FACE if primary is None else (match.pin or STRANGER)
    emitted = passes.observe(label)
    event = None
    if emitted is not None:
        message = verify_message(settings, emitted, match.score, face_jpeg(frame, primary))
        event = link.submit(message, emitted, match.score).public()
    shown = []
    for face in faces:
        item = {"box": face.box, "primary": face is primary, "tooSmall": face.size < settings.min_face_px}
        if face is primary:
            item.update(pin=match.pin, name=match.name, score=round(match.score, 3), known=match.pin is not None)
        shown.append(item)
    seen_label, seen, needed = passes.progress
    return {"width": frame.shape[1], "height": frame.shape[0], "faces": shown,
            "progress": {"label": seen_label, "seen": seen, "needed": needed}, "event": event}


@app.post("/api/frame")
async def frame(request: Request):
    data = await request.body()
    if len(data) > MAX_FRAME_BYTES:
        raise HTTPException(413, "Frame too large.")
    return await run_in_threadpool(_process, data)


def _enroll(pin: str, name: str, photos: list[bytes]) -> dict:
    vectors, rejected = [], 0
    for data in photos:
        frame = decode_jpeg(data)
        faces = engine.detect(frame) if frame is not None else []
        # Exactly one clear face per enrolment photo: a second face could enrol the wrong person.
        if len(faces) != 1 or faces[0].size < settings.min_face_px:
            rejected += 1
            continue
        vectors.append(engine.embed(frame, faces[0]))
    try:
        person = gallery.enroll(pin, name, vectors)
    except ValueError as ex:
        raise HTTPException(400, f"{ex} ({rejected} photo(s) rejected: need exactly one face, close enough to the camera).")
    return {"person": person.__dict__, "used": len(vectors), "rejected": rejected}


@app.post("/api/enroll")
async def enroll(pin: str = Form(...), name: str = Form(""), photos: list[UploadFile] = File(...)):
    if not 1 <= len(photos) <= 10:
        raise HTTPException(400, "Send 1-10 photos.")
    data = [await p.read() for p in photos]
    return await run_in_threadpool(_enroll, pin, name, data)


@app.get("/api/people")
def people():
    return [p.__dict__ for p in gallery.people()]


@app.delete("/api/people/{pin}")
def remove(pin: str):
    if not gallery.remove(pin):
        raise HTTPException(404, "Unknown PIN.")
    return {"removed": pin}


@app.get("/api/events")
def events():
    return link.log()


@app.get("/api/status")
def status():
    return {
        "deviceId": settings.device_id, "channel": settings.channel, "profile": settings.profile,
        "receiver": settings.receiver_endpoint(), "receiverKeySet": len(settings.receiver_key) >= 32,
        "heartbeat": link.last_heartbeat, "backlog": link.backlog, "people": len(gallery.people()),
        "threshold": settings.match_threshold, "stableFrames": settings.stable_frames,
        "cooldownSeconds": settings.cooldown_seconds, "dataDir": str(settings.data_dir),
    }


@app.get("/")
def index():
    return FileResponse(STATIC / "index.html")
