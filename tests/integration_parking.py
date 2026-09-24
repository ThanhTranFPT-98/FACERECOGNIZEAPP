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
        self.process = subprocess.Popen([dotnet, str(ROOT / 'MyfirstASP_NET/bin/Debug/net8.0/MyfirstASP_NET.dll'), '--urls', self.base],
            cwd=ROOT / 'MyfirstASP_NET', env=self.env, stdout=self.log, stderr=subprocess.STDOUT,
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

if __name__ == '__main__':
    unittest.main(verbosity=2)
