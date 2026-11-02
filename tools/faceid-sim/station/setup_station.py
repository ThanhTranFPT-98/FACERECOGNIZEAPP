"""
Configures a running station for the face-ID demo through its own admin API (same calls as the UI):
  1. device profile  faceid-sim      (how to read the simulator's messages)
  2. lane            PED-IN           (pedestrian entry, FaceOnly, simulated barrier, auto-open)
  3. camera          FACESIM-01       (the simulator, bound to the lane)
Safe to run again: every call is an upsert.

  STATION_PASSWORD=... python station/setup_station.py --station http://localhost:5085
"""
import argparse
import json
import os
import sys
from pathlib import Path

import requests

HERE = Path(__file__).parent
SAMPLE = {"event": "verify", "eventId": "sample-1", "device": {"sn": "FACESIM-01", "model": "DSA FaceID Simulator"},
          "channel": "1", "pin": "0007", "score": 82.4, "time": "2026-11-02T22:10:05+07:00"}


class Station:
    def __init__(self, url: str):
        self.url = url.rstrip("/")
        self.http = requests.Session()
        self.token = ""

    def _csrf(self):
        # Antiforgery tokens are bound to the signed-in user, so a new one is fetched after login.
        r = self.http.get(f"{self.url}/api/auth/csrf", timeout=10); r.raise_for_status()
        self.token = r.json()["token"]

    def login(self, user: str, password: str):
        self._csrf()
        r = self.http.post(f"{self.url}/api/auth/login", json={"username": user, "password": password}, headers={"X-CSRF-Token": self.token}, timeout=10)
        if r.status_code != 200:
            sys.exit(f"Login failed ({r.status_code}): {r.text[:200]}")
        self._csrf()

    def put(self, path: str, body: dict) -> dict:
        r = self.http.put(f"{self.url}{path}", json=body, headers={"X-CSRF-Token": self.token}, timeout=15)
        if r.status_code != 200:
            sys.exit(f"PUT {path} failed ({r.status_code}): {r.text[:400]}")
        return r.json() if r.content else {}


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--station", default=os.environ.get("FACESIM_STATION_URL", "http://localhost:5085"))
    p.add_argument("--user", default="admin")
    p.add_argument("--profile", default="faceid-sim")
    p.add_argument("--lane", default="PED-IN")
    p.add_argument("--device", default=os.environ.get("FACESIM_DEVICE_ID", "FACESIM-01"))
    p.add_argument("--channel", default="1")
    # ArcFace cosine scores of a genuine match are often 0.5-0.75, so the station's default 0.8 would send most passes to review.
    p.add_argument("--face-threshold", type=float, default=0.55)
    # The station never trusts a device more than this; 1.0 = take the reported score as is (demo only, not evaluated on site).
    p.add_argument("--trusted", type=float, default=1.0)
    args = p.parse_args()
    password = os.environ.get("STATION_PASSWORD") or sys.exit("Set STATION_PASSWORD (the station admin password).")

    station = Station(args.station)
    station.login(args.user, password)
    print(f"Signed in to {station.url} as {args.user}")

    profile = station.put("/api/v1/parking/device-profiles", {
        "id": args.profile, "name": "FaceID Simulator (Face-S stand-in)", "enabled": True, "format": "json",
        "definition": (HERE / "faceid-sim.profile.json").read_text(encoding="utf-8"),
        "sample": json.dumps(SAMPLE, ensure_ascii=False)})
    print(f"  profile {profile['id']} v{profile['version']}")

    station.put("/api/v1/parking/lanes", {
        "id": args.lane, "direction": "Entry", "subject": "Person", "policy": "FaceOnly", "faceThreshold": args.face_threshold,
        "shadowMode": False, "simulation": True, "autoOpen": True})
    print(f"  lane {args.lane}: pedestrian entry, FaceOnly >= {args.face_threshold}, simulated barrier, auto-open")

    station.put("/api/v1/parking/cameras", {
        "id": args.device, "name": "FaceID Simulator", "laneId": args.lane, "enabled": True, "record24x7": False,
        "externalDevice": args.device, "externalChannel": args.channel, "trustedConfidence": args.trusted, "silentAlertSeconds": 120})
    print(f"  camera {args.device} (channel {args.channel}) -> {args.lane}, trusted confidence {args.trusted}")
    print(f"\nSimulator target: {station.url}/api/v1/parking/receiver/{args.profile}?key=<Receiver:Key>")


if __name__ == "__main__":
    main()
