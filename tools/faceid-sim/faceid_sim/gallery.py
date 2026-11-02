"""The terminal's enrolled faces, keyed by the terminal's own user code (PIN), like a Face-S device's memory."""
import json
import re
import threading
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

PIN_PATTERN = re.compile(r"^[A-Z0-9]{1,20}$")
# Reserved: the code the simulator sends for a face it does not know.
STRANGER = "STRANGER"


def normalize_pin(pin: str) -> str:
    """Same transform as the station's device profile (trim, upper), so the PIN shown here is the one to type on the cloud."""
    return pin.strip().upper()


@dataclass
class Person:
    pin: str
    name: str
    enrolled_utc: str
    samples: int


@dataclass
class Match:
    pin: str | None
    name: str | None
    score: float          # best cosine similarity, also when below the threshold


class Gallery:
    def __init__(self, directory: Path):
        self._dir = directory
        self._dir.mkdir(parents=True, exist_ok=True)
        self._lock = threading.Lock()
        self._people: dict[str, Person] = {}
        self._vectors: dict[str, np.ndarray] = {}
        self._load()

    def _load(self):
        meta, vectors = self._dir / "people.json", self._dir / "embeddings.npz"
        if meta.exists():
            self._people = {p["pin"]: Person(**p) for p in json.loads(meta.read_text(encoding="utf-8"))}
        if vectors.exists():
            with np.load(vectors) as data:
                self._vectors = {pin: data[pin] for pin in data.files if pin in self._people}

    def _save(self):
        # Write to temporary files, then replace: a crash mid-save never leaves a half-written gallery.
        meta, vectors = self._dir / "people.json", self._dir / "embeddings.npz"
        tmp_meta, tmp_vectors = meta.with_suffix(".tmp"), self._dir / "embeddings.tmp.npz"
        tmp_meta.write_text(json.dumps([asdict(p) for p in self._people.values()], ensure_ascii=False, indent=1), encoding="utf-8")
        np.savez(tmp_vectors, **self._vectors)
        tmp_meta.replace(meta)
        tmp_vectors.replace(vectors)

    def people(self) -> list[Person]:
        with self._lock:
            return sorted(self._people.values(), key=lambda p: p.pin)

    def enroll(self, pin: str, name: str, vectors: list[np.ndarray]) -> Person:
        pin = normalize_pin(pin)
        if not PIN_PATTERN.match(pin) or pin == STRANGER:
            raise ValueError("PIN: 1-20 letters or digits, not STRANGER.")
        if not vectors:
            raise ValueError("No usable face in the photos.")
        with self._lock:
            person = Person(pin, name.strip() or pin, datetime.now(timezone.utc).isoformat(timespec="seconds"), len(vectors))
            self._people[pin] = person
            self._vectors[pin] = np.stack(vectors).astype(np.float32)
            self._save()
            return person

    def remove(self, pin: str) -> bool:
        pin = normalize_pin(pin)
        with self._lock:
            if self._people.pop(pin, None) is None:
                return False
            self._vectors.pop(pin, None)
            self._save()
            return True

    def match(self, vector: np.ndarray, threshold: float) -> Match:
        """Best sample over all people (vectors are L2-normalised, so the dot product is the cosine similarity)."""
        with self._lock:
            best_pin, best = None, -1.0
            for pin, samples in self._vectors.items():
                score = float(np.max(samples @ vector))
                if score > best:
                    best_pin, best = pin, score
            if best_pin is None or best < threshold:
                return Match(None, None, max(best, 0.0))
            return Match(best_pin, self._people[best_pin].name, best)
