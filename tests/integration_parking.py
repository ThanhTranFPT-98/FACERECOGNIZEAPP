"""Runs the actual API against an isolated temporary database. No hardware or external services."""
import http.cookiejar
import json
import os
import secrets
import socket
import subprocess
import tempfile
import time
import unittest
import threading
import http.server
import base64
import urllib.error
import urllib.request
from pathlib import Path
from datetime import datetime, timedelta, timezone

ROOT = Path(__file__).resolve().parents[1]

class ParkingIntegration(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='parking-integration-')
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0)); self.port = listener.getsockname()[1]
        self.base = f'http://127.0.0.1:{self.port}'
        self.password = secrets.token_urlsafe(24)
        self.device_key = secrets.token_urlsafe(32)
        self.bridge_calls = []
        self.bad_ack = False
        self.ocr_results = []
        owner = self
        class FakeDevice(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def do_POST(self):
                if self.headers.get('Transfer-Encoding') == 'chunked':
                    chunks = []
                    while True:
                        length = int(self.rfile.readline().strip(), 16)
                        if length == 0:
                            self.rfile.readline(); break
                        chunks.append(self.rfile.read(length)); self.rfile.read(2)
                    body = b''.join(chunks)
                else:
                    body = self.rfile.read(int(self.headers.get('Content-Length', 0)))
                if self.path == '/commands':
                    command = json.loads(body); owner.bridge_calls.append(command)
                    result = {'commandId': '00000000-0000-0000-0000-000000000000' if owner.bad_ack else command['commandId'], 'accepted': True}
                else:
                    result = {'plate': owner.ocr_results.pop(0) if owner.ocr_results else '59X112345', 'confidence': .99, 'modelVersion': 'integration-test-model'}
                self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
                self.wfile.write(json.dumps(result).encode())
        self.fake = http.server.ThreadingHTTPServer(('127.0.0.1', 0), FakeDevice)
        threading.Thread(target=self.fake.serve_forever, daemon=True).start()
        fake_url = f'http://127.0.0.1:{self.fake.server_port}'
        self.env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'Parking__BootstrapPassword': self.password,
            'Parking__DeviceKey': self.device_key, 'ConnectionStrings__Parking': 'Data Source=' + str(Path(self.temp.name) / 'parking.db'),
            'Parking__Provider': 'Sqlite', 'Parking__AlertWebhookUrl': '', 'Parking__EvidenceRetentionDays': '0',
            'Parking__EnableLiveBarrier': 'true', 'Parking__BarrierUrl': fake_url, 'Parking__PlateAiUrl': fake_url,
            'Parking__ApprovedPlateModel': 'integration-test-model'}
        self.env['Parking__EvidenceDirectory'] = str(Path(self.temp.name) / 'evidence')
        self.jar = http.cookiejar.CookieJar()
        self.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.log = open(Path(self.temp.name) / 'api.log', 'w+', encoding='utf-8')
        self.process = None
        self.start()

    def start(self):
        dotnet = os.environ.get('PARKING_DOTNET', 'dotnet')
        urls = ';'.join([self.base] + getattr(self, 'extra_urls', []))
        self.process = subprocess.Popen([dotnet, str(ROOT / 'DSAVehicle/bin/Debug/net8.0/DSAVehicle.dll'), '--urls', urls],
            cwd=ROOT / 'DSAVehicle', env=self.env, stdout=self.log, stderr=subprocess.STDOUT,
            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        for _ in range(100):
            if self.process.poll() is not None:
                self.log.flush(); self.log.seek(0)
                raise RuntimeError('API startup failed: ' + self.log.read()[-6000:])
            try:
                if self.call('GET', '/api/auth/csrf', csrf=False)[0] == 200: return
            except (OSError, urllib.error.URLError): pass
            time.sleep(.1)
        raise RuntimeError('API did not start')

    def call(self, method, path, body=None, csrf=True, headers=None):
        headers = dict(headers or {})
        if method != 'GET' and csrf:
            _, token = self.call('GET', '/api/auth/csrf', csrf=False)
            headers['X-CSRF-Token'] = token['token']
        if body is not None:
            body = json.dumps(body).encode(); headers['Content-Type'] = 'application/json'
        request = urllib.request.Request(self.base + path, data=body, method=method, headers=headers)
        try: response = self.client.open(request, timeout=10)
        except urllib.error.HTTPError as exc: response = exc
        text = response.read().decode()
        try: payload = json.loads(text)
        except ValueError: payload = text
        return response.status, payload

    def tearDown(self):
        if self.process:
            self.process.terminate(); self.process.wait(timeout=10)
        self.fake.shutdown(); self.fake.server_close()
        self.log.close(); self.temp.cleanup()

    def capture(self, lane, burst=False):
        boundary = 'parking-test-boundary'
        image = base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=')
        body = b''
        for key, value in {'laneId': lane, 'correlationId': secrets.token_hex(12)}.items():
            body += f'--{boundary}\r\nContent-Disposition: form-data; name="{key}"\r\n\r\n{value}\r\n'.encode()
        for _ in range(3 if burst else 1):
            field = 'images' if burst else 'image'
            body += f'--{boundary}\r\nContent-Disposition: form-data; name="{field}"; filename="test.png"\r\nContent-Type: image/png\r\n\r\n'.encode() + image + b'\r\n'
        body += f'--{boundary}--\r\n'.encode()
        token = self.call('GET', '/api/auth/csrf')[1]['token']
        path = '/api/v1/parking/device/capture-burst' if burst else '/api/v1/parking/recognize'
        req = urllib.request.Request(self.base + path, data=body,
            headers={'Content-Type': 'multipart/form-data; boundary=' + boundary, 'X-CSRF-Token': token, 'X-Device-Key': self.device_key})
        with self.client.open(req) as response: return json.load(response)

    def test_operator_workflow_and_security(self):
        root = '/api/v1/parking'
        self.assertEqual(self.call('GET', root + '/snapshot')[0], 401)
        self.assertEqual(self.call('POST', '/api/auth/login', {'username': 'admin', 'password': self.password}, csrf=False)[0], 400)
        self.assertEqual(self.call('POST', '/api/auth/login', {'username': 'admin', 'password': self.password})[0], 200)
        status, snapshot = self.call('GET', root + '/snapshot'); self.assertEqual(status, 200)
        self.assertEqual(len(snapshot['lanes']), 2)
        vehicle = {'plate': '59-X1 123.45', 'owner': 'Test owner', 'kind': 'Motorcycle', 'personId': 42, 'additionalPersonIds': [43]}
        status, registered = self.call('POST', root + '/vehicles', vehicle); self.assertEqual(status, 200, registered)
        self.assertEqual(registered['normalizedPlate'], '59X112345')
        self.assertEqual(self.call('POST', root + '/vehicles', vehicle)[0], 409)
        observe = {'laneId': 'entry-1', 'correlationId': 'capture-1', 'plate': '59X112345', 'plateConfidence': .99, 'faceConfidence': 0}
        status, event = self.call('POST', root + '/simulate', observe); self.assertEqual(status, 200, event)
        self.assertEqual(event['outcome'], 'Allow')
        self.assertEqual(self.call('POST', root + '/simulate', observe)[1]['id'], event['id'])
        self.assertEqual(self.call('POST', root + '/events/' + event['id'] + '/open', {})[0], 409)  # shadow
        lane = {'id': 'entry-1', 'direction': 'Entry', 'policy': 'PlateOnly', 'plateThreshold': .95, 'faceThreshold': .8, 'simulation': True, 'shadowMode': False}
        self.assertEqual(self.call('PUT', root + '/lanes', lane)[0], 200)
        status, command = self.call('POST', root + '/events/' + event['id'] + '/open', {}); self.assertEqual(status, 200, command)
        self.assertEqual(command['status'], 'Simulated')
        self.assertEqual(self.call('POST', root + '/events/' + event['id'] + '/open', {})[1]['id'], command['id'])
        status, unknown = self.call('POST', root + '/simulate', {**observe, 'correlationId': 'unknown', 'plate': '30A99999'})
        self.assertEqual(unknown['outcome'], 'ManualReview')
        self.assertEqual(self.call('POST', root + '/events/' + unknown['id'] + '/open', {})[0], 409)
        self.assertEqual(self.call('POST', root + '/events/' + unknown['id'] + '/review', {'approve': True, 'note': 'Identity checked'})[1]['outcome'], 'Allow')
        self.assertEqual(self.call('POST', root + '/events/' + unknown['id'] + '/review', {'approve': True, 'note': 'Duplicate'})[0], 409)
        self.assertEqual(self.call('PUT', root + '/vehicles/' + registered['id'], {**vehicle, 'blocked': True})[0], 200)
        self.assertEqual(self.call('POST', root + '/simulate', {**observe, 'correlationId': 'blocked'})[1]['outcome'], 'Deny')
        self.assertEqual(self.call('PUT', root + '/vehicles/' + registered['id'], {**vehicle, 'blocked': False})[0], 200)
        # Face/plate binding and all policy modes.
        for policy, face, expected in [('Both', 42, 'Allow'), ('Both', 43, 'Allow'), ('Both', 7, 'ManualReview'), ('Either', None, 'Allow'), ('FaceOnly', 42, 'Allow'), ('FaceOnly', 43, 'Allow'), ('ManualReview', 42, 'ManualReview')]:
            self.call('PUT', root + '/lanes', {**lane, 'policy': policy})
            result = self.call('POST', root + '/simulate', {**observe, 'correlationId': secrets.token_hex(8), 'personId': face, 'faceConfidence': .99})[1]
            self.assertEqual(result['outcome'], expected, (policy, result))
        # Visitor quota is reserved on opening and cannot be reused on a new lane.
        now = datetime.now(timezone.utc)
        status, visit = self.call('POST', root + '/visits', {'name': 'Visitor', 'host': 'Resident', 'plate': '30A12345', 'from': (now-timedelta(minutes=1)).isoformat(), 'until': (now+timedelta(hours=1)).isoformat(), 'maxEntries': 1})
        self.assertEqual(status, 200, visit)
        self.call('PUT', root + '/lanes', {**lane, 'id': 'visitor-test'})
        visitor_obs = {**observe, 'laneId': 'visitor-test', 'plate': '30A12345', 'correlationId': 'visit-1'}
        visitor_event = self.call('POST', root + '/simulate', visitor_obs)[1]
        self.assertEqual(visitor_event['outcome'], 'Allow')
        self.assertEqual(self.call('POST', root + '/events/' + visitor_event['id'] + '/open', {})[0], 200)
        visitor_command = self.call('POST', root + '/events/' + visitor_event['id'] + '/open', {})[1]
        self.assertEqual(self.call('POST', root + '/commands/' + visitor_command['id'] + '/simulate-passage', {})[0], 200)
        self.assertEqual(len(self.call('GET', root + '/snapshot')[1]['sessions']), 1)
        # A consumed visitor pass still permits exit when there is a confirmed entry.
        self.call('PUT', root + '/lanes', {**lane, 'id': 'exit-1', 'direction': 'Exit'})
        exit_event = self.call('POST', root + '/simulate', {**visitor_obs, 'laneId': 'exit-1', 'correlationId': 'exit-visit'})[1]
        self.assertEqual(exit_event['outcome'], 'Allow')
        exit_command = self.call('POST', root + '/events/' + exit_event['id'] + '/open', {})[1]
        self.assertEqual(self.call('POST', root + '/commands/' + exit_command['id'] + '/simulate-passage', {})[0], 200)
        self.assertIsNotNone(self.call('GET', root + '/snapshot')[1]['sessions'][0]['exitUtc'])
        self.assertEqual(self.call('POST', root + '/simulate', {**visitor_obs, 'correlationId': 'visit-2'})[1]['outcome'], 'ManualReview')
        # Device authentication is isolated from session cookies.
        heartbeat = {'laneId': 'entry-1', 'safetyHealthy': True, 'vehiclePresent': True, 'barrierState': 'Closed'}
        self.assertEqual(self.call('POST', root + '/device/heartbeat', heartbeat, csrf=False)[0], 401)
        self.assertEqual(self.call('POST', root + '/device/heartbeat', heartbeat, csrf=False, headers={'X-Device-Key': self.device_key})[0], 200)
        # Real HTTP OCR and bridge contract against a local fake device, never physical hardware.
        for live_id in ['live-test', 'unknown-test']:
            self.call('PUT', root + '/lanes', {**lane, 'id': live_id, 'simulation': False})
            captured = self.capture(live_id)
            self.assertEqual(captured['outcome'], 'Allow')
            self.assertEqual(self.call('POST', root + '/events/' + captured['id'] + '/open', {})[0], 409)  # no sensors
            self.call('POST', root + '/device/heartbeat', {**heartbeat, 'laneId': live_id}, csrf=False, headers={'X-Device-Key': self.device_key})
            self.bad_ack = live_id == 'unknown-test'
            status, live_command = self.call('POST', root + '/events/' + captured['id'] + '/open', {})
            self.assertEqual(status, 200, live_command)
            self.assertEqual(live_command['status'], 'Unknown' if self.bad_ack else 'Acknowledged')
            sent = len(self.bridge_calls)
            self.call('POST', root + '/events/' + captured['id'] + '/open', {})
            self.assertEqual(len(self.bridge_calls), sent)
        self.assertEqual(len(self.bridge_calls), 2)
        self.ocr_results = ['59X112345', '59X112345', '']
        self.assertEqual(self.capture('live-test', burst=True)['outcome'], 'Allow')
        self.ocr_results = ['59X112345', '59X112345', '30A99999']
        self.assertEqual(self.capture('live-test', burst=True)['outcome'], 'ManualReview')
        self.bad_ack = False
        self.call('PUT', root + '/lanes', {**lane, 'id': 'auto-test', 'simulation': False, 'autoOpen': True})
        self.call('POST', root + '/device/heartbeat', {**heartbeat, 'laneId': 'auto-test'}, csrf=False, headers={'X-Device-Key': self.device_key})
        self.capture('auto-test', burst=True)
        self.assertEqual(len(self.bridge_calls), 3)
        self.capture('auto-test', burst=True)
        self.assertEqual(len(self.bridge_calls), 3)  # no second opening while passage is unconfirmed
        # Guard cannot alter lanes or register vehicles. Auditor cannot approve access.
        for role in ['Guard', 'Auditor']:
            self.assertEqual(self.call('POST', '/api/auth/users', {'username': role, 'password': self.password, 'role': role})[0], 200)
        self.call('POST', '/api/auth/logout', {})
        self.call('POST', '/api/auth/login', {'username': 'Guard', 'password': self.password})
        self.assertEqual(self.call('PUT', root + '/lanes', lane)[0], 403)
        self.assertEqual(self.call('POST', root + '/vehicles', vehicle)[0], 403)
        self.call('POST', '/api/auth/logout', {})
        self.call('POST', '/api/auth/login', {'username': 'Auditor', 'password': self.password})
        self.assertEqual(self.call('POST', root + '/simulate', observe)[0], 403)
        self.assertEqual(self.call('GET', root + '/audit')[0], 200)
        # Restart against the same database: records and command idempotency persist.
        self.process.terminate(); self.process.wait(timeout=10); self.start()
        self.call('POST', '/api/auth/login', {'username': 'admin', 'password': self.password})
        snapshot = self.call('GET', root + '/snapshot')[1]
        self.assertEqual(len(snapshot['vehicles']), 1)
        self.assertEqual(self.call('POST', root + '/events/' + event['id'] + '/open', {})[1]['id'], command['id'])
        print('Verified authentication, CSRF, roles, persistence, 5 policies, review, visitors, devices and idempotency.')

    def test_areas_and_locations(self):
        root = '/api/v1/parking'
        self.call('POST', '/api/auth/login', {'username': 'admin', 'password': self.password})
        areas = self.call('GET', root + '/areas')[1]
        self.assertEqual([a['code'] for a in areas], ['DEFAULT'])
        self.assertEqual(sorted(areas[0]['locations'][0]['lanes']), ['entry-1', 'exit-1'])  # upgraded lanes get a home
        status, mine = self.call('PUT', root + '/areas', {'code': 'gieng-chinh', 'name': 'Cửa lò Giếng chính', 'kind': 'Mine'})
        self.assertEqual(status, 200, mine)
        self.assertEqual(self.call('PUT', root + '/areas', {'code': 'GIENG-CHINH', 'name': 'Copy'})[0], 409)
        status, shaft = self.call('PUT', root + '/locations', {'areaId': mine['id'], 'name': 'Flap lanes'})
        self.assertEqual(status, 200, shaft)
        lane = {'id': 'flap-1', 'direction': 'Entry', 'policy': 'PlateOnly', 'plateThreshold': .9, 'faceThreshold': .8,
                'shadowMode': True, 'simulation': True, 'locationId': shaft['id']}
        self.assertEqual(self.call('PUT', root + '/lanes', lane)[0], 200)
        self.assertEqual(self.call('PUT', root + '/lanes', {**lane, 'id': 'bad', 'locationId': '00000000-0000-0000-0000-000000000001'})[0], 409)
        for lane_id in ['flap-1', 'entry-1']:
            self.call('POST', root + '/simulate', {'laneId': lane_id, 'correlationId': secrets.token_hex(8), 'plate': '30A12345', 'plateConfidence': .99, 'faceConfidence': 0})
        in_mine = self.call('GET', root + '/events?areaId=' + mine['id'])[1]
        self.assertEqual([e['laneId'] for e in in_mine['items']], ['flap-1'])
        self.assertEqual(self.call('DELETE', root + '/locations/' + shaft['id'])[0], 409)
        self.assertEqual(self.call('DELETE', root + '/areas/' + mine['id'])[0], 409)
        audit = self.call('GET', root + '/audit')[1]
        self.assertTrue({'SaveArea', 'SaveLocation'} <= {a['action'] for a in audit})
        self.assertEqual(self.call('POST', '/api/auth/users', {'username': 'Guard', 'password': self.password, 'role': 'Guard'})[0], 200)
        self.call('POST', '/api/auth/logout', {})
        self.call('POST', '/api/auth/login', {'username': 'Guard', 'password': self.password})
        self.assertEqual(self.call('GET', root + '/areas')[0], 200)
        self.assertEqual(self.call('PUT', root + '/areas', {'code': 'X', 'name': 'X'})[0], 403)
        print('Verified areas, locations, lane placement, area filter, delete protection, audit and roles.')

    def test_configurable_timings(self):
        root = '/api/v1/parking'
        self.call('POST', '/api/auth/login', {'username': 'admin', 'password': self.password})
        self.assertEqual(self.call('GET', root + '/settings/timings')[1]['decisionValidSeconds'], 30)
        timings = {'decisionValidSeconds': 60, 'reviewWindowSeconds': 300, 'cooldownSeconds': 8, 'heartbeatFreshSeconds': 10, 'offlineAlertSeconds': 30}
        self.assertEqual(self.call('PUT', root + '/settings/timings', timings)[0], 200)
        self.assertEqual(self.call('PUT', root + '/settings/timings', {**timings, 'cooldownSeconds': 0})[0], 400)

        def validity(lane_id):
            event = self.call('POST', root + '/simulate', {'laneId': lane_id, 'correlationId': secrets.token_hex(8), 'plate': '30A12345', 'plateConfidence': .99, 'faceConfidence': 0})[1]
            parse = lambda v: datetime.fromisoformat(v.rstrip('Z')[:26])
            return round((parse(event['expiresUtc']) - parse(event['createdUtc'])).total_seconds())
        self.assertEqual(validity('entry-1'), 60)  # site default
        lane = {'id': 'cargo-out', 'direction': 'Exit', 'policy': 'PlateOnly', 'plateThreshold': .9, 'faceThreshold': .8,
                'shadowMode': True, 'simulation': True, 'decisionValidSeconds': 45}
        self.assertEqual(self.call('PUT', root + '/lanes', lane)[0], 200)
        self.assertEqual(validity('cargo-out'), 45)  # lane override
        self.assertEqual(self.call('PUT', root + '/lanes', {**lane, 'cooldownSeconds': 1})[0], 400)
        self.assertIn('SaveSiteTimings', {a['action'] for a in self.call('GET', root + '/audit')[1]})
        self.assertEqual(self.call('POST', '/api/auth/users', {'username': 'Guard', 'password': self.password, 'role': 'Guard'})[0], 200)
        self.call('POST', '/api/auth/logout', {})
        self.call('POST', '/api/auth/login', {'username': 'Guard', 'password': self.password})
        self.assertEqual(self.call('GET', root + '/settings/timings')[0], 200)
        self.assertEqual(self.call('PUT', root + '/settings/timings', timings)[0], 403)
        # GC-43: custom plate formats.
        self.login_as('admin')
        self.assertEqual(self.call('PUT', root + '/settings/plates', {'patterns': '[A-Z'})[0], 409)
        self.assertEqual(self.call('PUT', root + '/settings/plates', {'patterns': '[A-Z]{2}[0-9]{4}'})[0], 200)
        self.assertEqual(self.call('POST', root + '/vehicles', {'plate': 'AB-1234', 'owner': 'Foreign', 'kind': 'Car'})[0], 200)
        self.assertEqual(self.call('POST', root + '/vehicles', {'plate': '30A-123.45', 'owner': 'Local', 'kind': 'Car'})[0], 409)
        print('Verified site timing defaults, lane overrides, bounds, audit, roles and plate formats.')

    def login_as(self, username):
        # Fresh cookie jar per account, so two sessions can exist side by side.
        self.jar = http.cookiejar.CookieJar()
        self.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        return self.call('POST', '/api/auth/login', {'username': username, 'password': self.password})

    def test_roles_permissions_and_expiry(self):
        root = '/api/v1/parking'
        self.login_as('admin')
        self.assertEqual(len(self.call('GET', '/api/auth/me')[1]['permissions']), 13)
        roles = self.call('GET', '/api/auth/roles')[1]
        self.assertEqual({'Admin', 'Guard', 'Auditor', 'Viewer', 'SafetyOfficer', 'Manager', 'Technician'}, {r['id'] for r in roles['roles']})
        self.assertEqual(self.call('PUT', '/api/auth/roles', {'id': 'Admin', 'name': 'x', 'permissions': []})[0], 409)
        self.assertEqual(self.call('PUT', '/api/auth/roles', {'id': 'UserDesk', 'name': 'User desk', 'permissions': ['ManageUsers']})[0], 200)
        for name, role in [('TECH', 'Technician'), ('DESK', 'UserDesk'), ('VIEW', 'Viewer')]:
            self.assertEqual(self.call('POST', '/api/auth/users', {'username': name, 'password': self.password, 'role': role})[0], 200)
        past = (datetime.now(timezone.utc) - timedelta(days=1)).isoformat()
        self.assertEqual(self.call('POST', '/api/auth/users', {'username': 'OLD', 'password': self.password, 'role': 'Guard', 'expiresUtc': past})[0], 200)
        self.assertEqual(self.call('POST', '/api/auth/users', {'username': 'X', 'password': self.password, 'role': 'Nope'})[0], 409)
        users = {u['username']: u for u in self.call('GET', '/api/auth/users')[1]}
        admin_id = users['ADMIN']['id']
        self.assertEqual(self.call('PUT', '/api/auth/users/' + admin_id, {'role': 'Guard', 'active': True})[0], 409)  # not yourself

        # Technician: manages the site, can't operate lanes or read the audit log.
        self.login_as('TECH')
        self.assertEqual(self.call('PUT', root + '/areas', {'code': 'TECH-AREA', 'name': 'Tech area'})[0], 200)
        self.assertEqual(self.call('POST', root + '/simulate', {'laneId': 'entry-1', 'correlationId': 'x1', 'plate': '30A12345', 'plateConfidence': .99, 'faceConfidence': 0})[0], 403)
        self.assertEqual(self.call('GET', root + '/audit')[0], 403)
        # User desk: manages accounts but can't create or grant Admin, and can't edit roles.
        self.login_as('DESK')
        self.assertEqual(self.call('POST', '/api/auth/users', {'username': 'SNEAKY', 'password': self.password, 'role': 'Admin'})[0], 409)
        self.assertEqual(self.call('PUT', '/api/auth/users/' + users['VIEW']['id'], {'role': 'Admin', 'active': True})[0], 409)
        self.assertEqual(self.call('PUT', '/api/auth/roles', {'id': 'UserDesk', 'name': 'x', 'permissions': ['ManageUsers', 'ManageSite']})[0], 403)
        # Expired account can't sign in.
        status, body = self.login_as('OLD')
        self.assertEqual(status, 401); self.assertIn('expired', body['message'])
        # An open session ends when the account is expired.
        self.login_as('VIEW')
        self.assertEqual(self.call('GET', root + '/snapshot')[0], 200)
        viewer_client = self.client
        self.login_as('admin')
        self.assertEqual(self.call('PUT', '/api/auth/users/' + users['VIEW']['id'], {'role': 'Viewer', 'active': True, 'expiresUtc': past})[0], 200)
        self.client = viewer_client
        self.assertEqual(self.call('GET', root + '/snapshot')[0], 401)
        self.login_as('admin')
        self.assertTrue({'SaveRole:ManageUsers', 'CreateUser'} <= {a['action'] for a in self.call('GET', root + '/audit')[1]})
        print('Verified permissions, custom roles, escalation guards, expiry at login and on open sessions.')

    def test_area_scope(self):
        root = '/api/v1/parking'
        self.login_as('admin')
        # Two gates, one lane each.
        ids = {}
        for code in ['GATE1', 'GATE2']:
            area = self.call('PUT', root + '/areas', {'code': code, 'name': code, 'kind': 'VehicleGate'})[1]
            location = self.call('PUT', root + '/locations', {'areaId': area['id'], 'name': 'Lanes'})[1]
            lane = code.lower() + '-in'
            self.assertEqual(self.call('PUT', root + '/lanes', {'id': lane, 'direction': 'Entry', 'policy': 'PlateOnly', 'plateThreshold': .9,
                'faceThreshold': .8, 'shadowMode': True, 'simulation': True, 'locationId': location['id']})[0], 200)
            ids[code] = area['id']
        observe = lambda lane: self.call('POST', root + '/simulate', {'laneId': lane, 'correlationId': secrets.token_hex(8), 'plate': '30A12345', 'plateConfidence': .5, 'faceConfidence': 0})
        gate1_event = observe('gate1-in')[1]
        for name, role in [('G2GUARD', 'Guard'), ('G2TECH', 'Technician')]:
            self.assertEqual(self.call('POST', '/api/auth/users', {'username': name, 'password': self.password, 'role': role, 'areaIds': [ids['GATE2']]})[0], 200)
        self.assertEqual(self.call('POST', '/api/auth/users', {'username': 'BOSS2', 'password': self.password, 'role': 'Admin', 'areaIds': [ids['GATE2']]})[0], 409)
        self.assertEqual(self.call('DELETE', root + '/areas/' + ids['GATE2'])[0], 409)  # would lift the guard's limit

        self.login_as('G2GUARD')
        self.assertEqual(self.call('GET', '/api/auth/me')[1]['areaIds'], [ids['GATE2']])
        snapshot = self.call('GET', root + '/snapshot')[1]
        self.assertEqual([l['id'] for l in snapshot['lanes']], ['gate2-in'])
        self.assertEqual(snapshot['events'], [])  # the Gate I event is hidden
        self.assertEqual([a['code'] for a in self.call('GET', root + '/areas')[1]], ['GATE2'])
        self.assertEqual(observe('gate1-in')[0], 403)
        status, own = observe('gate2-in'); self.assertEqual(status, 200, own)
        self.assertEqual(self.call('POST', root + '/events/' + gate1_event['id'] + '/review', {'approve': True, 'note': 'not mine'})[0], 403)
        self.assertEqual(self.call('POST', root + '/events/' + own['id'] + '/review', {'approve': True, 'note': 'checked'})[0], 200)
        self.assertEqual([e['laneId'] for e in self.call('GET', root + '/events')[1]['items']], ['gate2-in'])
        alerts = self.call('GET', root + '/snapshot')[1]['alerts']
        self.assertTrue(alerts and all(a['eventId'] == own['id'] for a in alerts))
        gate1_alert = None
        self.login_as('admin')
        gate1_alert = next(a for a in self.call('GET', root + '/snapshot')[1]['alerts'] if a['eventId'] == gate1_event['id'])
        self.login_as('G2GUARD')
        self.assertEqual(self.call('POST', root + '/alerts/' + gate1_alert['id'] + '/resolve', {})[0], 403)
        # A limited technician can't change site-wide configuration.
        self.login_as('G2TECH')
        self.assertEqual(self.call('PUT', root + '/areas', {'code': 'NEW', 'name': 'New'})[0], 403)
        print('Verified area-limited accounts: visibility, actions, alerts, admin/config exceptions, delete guard.')

    PNG = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII='

    def t1550(self, plate, grp, mac='38-24-F1-0E-80-F8'):
        return {'data': {'dev_net_info': [{'device_name': 'T1550FAI-Z', 'mac': mac}], 'ai_snap_picture': {'PlateInfo': [
            {'Id': '', 'GrpId': grp, 'SnapId': plate, 'StrChn': 'CH1', 'StartTime': int(time.time()), 'PlateImg': self.PNG, 'BgImg': self.PNG}]},
            'subscribe_id': 1, 'data_pos': grp}}

    def post_raw(self, url, payload):
        request = urllib.request.Request(url, data=json.dumps(payload).encode(), method='POST', headers={'Content-Type': 'application/json'})
        try: return urllib.request.urlopen(request, timeout=10).status
        except urllib.error.HTTPError as exc: return exc.code

    def wait_events(self, predicate, seconds=8):
        for _ in range(seconds * 10):
            events = [e for e in self.call('GET', '/api/v1/parking/snapshot')[1]['events'] if predicate(e)]
            if events: return events
            time.sleep(.1)
        return []

    def test_third_party_camera_receiver(self):
        root = '/api/v1/parking'
        receiver_key = secrets.token_urlsafe(32)
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0)); camera_port = listener.getsockname()[1]
        self.env.update({'Receiver__Key': receiver_key, 'Receiver__Port': str(camera_port), 'Receiver__AllowedIps': '127.0.0.1'})
        self.extra_urls = [f'http://127.0.0.1:{camera_port}']
        self.process.terminate(); self.process.wait(timeout=10); self.start()
        self.login_as('admin')
        self.assertEqual(self.call('PUT', root + '/cameras', {'id': 'lpr-1', 'name': 'Plate camera', 'laneId': 'entry-1',
            'externalDevice': '38-24-F1-0E-80-F8', 'externalChannel': 'CH1'})[0], 200)
        url = f'{self.base}{root}/receiver/t1550?key='
        self.assertEqual(self.post_raw(url + 'wrong', self.t1550('30A12345', 1)), 401)
        message = self.t1550('30A12345', 1)
        self.assertEqual(self.post_raw(url + receiver_key, message), 200)
        self.assertEqual(self.post_raw(url + receiver_key, message), 200)          # camera retry of the same message
        self.assertEqual(self.post_raw(url + receiver_key, self.t1550('30A12345', 2)), 200)  # same car seen again
        events = self.wait_events(lambda e: e['source'] == 'ThirdPartyCamera')
        self.assertEqual(len(events), 1, events)                                    # stored once + merged (GC-24)
        event = events[0]
        self.assertEqual((event['plate'], event['outcome']), ('30A12345', 'ManualReview'))  # untrusted camera → guard
        self.assertTrue(event['receivedUtc'] and event['evidenceId'])
        # After field evaluation an admin trusts the camera; a registered car is then allowed.
        self.call('PUT', root + '/cameras', {'id': 'lpr-1', 'name': 'Plate camera', 'laneId': 'entry-1', 'externalDevice': '3824F10E80F8', 'externalChannel': 'CH1', 'trustedConfidence': .99})
        self.call('PUT', root + '/lanes', {'id': 'entry-1', 'direction': 'Entry', 'policy': 'PlateOnly', 'plateThreshold': .95, 'faceThreshold': .8, 'shadowMode': True, 'simulation': True})
        self.assertEqual(self.call('POST', root + '/vehicles', {'plate': '30A67890', 'owner': 'Registered', 'kind': 'Car'})[0], 200)
        self.post_raw(url + receiver_key, self.t1550('30A67890', 3))
        allowed = self.wait_events(lambda e: e['plate'] == '30A67890')
        self.assertEqual(allowed[0]['outcome'], 'Allow')
        # Unknown device → alert, no event. Alarm-only → ignored.
        self.post_raw(url + receiver_key, self.t1550('51F11111', 4, mac='AA-BB-CC-00-11-22'))
        self.post_raw(url + receiver_key, {'data': {'dev_net_info': [{'mac': '38-24-F1-0E-80-F8'}], 'alarm_list': [{'channel_alarm': []}]}})
        for _ in range(80):
            health = self.call('GET', root + '/health')[1]
            if sum(x['count'] for x in health['inbox']) >= 4 and all(x['status'] != 'Pending' for x in health['inbox']): break
            time.sleep(.1)
        self.assertIn('Ignored', {x['status'] for x in health['inbox']})
        self.assertTrue(any(a['reason'].startswith('UNKNOWN_CAMERA:AABBCC001122') for a in self.call('GET', root + '/snapshot')[1]['alerts']))
        self.assertFalse(self.wait_events(lambda e: e['plate'] == '51F11111', seconds=1))
        # Fixed-path cameras: POST /api only on the receiver port, only from allowed IPs; nothing else is served there.
        self.assertEqual(self.post_raw(f'http://127.0.0.1:{camera_port}/api', self.t1550('30B55555', 5)), 200)
        self.assertEqual(self.post_raw(f'http://127.0.0.1:{camera_port}/api/auth/login', {}), 404)
        self.assertIn(self.post_raw(f'{self.base}/api', self.t1550('30C77777', 6)), (400, 404))  # refused on the normal port (CSRF or 404)
        self.assertTrue(self.wait_events(lambda e: e['plate'] == '30B55555'))
        self.assertFalse(self.wait_events(lambda e: e['plate'] == '30C77777', seconds=1))
        print('Verified camera receiver: key, store-before-ACK dedupe, merge, trust, unknown device, alarm-only, receiver-port isolation.')

    def test_device_profiles_and_identities(self):
        # CR-015: a new device is added by configuration (profile + identity mapping), no code, no rebuild.
        root = '/api/v1/parking'
        key = secrets.token_urlsafe(32)
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0)); xml_port = listener.getsockname()[1]
        self.env.update({'Receiver__Key': key, 'Receiver__Ports': f'{xml_port}=hik-xml', 'Receiver__AllowedIps': '127.0.0.1'})
        self.extra_urls = [f'http://127.0.0.1:{xml_port}']
        self.process.terminate(); self.process.wait(timeout=10); self.start()
        self.login_as('admin')
        face = {'match': [{'path': 'event', 'equals': 'verify'}],
                'fields': {'deviceId': '$.device.sn', 'personId': {'path': 'userCode', 'transform': ['trim', 'upper']}, 'confidence': {'path': 'score', 'scale': 100}}}
        sample = {'event': 'verify', 'device': {'sn': 'ZK-09'}, 'userCode': ' nv042 ', 'score': 97}
        status, result = self.call('POST', root + '/device-profiles/test', {'format': 'json', 'definition': json.dumps(face), 'sample': json.dumps(sample)})
        self.assertEqual(status, 200)
        self.assertTrue(result['ok'], result)
        self.assertEqual((result['observations'][0]['externalPersonId'], result['observations'][0]['confidence']), ('NV042', '0.97'))
        status, result = self.call('POST', root + '/device-profiles/test', {'format': 'json', 'definition': '{"fields":{"plate":"a"}}', 'sample': '{}'})
        self.assertFalse(result['ok']); self.assertTrue(any('deviceId' in e for e in result['errors']))
        self.assertEqual(self.call('PUT', root + '/device-profiles', {'id': 'zk-face', 'name': 'Face', 'definition': '{"fields":{}}'})[0], 409)
        self.assertEqual(self.call('PUT', root + '/device-profiles', {'id': 't1550', 'name': 'Clash', 'definition': json.dumps(face)})[0], 409)
        profile = {'id': 'zk-face', 'name': 'Face terminal', 'format': 'json', 'definition': json.dumps(face), 'sample': json.dumps(sample)}
        self.assertEqual(self.call('PUT', root + '/device-profiles', profile)[1]['version'], 1)
        self.assertEqual(self.call('PUT', root + '/device-profiles', profile)[1]['version'], 2)
        lane = {'id': 'flap-9', 'direction': 'Entry', 'policy': 'FaceOnly', 'plateThreshold': .9, 'faceThreshold': .8, 'shadowMode': True, 'simulation': True, 'subject': 'Person'}
        self.assertEqual(self.call('PUT', root + '/lanes', lane)[0], 200)
        self.assertEqual(self.call('PUT', root + '/cameras', {'id': 'face-9', 'name': 'Face 9', 'laneId': 'flap-9', 'externalDevice': 'ZK-09', 'trustedConfidence': .99})[0], 200)
        self.assertEqual(self.call('PUT', root + '/people', {'personId': 42, 'name': 'Worker 42', 'active': True})[0], 200)
        self.assertEqual(self.call('PUT', root + '/person-identities', {'source': 'nope', 'externalId': 'NV042', 'personId': 42})[0], 409)
        self.assertEqual(self.call('PUT', root + '/person-identities', {'source': 'zk-face', 'externalId': 'NV042', 'personId': 42})[0], 200)
        self.assertEqual(self.call('POST', root + '/person-identities/import', [{'source': 'zk-face', 'externalId': 'NV043', 'personId': 43}])[1]['imported'], 1)
        url = f'{self.base}{root}/receiver/zk-face?key={key}'
        self.assertEqual(self.post_raw(url, {**sample, 't': 1}), 200)
        known = self.wait_events(lambda e: e['laneId'] == 'flap-9' and e.get('personId') == 42)
        self.assertTrue(known)
        self.assertEqual(known[0]['outcome'], 'Allow')                                  # mapped ID + listed person + trusted device
        self.assertEqual(self.post_raw(url, {**sample, 'userCode': 'nv999', 't': 2}), 200)
        unknown = self.wait_events(lambda e: e['laneId'] == 'flap-9' and e.get('personId') is None)
        self.assertEqual(unknown[0]['outcome'], 'ManualReview')                         # unmapped ID never authorises
        self.assertTrue(any(a['reason'] == 'UNKNOWN_PERSON:zk-face/NV999' for a in self.call('GET', root + '/snapshot')[1]['alerts']))
        # XML device on its own receiver port (it can only post to host:port/api).
        xml_profile = {'records': 'Alert.ANPR[*]', 'fields': {'deviceId': '$.Alert.mac', 'plate': 'plate'}}
        self.assertEqual(self.call('PUT', root + '/device-profiles', {'id': 'hik-xml', 'name': 'XML ANPR', 'format': 'xml', 'definition': json.dumps(xml_profile)})[0], 200)
        self.assertEqual(self.call('PUT', root + '/cameras', {'id': 'lpr-9', 'name': 'XML plate', 'laneId': 'entry-1', 'externalDevice': 'A4-14-37-00-00-09'})[0], 200)
        def post_xml(body, content_type='application/xml'):
            request = urllib.request.Request(f'http://127.0.0.1:{xml_port}/api', data=body.encode(), method='POST', headers={'Content-Type': content_type})
            try: return urllib.request.urlopen(request, timeout=10).status
            except urllib.error.HTTPError as exc: return exc.code
        self.assertEqual(post_xml('<Alert xmlns="urn:x"><mac>a4:14:37:00:00:09</mac><ANPR><plate>30H99999</plate></ANPR></Alert>'), 200)
        self.assertEqual(post_xml('{"Alert":{}}', 'application/json'), 400)             # the port's profile expects XML
        self.assertEqual(self.wait_events(lambda e: e['plate'] == '30H99999')[0]['outcome'], 'ManualReview')
        # Disabled profile: its messages are refused; deleting keeps the audit trail.
        self.call('PUT', root + '/device-profiles', {**profile, 'enabled': False})
        self.assertEqual(self.post_raw(url, {**sample, 't': 3}), 404)
        self.assertEqual(self.call('DELETE', root + '/device-profiles/zk-face')[0], 204)
        audit = json.dumps(self.call('GET', root + '/audit')[1])
        self.assertIn('SaveDeviceProfile', audit); self.assertIn('DeleteDeviceProfile', audit)
        print('Verified device profiles: test endpoint, validation, versioning, identity mapping (known -> Allow, unknown -> review + alert), XML on its own port, disable/delete.')

    def test_person_lane_face_terminal_and_ppe(self):
        root = '/api/v1/parking'
        key = secrets.token_urlsafe(32)
        self.env['Receiver__Key'] = key
        self.process.terminate(); self.process.wait(timeout=10); self.start()
        self.login_as('admin')
        lane = {'id': 'flap-1', 'direction': 'Entry', 'policy': 'PlateOnly', 'plateThreshold': .9, 'faceThreshold': .8, 'shadowMode': True,
                'simulation': True, 'subject': 'Person', 'ppeRules': 'helmet=required,boots=warn', 'ppeMissingMode': 'Review'}
        self.assertEqual(self.call('PUT', root + '/lanes', lane)[0], 409)          # person lane must decide by face
        self.assertEqual(self.call('PUT', root + '/lanes', {**lane, 'policy': 'FaceOnly', 'ppeRules': 'helmet=perhaps'})[0], 409)
        self.assertEqual(self.call('PUT', root + '/lanes', {**lane, 'policy': 'FaceOnly'})[0], 200)
        for cam, device in [('face-1', 'ZK-01'), ('ppe-1', 'PPE-01')]:
            self.assertEqual(self.call('PUT', root + '/cameras', {'id': cam, 'name': cam, 'laneId': 'flap-1', 'externalDevice': device, 'trustedConfidence': .99})[0], 200)
        for person in [7, 8, 9]:
            self.assertEqual(self.call('PUT', root + '/people', {'personId': person, 'name': f'Worker {person}'})[0], 200)
        frames = iter(range(1, 1000))  # real PPE cameras send a frame counter/timestamp; identical bodies are treated as retries
        ppe = lambda people, helmet, boots=True: self.post_raw(f'{self.base}{root}/receiver/ppe?key={key}',
            {'deviceId': 'PPE-01', 'frame': next(frames), 'peopleInRoi': people, 'items': {'helmet': helmet, 'boots': boots}})
        face = lambda person: self.post_raw(f'{self.base}{root}/receiver/generic?key={key}', {'deviceId': 'ZK-01', 'personId': person, 'confidence': .97})
        def decision(person):
            events = self.wait_events(lambda e: e['personId'] == person)
            self.assertTrue(events, f'no event for person {person}'); return events[0]
        ppe(1, True); face(7)
        self.assertEqual((decision(7)['outcome'], decision(7)['ppeOutcome']), ('Allow', 'Ok'))
        ppe(1, False); ppe(1, False); face(8)
        self.assertEqual((decision(8)['outcome'], decision(8)['reason']), ('Deny', 'PPE_MISSING:helmet'))
        ppe(2, True); face(9)
        self.assertEqual((decision(9)['outcome'], decision(9)['reason']), ('ManualReview', 'PPE_ROI_NOT_SINGLE'))
        ppe(1, True); face(99)                                                      # not on the access list
        self.assertEqual(decision(99)['outcome'], 'ManualReview')
        print('Verified person lane: face terminal via generic adapter, access list, PPE ok/deny/ROI review.')

    def test_gate_operations(self):
        root = '/api/v1/parking'
        self.login_as('admin')
        base_lane = {'direction': 'Entry', 'policy': 'PlateOnly', 'plateThreshold': .9, 'faceThreshold': .8, 'shadowMode': True, 'simulation': True}
        simulate = lambda lane, plate: self.call('POST', root + '/simulate', {'laneId': lane, 'correlationId': secrets.token_hex(8), 'plate': plate, 'plateConfidence': .99, 'faceConfidence': 0})[1]
        # GC-15: a delivery vehicle is registered but always goes to the guard.
        self.assertEqual(self.call('POST', root + '/vehicles', {'plate': '30D11111', 'owner': 'Supplier', 'kind': 'Car', 'category': 'Delivery'})[0], 200)
        self.assertEqual(simulate('entry-1', '30D11111')['reason'], 'CATEGORY_REVIEW:Delivery')
        # GC-16: walk-in pass for today.
        status, walk = self.call('POST', root + '/visits/walk-in', {'plate': '30K-999.99', 'name': 'Unknown driver'})
        self.assertEqual(status, 200, walk)
        self.assertEqual(walk['kind'], 'WalkIn')
        until = datetime.fromisoformat(walk['untilUtc'].rstrip('Z')[:26])
        self.assertTrue(0 < (until - datetime.utcnow()).total_seconds() <= 24 * 3600)
        self.assertEqual(simulate('entry-1', '30K99999')['reason'], 'ACTIVE_VISITOR')
        # GC-12: exit needs an entry.
        self.assertEqual(self.call('POST', root + '/vehicles', {'plate': '30A12345', 'owner': 'Resident', 'kind': 'Car'})[0], 200)
        self.assertEqual(self.call('PUT', root + '/lanes', {**base_lane, 'id': 'exit-1', 'direction': 'Exit', 'exitRequiresEntry': True})[0], 200)
        self.assertEqual(simulate('exit-1', '30A12345')['reason'], 'EXIT_WITHOUT_ENTRY')
        # GC-17: inspection photo required before opening.
        self.assertEqual(self.call('PUT', root + '/lanes', {**base_lane, 'id': 'entry-1', 'shadowMode': False, 'requireInspectionPhoto': True})[0], 200)
        entry = simulate('entry-1', '30A12345')
        self.assertEqual(entry['outcome'], 'Allow')
        status, body = self.call('POST', root + '/events/' + entry['id'] + '/open', {})
        self.assertEqual(status, 409); self.assertIn('inspection photo', body['message'])
        boundary = 'photo-boundary'
        png = base64.b64decode(self.PNG)
        form = (f'--{boundary}\r\nContent-Disposition: form-data; name="note"\r\n\r\ncargo ok\r\n'
                f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="p.png"\r\nContent-Type: image/png\r\n\r\n').encode() + png + f'\r\n--{boundary}--\r\n'.encode()
        token = self.call('GET', '/api/auth/csrf')[1]['token']
        request = urllib.request.Request(self.base + root + '/events/' + entry['id'] + '/photos', data=form, method='POST',
            headers={'Content-Type': 'multipart/form-data; boundary=' + boundary, 'X-CSRF-Token': token})
        photo = json.load(self.client.open(request))
        self.assertEqual((photo['kind'], photo['takenBy'], len(photo['sha256'])), ('Inspection', 'ADMIN', 64))
        status, command = self.call('POST', root + '/events/' + entry['id'] + '/open', {})
        self.assertEqual(status, 200, command)
        with self.client.open(self.base + root + '/photos/' + photo['id']) as response:
            self.assertEqual((response.status, response.read()), (200, png))
        # Passage creates the session; the exit event can then be compared with its entry.
        self.call('POST', root + '/commands/' + command['id'] + '/simulate-passage', {})
        exit_event = simulate('exit-1', '30A12345')
        self.assertEqual(exit_event['outcome'], 'Allow')
        linked = self.call('GET', root + '/events/' + exit_event['id'] + '/entry')[1]
        self.assertTrue(linked['found']); self.assertEqual(linked['entry']['id'], entry['id']); self.assertEqual(len(linked['photos']), 1)
        print('Verified categories, walk-in until next day start, exit-needs-entry, inspection photo gate, entry/exit comparison.')

    def on(self, base, fn):
        """Run fn against another station with its own cookies."""
        saved = (self.base, self.client, self.jar)
        self.base = base
        try:
            for _ in range(100):
                try:
                    if self.call('GET', '/api/auth/csrf', csrf=False)[0] == 200: break
                except (OSError, urllib.error.URLError): time.sleep(.1)
            self.login_as('admin'); return fn()
        finally: self.base, self.client, self.jar = saved

    def test_presence_across_two_stations(self):
        root = '/api/v1/parking'
        sync_key = secrets.token_urlsafe(32)
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0)); b_port = listener.getsockname()[1]
        b_url = f'http://127.0.0.1:{b_port}'
        self.env.update({'Sync__Key': sync_key, 'Parking__StationId': 'GATE-I', 'Sync__Peers': b_url})
        self.process.terminate(); self.process.wait(timeout=10); self.start()
        station_b_env = {'Sync__Key': sync_key, 'Parking__StationId': 'GATE-II', 'Sync__Peers': self.base}
        # Station B listens on the reserved port so A's peer URL stays valid across a restart.
        def start_b():
            env = {**self.env, **station_b_env, 'ConnectionStrings__Parking': 'Data Source=' + str(Path(self.temp.name) / 'station-b.db'),
                   'Parking__EvidenceDirectory': str(Path(self.temp.name) / 'evidence-b')}
            log = open(Path(self.temp.name) / 'station-b.log', 'a+', encoding='utf-8')
            p = subprocess.Popen([os.environ.get('PARKING_DOTNET', 'dotnet'), str(ROOT / 'DSAVehicle/bin/Debug/net8.0/DSAVehicle.dll'), '--urls', b_url],
                cwd=ROOT / 'DSAVehicle', env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            started.append((p, log))
            return p
        started = []
        def stop_b():
            for p, log in started:
                if p.poll() is None: p.terminate(); p.wait(timeout=10)
                log.close()
        station_b = start_b()
        try:
            self.run_two_stations(root, b_url, start_b, lambda: station_b)
        finally:
            stop_b()

    def run_two_stations(self, root, b_url, start_b, current_b):
        station_b = current_b()

        def setup_station(direction, lane_id):
            area = self.call('PUT', root + '/areas', {'code': 'MINE', 'name': 'Main shaft', 'kind': 'Mine'})[1]
            location = self.call('PUT', root + '/locations', {'areaId': area['id'], 'name': 'Flap gates'})[1]
            self.assertEqual(self.call('PUT', root + '/lanes', {'id': lane_id, 'direction': direction, 'policy': 'FaceOnly', 'plateThreshold': .9, 'faceThreshold': .8,
                'shadowMode': False, 'simulation': True, 'subject': 'Person', 'locationId': location['id']})[0], 200)
            for person in (7, 8): self.call('PUT', root + '/people', {'personId': person, 'name': f'Worker {person}'})
        def pass_through(lane_id, person):
            event = self.call('POST', root + '/simulate', {'laneId': lane_id, 'correlationId': secrets.token_hex(8), 'plate': '', 'plateConfidence': 0, 'personId': person, 'faceConfidence': .99})[1]
            self.assertEqual(event['outcome'], 'Allow', event)
            command = self.call('POST', root + '/events/' + event['id'] + '/open', {})[1]
            self.assertEqual(self.call('POST', root + '/commands/' + command['id'] + '/simulate-passage', {})[0], 200)
            time.sleep(8.2)  # lane cooldown before the next command on this lane
        def inside():
            return {x['subjectKey'] for x in self.call('GET', root + '/presence')[1]['inside']}
        def wait_for(predicate, seconds=10):
            for _ in range(seconds * 10):
                value = inside()
                if predicate(value): return value
                time.sleep(.1)
            return inside()

        self.login_as('admin'); setup_station('Entry', 'flap-in')
        self.on(b_url, lambda: setup_station('Exit', 'flap-out'))
        # Worker 7 enters at Gate I → Gate II knows within seconds (LAN path).
        self.login_as('admin'); pass_through('flap-in', 7)
        self.assertEqual(self.on(b_url, lambda: wait_for(lambda v: 'P:7' in v)), {'P:7'})
        # Worker 7 leaves at Gate II → Gate I sees nobody inside.
        self.on(b_url, lambda: pass_through('flap-out', 7))
        self.login_as('admin')
        self.assertEqual(wait_for(lambda v: 'P:7' not in v), set())
        self.assertEqual(self.call('GET', root + '/presence')[1]['unmatched'], [])
        # Gate II is down while worker 8 enters; the record is delivered when it comes back.
        station_b.terminate(); station_b.wait(timeout=10)
        pass_through('flap-in', 8)
        station_b = start_b()
        self.assertEqual(self.on(b_url, lambda: wait_for(lambda v: 'P:8' in v, seconds=20)), {'P:8'})
        print('Verified presence across two stations over LAN: entry at I seen at II, exit at II closes it at I, backlog after II restarts.')

    def test_monitoring_alerts_wall_and_device_health(self):
        root = '/api/v1/parking'
        self.login_as('admin')
        for name, role in [('GUARD1', 'Guard'), ('SAFETY1', 'SafetyOfficer')]:
            self.assertEqual(self.call('POST', '/api/auth/users', {'username': name, 'password': self.password, 'role': role})[0], 200)
        self.call('POST', root + '/simulate', {'laneId': 'entry-1', 'correlationId': 'a1', 'plate': '30A12345', 'plateConfidence': .2, 'faceConfidence': 0})
        alert = next(a for a in self.call('GET', root + '/snapshot')[1]['alerts'] if a['reason'] == 'INSUFFICIENT_IDENTITY_EVIDENCE')
        self.login_as('GUARD1')
        status, acked = self.call('POST', root + '/alerts/' + alert['id'] + '/acknowledge', {})
        self.assertEqual((status, acked['status'], acked['acknowledgedBy']), (200, 'Acknowledged', 'GUARD1'))
        self.assertEqual(self.call('POST', root + '/alerts/' + alert['id'] + '/acknowledge', {})[0], 409)
        self.assertEqual(self.call('POST', root + '/alerts/' + alert['id'] + '/resolve', {})[0], 409)                      # note required
        self.assertEqual(self.call('POST', root + '/alerts/' + alert['id'] + '/resolve', {'note': 'bird', 'falseAlarm': True})[0], 409)  # guard can't mark false alarm
        self.login_as('SAFETY1')
        status, closed = self.call('POST', root + '/alerts/' + alert['id'] + '/resolve', {'note': 'Reflection on the plate', 'falseAlarm': True})
        self.assertEqual((status, closed['status'], closed['resolved']), (200, 'FalseAlarm', True))
        self.assertNotIn(alert['id'], [a['id'] for a in self.call('GET', root + '/snapshot')[1]['alerts']])
        # Video wall + gateway config.
        self.login_as('admin')
        self.assertEqual(self.call('PUT', root + '/cameras', {'id': 'wall-1', 'name': 'Gate cam', 'laneId': 'entry-1', 'source': 'rtsp://user:secret@10.0.0.9/main',
            'wallSource': 'rtsp://user:secret@10.0.0.9/sub', 'record24x7': False, 'externalDevice': 'AA-00-00-00-00-01', 'silentAlertSeconds': 30})[0], 200)
        wall = self.call('GET', root + '/video/wall')[1]
        self.assertEqual([c['id'] for g in wall['groups'] for c in g['cameras']], ['wall-1'])
        with self.client.open(self.base + root + '/video/gateway-config') as response:
            config = response.read().decode()
        self.assertIn('wall-1:', config); self.assertIn('rtsp://user:secret@10.0.0.9/sub', config)
        self.login_as('GUARD1')
        self.assertEqual(self.call('GET', root + '/video/gateway-config')[0], 403)   # contains camera passwords
        self.assertEqual(self.call('GET', root + '/video/wall')[0], 200)
        # GC-27: the AI device never sent anything → silent alert from the maintenance loop (every 15 s).
        for _ in range(250):
            if any(a['reason'] == 'CAMERA_SILENT:wall-1' for a in self.call('GET', root + '/snapshot')[1]['alerts']): break
            time.sleep(.1)
        self.assertTrue(any(a['reason'] == 'CAMERA_SILENT:wall-1' for a in self.call('GET', root + '/snapshot')[1]['alerts']))
        print('Verified alert lifecycle and permissions, video wall list, admin-only gateway config, silent device alert.')

    def test_search_stats_and_excel(self):
        import zipfile, io
        root = '/api/v1/parking'
        self.login_as('admin')
        self.assertEqual(self.call('POST', root + '/vehicles', {'plate': '30A12345', 'owner': 'Resident', 'kind': 'Car'})[0], 200)
        for lane, plate, conf in [('entry-1', '30A12345', .99), ('entry-1', '30B22222', .5), ('exit-1', '30A12345', .99)]:
            self.call('POST', root + '/simulate', {'laneId': lane, 'correlationId': secrets.token_hex(8), 'plate': plate, 'plateConfidence': conf, 'faceConfidence': 0})
        search = lambda **q: self.call('GET', root + '/search?' + urllib.parse.urlencode(q))[1]
        self.assertEqual(search()['total'], 3)
        self.assertEqual(search(outcome='ManualReview')['total'], 1)
        self.assertEqual(search(laneId='exit-1', plate='30A-123.45')['total'], 1)
        self.assertEqual(search(reason='INSUFFICIENT')['total'], 1)
        stats = self.call('GET', root + '/stats?groupBy=lane')[1]
        by_lane = {r['bucket']: r for r in stats['rows']}
        self.assertEqual((by_lane['entry-1']['events'], by_lane['entry-1']['review'], by_lane['exit-1']['allow']), (2, 1, 1))
        self.assertEqual(len(self.call('GET', root + '/stats?groupBy=day')[1]['rows']), 1)
        with self.client.open(self.base + root + '/export?outcome=Allow&lang=en') as response:
            self.assertEqual(response.status, 200)
            workbook = zipfile.ZipFile(io.BytesIO(response.read()))
        sheets = workbook.read('xl/workbook.xml').decode()
        for name in ['Overview', 'Events', 'By area', 'Performance', 'Evidence']: self.assertIn(f'name="{name}"', sheets)
        strings = workbook.read('xl/sharedStrings.xml').decode()
        self.assertIn('30A12345', strings); self.assertNotIn('30B22222', strings)   # the export follows the filter
        self.assertIn('outcome Allow', strings)                                     # filters written on the Overview sheet
        with self.client.open(self.base + root + '/export') as response:   # default language: Vietnamese sheet names
            vi_sheets = zipfile.ZipFile(io.BytesIO(response.read())).read('xl/workbook.xml').decode()
        for name in ['Tổng quan', 'Sự kiện', 'Theo khu vực', 'Hiệu suất', 'Bằng chứng']: self.assertIn(f'name="{name}"', vi_sheets)
        self.assertIn('ExportExcel:2 events', {a['action'] for a in self.call('GET', root + '/audit')[1]})
        self.assertEqual(self.call('POST', '/api/auth/users', {'username': 'G9', 'password': self.password, 'role': 'Guard'})[0], 200)
        self.login_as('G9')
        self.assertEqual(self.call('GET', root + '/export')[0], 403)
        self.assertEqual(self.call('GET', root + '/search')[0], 200)
        print('Verified multi-filter search, stats by lane/day, 5-sheet Excel following the filter, audit, ViewReports permission.')

if __name__ == '__main__':
    unittest.main(verbosity=2)
