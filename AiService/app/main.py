import asyncio
import time
from datetime import datetime
from typing import List, Optional, Dict, Any

from fastapi import UploadFile, File
import cv2
import numpy as np
import torch
import yaml
import httpx
import json
import requests
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from torchvision import transforms

from face_alignment.alignment import norm_crop
from face_detection.scrfd.detector import SCRFD
# from face_detection.yolov5_face.detector import Yolov5Face
from face_recognition.arcface.model import iresnet_inference
from face_recognition.arcface.utils import compare_encodings, read_features
from face_tracking.tracker.byte_tracker import BYTETracker

# =========================
# CONFIG
# =========================
CAMERA_SOURCE = 0
PROCESS_EVERY_SECONDS = 1
ASP_NET_API_URL = "http://localhost:5085/api/face-events/receive-ai-result"
TRACKING_CONFIG_PATH = "./face_tracking/config/config_tracking.yaml"
FEATURE_PATH = "./datasets/face_features/feature"
UNKNOWN_THRESHOLD = 0.5

# =========================
# FASTAPI APP
# =========================
from contextlib import asynccontextmanager

@asynccontextmanager
async def lifespan(app: FastAPI):
    global tracking_config, tracker

    tracking_config = load_config(TRACKING_CONFIG_PATH)
    tracker = BYTETracker(args=tracking_config, frame_rate=30)

    # Retry loading embeddings — backend may still be starting up
    for attempt in range(1, 7):
        try:
            load_embeddings_from_api()
            if EMBEDDINGS_DB:
                break
            print(f"[Startup] No embeddings loaded yet, retrying ({attempt}/6)...")
        except Exception as ex:
            print(f"[Startup] Load embeddings attempt {attempt}/6 failed: {ex}")
        await asyncio.sleep(5)

    yield
    
app = FastAPI(
    title="Face Recognition FastAPI Demo",
    lifespan=lifespan
)

# =========================
# GLOBAL STATE
# =========================
device = torch.device("cuda" if torch.cuda.is_available() else "cpu")

detector = SCRFD(model_file="face_detection/scrfd/weights/scrfd_2.5g_bnkps.onnx")
# detector = Yolov5Face(model_file="face_detection/yolov5_face/weights/yolov5n-face.pt")

recognizer = iresnet_inference(
    model_name="r50",
    path="face_recognition/arcface/weights/arcface_r50.pth",
    device=device
)

images_names, images_embs = read_features(feature_path=FEATURE_PATH)

camera_worker_task: Optional[asyncio.Task] = None
is_running = False
last_result: Dict[str, Any] = {}
video_capture: Optional[cv2.VideoCapture] = None
tracker: Optional[BYTETracker] = None
tracking_config: Optional[dict] = None


# =========================
# DTOs
# =========================
class RecognizedPersonDto(BaseModel):
    track_id: int
    name: str
    score: float
    is_unknown: bool
    bbox: List[float]


class FrameResultDto(BaseModel):
    camera_id: str
    captured_at: str
    total_faces: int
    recognized_people: List[RecognizedPersonDto]


class StartCameraRequest(BaseModel):
    camera_source: Optional[int] = 0
    camera_id: Optional[str] = "CAM_01"


class RecognizedFaceDto(BaseModel):
    peopleId: Optional[int]
    confidence: float

class UnknowFaceDto(BaseModel):
    confidence: float

class FaceRecognizeResponseDto(BaseModel):
    totalFaces: int
    recognizedFaces: List[RecognizedFaceDto]
    UnknowFaces: list[UnknowFaceDto]   

# =========================
# HELPER FUNCTIONS
# =========================
def load_config(file_name: str) -> dict:
    with open(file_name, "r", encoding="utf-8") as stream:
        return yaml.safe_load(stream)

EMBEDDINGS_DB = []

def load_embeddings_from_api():
    global EMBEDDINGS_DB

    url = "http://localhost:5085/api/embeddings"
    response = requests.get(url, timeout=10)
    response.raise_for_status()

    data = response.json()
    loaded = []

    for item in data:
        try:
            vector_str = item.get("vector", "")

            if not vector_str:
                continue

            embedding_list = json.loads(vector_str)

            if not isinstance(embedding_list, list) or len(embedding_list) == 0:
                continue

            loaded.append({
                "embedding_id": item.get("id"),
                "person_id": item.get("peopleId"),
                "embedding": np.array(embedding_list, dtype=np.float32)
            })

        except Exception as ex:
            print(f"Skip embedding id={item.get('id')}, error={ex}")
            continue

    EMBEDDINGS_DB = loaded
    print(f"Loaded {len(EMBEDDINGS_DB)} embeddings from API")



@torch.no_grad()
def get_feature(face_image):
    face_preprocess = transforms.Compose([
        transforms.ToTensor(),
        transforms.Resize((112, 112)),
        transforms.Normalize(mean=[0.5, 0.5, 0.5], std=[0.5, 0.5, 0.5]),
    ])

    face_image = cv2.cvtColor(face_image, cv2.COLOR_BGR2RGB)
    face_image = face_preprocess(face_image).unsqueeze(0).to(device)
    emb_img_face = recognizer(face_image).cpu().numpy()
    images_emb = emb_img_face / np.linalg.norm(emb_img_face)
    return images_emb

def recognize_face_from_api_embeddings(face_image):
    global EMBEDDINGS_DB

    if not EMBEDDINGS_DB:
        return 0.0, None, True

    query_emb = get_feature(face_image)[0]

    best_score = -1.0
    best_person_id = None

    for item in EMBEDDINGS_DB:
        db_emb = item["embedding"]

        score = float(np.dot(query_emb, db_emb))

        if score > best_score:
            best_score = score
            best_person_id = item["person_id"]

    if best_score < UNKNOWN_THRESHOLD:
        return best_score, None, True

    return best_score, best_person_id, False


def mapping_bbox(box1, box2):
    x_min_inter = max(box1[0], box2[0])
    y_min_inter = max(box1[1], box2[1])
    x_max_inter = min(box1[2], box2[2])
    y_max_inter = min(box1[3], box2[3])

    intersection_area = max(0, x_max_inter - x_min_inter + 1) * max(
        0, y_max_inter - y_min_inter + 1
    )

    area_box1 = (box1[2] - box1[0] + 1) * (box1[3] - box1[1] + 1)
    area_box2 = (box2[2] - box2[0] + 1) * (box2[3] - box2[1] + 1)
    union_area = area_box1 + area_box2 - intersection_area

    if union_area <= 0:
        return 0.0

    return intersection_area / union_area


def process_one_frame(frame: np.ndarray) -> FrameResultDto:
    global tracker, tracking_config

    outputs, img_info, detection_bboxes, detection_landmarks = detector.detect_tracking(image=frame)

    tracked_people = []

    tracking_bboxes = []
    tracking_ids = []

    if outputs is not None:
        online_targets = tracker.update(
            outputs,
            [img_info["height"], img_info["width"]],
            (128, 128)
        )

        for t in online_targets:
            tlwh = t.tlwh
            tid = t.track_id
            vertical = tlwh[2] / tlwh[3] > tracking_config["aspect_ratio_thresh"]

            if tlwh[2] * tlwh[3] > tracking_config["min_box_area"] and not vertical:
                x1, y1, w, h = tlwh
                tracking_bboxes.append([x1, y1, x1 + w, y1 + h])
                tracking_ids.append(tid)

    used_detection_indexes = set()

    for i in range(len(tracking_bboxes)):
        best_j = -1
        best_iou = 0.0

        for j in range(len(detection_bboxes)):
            if j in used_detection_indexes:
                continue

            iou = mapping_bbox(tracking_bboxes[i], detection_bboxes[j])
            if iou > best_iou:
                best_iou = iou
                best_j = j

        if best_j != -1 and best_iou > 0.9:
            face_alignment = norm_crop(
                img=img_info["raw_img"],
                landmark=detection_landmarks[best_j]
            )

            score, name, is_unknown = recognize_face_from_api_embeddings(face_alignment)

            tracked_people.append(
                RecognizedPersonDto(
                    track_id=int(tracking_ids[i]),
                    name=name,
                    score=round(score, 4),
                    is_unknown=is_unknown,
                    bbox=[float(x) for x in tracking_bboxes[i]]
                )
            )

            used_detection_indexes.add(best_j)

    result = FrameResultDto(
        camera_id="CAM_01",
        captured_at=datetime.timezone.utc.now().isoformat(),
        total_faces=len(tracked_people),
        recognized_people=tracked_people
    )

    return result


async def send_result_to_aspnet(result: FrameResultDto):
    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            response = await client.post(
                ASP_NET_API_URL,
                json=result.model_dump()
            )
            response.raise_for_status()
    except Exception as ex:
        print(f"[ERROR] Send result to ASP.NET failed: {ex}")


async def camera_worker(camera_source: int, camera_id: str):
    global is_running, last_result, video_capture

    video_capture = cv2.VideoCapture(camera_source)

    if not video_capture.isOpened():
        is_running = False
        raise RuntimeError(f"Cannot open camera source: {camera_source}")

    print("[INFO] Camera worker started")

    try:
        while is_running:
            ret, frame = video_capture.read()
            if not ret:
                await asyncio.sleep(1)
                continue

            try:
                frame_result = process_one_frame(frame)
                frame_result.camera_id = camera_id
                last_result = frame_result.model_dump()

                if frame_result.total_faces > 0:
                    await send_result_to_aspnet(frame_result)

            except Exception as ex:
                print(f"[ERROR] Processing frame failed: {ex}")

            await asyncio.sleep(PROCESS_EVERY_SECONDS)

    finally:
        if video_capture is not None:
            video_capture.release()
        print("[INFO] Camera worker stopped")



# =========================
# API ENDPOINTS
# =========================
# @app.on_event("startup")
# async def startup_event():
#     global tracking_config, tracker
#     tracking_config = load_config(TRACKING_CONFIG_PATH)
#     tracker = BYTETracker(args=tracking_config, frame_rate=30)
#     try:
#         load_embeddings_from_api()
#     except Exception as ex:
#         print(f"Load embeddings failed: {ex}")



@app.get("/")
def root():
    return {"message": "Face Recognition FastAPI is running"}


@app.post("/reload-embeddings")
def reload_embeddings():
    try:
        load_embeddings_from_api()
        return {"success": True, "loaded": len(EMBEDDINGS_DB)}
    except Exception as ex:
        raise HTTPException(status_code=500, detail=str(ex))


@app.post("/face/embedding")
async def get_face_embedding(file: UploadFile = File(...)):
    try:
        # đọc file ảnh
        contents = await file.read()
        np_arr = np.frombuffer(contents, np.uint8)
        frame = cv2.imdecode(np_arr, cv2.IMREAD_COLOR)

        if frame is None:
            raise HTTPException(status_code=400, detail="Invalid image")

        # detect face
        outputs, img_info, bboxes, landmarks = detector.detect_tracking(image=frame)

        if bboxes is None or len(bboxes) == 0:
            return {
                "face_detected": False,
                "embedding": None
            }

        # lấy face đầu tiên (demo)
        face = norm_crop(
            img=img_info["raw_img"],
            landmark=landmarks[0]
        )

        # extract embedding
        embedding = get_feature(face)[0].tolist()

        return {
            "face_detected": True,
            "embedding": embedding,
            "bbox": [float(x) for x in bboxes[0]]
        }

    except Exception as ex:
        raise HTTPException(status_code=500, detail=str(ex))

@app.get("/health")
async def health_check():
    """Health check endpoint to verify API is running"""
    return {
        "status": "healthy",
        "detector_loaded": detector is not None,
        "model_path": "face_detection/scrfd/weights/scrfd_2.5g_bnkps.onnx"
    }

@app.post("/face/recognize", response_model = FaceRecognizeResponseDto)
async def recognize_face_endpoint(file: UploadFile = File(..., alias="Image")):
    try:
        print(f"[Python API] Received request, file: {file.filename}, content-type: {file.content_type}")
        contents = await file.read()
        print(f"[Python API] File size: {len(contents)} bytes")
        
        np_arr = np.frombuffer(contents, np.uint8)
        frame = cv2.imdecode(np_arr, cv2.IMREAD_COLOR)

        if frame is None:
            print("[Python API] ERROR: Invalid image, cannot decode")
            raise HTTPException(status_code=400, detail="Invalid image")
        
        print(f"[Python API] Image decoded: {frame.shape}")

        outputs, img_info, bboxes, landmarks = detector.detect_tracking(image=frame)
        print(f"[Python API] Detection result: {len(bboxes) if bboxes is not None else 0} faces found")

        if bboxes is None or len(bboxes) == 0:
            return FaceRecognizeResponseDto(
                totalFaces=0,
                recognizedFaces=[],
                UnknowFaces=[]
            )

        recognized_faces = []
        Unknow_faces = []
    
       
        for i in range(len(bboxes)):
            face = norm_crop(
                img=img_info["raw_img"],
                landmark=landmarks[i]
            )

            score, people_id, is_unknown = recognize_face_from_api_embeddings(face)

            if is_unknown or people_id is None or people_id == 0:
                Unknow_faces.append(
                    UnknowFaceDto(
                        confidence=round(float(score), 4)
                    )
                )
            else:
                recognized_faces.append(
                    RecognizedFaceDto(
                        peopleId=int(people_id),
                        confidence=round(float(score), 4)
                    )
                )

        response = FaceRecognizeResponseDto(
            totalFaces=len(recognized_faces)+len(Unknow_faces),
            recognizedFaces=recognized_faces,
            UnknowFaces=Unknow_faces
        )
        print(f"[Python API] Response: {len(recognized_faces)} recognized, {len(Unknow_faces)} unknown")
        return response
    

    except Exception as ex:
        print(f"[Python API] ERROR: {type(ex).__name__}: {str(ex)}")
        raise HTTPException(status_code=500, detail=str(ex))



@app.post("/camera/start")
async def start_camera(request: StartCameraRequest):
    global is_running, camera_worker_task

    if is_running:
        return {"message": "Camera worker is already running"}

    is_running = True
    camera_worker_task = asyncio.create_task(
        camera_worker(request.camera_source, request.camera_id)
    )

    return {
        "message": "Camera worker started",
        "camera_source": request.camera_source,
        "camera_id": request.camera_id
    }


@app.post("/camera/stop")
async def stop_camera():
    global is_running, camera_worker_task

    if not is_running:
        return {"message": "Camera worker is not running"}

    is_running = False

    if camera_worker_task:
        try:
            await asyncio.wait_for(camera_worker_task, timeout=5)
        except asyncio.TimeoutError:
            pass

    return {"message": "Camera worker stopped"}


@app.get("/camera/status")
def camera_status():
    return {
        "is_running": is_running,
        "has_last_result": bool(last_result)
    }


@app.get("/last-result")
def get_last_result():
    return last_result