"""Local CPU OCR baseline. Run on loopback: uvicorn plate_service:app --port 8001.

No model accuracy is claimed. Uncalibrated candidates are held for guard review by the API.
"""
import csv
import io
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path

import cv2
import numpy as np
from fastapi import FastAPI, File, HTTPException, UploadFile
from fastapi.responses import JSONResponse
from PIL import Image, UnidentifiedImageError
from starlette.concurrency import run_in_threadpool

MODEL = "vn-contours-tesseract-baseline-v1"
app = FastAPI(title="Vietnam plate recognition")

def normalize(text: str) -> str:
    return re.sub(r"[\s.\-]", "", text.upper())

def common_plate(text: str) -> bool:
    return re.fullmatch(r"[0-9]{2}[A-Z]{1,2}[0-9]{4,6}", text) is not None

def tesseract_path():
    return shutil.which(os.getenv("TESSERACT_CMD", "tesseract"))

def recognize_bytes(contents: bytes) -> dict:
    executable = tesseract_path()
    if not executable:
        raise HTTPException(503, "Tesseract is not installed. Set TESSERACT_CMD to its executable.")
    try:
        with Image.open(io.BytesIO(contents)) as image:
            if image.format not in ("JPEG", "PNG") or image.width * image.height > 12_000_000:
                raise HTTPException(400, "Use a JPEG/PNG with at most 12 million pixels.")
            image.verify()
    except (UnidentifiedImageError, OSError, Image.DecompressionBombError) as exc:
        raise HTTPException(400, "Invalid image.") from exc
    frame = cv2.imdecode(np.frombuffer(contents, np.uint8), cv2.IMREAD_COLOR)
    if frame is None:
        raise HTTPException(400, "Image decoding failed.")
    scale = min(1., 1600 / max(frame.shape[:2]))
    frame = cv2.resize(frame, None, fx=scale, fy=scale)
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    edges = cv2.Canny(gray, 70, 180)
    contours, _ = cv2.findContours(edges, cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    boxes = []
    for contour in sorted(contours, key=cv2.contourArea, reverse=True):
        x, y, w, h = cv2.boundingRect(contour)
        if w >= 60 and h >= 20 and 1.0 <= w / h <= 6.5 and w * h < gray.size * .9:
            if all(abs(x-a) + abs(y-b) + abs(w-c) + abs(h-d) > 30 for a, b, c, d in boxes):
                boxes.append((x, y, w, h))
        if len(boxes) >= 8:
            break
    # The whole image also supports manually cropped one- or two-line plate photos.
    boxes.append((0, 0, gray.shape[1], gray.shape[0]))
    candidates = {}
    with tempfile.TemporaryDirectory(prefix="parking-ocr-") as directory:
        filename = str(Path(directory) / "crop.png")
        for x, y, w, h in boxes:
            crop = gray[y:y+h, x:x+w]
            crop = cv2.resize(crop, (max(300, w), max(100, h)))
            cv2.imwrite(filename, crop)
            try:
                result = subprocess.run([executable, filename, "stdout", "--psm", "6", "-l", "eng", "-c", "tessedit_char_whitelist=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-", "tsv"], capture_output=True, text=True, timeout=3, check=True)
            except (subprocess.TimeoutExpired, subprocess.CalledProcessError) as exc:
                raise HTTPException(503, "OCR engine failed or timed out.") from exc
            rows = [r for r in csv.DictReader(io.StringIO(result.stdout), delimiter="\t") if r.get("text", "").strip()]
            text = "".join(r["text"] for r in rows)
            plate = normalize(text)
            if not common_plate(plate):
                continue
            scores = [max(0., min(1., float(r["conf"]) / 100)) for r in rows]
            score = min(scores) if scores else 0.
            candidate = {"plate": plate, "rawText": text, "confidence": score, "bbox": [round(v / scale) for v in (x, y, w, h)]}
            if plate not in candidates or score > candidates[plate]["confidence"]:
                candidates[plate] = candidate
    ranked = sorted(candidates.values(), key=lambda c: c["confidence"], reverse=True)
    # Conflicting candidates (possibly adjacent vehicles) must never be treated as a certain identity.
    best = ranked[0] if len(ranked) == 1 else {"plate": "", "confidence": 0.}
    return {**best, "candidates": ranked, "modelVersion": MODEL, "calibrated": False}

@app.get("/health")
def health():
    ready = bool(tesseract_path())
    return JSONResponse({"ready": ready, "modelVersion": MODEL, "calibrated": False}, status_code=200 if ready else 503)

@app.post("/v1/recognition/plate")
async def recognize(file: UploadFile = File(...)):
    contents = await file.read(5_000_001)
    if not contents or len(contents) > 5_000_000:
        raise HTTPException(400, "Upload an image up to 5 MB.")
    return await run_in_threadpool(recognize_bytes, contents)
