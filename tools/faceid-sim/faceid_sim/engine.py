"""Face detection + embedding, reusing AiService's SCRFD and ArcFace code and weights (no copy of either)."""
import sys
import threading
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

from .config import AISERVICE, Settings


@dataclass
class Face:
    box: tuple[int, int, int, int]       # x1, y1, x2, y2 in frame pixels
    landmarks: np.ndarray                # 5 x 2

    @property
    def size(self) -> int:
        return min(self.box[2] - self.box[0], self.box[3] - self.box[1])


class Engine:
    def __init__(self, settings: Settings):
        for path in (settings.detector_weights, settings.recognizer_weights):
            if not Path(path).is_file():
                raise SystemExit(f"Model weights not found: {path}\nCopy them into AiService (see AiService/*/weights/README.md) or set FACESIM_SCRFD / FACESIM_ARCFACE.")
        if str(AISERVICE) not in sys.path:
            sys.path.insert(0, str(AISERVICE))
        import onnxruntime
        import torch
        from face_alignment.alignment import norm_crop
        from face_detection.scrfd.detector import SCRFD
        from face_recognition.arcface.model import iresnet_inference

        self._torch = torch
        self._norm_crop = norm_crop
        # An explicit CPU session: a GPU build of onnxruntime refuses a session created without providers.
        session = onnxruntime.InferenceSession(str(settings.detector_weights), providers=["CPUExecutionProvider"])
        self._detector = SCRFD(session=session)
        self._device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        self._recognizer = iresnet_inference("r50", str(settings.recognizer_weights), device=self._device)
        # One frame at a time: the models are not thread-safe and the CPU is the bottleneck anyway.
        self._lock = threading.Lock()

    def detect(self, frame: np.ndarray) -> list[Face]:
        with self._lock:
            boxes, landmarks = self._detector.detect(frame, thresh=0.5, input_size=(640, 640))
        faces = [Face(tuple(int(v) for v in b[:4]), lm.astype(np.float32)) for b, lm in zip(boxes, landmarks)]
        return sorted(faces, key=lambda f: f.size, reverse=True)

    def embed(self, frame: np.ndarray, face: Face) -> np.ndarray:
        """L2-normalised 512-d ArcFace embedding, same preprocessing as AiService (RGB, 112x112, (x - 0.5) / 0.5)."""
        crop = self._norm_crop(frame, face.landmarks)
        rgb = cv2.cvtColor(crop, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
        tensor = self._torch.from_numpy(((rgb - 0.5) / 0.5).transpose(2, 0, 1)).unsqueeze(0).to(self._device)
        with self._lock, self._torch.no_grad():
            vector = self._recognizer(tensor).cpu().numpy()[0]
        return vector / np.linalg.norm(vector)


def decode_jpeg(data: bytes) -> np.ndarray | None:
    return cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR) if data else None


def face_jpeg(frame: np.ndarray, face: Face, margin: float = 0.6) -> bytes:
    """The photo a terminal attaches to an event: the face with some context, not the whole camera frame."""
    x1, y1, x2, y2 = face.box
    pad_x, pad_y = int((x2 - x1) * margin), int((y2 - y1) * margin)
    h, w = frame.shape[:2]
    crop = frame[max(0, y1 - pad_y):min(h, y2 + pad_y), max(0, x1 - pad_x):min(w, x2 + pad_x)]
    ok, data = cv2.imencode(".jpg", crop, [cv2.IMWRITE_JPEG_QUALITY, 85])
    return data.tobytes() if ok else b""
