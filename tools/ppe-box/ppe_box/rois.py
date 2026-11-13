"""
Which part of the picture belongs to which lane.

The operator draws the regions on the station, never on the box: the station is the only place anything is
configured, and the box has no screen at the mine. The box asks the station for them and keeps asking, so a
region redrawn on the station takes effect without anyone restarting or logging into the AI box.
Until a region is drawn, one region covers the whole frame, which is how the station maps a camera that
watches exactly one lane (CameraFor: a reading with no roiId belongs to the camera row that names none).
"""
import threading
import time
from dataclasses import dataclass

import requests

from .config import Settings
from .geometry import Point, anchor, in_polygon


@dataclass(frozen=True)
class Roi:
    id: str                       # "" means the whole frame (one camera, one lane)
    polygon: tuple[Point, ...]    # fewer than 3 points: the whole frame
    label: str = ""

    def contains(self, point: Point) -> bool:
        return True if len(self.polygon) < 3 else in_polygon(point, list(self.polygon))

    def public(self) -> dict:
        return {"roiId": self.id, "label": self.label, "polygon": [list(p) for p in self.polygon]}


class RoiStore:
    """Holds the regions in memory, refreshed from the station in the background."""

    def __init__(self, settings: Settings):
        self._s = settings
        self._lock = threading.Lock()
        self._rois: tuple[Roi, ...] = (Roi(settings.default_roi, ()),)
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._refresh_loop, daemon=True)
        self.source = "mặc định (toàn khung hình)"
        self.last_error = ""
        self.last_refresh: float | None = None

    def start(self):
        self._thread.start()

    def stop(self):
        self._stop.set()

    @property
    def rois(self) -> tuple[Roi, ...]:
        with self._lock:
            return self._rois

    def assign(self, box) -> Roi | None:
        """The region a person stands in. Their feet decide, not their head: a worker leaning over a line is still on their side of it."""
        point = anchor(box)
        for roi in self.rois:
            if roi.contains(point):
                return roi
        return None

    def refresh(self) -> bool:
        try:
            response = requests.get(self._s.ai_config_endpoint(),
                                    params={"device": self._s.device_id, "channel": self._s.channel, "key": self._s.receiver_key},
                                    timeout=8)
        except requests.RequestException as ex:
            self.last_error = f"{type(ex).__name__}: {str(ex)[:160]}"
            return False
        if response.status_code == 404:
            # An older station without the endpoint: keep the whole-frame fallback, and say so plainly.
            self.last_error = "trạm chưa có /api/v1/parking/ai-config (bản cũ): dùng toàn khung hình"
            return False
        if response.status_code != 200:
            self.last_error = f"HTTP {response.status_code}: {response.text[:160]}"
            return False
        try:
            payload = response.json()
        except ValueError:
            self.last_error = "trạm trả về nội dung không phải JSON"
            return False
        parsed = self._parse(payload)
        if not parsed:
            self.last_error = "trạm chưa khai báo vùng nào cho thiết bị này: dùng toàn khung hình"
            with self._lock:
                self._rois = (Roi(self._s.default_roi, ()),)
            self.source = "mặc định (toàn khung hình)"
            self.last_refresh = time.time()
            return True
        with self._lock:
            self._rois = parsed
        self.source = f"trạm ({len(parsed)} vùng)"
        self.last_error = ""
        self.last_refresh = time.time()
        return True

    @staticmethod
    def _parse(payload) -> tuple[Roi, ...]:
        rows = payload.get("rois", []) if isinstance(payload, dict) else payload
        out: list[Roi] = []
        for row in rows if isinstance(rows, list) else []:
            if not isinstance(row, dict):
                continue
            points = []
            for point in row.get("polygon") or []:
                if isinstance(point, (list, tuple)) and len(point) == 2:
                    try:
                        points.append((float(point[0]), float(point[1])))
                    except (TypeError, ValueError):
                        points = []
                        break
            out.append(Roi(str(row.get("roiId") or ""), tuple(points), str(row.get("label") or "")))
        return tuple(out)

    def _refresh_loop(self):
        while not self._stop.is_set():
            self.refresh()
            self._stop.wait(self._s.roi_refresh_seconds)
