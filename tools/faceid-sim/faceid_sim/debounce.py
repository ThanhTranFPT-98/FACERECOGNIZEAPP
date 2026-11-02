"""Turns a stream of per-frame answers into one event per pass, like a terminal that verifies once per presentation."""
import time
from typing import Callable

NO_FACE = None


class PassDetector:
    """
    Feed one label per frame: a PIN, gallery.STRANGER, or NO_FACE.
    An event fires when the same label has been seen `stable_frames` times in a row, at most once while the person stays
    in view, and not again for that label within its cooldown (a second pass straight after the first is the same pass).
    """

    def __init__(self, stable_frames: int, cooldown: float, stranger_cooldown: float, stranger: str,
                 clock: Callable[[], float] = time.monotonic):
        self._stable, self._cooldown, self._stranger_cooldown, self._stranger = max(1, stable_frames), cooldown, stranger_cooldown, stranger
        self._clock = clock
        self._label: str | None = NO_FACE
        self._streak = 0
        self._decided = False
        self._last_event: dict[str, float] = {}

    def observe(self, label: str | None) -> str | None:
        """Returns the label to report now, or None."""
        if label is NO_FACE:
            self._label, self._streak, self._decided = NO_FACE, 0, False
            return None
        if label != self._label:
            self._label, self._streak, self._decided = label, 0, False
        self._streak += 1
        if self._decided or self._streak < self._stable:
            return None
        # Decided for this presentation either way: inside the cooldown the pass is suppressed, not postponed.
        self._decided = True
        now = self._clock()
        wait = self._stranger_cooldown if label == self._stranger else self._cooldown
        if now - self._last_event.get(label, float("-inf")) < wait:
            return None
        self._last_event[label] = now
        return label

    @property
    def progress(self) -> tuple[str | None, int, int]:
        """(label, frames seen, frames needed) for the live view."""
        return self._label, min(self._streak, self._stable), self._stable
