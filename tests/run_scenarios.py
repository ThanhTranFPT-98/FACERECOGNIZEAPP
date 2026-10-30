"""Plays the scenarios in tests/scenarios/*.yaml against the real API and prints ĐẠT / KHÔNG ĐẠT for each (station plan §6, §7).

A scenario is a timeline of device messages (what the simulator plays in place of Face-S, the AI box and the plate cameras)
followed by what the station must have done: the decision, whether the barrier was told to open, the alerts.
Needs the Debug build of the API (`dotnet build DSAVehicle`). No hardware, no external service.

    python tests/run_scenarios.py              # all scenarios
    python tests/run_scenarios.py K1 K6        # some of them
"""
import itertools
import secrets
import sys
import time
import urllib.parse
from pathlib import Path

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'scripts'))
from integration_parking import ParkingIntegration  # noqa: E402  (the harness: temp DB, API process, fake barrier bridge)
import simulator  # noqa: E402  (the same device messages the simulator sends to a running station)

DIRECTORY = Path(__file__).resolve().parent / 'scenarios'
ROOT = '/api/v1/parking'
SETTLE = 1.0   # seconds to wait before asserting that something did NOT happen (an opening, an extra event)


class Mismatch(AssertionError):
    pass


class Runner(ParkingIntegration):
    def runTest(self):   # unittest wants one; the scenarios go through play()
        pass

    # ---- setup ---------------------------------------------------------------------------------
    def prepare(self, setup):
        self.key = secrets.token_urlsafe(32)
        self.env['Receiver__Key'] = self.key
        self.process.terminate(); self.process.wait(timeout=10); self.start()   # the receiver key is read at startup
        self.login_as('admin')
        self.frames = itertools.count(1)   # real devices send a frame counter or timestamp; identical bodies count as one
        patch = setup.get('lane') or {}
        for lane in setup.get('lanes', []):
            self.must(self.call('PUT', ROOT + '/lanes', {'plateThreshold': .9, **lane, **patch}), 'save lane ' + lane['id'])
        for camera in setup.get('cameras', []):
            self.must(self.call('PUT', ROOT + '/cameras', {'name': camera['id'], **camera}), 'save camera ' + camera['id'])
        for person in setup.get('people', []):
            self.must(self.call('PUT', ROOT + '/people', person), 'save person %s' % person['personId'])
        for vehicle in setup.get('vehicles', []):
            self.must(self.call('POST', ROOT + '/vehicles', vehicle), 'save vehicle ' + vehicle['plate'])

    def must(self, answer, what):
        status, body = answer
        if status != 200:
            raise Mismatch(f'setup failed ({what}): {status} {body}')

    # ---- device messages -----------------------------------------------------------------------
    def send(self, adapter, body):
        status = self.post_raw(f'{self.base}{ROOT}/receiver/{adapter}?key={urllib.parse.quote(self.key)}', body)
        if status >= 300:
            raise Mismatch(f'the receiver refused a {adapter} message: {status}')

    def ppe(self, step):   # the AI box: per ROI the people it follows, each with a track and the items it sees
        self.send('ppe', simulator.ppe_message(step.get('box', 'AIBOX-1'), step['rois'], next(self.frames)))

    def face(self, step):   # Face-S: a PIN/person ID and a confidence
        for _ in range(step.get('repeat', 1)):
            self.send('generic', simulator.face_message(step['device'], step['person'], step.get('confidence', .97), 0 if step.get('identical') else next(self.frames)))

    def plate(self, step):   # a plate camera; with `person`, a terminal that also reads the rider's face
        self.send('generic', simulator.plate_message(step['device'], step['plate'], step.get('confidence', .99), step.get('person'), next(self.frames)))

    def walkin(self, step):   # the guard registers a walk-in vehicle at the gate
        self.must(self.call('POST', ROOT + '/visits/walk-in', {'plate': step['plate'], 'name': step.get('name', 'Walk-in')}), 'walk-in ' + step['plate'])

    def passage(self, step):   # the loop confirms the newest barrier command was passed: this is what opens a parking session
        commands = self.snapshot()['commands']
        if not commands: raise Mismatch('there is no barrier command to confirm a passage for')
        newest = max(commands, key=lambda c: c['createdUtc'])
        self.must(self.call('POST', f"{ROOT}/commands/{newest['id']}/simulate-passage", {}), 'confirm passage')

    # ---- what must have happened ---------------------------------------------------------------
    def snapshot(self):
        return self.call('GET', ROOT + '/snapshot')[1]

    def newest(self, events, where):
        keys = {'person': 'personId', 'plate': 'plate', 'lane': 'laneId'}
        found = [e for e in events if all(e.get(keys[k]) == where[k] for k in keys if k in where)]
        return max(found, key=lambda e: e['createdUtc']) if found else None

    def expect(self, wanted):
        if 'event' in wanted:
            where = wanted['event']
            fields = {k: v for k, v in where.items() if k not in ('person', 'plate', 'lane')}
            event = None
            for _ in range(80):   # the inbox worker and the auto-open queue run in the background
                event = self.newest(self.snapshot()['events'], where)
                if event and all(event.get(k) == v for k, v in fields.items()): break
                time.sleep(.1)
            if event is None: raise Mismatch(f'no event for {where}')
            for key, value in fields.items():
                if event.get(key) != value: raise Mismatch(f'event {key}: expected {value!r}, got {event.get(key)!r}')
        if 'opened' in wanted:
            for _ in range(50 if wanted['opened'] else 0):
                if any(c['eventId'] == event['id'] for c in self.snapshot()['commands']): break
                time.sleep(.1)
            if not wanted['opened']: time.sleep(SETTLE)
            opened = any(c['eventId'] == event['id'] for c in self.snapshot()['commands'])
            if opened != wanted['opened']: raise Mismatch(f'barrier told to open: expected {wanted["opened"]}, got {opened}')
        snapshot = self.snapshot()
        if 'events' in wanted and len(snapshot['events']) != wanted['events']:
            raise Mismatch(f'events: expected {wanted["events"]}, got {len(snapshot["events"])}')
        if 'commands' in wanted and len(snapshot['commands']) != wanted['commands']:
            raise Mismatch(f'open commands: expected {wanted["commands"]}, got {len(snapshot["commands"])}')
        for reason in wanted.get('alerts', []):
            if not any(a['reason'].startswith(reason) for a in snapshot['alerts']):
                raise Mismatch(f'no alert {reason}; there are {[a["reason"] for a in snapshot["alerts"]]}')

    # ---- one scenario --------------------------------------------------------------------------
    def play(self, scenario, bases):
        setup = {**bases.get(scenario['base'], {}), **{k: scenario[k] for k in ('lane', 'lanes', 'cameras', 'people', 'vehicles') if k in scenario}}
        self.prepare(setup)
        actions = {'ppe': self.ppe, 'face': self.face, 'plate': self.plate, 'walkin': self.walkin, 'passage': self.passage, 'expect': self.expect}
        for number, step in enumerate(scenario['steps'], 1):
            for name, argument in step.items():
                try:
                    if name == 'wait': time.sleep(argument)
                    else: actions[name](argument)
                except KeyError as unknown:
                    raise Mismatch(f'step {number}: unknown step or missing field {unknown}')
                except Mismatch as error:
                    raise Mismatch(f'step {number} ({name}): {error}')


def main(wanted):
    sys.stdout.reconfigure(encoding='utf-8')   # the Windows console and pipes default to cp1252, which has no Vietnamese
    bases = yaml.safe_load((DIRECTORY / '_base.yaml').read_text(encoding='utf-8'))
    scenarios = sorted((yaml.safe_load(f.read_text(encoding='utf-8')) for f in DIRECTORY.glob('[!_]*.yaml')), key=lambda s: (s['id'][0], int(s['id'][1:])))
    scenarios = [s for s in scenarios if not wanted or s['id'] in wanted]
    if not scenarios:
        print('No scenario matches.'); return 2
    runner = Runner()
    results = []
    for scenario in scenarios:
        if 'covered_by' in scenario:
            results.append((scenario, 'CÓ TEST KHÁC', scenario['covered_by'])); continue
        runner.setUp()
        try:
            runner.play(scenario, bases); results.append((scenario, 'ĐẠT', ''))
        except (Mismatch, RuntimeError, OSError) as error:
            results.append((scenario, 'KHÔNG ĐẠT', str(error)))
        finally:
            runner.tearDown()
        print(f'{results[-1][1]:<12} {scenario["id"]:<4} {scenario["title"]}' + (f'\n             → {results[-1][2]}' if results[-1][2] else ''), flush=True)
    for scenario, verdict, note in results:
        if verdict == 'CÓ TEST KHÁC': print(f'{verdict:<12} {scenario["id"]:<4} {scenario["title"]}\n             → {note}')
    passed = sum(1 for _, v, _ in results if v == 'ĐẠT'); failed = sum(1 for _, v, _ in results if v == 'KHÔNG ĐẠT')
    print(f'\n{passed} đạt, {failed} không đạt, {len(results) - passed - failed} có test khác.')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main(set(sys.argv[1:])))
