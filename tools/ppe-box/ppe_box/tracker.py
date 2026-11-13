"""
Keeps the same trackId on a person across frames.

The station pairs a PPE verdict with a FaceID event by track (CR-018) and re-checks the track before it opens
the barrier, so an id that jumps between people is worse than no id at all: an id is reused only when the new
box clearly overlaps the old one, and an id is never handed out twice.
"""
import itertools
import time
from dataclasses import dataclass, field

from .geometry import Box, iou


@dataclass
class Track:
    id: str
    box: Box
    first_seen: float
    last_seen: float
    frames: int = 1
    # Last value seen for each item, so a single blurred frame does not flip a verdict.
    items: dict[str, bool] = field(default_factory=dict)


class Tracker:
    def __init__(self, ttl: float = 3.0, min_iou: float = 0.3, prefix: str = "t"):
        self._ttl = ttl
        self._min_iou = min_iou
        self._ids = itertools.count(1)
        self._prefix = prefix
        self._tracks: list[Track] = []

    def update(self, boxes: list[Box], items: list[dict[str, bool]] | None = None, now: float | None = None) -> list[Track]:
        """Match this frame's boxes to the known tracks, greedily, best overlap first."""
        now = time.monotonic() if now is None else now
        items = items or [{} for _ in boxes]
        self._tracks = [t for t in self._tracks if now - t.last_seen <= self._ttl]

        pairs = sorted(
            ((iou(box, track.box), index, position)
             for index, box in enumerate(boxes)
             for position, track in enumerate(self._tracks)),
            key=lambda p: p[0], reverse=True)
        taken_boxes: set[int] = set()
        taken_tracks: set[int] = set()
        matched: dict[int, Track] = {}
        for score, index, position in pairs:
            if score < self._min_iou or index in taken_boxes or position in taken_tracks:
                continue
            taken_boxes.add(index)
            taken_tracks.add(position)
            matched[index] = self._tracks[position]

        seen: list[Track] = []
        for index, box in enumerate(boxes):
            track = matched.get(index)
            if track is None:
                track = Track(f"{self._prefix}-{next(self._ids)}", box, now, now)
                self._tracks.append(track)
            else:
                track.box = box
                track.last_seen = now
                track.frames += 1
            track.items.update(items[index])
            seen.append(track)
        return seen

    @property
    def live(self) -> list[Track]:
        now = time.monotonic()
        return [t for t in self._tracks if now - t.last_seen <= self._ttl]
