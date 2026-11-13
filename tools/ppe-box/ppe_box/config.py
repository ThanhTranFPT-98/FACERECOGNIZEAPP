"""Settings from environment variables, so the same code runs on this laptop and on the AI box at the mine."""
import os
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]          # DSA_Vehicles/
AISERVICE = REPO / "AiService"

# Which PPE items this box reports. The station decides what is required per lane; the box only observes.
DEFAULT_ITEMS = "helmet,vest"

# Model class name -> PPE item. A leading "!" means the class proves the item is MISSING
# ("no-helmet" is evidence of a bare head, not merely the absence of a helmet box).
DEFAULT_ITEM_CLASSES = (
    "helmet:helmet,hardhat:helmet,hard-hat:helmet,"
    "no-helmet:!helmet,nohelmet:!helmet,no-hardhat:!helmet,head:!helmet,"
    "vest:vest,safety-vest:vest,safety vest:vest,"
    "no-vest:!vest,novest:!vest,no-safety-vest:!vest"
)
DEFAULT_PERSON_CLASSES = "person,worker"


def _env(name: str, default: str) -> str:
    return os.environ.get(name, "").strip() or default


def _pairs(raw: str) -> dict[str, str]:
    out: dict[str, str] = {}
    for part in raw.split(","):
        if ":" in part:
            key, value = part.split(":", 1)
            out[key.strip().lower()] = value.strip().lower()
    return out


@dataclass(frozen=True)
class Settings:
    # Identity of the box. The station matches it against Camera.ExternalDevice (letters/digits, upper case).
    device_id: str = field(default_factory=lambda: _env("PPEBOX_DEVICE_ID", "PPEBOX-01"))
    channel: str = field(default_factory=lambda: _env("PPEBOX_CHANNEL", "1"))
    station_url: str = field(default_factory=lambda: _env("PPEBOX_STATION_URL", "http://localhost:5085").rstrip("/"))
    # The station's built-in code adapter for PPE messages; not a JSON device profile.
    profile: str = field(default_factory=lambda: _env("PPEBOX_PROFILE", "ppe"))
    receiver_key: str = field(default_factory=lambda: os.environ.get("PPEBOX_RECEIVER_KEY", ""))

    # face  : SCRFD finds the people (a face means a person); the PPE items come from the control panel.
    # model : an ONNX detector finds people and PPE items. Needs PPEBOX_MODEL.
    # manual: no camera at all; the control panel alone decides who is in the ROI.
    mode: str = field(default_factory=lambda: _env("PPEBOX_MODE", "face").lower())
    model_path: str = field(default_factory=lambda: _env("PPEBOX_MODEL", ""))
    model_classes: str = field(default_factory=lambda: _env("PPEBOX_CLASSES", ""))
    model_threshold: float = field(default_factory=lambda: float(_env("PPEBOX_MODEL_THRESHOLD", "0.35")))
    item_classes: dict[str, str] = field(default_factory=lambda: _pairs(_env("PPEBOX_ITEM_CLASSES", DEFAULT_ITEM_CLASSES)))
    person_classes: tuple[str, ...] = field(default_factory=lambda: tuple(c.strip().lower() for c in _env("PPEBOX_PERSON_CLASSES", DEFAULT_PERSON_CLASSES).split(",") if c.strip()))
    items: tuple[str, ...] = field(default_factory=lambda: tuple(i.strip().lower() for i in _env("PPEBOX_ITEMS", DEFAULT_ITEMS).split(",") if i.strip()))

    detector_weights: Path = field(default_factory=lambda: Path(_env("PPEBOX_SCRFD", str(AISERVICE / "face_detection/scrfd/weights/scrfd_2.5g_bnkps.onnx"))))
    min_face_px: int = field(default_factory=lambda: int(_env("PPEBOX_MIN_FACE", "50")))

    # A PPE message is a state report, not an event: one every interval, even when nobody is there,
    # which is also what keeps the station's "camera alive" clock fresh.
    send_interval: float = field(default_factory=lambda: float(_env("PPEBOX_SEND_INTERVAL", "1.0")))
    # A track that has not been seen for this long is forgotten, and its id is never reused.
    track_ttl: float = field(default_factory=lambda: float(_env("PPEBOX_TRACK_TTL", "3.0")))
    track_iou: float = field(default_factory=lambda: float(_env("PPEBOX_TRACK_IOU", "0.3")))

    # ROI polygons come from the station (it is the only place the operator configures anything).
    # Until one is configured there, this single ROI id is used; empty means "the whole image",
    # which is how the station maps a camera that covers exactly one lane.
    default_roi: str = field(default_factory=lambda: _env("PPEBOX_ROI", ""))
    roi_refresh_seconds: float = field(default_factory=lambda: float(_env("PPEBOX_ROI_REFRESH", "30")))

    def receiver_endpoint(self) -> str:
        return f"{self.station_url}/api/v1/parking/receiver/{self.profile}"

    def ai_config_endpoint(self) -> str:
        return f"{self.station_url}/api/v1/parking/ai-config"
