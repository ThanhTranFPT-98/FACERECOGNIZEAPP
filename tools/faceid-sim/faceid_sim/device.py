"""What the terminal sends to the station, and how: a buffered, in-order sender plus a heartbeat, like a real device."""
import base64
import collections
import itertools
import threading
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone

import requests

from .config import Settings


def _now_local() -> str:
    # ISO 8601 with offset: the station converts it to UTC whatever the time zone of this machine (WSL often runs in UTC).
    return datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds")


def verify_message(settings: Settings, pin: str, score: float, photo: bytes, event_id: str | None = None, when: str | None = None) -> dict:
    """One verification, in the terminal's own format. The station's device profile (station/faceid-sim.profile.json) maps it."""
    return {
        "event": "verify",
        "eventId": event_id or uuid.uuid4().hex,
        "device": {"sn": settings.device_id, "model": "DSA FaceID Simulator"},
        "channel": settings.channel,
        "pin": pin,
        "score": round(min(max(score, 0.0), 1.0) * 100, 1),     # 0-100, like most terminals
        "time": when or _now_local(),
        "photo": base64.b64encode(photo).decode("ascii") if photo else "",
    }


def heartbeat_message(settings: Settings) -> dict:
    # Skipped by the profile's "match", but the station still marks the camera alive because deviceId is a "$." path.
    return {"event": "heartbeat", "device": {"sn": settings.device_id, "model": "DSA FaceID Simulator"}, "time": _now_local()}


@dataclass
class Delivery:
    id: int
    pin: str
    score: float
    created: str
    message: dict = field(repr=False)
    status: str = "queued"          # queued | sent | retrying | refused
    attempts: int = 0
    detail: str = ""

    def public(self) -> dict:
        return {"id": self.id, "pin": self.pin, "score": round(self.score, 3), "created": self.created,
                "status": self.status, "attempts": self.attempts, "detail": self.detail}


class StationLink:
    """Sends events oldest first. A network error or 5xx keeps the event and retries with back-off; 4xx is final."""

    def __init__(self, settings: Settings, max_buffer: int = 500):
        self._s = settings
        self._http = requests.Session()
        self._ids = itertools.count(1)
        self._queue: collections.deque[Delivery] = collections.deque()
        self._log: collections.deque[Delivery] = collections.deque(maxlen=100)
        self._max = max_buffer
        self._wake = threading.Event()
        self._stop = threading.Event()
        self.last_heartbeat: dict = {"ok": None, "at": None, "detail": "not sent yet"}
        self._threads = [threading.Thread(target=self._send_loop, daemon=True), threading.Thread(target=self._heartbeat_loop, daemon=True)]

    def start(self):
        for t in self._threads:
            t.start()

    def stop(self):
        self._stop.set(); self._wake.set()

    def submit(self, message: dict, pin: str, score: float) -> Delivery:
        item = Delivery(next(self._ids), pin, score, message["time"], message)
        if len(self._queue) >= self._max:
            dropped = self._queue.popleft()
            dropped.status, dropped.detail = "refused", "buffer full, dropped oldest"
        self._queue.append(item)
        self._log.appendleft(item)
        self._wake.set()
        return item

    def log(self) -> list[dict]:
        return [d.public() for d in list(self._log)]

    @property
    def backlog(self) -> int:
        return len(self._queue)

    def _post(self, message: dict) -> requests.Response:
        return self._http.post(self._s.receiver_endpoint(), params={"key": self._s.receiver_key}, json=message, timeout=10)

    def _send_loop(self):
        backoff = 2.0
        while not self._stop.is_set():
            if not self._queue:
                self._wake.wait(1); self._wake.clear(); continue
            item = self._queue[0]
            if len(self._s.receiver_key) < 32:
                item.status, item.detail = "retrying", "FACESIM_RECEIVER_KEY is not set (32+ characters, = Receiver:Key of the station)"
                self._stop.wait(5); continue
            item.attempts += 1
            try:
                response = self._post(item.message)
                if response.status_code == 200:
                    item.status, item.detail = "sent", "station stored it"
                elif response.status_code < 500:
                    hint = {401: "wrong receiver key", 404: f"profile '{self._s.profile}' not found on the station"}.get(response.status_code, response.text[:200])
                    item.status, item.detail = "refused", f"HTTP {response.status_code}: {hint}"
                else:
                    raise requests.HTTPError(f"HTTP {response.status_code}")
                self._queue.popleft(); backoff = 2.0
            except requests.RequestException as ex:
                item.status, item.detail = "retrying", f"{type(ex).__name__}: {str(ex)[:160]}"
                self._stop.wait(backoff); backoff = min(backoff * 2, 60)

    def _heartbeat_loop(self):
        while not self._stop.is_set():
            if len(self._s.receiver_key) >= 32:
                try:
                    response = self._post(heartbeat_message(self._s))
                    self.last_heartbeat = {"ok": response.status_code == 200, "at": _now_local(), "detail": f"HTTP {response.status_code}"}
                except requests.RequestException as ex:
                    self.last_heartbeat = {"ok": False, "at": _now_local(), "detail": type(ex).__name__}
            else:
                self.last_heartbeat = {"ok": False, "at": _now_local(), "detail": "FACESIM_RECEIVER_KEY is not set"}
            self._stop.wait(self._s.heartbeat_seconds)
