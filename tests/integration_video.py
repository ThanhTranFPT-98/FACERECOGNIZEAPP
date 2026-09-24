"""CR-001 end-to-end: real API + real ffmpeg recording a simulated camera, local ring buffer, upload to a folder "cloud".

Requires PARKING_FFMPEG (path to ffmpeg) and the API built in Debug. No real camera or cloud account is contacted.
"""
import os
import sqlite3
import subprocess
import time
import unittest
import urllib.request
from datetime import datetime
from pathlib import Path

from integration_parking import ParkingIntegration

FFMPEG = os.environ.get('PARKING_FFMPEG')


@unittest.skipUnless(FFMPEG, 'Set PARKING_FFMPEG to an ffmpeg executable to run the video test.')
class VideoIntegration(ParkingIntegration):
    test_operator_workflow_and_security = None  # covered by integration_parking.py

    def start(self):
        temp = Path(self.temp.name)
        self.sample = temp / 'sample.ts'
        if not self.sample.exists():
            subprocess.run([FFMPEG, '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc=size=320x240:rate=10', '-t', '20',
                            '-c:v', 'libx264', '-g', '10', '-pix_fmt', 'yuv420p', str(self.sample)], check=True)
        self.video_dir, self.cloud_dir = temp / 'video', temp / 'cloud'
        self.env.update({'Video__Enabled': 'true', 'Video__FfmpegPath': FFMPEG, 'Video__Directory': str(self.video_dir), 'Video__SegmentSeconds': '2',
                         'Cloud__Enabled': 'true', 'Cloud__Provider': 'Folder', 'Cloud__FolderPath': str(self.cloud_dir), 'Cloud__SiteId': 'site-it'})
        super().start()

    def tearDown(self):
        # A hard-killed API cannot stop its ffmpeg children; the next start kills them through ffmpeg.pid, so the test does the same.
        pid_file = self.video_dir / 'cam1' / 'ffmpeg.pid'
        if self.process: self.process.terminate(); self.process.wait(timeout=10); self.process = None
        if pid_file.exists():
            subprocess.run(['taskkill', '/F', '/T', '/PID', pid_file.read_text().strip()], capture_output=True)
        time.sleep(1)
        super().tearDown()

    @staticmethod
    def when(value):
        head, _, fraction = value.rstrip('Z').partition('.')
        return datetime.fromisoformat(head + ('.' + fraction[:6] if fraction else ''))

    def wait_for(self, check, seconds, message):
        deadline = time.time() + seconds
        while time.time() < deadline:
            result = check()
            if result: return result
            time.sleep(1)
        self.log.flush(); self.log.seek(0)
        self.fail(message + '\nAPI log tail:\n' + self.log.read()[-3000:])

    def raw(self, path):
        with self.client.open(urllib.request.Request(self.base + path), timeout=30) as response:
            return response.status, response.headers.get('Content-Type'), response.read()

    def cloud_files(self, *parts):
        folder = self.cloud_dir.joinpath(*parts)
        return list(folder.rglob('*.*')) if folder.exists() else []

    def test_record_upload_link_and_play(self):
        root = '/api/v1/parking'
        self.assertEqual(self.call('POST', '/api/auth/login', {'username': 'admin', 'password': self.password})[0], 200)
        camera = {'id': 'cam1', 'name': 'Gate in 1', 'laneId': 'entry-1', 'source': 'file:' + str(self.sample), 'enabled': True, 'record24x7': True}
        self.assertEqual(self.call('PUT', root + '/cameras', {**camera, 'source': 'http://example.com/x'})[0], 409)
        status, body = self.call('PUT', root + '/cameras', camera); self.assertEqual(status, 200, body)

        # The stream password must not be readable in the database.
        with sqlite3.connect(Path(self.temp.name) / 'parking.db') as db:
            stored = db.execute("SELECT SourceProtected FROM Cameras WHERE Id = 'cam1'").fetchone()[0]
        self.assertNotIn('sample.ts', stored)
        listed = self.call('GET', root + '/cameras')[1]['cameras'][0]
        self.assertEqual(listed['source'], 'simulated file')

        # Recording produces closed segments, and they reach the cloud folder.
        self.wait_for(lambda: self.call('GET', root + '/cameras')[1]['cameras'][0]['lastSegmentUtc'], 40, 'No segment was recorded.')
        self.wait_for(lambda: [p for p in self.cloud_files('site-it', 'video', 'cam1') if p.suffix == '.ts'], 40, 'No segment was uploaded.')

        # A decision on the camera's lane is synced as a record and linked to the video around it.
        status, event = self.call('POST', root + '/simulate', {'laneId': 'entry-1', 'correlationId': 'video-it-1', 'plate': '59X112345', 'plateConfidence': .99})
        self.assertEqual(status, 200, event)
        self.wait_for(lambda: self.cloud_files('site-it', 'records', 'AccessEvent', event['id']), 30, 'Decision record was not uploaded.')
        segments = self.wait_for(lambda: [s for s in self.call('GET', root + '/events/' + event['id'] + '/video')[1] if self.when(s['endUtc']) >= self.when(event['createdUtc'])], 40, 'No segment covers the event.')

        status, content_type, data = self.raw(root + '/video/segments/' + segments[0]['id'] + '/play')
        self.assertEqual(status, 200)
        self.assertEqual(content_type, 'video/mp4')
        self.assertEqual(data[4:8], b'ftyp', 'Browser-playable MP4 expected')

        # Restart: the recorder resumes and keeps producing new segments.
        before = self.call('GET', root + '/cameras')[1]['cameras'][0]['lastSegmentUtc']
        self.process.terminate(); self.process.wait(timeout=10); self.start()
        self.call('POST', '/api/auth/login', {'username': 'admin', 'password': self.password})
        self.wait_for(lambda: self.when(self.call('GET', root + '/cameras')[1]['cameras'][0]['lastSegmentUtc'] or before) > self.when(before), 40, 'Recording did not resume after restart.')
        audit = self.call('GET', root + '/audit')[1]
        self.assertTrue(any(a['action'] == 'ViewVideo' for a in audit))
        print('Verified recording, encrypted camera source, cloud upload, event record sync, event-video link, MP4 playback, restart and audit.')


if __name__ == '__main__':
    unittest.main(verbosity=2)
