"""Device simulator for a running station (station plan §6): plays Face-S, the AI box and the plate cameras, and acts as the barrier controller.
No hardware. The station cannot tell these messages from a real device's.

Messages go to the receiver of the station, with its key:

    python scripts/simulator.py face  --station http://127.0.0.1:5000 --key RECEIVER_KEY --device ZK-01 --person 7
    python scripts/simulator.py ppe   --station ... --key ... --box AIBOX-1 --roi L1=t-1:helmet,vest,-boots --roi L2=
    python scripts/simulator.py plate --station ... --key ... --device LPR-IN --plate 30A12345 [--person 7]
    python scripts/simulator.py play  tests/scenarios/K1.yaml --station ... --key ...      # the device steps of a scenario, no checks

`--roi ID=TRACK:items` sends one person in that ROI (an item with a leading `-` is missing); `--roi ID=` sends an empty ROI;
repeat `--roi` for more ROIs, or give `TRACK:items` twice for the same ID to put two people in it.

The virtual barrier controller answers the station's open commands and sends the lane heartbeat the station needs before it opens a live lane:

    python scripts/simulator.py bridge --station http://127.0.0.1:5000 --device-key DEVICE_KEY --lanes flap-1,flap-2 --port 8099 --delay 2

then start the station with `Parking__EnableLiveBarrier=true Parking__BarrierUrl=http://127.0.0.1:8099 Parking__DeviceKey=…` and set the lane
to `simulation: false`. `--mode bad-ack` answers with the wrong command ID (the station records an Unknown outcome); `--mode offline` answers 503.
"""
import argparse
import http.server
import json
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = '/api/v1/parking'


# ---- messages (also used by tests/run_scenarios.py, so the runner and the simulator never drift apart) ----
def frame():
    return int(time.time() * 1000)   # real devices send a counter or a timestamp; identical bodies are stored once


def ppe_message(box, rois, seq=None):
    """rois: {roiId: [{'track': 't-1', 'items': {'helmet': True}}, ...]}  (an empty list is an empty ROI)."""
    return {'deviceId': box, 'channel': '1', 'frame': seq if seq is not None else frame(),
            'rois': [{'roiId': roi, 'tracks': [{'trackId': t['track'], 'items': t['items']} for t in tracks or []]} for roi, tracks in rois.items()]}


def face_message(device, person, confidence=.97, seq=None):
    return {'deviceId': device, 'personId': person, 'confidence': confidence, 'seq': seq if seq is not None else frame()}


def plate_message(device, plate, confidence=.99, person=None, seq=None):
    body = {'deviceId': device, 'plate': plate, 'confidence': confidence, 'seq': seq if seq is not None else frame()}
    if person is not None: body['personId'] = person
    return body


def send(station, key, adapter, body):
    """POST to the station's receiver. Returns the HTTP status."""
    url = f'{station.rstrip("/")}{ROOT}/receiver/{adapter}?key={urllib.parse.quote(key)}'
    request = urllib.request.Request(url, data=json.dumps(body).encode(), method='POST', headers={'Content-Type': 'application/json'})
    try: return urllib.request.urlopen(request, timeout=10).status
    except urllib.error.HTTPError as error: return error.code


def parse_roi(text):
    """'L1=t-1:helmet,vest,-boots' → ('L1', {'track': 't-1', 'items': {...}}); 'L2=' → ('L2', None)."""
    roi, _, rest = text.partition('=')
    if not roi: raise ValueError(f'--roi needs ID=…, got "{text}"')
    if not rest: return roi, None
    track, _, items = rest.partition(':')
    return roi, {'track': track, 'items': {i.lstrip('-'): not i.startswith('-') for i in items.split(',') if i}}


# ---- virtual barrier controller ----
def bridge(station, device_key, lanes, host, port, delay, mode):
    acked = {}   # lane -> last acknowledged command (the "loop" then reports the passage)

    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *args): pass
        def do_POST(self):
            if self.headers.get('Transfer-Encoding') == 'chunked':   # .NET HttpClient JsonContent sends chunked bodies
                chunks = []
                while (size := int(self.rfile.readline().strip(), 16)) > 0:
                    chunks.append(self.rfile.read(size)); self.rfile.read(2)
                self.rfile.readline(); raw = b''.join(chunks)
            else: raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
            command = json.loads(raw)
            time.sleep(delay)
            if mode == 'offline':
                self.send_response(503); self.end_headers(); return
            if mode == 'ack': acked[command['laneId']] = command['commandId']
            answer = {'commandId': command['commandId'] if mode == 'ack' else '00000000-0000-0000-0000-000000000000', 'accepted': True}
            self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
            self.wfile.write(json.dumps(answer).encode())
            print(f'{time.strftime("%H:%M:%S")} open {command["laneId"]} {command["commandId"]} → {mode}', flush=True)

    def heartbeats(lane):
        reported = None
        while True:
            body = {'laneId': lane, 'safetyHealthy': True, 'vehiclePresent': True, 'obstructed': False, 'barrierState': 'Closed'}
            if acked.get(lane) and acked[lane] != reported:
                time.sleep(.5); body['passageCommandId'] = acked[lane]; reported = acked[lane]
            request = urllib.request.Request(f'{station.rstrip("/")}{ROOT}/device/heartbeat', data=json.dumps(body).encode(), method='POST',
                                             headers={'Content-Type': 'application/json', 'X-Device-Key': device_key})
            try: urllib.request.urlopen(request, timeout=5).read()
            except (urllib.error.URLError, OSError) as error: print(f'heartbeat {lane}: {error}', flush=True)
            time.sleep(.5)

    for lane in lanes: threading.Thread(target=heartbeats, args=(lane,), daemon=True).start()
    server = http.server.ThreadingHTTPServer((host, port), Handler)
    print(f'Virtual barrier controller on http://{host}:{server.server_port} for lanes {", ".join(lanes)}; mode {mode}, answers in {delay} s.', flush=True)
    return server


# ---- command line ----
def play(path, station, key, speed):
    import yaml
    scenario = yaml.safe_load(Path(path).read_text(encoding='utf-8'))
    for number, step in enumerate(scenario.get('steps', []), 1):
        for name, argument in step.items():
            if name == 'wait': time.sleep(argument / speed); print(f'step {number}: wait {argument} s')
            elif name == 'ppe': print(f'step {number}: ppe → {send(station, key, "ppe", ppe_message(argument.get("box", "AIBOX-1"), argument["rois"]))}')
            elif name == 'face':
                for _ in range(argument.get('repeat', 1)):
                    body = face_message(argument['device'], argument['person'], argument.get('confidence', .97))
                    if argument.get('identical'): body['seq'] = 1
                    print(f'step {number}: face {argument["person"]} → {send(station, key, "generic", body)}')
            elif name == 'plate': print(f'step {number}: plate {argument["plate"]} → {send(station, key, "generic", plate_message(argument["device"], argument["plate"], argument.get("confidence", .99), argument.get("person")))}')
            else: print(f'step {number}: {name} is not a device message; skipped (a check or a guard action)')


def main(argv=None):
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')   # a piped Windows console is cp1252
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    def common(p):
        p.add_argument('--station', required=True, help='station base URL, e.g. http://127.0.0.1:5000')
        p.add_argument('--key', required=True, help='the station receiver key (Receiver:Key)')
    p = sub.add_parser('face'); common(p)
    p.add_argument('--device', required=True); p.add_argument('--person', type=int, required=True); p.add_argument('--confidence', type=float, default=.97)
    p = sub.add_parser('ppe'); common(p)
    p.add_argument('--box', default='AIBOX-1'); p.add_argument('--roi', action='append', required=True)
    p = sub.add_parser('plate'); common(p)
    p.add_argument('--device', required=True); p.add_argument('--plate', required=True); p.add_argument('--confidence', type=float, default=.99); p.add_argument('--person', type=int)
    p = sub.add_parser('play'); common(p)
    p.add_argument('scenario'); p.add_argument('--speed', type=float, default=1.0, help='2 = waits take half as long')
    p = sub.add_parser('bridge')
    p.add_argument('--station', required=True); p.add_argument('--device-key', required=True); p.add_argument('--lanes', required=True, help='comma-separated lane IDs')
    p.add_argument('--host', default='127.0.0.1'); p.add_argument('--port', type=int, default=8099); p.add_argument('--delay', type=float, default=2.0)
    p.add_argument('--mode', choices=['ack', 'bad-ack', 'offline'], default='ack')
    args = parser.parse_args(argv)

    if args.command == 'face':
        status = send(args.station, args.key, 'generic', face_message(args.device, args.person, args.confidence))
    elif args.command == 'plate':
        status = send(args.station, args.key, 'generic', plate_message(args.device, args.plate, args.confidence, args.person))
    elif args.command == 'ppe':
        rois = {}
        for text in args.roi:
            roi, person = parse_roi(text)
            rois.setdefault(roi, [])
            if person: rois[roi].append(person)
        status = send(args.station, args.key, 'ppe', ppe_message(args.box, rois))
    elif args.command == 'play':
        play(args.scenario, args.station, args.key, args.speed); return 0
    else:
        server = bridge(args.station, args.device_key, args.lanes.split(','), args.host, args.port, args.delay, args.mode)
        try: server.serve_forever()
        except KeyboardInterrupt: return 0
    print(f'{args.command} → HTTP {status}')
    return 0 if status < 300 else 1


if __name__ == '__main__':
    sys.exit(main())
