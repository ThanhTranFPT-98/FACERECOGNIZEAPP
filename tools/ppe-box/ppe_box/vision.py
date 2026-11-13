"""
Turning a frame into "who is in it and what are they wearing".

Three sources, chosen by PPEBOX_MODE, all returning the same thing so the rest of the box does not care:
  face   - SCRFD (the weights AiService already ships) finds faces; a face means a person. The PPE items
           come from the control panel. Real people, real boxes, real tracks; only the verdict is acted.
  model  - an ONNX detector finds people and PPE items by itself. This is the mode the mine will run.
  manual - no camera: the control panel alone says who stands in which ROI.
"""
import sys
import threading
import types
from pathlib import Path

import cv2
import numpy as np

from .config import AISERVICE, Settings
from .geometry import Box, covered, normalise


def decode_jpeg(data: bytes) -> np.ndarray | None:
    frame = cv2.imdecode(np.frombuffer(data, np.uint8), cv2.IMREAD_COLOR)
    return frame if frame is not None and frame.size else None


def encode_jpeg(frame: np.ndarray, quality: int = 80) -> bytes:
    ok, buffer = cv2.imencode(".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), quality])
    return buffer.tobytes() if ok else b""


class Vision:
    """A person detector. Returns boxes in frame fractions, plus whatever PPE items it could judge itself."""

    name = "none"

    def people(self, frame: np.ndarray) -> tuple[list[Box], list[dict[str, bool]]]:
        return [], []


class ManualVision(Vision):
    name = "manual"


class FaceVision(Vision):
    """A face is the only part of a worker a face model can find, so the person box is estimated around it."""

    name = "face"

    def __init__(self, settings: Settings):
        path = Path(settings.detector_weights)
        if not path.is_file():
            raise SystemExit(f"SCRFD weights not found: {path}\nMount AiService into the container, or set PPEBOX_SCRFD, or run with PPEBOX_MODE=manual.")
        if str(AISERVICE) not in sys.path:
            sys.path.insert(0, str(AISERVICE))
        # AiService imports torch at the top of the detector but only uses it in detect_tracking(), which we never
        # call. A stub keeps an 800 MB dependency out of an image that is meant to run on a small box.
        if "torch" not in sys.modules:
            sys.modules["torch"] = types.ModuleType("torch")
        import onnxruntime
        from face_detection.scrfd.detector import SCRFD

        session = onnxruntime.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        self._detector = SCRFD(session=session)
        self._min_face = settings.min_face_px
        self._lock = threading.Lock()

    def people(self, frame: np.ndarray) -> tuple[list[Box], list[dict[str, bool]]]:
        height, width = frame.shape[:2]
        with self._lock:
            boxes, _ = self._detector.detect(frame, thresh=0.5, input_size=(640, 640))
        people: list[Box] = []
        for face in boxes:
            x1, y1, x2, y2 = (float(v) for v in face[:4])
            fw, fh = x2 - x1, y2 - y1
            if min(fw, fh) < self._min_face:
                continue
            # Rule of thumb used for crowd counting: a standing adult is about 3 heads wide and 7 heads tall,
            # with roughly half a head of hair and helmet above the detected face.
            cx = (x1 + x2) / 2
            px, pw = cx - 1.5 * fw, 3.0 * fw
            py, ph = y1 - 0.5 * fh, 7.0 * fh
            people.append(normalise((px / width, py / height, pw / width, ph / height)))
        return people, [{} for _ in people]


class ModelVision(Vision):
    """
    A single ONNX detector that finds both people and PPE items, in the usual YOLO layout.

    Class names come from PPEBOX_CLASSES or from the model's own metadata. Which class means what is
    configuration (PPEBOX_ITEM_CLASSES), not code, because every public PPE dataset names them differently.
    """

    name = "model"

    def __init__(self, settings: Settings):
        path = Path(settings.model_path)
        if not path.is_file():
            raise SystemExit(f"PPE model not found: {path}\nSet PPEBOX_MODEL to an ONNX detector, or run with PPEBOX_MODE=face.")
        import onnxruntime

        self._session = onnxruntime.InferenceSession(str(path), providers=["CPUExecutionProvider"])
        self._input = self._session.get_inputs()[0]
        shape = self._input.shape
        self._size = (int(shape[3]) if isinstance(shape[3], int) else 640, int(shape[2]) if isinstance(shape[2], int) else 640)
        self._names = self._class_names(settings)
        self._settings = settings
        self._lock = threading.Lock()

    def _class_names(self, settings: Settings) -> list[str]:
        if settings.model_classes:
            return [n.strip().lower() for n in settings.model_classes.split(",")]
        raw = self._session.get_modelmeta().custom_metadata_map.get("names", "")
        if raw:
            try:
                # Ultralytics writes a Python dict literal, e.g. {0: 'person', 1: 'helmet'}.
                import ast
                parsed = ast.literal_eval(raw)
                return [str(parsed[key]).lower() for key in sorted(parsed)]
            except (ValueError, SyntaxError, KeyError, TypeError):
                pass
        raise SystemExit("The model does not name its classes. Set PPEBOX_CLASSES=person,helmet,… in the model's own order.")

    def _forward(self, frame: np.ndarray) -> np.ndarray:
        width, height = self._size
        resized = cv2.resize(frame, (width, height))
        blob = cv2.cvtColor(resized, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
        blob = np.expand_dims(blob.transpose(2, 0, 1), 0)
        with self._lock:
            output = self._session.run(None, {self._input.name: blob})[0]
        output = np.squeeze(output)
        # YOLOv8 exports (4 + classes, boxes); v5 exports (boxes, 5 + classes). Put boxes on the first axis.
        if output.ndim == 2 and output.shape[0] < output.shape[1]:
            output = output.T
        return output

    def _detections(self, frame: np.ndarray) -> list[tuple[str, float, Box]]:
        rows = self._forward(frame)
        if rows.ndim != 2 or rows.shape[1] < 5:
            return []
        classes = len(self._names)
        found: list[tuple[str, float, Box]] = []
        boxes_for_nms, scores_for_nms, labels = [], [], []
        width, height = self._size
        for row in rows:
            if rows.shape[1] == classes + 5:               # v5: cx cy w h objectness, then class scores
                scores = row[5:] * row[4]
            else:                                          # v8: cx cy w h, then class scores
                scores = row[4:4 + classes]
            index = int(np.argmax(scores))
            score = float(scores[index])
            if score < self._settings.model_threshold or index >= classes:
                continue
            cx, cy, w, h = (float(v) for v in row[:4])
            boxes_for_nms.append([cx - w / 2, cy - h / 2, w, h])
            scores_for_nms.append(score)
            labels.append(index)
        if not boxes_for_nms:
            return []
        keep = cv2.dnn.NMSBoxes(boxes_for_nms, scores_for_nms, self._settings.model_threshold, 0.45)
        for i in np.array(keep).flatten().tolist():
            x, y, w, h = boxes_for_nms[int(i)]
            found.append((self._names[labels[int(i)]], scores_for_nms[int(i)],
                          normalise((x / width, y / height, w / width, h / height))))
        return found

    def people(self, frame: np.ndarray) -> tuple[list[Box], list[dict[str, bool]]]:
        detections = self._detections(frame)
        persons = [box for name, _, box in detections if name in self._settings.person_classes]
        gear = [(self._settings.item_classes[name], box) for name, _, box in detections
                if name in self._settings.item_classes]
        items: list[dict[str, bool]] = []
        for person in persons:
            judged: dict[str, bool] = {}
            for meaning, box in gear:
                # Most of the PPE box must sit on this person; a helmet on the next worker is not theirs.
                if covered(box, person) < 0.6:
                    continue
                item, present = (meaning[1:], False) if meaning.startswith("!") else (meaning, True)
                # A negative detection wins: "no-helmet" is direct evidence, a missing helmet box is not.
                if item not in judged or not present:
                    judged[item] = present
            items.append(judged)
        return persons, items


def build(settings: Settings) -> Vision:
    if settings.mode == "face":
        return FaceVision(settings)
    if settings.mode == "model":
        return ModelVision(settings)
    return ManualVision()
