"""Explicitly configured camera ingestion; never invents safety-sensor heartbeats."""
import argparse
import os
import time
import uuid
import threading
import cv2
import requests

class LatestCamera:
    """Continuously drain the stream so HTTP/OCR latency does not queue old camera frames."""
    def __init__(self, source):
        self.source = source
        self.lock = threading.Lock()
        self.stopping = threading.Event()
        self.frame = None
        self.stamp = 0.
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self):
        camera = cv2.VideoCapture(self.source)
        try:
            while not self.stopping.is_set():
                if not camera.isOpened():
                    self.stopping.wait(2); camera.open(self.source); continue
                ok, frame = camera.read()
                if not ok:
                    camera.release(); self.stopping.wait(1); continue
                with self.lock:
                    self.frame, self.stamp = frame, time.monotonic()
        finally:
            camera.release()

    def snapshot(self, after=0.):
        with self.lock:
            if self.frame is None or self.stamp <= after or time.monotonic() - self.stamp > 1:
                return None, after
            return self.frame.copy(), self.stamp

    def close(self):
        self.stopping.set(); self.thread.join(timeout=2)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, help="Camera index or RTSP URL")
    parser.add_argument("--lane", required=True)
    parser.add_argument("--api", default="http://127.0.0.1:5085")
    parser.add_argument("--interval", type=float, default=3)
    args = parser.parse_args()
    key = os.environ.get("PARKING_DEVICE_KEY", "")
    if len(key) < 32:
        raise SystemExit("Set PARKING_DEVICE_KEY to the configured device credential (32+ characters).")
    source = int(args.source) if args.source.isdigit() else args.source
    camera = LatestCamera(source)
    session = requests.Session()
    session.headers["X-Device-Key"] = key
    try:
        while True:
            frames = []
            stamp = 0.
            deadline = time.monotonic() + 2
            while len(frames) < 3 and time.monotonic() < deadline:
                frame, stamp = camera.snapshot(after=stamp)
                if frame is None:
                    time.sleep(.1); continue
                encoded, data = cv2.imencode(".jpg", frame)
                if encoded:
                    frames.append(('images', ('frame.jpg', data.tobytes(), 'image/jpeg')))
                time.sleep(.1)
            if len(frames) < 2:
                print("Camera has no fresh burst; retrying", flush=True)
                time.sleep(1); continue
            correlation = str(uuid.uuid4())
            # Bounded retries retain the correlation ID. The API deduplicates events.
            for attempt in range(3):
                try:
                    response = session.post(args.api.rstrip('/') + "/api/v1/parking/device/capture-burst",
                        data={"laneId": args.lane, "correlationId": correlation},
                        files=frames, timeout=30)
                    response.raise_for_status()
                    print("Observation:", response.json().get("outcome"), flush=True)
                    break
                except requests.RequestException:
                    print("Capture delivery failed; attempt", attempt + 1, flush=True)
                    time.sleep(2 ** attempt)
            time.sleep(max(1, args.interval))
    finally:
        camera.close(); session.close()

if __name__ == "__main__":
    main()
