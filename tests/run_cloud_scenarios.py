"""Scenarios of the station plan that need the REAL FPT Cloud side: S1 (master list down, version acknowledged) and K10 (no Internet, then catch-up).

Runs the station API and DSA_Platform (PostgreSQL) as two processes on this machine. The platform gets its own throw-away database in the dev
PostgreSQL container (`deploy-postgres-1`, see DSA_Platform/deploy/docker-compose.dev.yml), dropped at the end. Nothing else in that server is touched.

Needs: the Debug builds of both (`dotnet build DSAVehicle`, `dotnet build DSA_Platform/src/Platform.Api`), Docker running with the container started.

    python tests/run_cloud_scenarios.py              # S1 and K10
    python tests/run_cloud_scenarios.py S1
    python tests/run_cloud_scenarios.py K10 --offline-seconds 60 --events 120
"""
import argparse
import http.cookiejar
import json
import os
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from integration_parking import ParkingIntegration  # noqa: E402  (the station harness)

REPO = Path(__file__).resolve().parents[3]                  # DSA_Management_Platform
PLATFORM = REPO / 'DSA_Platform'
CONTAINER = 'deploy-postgres-1'
ROOT = '/api/v1/parking'
STATION = 'CUALO-GC'


class Failed(AssertionError):
    pass


def free_port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0)); return s.getsockname()[1]


def password_from_env_file():
    text = (PLATFORM / 'deploy' / '.env').read_text(encoding='utf-8-sig')   # Windows editors add a BOM
    return re.search(r'^POSTGRES_PASSWORD=(.+)$', text, re.M).group(1).strip().strip('"\'')


def psql(sql):
    result = subprocess.run(['docker', 'exec', CONTAINER, 'psql', '-U', 'dsa_platform', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-c', sql], capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError('psql failed: ' + result.stderr.strip()[:400])


class Cloud:
    """DSA_Platform on its own database. stop() and start() again keep the same database and port: a lost Internet connection."""
    def __init__(self):
        self.database = 'dsa_e2e_' + secrets.token_hex(4)
        self.port = free_port()
        self.url = f'http://127.0.0.1:{self.port}'
        self.admin_password = secrets.token_urlsafe(18)
        self.process = None
        self.log = tempfile.NamedTemporaryFile('w+', suffix='.log', prefix='platform-', delete=False, encoding='utf-8')
        self.jar = http.cookiejar.CookieJar()
        self.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development',
                    'ConnectionStrings__Platform': f'Host=localhost;Port=5432;Database={self.database};Username=dsa_platform;Password={password_from_env_file()}',
                    'Platform__BootstrapPassword': self.admin_password, 'Platform__ArtifactLinkKey': secrets.token_urlsafe(40),
                    'AiModels__GpuWorkerKey': secrets.token_urlsafe(40)}
        psql(f'CREATE DATABASE {self.database}')   # last, so a failure above leaves no database behind

    def start(self):
        self.process = subprocess.Popen(['dotnet', str(PLATFORM / 'src/Platform.Api/bin/Debug/net8.0/Platform.Api.dll'), '--urls', f'http://127.0.0.1:{self.port}'],
                                        cwd=PLATFORM / 'src/Platform.Api', env=self.env, stdout=self.log, stderr=subprocess.STDOUT,
                                        creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        for _ in range(300):
            if self.process.poll() is not None:
                self.log.flush(); self.log.seek(0)
                raise RuntimeError('Platform failed to start: ' + self.log.read()[-3000:])
            try:
                if self.call('GET', '/api/auth/csrf', csrf=False)[0] == 200: break
            except (OSError, urllib.error.URLError): time.sleep(.2)
        self.login()

    def stop(self):
        if self.process and self.process.poll() is None:
            self.process.terminate()
            try: self.process.wait(timeout=15)
            except subprocess.TimeoutExpired: self.process.kill()

    def close(self):
        self.stop()
        try: psql(f'DROP DATABASE IF EXISTS {self.database} WITH (FORCE)')
        finally:
            self.log.close()

    def call(self, method, path, body=None, csrf=True, headers=None):
        headers = dict(headers or {})
        if method != 'GET' and csrf:
            headers['X-CSRF-Token'] = self.call('GET', '/api/auth/csrf', csrf=False)[1]['token']
        data = None
        if body is not None:
            data = json.dumps(body).encode(); headers['Content-Type'] = 'application/json'
        request = urllib.request.Request(self.url + path, data=data, method=method, headers=headers)
        try: response = self.client.open(request, timeout=15)
        except urllib.error.HTTPError as error: response = error
        text = response.read().decode()
        try: return response.status, json.loads(text)
        except ValueError: return response.status, text

    def login(self):
        self.jar.clear()
        status, body = self.call('POST', '/api/auth/login', {'username': 'admin', 'password': self.admin_password})   # login needs the anti-forgery token too
        if status != 200: raise RuntimeError(f'Platform login failed: {status} {body}')

    def must(self, answer, what):
        status, body = answer
        if status >= 300: raise Failed(f'Platform refused "{what}": {status} {body}')
        return body

    def as_station(self, key, path):
        request = urllib.request.Request(self.url + path, headers={'X-Station-Key': key})
        with urllib.request.urlopen(request, timeout=15) as response: return json.loads(response.read())

    def station_row(self):
        return next((s for s in self.call('GET', '/api/v1/platform/stations')[1] if s['id'] == STATION), None)

    def create_station(self):
        self.must(self.call('PUT', '/api/v1/platform/areas', {'code': 'CUALO', 'name': 'Cửa lò Giếng chính'}), 'area')
        return self.must(self.call('POST', '/api/v1/platform/stations', {'id': STATION, 'name': 'Cửa lò', 'areaCodes': ['CUALO']}), 'station')['key']


class Scenario(ParkingIntegration):
    """The station API (harness of integration_parking.py) wired to a Cloud."""
    def runTest(self):
        pass

    def link(self, cloud, key, master_from_cloud, pull=5):
        self.env.update({'Platform__Enabled': 'true', 'Platform__Url': cloud.url, 'Platform__StationId': STATION, 'Platform__Key': key,
                         'Platform__HeartbeatSeconds': '5', 'Platform__MasterPullSeconds': str(pull), 'Receiver__Key': secrets.token_urlsafe(32)})
        if master_from_cloud: self.env['Sync__MasterSource'] = 'Cloud'
        self.process.terminate(); self.process.wait(timeout=10); self.start()
        self.login_as('admin')

    def wait(self, what, check, seconds, every=1.0):
        began = time.time()
        while time.time() - began < seconds:
            value = check()
            if value: return time.time() - began
            time.sleep(every)
        raise Failed(f'{what}: not within {seconds} s')

    def must(self, answer, what):
        if answer[0] != 200: raise Failed(f'the station refused "{what}": {answer}')

    def people(self):
        return {p['personId']: p for p in self.call('GET', ROOT + '/people')[1]}

    def pending(self):
        return self.call('GET', ROOT + '/cameras')[1]['outboxPending']


def s1(report):
    """S1: add or lock a worker on the Cloud → the station applies it in ≤ 60 s; the Cloud sees the station acknowledge the right version."""
    cloud = Cloud(); station = Scenario(); station.setUp()
    try:
        cloud.start(); key = cloud.create_station()
        station.link(cloud, key, master_from_cloud=True)
        station.wait('the station reports its first applied version', lambda: (cloud.station_row() or {}).get('lastHealth') is not None, 60)
        cloud.must(cloud.call('PUT', '/api/v1/identity/units', {'code': 'PX3', 'name': 'PX Đào lò 3'}), 'unit')
        person = lambda i, name, active=True: {'id': i, 'code': f'CN-{i:02d}', 'name': name, 'unitCode': 'PX3', 'areaCodes': ['CUALO'], 'active': active,
                                                'faceIds': [{'source': 'zk-face-s', 'externalId': str(1000 + i)}]}
        cloud.must(cloud.call('POST', '/api/v1/identity/people/import', [person(1, 'Nguyễn Văn A'), person(2, 'Trần Văn B')]), 'import people')
        added = station.wait('workers 1 and 2 reach the station', lambda: {1, 2} <= set(station.people()), 60)
        version = cloud.as_station(key, f'/api/v1/stations/{STATION}/master?since=0')['version']
        station.wait('the Cloud shows the version the station applied', lambda: ((cloud.station_row() or {}).get('lastHealth') or {}).get('masterVersion') == version, 30)
        cloud.must(cloud.call('PUT', '/api/v1/identity/people', person(2, 'Trần Văn B', active=False)), 'lock worker')
        locked = station.wait('the lock reaches the station', lambda: 2 in station.people() and not station.people()[2]['active'] or 2 not in station.people(), 60)
        version = cloud.as_station(key, f'/api/v1/stations/{STATION}/master?since=0')['version']
        station.wait('the Cloud shows the version after the lock', lambda: ((cloud.station_row() or {}).get('lastHealth') or {}).get('masterVersion') == version, 30)
        report(f'a new worker reached the station in {added:.0f} s, a lock in {locked:.0f} s (limit 60 s); the Cloud showed version {version} acknowledged')
    finally:
        station.tearDown(); cloud.close()


def k10(report, offline_seconds, count):
    """K10 (scaled down): the Cloud is unreachable for a while during traffic. Every decision is still right, and when it is back the station sends
    everything once. The 4 hours of the plan are extrapolated from the rate measured here."""
    cloud = Cloud(); station = Scenario(); station.setUp()
    try:
        cloud.start(); key = cloud.create_station()
        station.link(cloud, key, master_from_cloud=False)
        lane = {'id': 'flap-1', 'direction': 'Entry', 'policy': 'FaceOnly', 'plateThreshold': .9, 'faceThreshold': .8, 'subject': 'Person',
                'autoOpen': True, 'shadowMode': False, 'simulation': True}
        station.must(station.call('PUT', ROOT + '/lanes', lane), 'lane')
        station.must(station.call('PUT', ROOT + '/cameras', {'id': 'face-1', 'name': 'face-1', 'laneId': 'flap-1', 'externalDevice': 'ZK-01', 'trustedConfidence': .99}), 'camera')
        people = list(range(100, 100 + count))
        for p in people:
            station.must(station.call('PUT', ROOT + '/people', {'personId': p, 'name': f'Worker {p}'}), f'person {p}')
        key_in_url = station.env['Receiver__Key']
        sent = iter(range(1, 10 ** 6))
        face = lambda p: station.post_raw(f'{station.base}{ROOT}/receiver/generic?key={key_in_url}', {'deviceId': 'ZK-01', 'personId': p, 'confidence': .97, 'seq': next(sent)})

        cloud.stop()                                                   # the Internet goes away
        began = time.time()
        for p in people:
            if face(p) >= 300: raise Failed(f'the station refused the face event of {p}')
            time.sleep(offline_seconds / count)                       # spread over the outage like real traffic
        outcomes = {}
        station.wait('all decisions are made', lambda: len([e for e in station.call('GET', ROOT + '/snapshot')[1]['events']]) >= count, 60)
        for e in station.call('GET', ROOT + '/snapshot')[1]['events']: outcomes[e['outcome']] = outcomes.get(e['outcome'], 0) + 1
        if outcomes != {'Allow': count}: raise Failed(f'decisions during the outage should all be Allow, got {outcomes}')
        buffered = station.pending()
        if buffered < count: raise Failed(f'only {buffered} records buffered for {count} decisions')
        offline = time.time() - began

        cloud.start()                                                  # the Internet is back
        back = time.time()
        drained = station.wait('the station sends the buffer', lambda: station.pending() == 0, 600, every=2.0)
        found = {}
        for p in people:
            rows = cloud.call('GET', f'/api/v1/identity/trace?personId={p}&take=50')[1]['events']
            found[p] = len({r['itemId'] for r in rows if r['type'] == 'access_event'})
        missing = [p for p, n in found.items() if n == 0]; duplicated = [p for p, n in found.items() if n > 1]
        if missing or duplicated: raise Failed(f'after catching up: missing for {missing[:5]}, duplicated for {duplicated[:5]}')
        rate = buffered / max(drained, 1)                              # records sent per second
        per_event = buffered / count                                   # an event brings its commands, presence... with it
        records_72h = 3 * 4000 * per_event                             # plan §4.2: 4,000 events/day at the cổng bảo vệ, 72 h of buffer
        report(f'{count} decisions during {offline:.0f} s without the Cloud, all Allow; {buffered} records buffered ({per_event:.1f} per decision); '
               f'sent in {drained:.0f} s after reconnect ({rate:.1f} records/s), none missing or duplicated on the Cloud. '
               f'Extrapolated: 72 h at 4,000 events/day = about {records_72h:,.0f} records, about {records_72h / max(rate, .1) / 60:.0f} min to send at this rate.')
    finally:
        station.tearDown(); cloud.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('scenarios', nargs='*', help='S1 and/or K10 (default: both)')
    parser.add_argument('--offline-seconds', type=int, default=30)
    parser.add_argument('--events', type=int, default=60)
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')
    plan = {'S1': ('Thêm hoặc khóa một công nhân trên Cloud → trạm áp dụng trong ≤ 60 giây; Cloud thấy đúng phiên bản', s1),
            'K10': ('Mất Internet khi đang có người qua → mọi quyết định vẫn đúng; có mạng lại thì gửi đủ, không trùng', lambda r: k10(r, args.offline_seconds, args.events))}
    unknown = [n for n in args.scenarios if n not in plan]
    if unknown:
        print('Unknown scenario: ' + ', '.join(unknown)); return 2
    failed = 0
    for name in (args.scenarios or list(plan)):
        title, run = plan[name]
        try:
            notes = []
            run(notes.append)
            print(f'ĐẠT          {name:<4} {title}\n             → {notes[0]}', flush=True)
        except (Failed, RuntimeError, OSError, KeyError, urllib.error.URLError) as error:
            failed += 1; print(f'KHÔNG ĐẠT    {name:<4} {title}\n             → {error}', flush=True)
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
