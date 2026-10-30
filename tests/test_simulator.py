"""The device simulator (scripts/simulator.py) against the real station API, with a LIVE lane (simulation off).

The virtual barrier controller must answer the station's open command and send the lane heartbeat the station needs before it opens a live lane;
the command-line tools must send messages the station accepts. Needs the Debug build of the API.

    python tests/test_simulator.py
"""
import secrets
import socket
import subprocess
import sys
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import integration_parking as harness  # noqa: E402  (the module, not the class: unittest would collect an imported TestCase and run its whole suite)

ROOT = '/api/v1/parking'
SIMULATOR = Path(__file__).resolve().parents[1] / 'scripts' / 'simulator.py'


class SimulatorTests(harness.ParkingIntegration):
    def run_lane(self, mode):
        """A live person lane with PPE rules, a virtual controller in `mode`, one worker who comes in with all PPE. Returns the barrier command."""
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0)); bridge_port = listener.getsockname()[1]
        key = secrets.token_urlsafe(32)
        self.env.update({'Receiver__Key': key, 'Parking__BarrierUrl': f'http://127.0.0.1:{bridge_port}'})
        self.process.terminate(); self.process.wait(timeout=10); self.start()
        self.login_as('admin')
        self.bridge = subprocess.Popen([sys.executable, str(SIMULATOR), 'bridge', '--station', self.base, '--device-key', self.device_key, '--lanes', 'flap-1',
                                        '--port', str(bridge_port), '--delay', '0.3', '--mode', mode], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        lane = {'id': 'flap-1', 'direction': 'Entry', 'policy': 'FaceOnly', 'plateThreshold': .9, 'faceThreshold': .8, 'subject': 'Person',
                'ppeRules': 'helmet=required', 'autoOpen': True, 'shadowMode': False, 'simulation': False}
        self.assertEqual(self.call('PUT', ROOT + '/lanes', lane)[0], 200)
        for camera in [{'id': 'face-1', 'externalDevice': 'ZK-01'}, {'id': 'ppe-1', 'externalDevice': 'AIBOX-1', 'externalRoi': 'L1'}]:
            self.assertEqual(self.call('PUT', ROOT + '/cameras', {'name': camera['id'], 'laneId': 'flap-1', 'trustedConfidence': .99, **camera})[0], 200)
        self.assertEqual(self.call('PUT', ROOT + '/people', {'personId': 7, 'name': 'Worker 7'})[0], 200)
        time.sleep(2)   # a few heartbeats from the virtual controller

        def tool(*arguments):
            done = subprocess.run([sys.executable, str(SIMULATOR), *arguments, '--station', self.base, '--key', key], capture_output=True, text=True)
            self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        tool('ppe', '--roi', 'L1=t-1:helmet,vest,boots')
        tool('face', '--device', 'ZK-01', '--person', '7')
        for _ in range(100):
            commands = self.call('GET', ROOT + '/snapshot')[1]['commands']
            if commands and (mode != 'ack' or commands[0].get('passageUtc')) and commands[0]['status'] != 'Pending': return commands[0]
            time.sleep(.1)
        self.fail('the station never produced a settled barrier command; commands: %s' % commands)

    def tearDown(self):
        if getattr(self, 'bridge', None): self.bridge.terminate(); self.bridge.wait(timeout=10)
        super().tearDown()

    def test_virtual_controller_acknowledges_and_reports_the_passage(self):
        command = self.run_lane('ack')
        self.assertEqual(command['status'], 'Acknowledged')
        self.assertTrue(command['passageUtc'], 'the loop heartbeat should have confirmed the passage')
        print('Verified simulator: PPE + face from the command line, the live lane opens, the virtual controller acknowledges and reports the passage.')

    def test_bad_ack_is_an_unknown_outcome(self):
        command = self.run_lane('bad-ack')
        self.assertEqual(command['status'], 'Unknown')
        self.assertTrue(any(a['reason'] == 'BARRIER_OUTCOME_UNKNOWN' and a['severity'] == 'Critical' for a in self.call('GET', ROOT + '/snapshot')[1]['alerts']))
        print('Verified simulator --mode bad-ack: command Unknown and a Critical BARRIER_OUTCOME_UNKNOWN alert (the gap noted for K11).')

    def test_play_replays_the_device_steps_of_a_scenario(self):
        key = secrets.token_urlsafe(32)
        self.env['Receiver__Key'] = key
        self.process.terminate(); self.process.wait(timeout=10); self.start()
        self.login_as('admin')
        lane = {'id': 'flap-1', 'direction': 'Entry', 'policy': 'FaceOnly', 'plateThreshold': .9, 'faceThreshold': .8, 'subject': 'Person',
                'ppeRules': 'helmet=required,vest=required,boots=required', 'autoOpen': True, 'shadowMode': False, 'simulation': True}
        self.assertEqual(self.call('PUT', ROOT + '/lanes', lane)[0], 200)
        for camera in [{'id': 'face-1', 'externalDevice': 'ZK-01'}, {'id': 'ppe-1', 'externalDevice': 'AIBOX-1', 'externalRoi': 'L1'}]:
            self.assertEqual(self.call('PUT', ROOT + '/cameras', {'name': camera['id'], 'laneId': 'flap-1', 'trustedConfidence': .99, **camera})[0], 200)
        self.assertEqual(self.call('PUT', ROOT + '/people', {'personId': 7, 'name': 'Worker 7'})[0], 200)
        done = subprocess.run([sys.executable, str(SIMULATOR), 'play', str(SIMULATOR.parents[1] / 'tests' / 'scenarios' / 'K1.yaml'), '--station', self.base, '--key', key],
                              capture_output=True, text=True)
        self.assertEqual(done.returncode, 0, done.stdout + done.stderr)
        self.assertIn('expect is not a device message', done.stdout)   # the checks of the scenario are skipped, not played
        for _ in range(80):
            events = self.call('GET', ROOT + '/snapshot')[1]['events']
            if events: break
            time.sleep(.1)
        self.assertEqual((events[0]['personId'], events[0]['outcome'], events[0]['ppeOutcome']), (7, 'Allow', 'Ok'))
        print('Verified simulator play: the device steps of K1 reach the station and decide Allow with PPE Ok.')

    def test_roi_notation(self):
        sys.path.insert(0, str(SIMULATOR.parent)); import simulator
        self.assertEqual(simulator.parse_roi('L1=t-1:helmet,vest,-boots'), ('L1', {'track': 't-1', 'items': {'helmet': True, 'vest': True, 'boots': False}}))
        self.assertEqual(simulator.parse_roi('L2='), ('L2', None))


# The harness class carries the whole integration suite; this module only runs its own tests.
for _name in [n for n in dir(harness.ParkingIntegration) if n.startswith('test_')]:
    setattr(SimulatorTests, _name, None)

if __name__ == '__main__':
    unittest.main(verbosity=2)
