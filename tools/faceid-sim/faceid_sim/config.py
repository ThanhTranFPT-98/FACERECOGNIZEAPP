"""Settings from environment variables, so the same code runs on a laptop, in WSL2 or on a test PC."""
import os
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]          # DSA_Vehicles/
AISERVICE = REPO / "AiService"


def _env(name: str, default: str) -> str:
    value = os.environ.get(name, "").strip()
    return value or default


@dataclass(frozen=True)
class Settings:
    # Identity of the simulated terminal. The station matches it against Camera.ExternalDevice (normalised: letters/digits, upper case).
    device_id: str = field(default_factory=lambda: _env("FACESIM_DEVICE_ID", "FACESIM-01"))
    channel: str = field(default_factory=lambda: _env("FACESIM_CHANNEL", "1"))
    # Where the station receives device messages: …/api/v1/parking/receiver/<profile>?key=<Receiver:Key>
    station_url: str = field(default_factory=lambda: _env("FACESIM_STATION_URL", "http://localhost:5085").rstrip("/"))
    profile: str = field(default_factory=lambda: _env("FACESIM_PROFILE", "faceid-sim"))
    receiver_key: str = field(default_factory=lambda: os.environ.get("FACESIM_RECEIVER_KEY", ""))
    # The terminal's own memory: enrolled faces never leave this folder (biometric data, not for git or the cloud).
    data_dir: Path = field(default_factory=lambda: Path(_env("FACESIM_DATA", str(Path.home() / ".local/share/dsa-faceid-sim"))))
    detector_weights: Path = field(default_factory=lambda: Path(_env("FACESIM_SCRFD", str(AISERVICE / "face_detection/scrfd/weights/scrfd_2.5g_bnkps.onnx"))))
    recognizer_weights: Path = field(default_factory=lambda: Path(_env("FACESIM_ARCFACE", str(AISERVICE / "face_recognition/arcface/weights/arcface_r50.pth"))))
    # Cosine similarity of ArcFace embeddings. 0.5 is what AiService uses; not evaluated on site.
    match_threshold: float = field(default_factory=lambda: float(_env("FACESIM_MATCH_THRESHOLD", "0.5")))
    # One event per person per pass: the same answer for N frames in a row, then a pause for that PIN.
    stable_frames: int = field(default_factory=lambda: int(_env("FACESIM_STABLE_FRAMES", "3")))
    cooldown_seconds: float = field(default_factory=lambda: float(_env("FACESIM_COOLDOWN", "10")))
    stranger_cooldown_seconds: float = field(default_factory=lambda: float(_env("FACESIM_STRANGER_COOLDOWN", "15")))
    heartbeat_seconds: float = field(default_factory=lambda: float(_env("FACESIM_HEARTBEAT", "30")))
    # Faces smaller than this (pixels, shorter side) are too far from the terminal to count.
    min_face_px: int = field(default_factory=lambda: int(_env("FACESIM_MIN_FACE", "80")))

    def receiver_endpoint(self) -> str:
        return f"{self.station_url}/api/v1/parking/receiver/{self.profile}"
