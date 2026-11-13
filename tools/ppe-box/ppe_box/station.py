"""
What the AI box sends to the station, and how.

A PPE message is a state report, not an event: the station keeps the last few readings per lane (PpeCache)
and decides when a worker is read. So an undelivered report is thrown away rather than queued - a two minute
old picture of someone's helmet would be worse than silence. One report per interval, even with nobody in
front of the camera, is also what keeps the station's "camera alive" clock fresh.
"""
import threading
from dataclasses import dataclass, field
from datetime import datetime, timezone

import requests

from .config import Settings


def _now_utc() -> str:
    # The station only trusts the camera clock when it is within 5 seconds of its own; UTC keeps that honest.
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def ppe_message(settings: Settings, rois: list[dict], when: str | None = None) -> dict:
    """One state report in the station's ppe/1 format (PpeMessage.Parse)."""
    return {
        "deviceId": settings.device_id,
        "channel": settings.channel,
        "capturedUtc": when or _now_utc(),
        "rois": rois,
    }


def roi_payload(roi_id: str, tracks: list[dict]) -> dict:
    """One region of the frame. The station reads items and track only when exactly one person is inside."""
    return {"roiId": roi_id, "peopleInRoi": len(tracks), "tracks": tracks}


@dataclass
class Report:
    at: str
    rois: int
    people: int
    status: str = "sending"      # sending | sent | refused | failed
    detail: str = ""
    message: dict = field(default_factory=dict, repr=False)

    def public(self) -> dict:
        return {"at": self.at, "rois": self.rois, "people": self.people, "status": self.status, "detail": self.detail}


class StationLink:
    """Posts the latest report; never retries an old one."""

    def __init__(self, settings: Settings, log_size: int = 50):
        self._s = settings
        self._http = requests.Session()
        self._lock = threading.Lock()
        self._log: list[Report] = []
        self._log_size = log_size
        self.sent = 0
        self.failed = 0

    def send(self, rois: list[dict]) -> Report:
        message = ppe_message(self._s, rois)
        report = Report(message["capturedUtc"], len(rois), sum(len(r.get("tracks", [])) for r in rois), message=message)
        if len(self._s.receiver_key) < 32:
            report.status, report.detail = "refused", "PPEBOX_RECEIVER_KEY chưa đặt (32+ ký tự, bằng Receiver:Key của trạm)"
            self._record(report)
            return report
        try:
            response = self._http.post(self._s.receiver_endpoint(), params={"key": self._s.receiver_key}, json=message, timeout=8)
            if response.status_code == 200:
                report.status, report.detail = "sent", "trạm đã nhận"
                self.sent += 1
            else:
                hint = {401: "sai khoá receiver",
                        404: f"trạm không có adapter '{self._s.profile}'"}.get(response.status_code, response.text[:160])
                report.status, report.detail = "refused", f"HTTP {response.status_code}: {hint}"
                self.failed += 1
        except requests.RequestException as ex:
            report.status, report.detail = "failed", f"{type(ex).__name__}: {str(ex)[:160]}"
            self.failed += 1
        self._record(report)
        return report

    def _record(self, report: Report):
        with self._lock:
            self._log.insert(0, report)
            del self._log[self._log_size:]

    def log(self) -> list[dict]:
        with self._lock:
            return [r.public() for r in self._log]

    def last(self) -> dict | None:
        with self._lock:
            return self._log[0].public() if self._log else None
