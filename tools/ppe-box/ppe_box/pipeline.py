"""
The box itself: frame in, state report out.

One place decides everything that is timing-sensitive, so the HTTP layer stays thin: how often a report
goes to the station, which region a person belongs to, and which PPE verdict is attached to a track.
"""
import threading
import time
from dataclasses import dataclass, field

import numpy as np

from .config import Settings
from .rois import RoiStore
from .station import StationLink, roi_payload
from .tracker import Tracker
from .vision import Vision, encode_jpeg


@dataclass
class Panel:
    """The control panel on the page. In face and manual mode it supplies the PPE verdict the model cannot."""

    items: dict[str, bool] = field(default_factory=dict)
    people: int = 0              # manual mode only: how many people the operator says are in the region
    roi_id: str | None = None    # manual mode only: which region they are standing in

    def public(self) -> dict:
        return {"items": dict(self.items), "people": self.people, "roiId": self.roi_id}


class Pipeline:
    def __init__(self, settings: Settings, vision: Vision, rois: RoiStore, link: StationLink):
        self._s = settings
        self._vision = vision
        self._rois = rois
        self._link = link
        self._tracker = Tracker(settings.track_ttl, settings.track_iou)
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._last_sent = 0.0
        self._last_frame_at = 0.0
        self._snapshot: bytes = b""
        self.panel = Panel(items={item: True for item in settings.items})
        self.frames = 0
        self._idle = threading.Thread(target=self._idle_loop, daemon=True)

    def start(self):
        self._idle.start()

    def stop(self):
        self._stop.set()

    # ---------- state ----------

    @property
    def snapshot(self) -> bytes:
        with self._lock:
            return self._snapshot

    @property
    def seconds_since_frame(self) -> float | None:
        return None if self._last_frame_at == 0 else time.monotonic() - self._last_frame_at

    def set_panel(self, items: dict[str, bool] | None, people: int | None, roi_id: str | None) -> Panel:
        with self._lock:
            if items is not None:
                self.panel.items = {str(k).lower(): bool(v) for k, v in items.items()}
            if people is not None:
                self.panel.people = max(0, int(people))
            if roi_id is not None:
                self.panel.roi_id = roi_id or None
            return self.panel

    # ---------- the frame path ----------

    def observe(self, frame: np.ndarray) -> dict:
        boxes, judged = self._vision.people(frame)
        # In face mode the model cannot see a helmet, so the panel answers for every person in view. Doing it
        # per person would need a UI that picks one, and a station only ever judges a single person in a region.
        items = [dict(self.panel.items) | found for found in judged] if self._s.mode == "face" else judged
        tracks = self._tracker.update(boxes, items)
        with self._lock:
            self._snapshot = encode_jpeg(frame)
            self._last_frame_at = time.monotonic()
            self.frames += 1
        grouped = self._group(tracks)
        report = self._maybe_send(grouped)
        return {
            "width": frame.shape[1], "height": frame.shape[0], "mode": self._vision.name,
            "people": [{"trackId": t.id, "box": [round(v, 4) for v in t.box], "items": t.items,
                        "roiId": self._roi_of(t.id, grouped)} for t in tracks],
            "rois": [r.public() for r in self._rois.rois],
            "report": report.public() if report else self._link.last(),
        }

    @staticmethod
    def _roi_of(track_id: str, grouped: dict[str, list[dict]]) -> str | None:
        for roi_id, tracks in grouped.items():
            if any(t["trackId"] == track_id for t in tracks):
                return roi_id
        return None

    def _group(self, tracks) -> dict[str, list[dict]]:
        """Every configured region appears in the report, empty ones included: "nobody there" is information."""
        grouped: dict[str, list[dict]] = {roi.id: [] for roi in self._rois.rois}
        for track in tracks:
            roi = self._rois.assign(track.box)
            if roi is None:
                continue                                  # outside every region: not this camera's business
            grouped.setdefault(roi.id, []).append({
                "trackId": track.id,
                "items": {k: bool(v) for k, v in track.items.items()},
                "box": [round(v, 4) for v in track.box],
            })
        return grouped

    def _manual_group(self) -> dict[str, list[dict]]:
        rois = self._rois.rois
        target = self.panel.roi_id if self.panel.roi_id is not None else rois[0].id if rois else ""
        grouped: dict[str, list[dict]] = {roi.id: [] for roi in rois}
        grouped.setdefault(target, [])
        for index in range(self.panel.people):
            # A stable id per slot: the station re-checks the track before it opens, so it must not churn.
            grouped[target].append({"trackId": f"m-{index + 1}", "items": dict(self.panel.items)})
        return grouped

    def _maybe_send(self, grouped: dict[str, list[dict]]):
        now = time.monotonic()
        if now - self._last_sent < self._s.send_interval:
            return None
        self._last_sent = now
        return self._link.send([roi_payload(roi_id, tracks) for roi_id, tracks in grouped.items()])

    def _idle_loop(self):
        """Keeps reporting when no frames arrive: an empty region in manual mode, or liveness with the tab closed."""
        while not self._stop.is_set():
            self._stop.wait(self._s.send_interval)
            if self._stop.is_set():
                return
            since = self.seconds_since_frame
            if self._s.mode == "manual":
                self._maybe_send(self._manual_group())
            elif since is None or since > 2 * self._s.send_interval:
                self._maybe_send({roi.id: [] for roi in self._rois.rois})
