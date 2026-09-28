"""Load test (GC-22/23): AI-camera messages on two live lanes with auto-open, a slow barrier bridge and sensor heartbeats.

Measures our part of the latency per vehicle:  t1 message received -> t2 decision -> t3 open command -> t4 controller ack.
Reference site: 100 vehicles / 15 min over 2 lanes = one vehicle every 18 s per lane. This test compresses it.

    python tests/load_test.py --vehicles 40 --lane-gap 3 --bridge-delay 2 --target 1.0

Exit code 1 when P95 of t1->t3 is above --target seconds, or when a vehicle got no command.
Needs a Debug build of the API (same as integration_parking.py). No hardware.
"""
import argparse, http.cookiejar, http.server, json, os, secrets, socket, statistics, subprocess, sys, tempfile, threading, time, urllib.error, urllib.request
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PNG = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII='
LANES = {'lane-a': 'AA-00-00-00-00-0A', 'lane-b': 'AA-00-00-00-00-0B'}

def free_port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); return s.getsockname()[1]

def parse(ts):
    return datetime.fromisoformat(ts.rstrip('Z')[:26]) if ts else None

def pct(values, p):
    if not values: return None
    values = sorted(values); k = (len(values) - 1) * p / 100; lo = int(k); hi = min(lo + 1, len(values) - 1)
    return values[lo] + (values[hi] - values[lo]) * (k - lo)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--vehicles', type=int, default=40)
    ap.add_argument('--lane-gap', type=float, default=3.0, help='seconds between vehicles on the same lane')
    ap.add_argument('--bridge-delay', type=float, default=2.0, help='seconds the fake barrier controller takes to answer')
    ap.add_argument('--target', type=float, default=1.0, help='P95 limit for t1->t3 in seconds')
    args = ap.parse_args()

    temp = tempfile.TemporaryDirectory(prefix='parking-load-')
    password, device_key, receiver_key = secrets.token_urlsafe(24), secrets.token_urlsafe(32), secrets.token_urlsafe(32)
    acked = {}   # lane -> last acknowledged command id (the "controller" then reports passage)
    class Bridge(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a): pass
        def do_POST(self):
            if self.headers.get('Transfer-Encoding') == 'chunked':   # HttpClient JsonContent sends chunked bodies
                chunks = []
                while (size := int(self.rfile.readline().strip(), 16)) > 0:
                    chunks.append(self.rfile.read(size)); self.rfile.read(2)
                self.rfile.readline(); raw = b''.join(chunks)
            else: raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
            body = json.loads(raw)
            time.sleep(args.bridge_delay)
            acked[body['laneId']] = body['commandId']
            self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
            self.wfile.write(json.dumps({'commandId': body['commandId'], 'accepted': True}).encode())
    bridge = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Bridge)
    threading.Thread(target=bridge.serve_forever, daemon=True).start()
    port = free_port(); base = f'http://127.0.0.1:{port}'
    env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'Parking__BootstrapPassword': password, 'Parking__DeviceKey': device_key,
           'ConnectionStrings__Parking': 'Data Source=' + str(Path(temp.name) / 'parking.db'), 'Parking__Provider': 'Sqlite',
           'Parking__EnableLiveBarrier': 'true', 'Parking__BarrierUrl': f'http://127.0.0.1:{bridge.server_port}',
           'Parking__EvidenceDirectory': str(Path(temp.name) / 'evidence'), 'Receiver__Key': receiver_key, 'Logging__LogLevel__Default': 'Warning'}
    log = open(Path(temp.name) / 'api.log', 'w+', encoding='utf-8')
    api = subprocess.Popen([os.environ.get('PARKING_DOTNET', 'dotnet'), str(ROOT / 'DSAVehicle/bin/Debug/net8.0/DSAVehicle.dll'), '--urls', base],
                           cwd=ROOT / 'DSAVehicle', env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    jar = http.cookiejar.CookieJar(); client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    def call(method, path, body=None, headers=None, use_client=True):
        headers = dict(headers or {})
        if method != 'GET' and use_client:
            headers['X-CSRF-Token'] = json.load(client.open(base + '/api/auth/csrf'))['token']
        data = None if body is None else json.dumps(body).encode()
        if data: headers['Content-Type'] = 'application/json'
        req = urllib.request.Request(base + path, data=data, method=method, headers=headers)
        try:
            r = (client.open if use_client else urllib.request.urlopen)(req, timeout=30)
        except urllib.error.HTTPError as e: r = e
        text = r.read().decode(); return r.status, (json.loads(text) if text.startswith(('{', '[')) else text)
    stop = threading.Event()
    try:
        for _ in range(150):
            try:
                if call('GET', '/api/auth/csrf')[0] == 200: break
            except OSError: time.sleep(.1)
        call('POST', '/api/auth/login', {'username': 'admin', 'password': password})
        root = '/api/v1/parking'
        plates = [f'30A{10000 + i}' for i in range(args.vehicles)]
        for plate in plates: call('POST', root + '/vehicles', {'plate': plate, 'owner': 'Load', 'kind': 'Car'})
        for lane, mac in LANES.items():
            call('PUT', root + '/lanes', {'id': lane, 'direction': 'Entry', 'policy': 'PlateOnly', 'plateThreshold': .9, 'faceThreshold': .8,
                 'shadowMode': False, 'simulation': False, 'autoOpen': True, 'cooldownSeconds': 2})
            call('PUT', root + '/cameras', {'id': 'cam-' + lane, 'name': lane, 'laneId': lane, 'externalDevice': mac, 'trustedConfidence': .99})
        # Lane controllers: healthy heartbeat every 0.5 s; report passage 0.5 s after an ack so the lane frees up.
        def controller(lane):
            reported = None
            while not stop.is_set():
                body = {'laneId': lane, 'safetyHealthy': True, 'vehiclePresent': True, 'obstructed': False, 'barrierState': 'Closed'}
                if acked.get(lane) and acked[lane] != reported:
                    time.sleep(.5); body['passageCommandId'] = acked[lane]; reported = acked[lane]
                call('POST', root + '/device/heartbeat', body, {'X-Device-Key': device_key}, use_client=False)
                time.sleep(.5)
        for lane in LANES: threading.Thread(target=controller, args=(lane,), daemon=True).start()
        time.sleep(1.5)
        # Traffic: both lanes at once (worst case for the shared lock), one vehicle per lane every --lane-gap seconds.
        print(f'Sending {args.vehicles} vehicles on 2 lanes, {args.lane_gap}s apart per lane, bridge answers in {args.bridge_delay}s ...')
        started = time.time()
        for i, plate in enumerate(plates):
            lane = 'lane-a' if i % 2 == 0 else 'lane-b'
            message = {'data': {'dev_net_info': [{'mac': LANES[lane]}], 'ai_snap_picture': {'PlateInfo': [
                {'GrpId': i, 'SnapId': plate, 'StrChn': 'CH1', 'StartTime': int(time.time()), 'PlateImg': PNG}]}}}
            urllib.request.urlopen(urllib.request.Request(f'{base}{root}/receiver/t1550?key={receiver_key}', data=json.dumps(message).encode(),
                                                          method='POST', headers={'Content-Type': 'application/json'}), timeout=30)
            if i % 2 == 1: time.sleep(max(0, started + (i // 2 + 1) * args.lane_gap - time.time()))
        time.sleep(args.bridge_delay + 3)
        stop.set()
        events = call('GET', root + '/search?pageSize=500&source=ThirdPartyCamera')[1]['items']
        commands = {c['eventId']: c for c in call('GET', root + '/snapshot')[1]['commands']}
        t12, t13, t34, missing = [], [], [], []
        for e in events:
            t1, t2 = parse(e['receivedUtc']), parse(e['createdUtc'])
            t12.append((t2 - t1).total_seconds())
            c = commands.get(e['id'])
            if not c: missing.append(e['plate']); continue
            t13.append((parse(c['createdUtc']) - t1).total_seconds())
            if c.get('ackUtc'): t34.append((parse(c['ackUtc']) - parse(c['createdUtc'])).total_seconds())
        held = [a for a in call('GET', root + '/snapshot')[1]['alerts'] if a['reason'] == 'AUTO_OPEN_HELD']
        row = lambda name, v: print(f'  {name:<34} n={len(v):>3}  median={pct(v, 50) or 0:6.3f}s  P95={pct(v, 95) or 0:6.3f}s  max={max(v) if v else 0:6.3f}s')
        print(f'Events: {len(events)}/{args.vehicles}   commands: {len(t13)}   no command: {len(missing)}   auto-open held: {len(held)}')
        row('t1->t2 received -> decision', t12); row('t1->t3 received -> open command', t13); row('t3->t4 command -> controller ack', t34)
        ok = len(events) == args.vehicles and not missing and (pct(t13, 95) or 0) <= args.target
        print('RESULT:', 'PASS' if ok else 'FAIL', f'(target P95 t1->t3 <= {args.target}s)')
        return 0 if ok else 1
    finally:
        stop.set(); api.terminate(); api.wait(timeout=10); bridge.shutdown(); log.close()
        try: temp.cleanup()
        except OSError: pass

if __name__ == '__main__':
    sys.exit(main())
