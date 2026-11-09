"""Demo of the station overview boards (CR-025) when no camera is installed yet.

Sets up a station like the drawings (4 car lanes, 2 motorbike lanes, the Cửa lò entry and exit), then plays the devices
without stopping: plate cameras, motorbike terminals (plate + rider's face), the Cửa lò face terminal and the PPE AI box.
Messages go through the station's receiver exactly like a real device's, so every decision, barrier command and passage
is made by the station itself (evidence → decision → command → passage); nothing is written to the database directly.

Start the station with a receiver key (at least 32 characters), then:

    STATION_PASSWORD=... python scripts/demo_station.py --station http://localhost:5085 --key RECEIVER_KEY
    python scripts/demo_station.py ... --setup-only        # lanes, devices, workers and vehicles only
    python scripts/demo_station.py ... --interval 1 --warmup 40

Use a demo or test station: the setup changes lanes entry-1 / exit-1 and adds simulated lanes, workers and vehicles.
Every lane stays in simulation (virtual barrier). Some passes are left for the guard on purpose: missing PPE,
an unknown plate, a rider who is not the motorbike's owner.
"""
import argparse
import http.cookiejar
import json
import os
import random
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import simulator  # noqa: E402  (same device messages as the simulator and the scenario runner)

ROOT = '/api/v1/parking'
SIM = {'autoOpen': True, 'shadowMode': False, 'simulation': True, 'plateThreshold': .9, 'faceThreshold': .8}
LANES = [
    {'id': 'entry-1', 'name': 'Ô tô vào 1', 'kind': 'Car', 'direction': 'Entry', 'subject': 'Vehicle', 'policy': 'PlateOnly'},
    {'id': 'entry-2', 'name': 'Ô tô vào 2', 'kind': 'Car', 'direction': 'Entry', 'subject': 'Vehicle', 'policy': 'PlateOnly'},
    {'id': 'exit-1', 'name': 'Ô tô ra 1', 'kind': 'Car', 'direction': 'Exit', 'subject': 'Vehicle', 'policy': 'PlateOnly'},
    {'id': 'exit-2', 'name': 'Ô tô ra 2', 'kind': 'Car', 'direction': 'Exit', 'subject': 'Vehicle', 'policy': 'PlateOnly'},
    {'id': 'moto-in', 'name': 'Xe máy vào', 'kind': 'Motorbike', 'direction': 'Entry', 'subject': 'Vehicle', 'policy': 'Both'},
    {'id': 'moto-out', 'name': 'Xe máy ra', 'kind': 'Motorbike', 'direction': 'Exit', 'subject': 'Vehicle', 'policy': 'Both'},
    {'id': 'cualo-in', 'name': 'Cửa lò vào', 'kind': 'Person', 'direction': 'Entry', 'subject': 'Person', 'policy': 'FaceOnly',
     'ppeRules': 'helmet=required,vest=required,boots=required', 'ppeMissingMode': 'Review'},
    {'id': 'cualo-out', 'name': 'Cửa lò ra', 'kind': 'Person', 'direction': 'Exit', 'subject': 'Person', 'policy': 'FaceOnly'},
]
DEVICE = {lane['id']: 'DEMO-' + lane['id'].upper() for lane in LANES}
PPE_BOX, PPE_ROI = 'DEMO-AIBOX-1', 'L1'
FAMILY = ['Nguyễn', 'Trần', 'Lê', 'Phạm', 'Hoàng', 'Vũ', 'Đặng', 'Bùi', 'Đỗ', 'Ngô']
MIDDLE = ['Văn', 'Đức', 'Minh', 'Quang', 'Thị', 'Hữu', 'Thanh', 'Công']
GIVEN = ['An', 'Bình', 'Cường', 'Dũng', 'Hải', 'Hùng', 'Khánh', 'Long', 'Nam', 'Phong', 'Quân', 'Sơn', 'Thắng', 'Tuấn', 'Việt', 'Hà']
WORKERS = list(range(101, 125))
CARS = ['14A' + str(10000 + 731 * i)[-5:] for i in range(1, 11)]
MOTOS = [('14' + 'BCDEFGHK'[i] + '1' + str(20000 + 417 * i)[-5:], WORKERS[i]) for i in range(8)]
UNKNOWN = ['29A99881', '30K55120', '15C33007']


class Station:
    def __init__(self, url, user, password, key):
        self.url, self.key = url.rstrip('/'), key
        self.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        status, body = self.call('POST', '/api/auth/login', {'username': user, 'password': password})
        if status != 200: sys.exit(f'Login failed ({status}): {body}')

    def call(self, method, path, body=None):
        headers = {}
        if method != 'GET':   # antiforgery tokens are bound to the signed-in user: fetch one per call
            headers['X-CSRF-Token'] = self.call('GET', '/api/auth/csrf')[1]['token']
        data = None
        if body is not None: data = json.dumps(body).encode(); headers['Content-Type'] = 'application/json'
        try: response = self.client.open(urllib.request.Request(self.url + path, data=data, method=method, headers=headers), timeout=15)
        except urllib.error.HTTPError as error: response = error
        text = response.read().decode()
        try: return response.status, json.loads(text)
        except ValueError: return response.status, text

    def must(self, answer, what):
        if answer[0] != 200: sys.exit(f'{what}: {answer[0]} {answer[1]}')

    def send(self, adapter, body):
        status = simulator.send(self.url, self.key, adapter, body)
        if status >= 300: sys.exit(f'The receiver refused a {adapter} message ({status}). Is the station started with Receiver__Key set to --key?')

    def snapshot(self):
        return self.call('GET', ROOT + '/snapshot')[1]


def setup(station):
    for lane in LANES:
        station.must(station.call('PUT', ROOT + '/lanes', {**SIM, **lane}), 'lane ' + lane['id'])
        station.must(station.call('PUT', ROOT + '/cameras', {'id': 'cam-' + lane['id'], 'name': 'Thiết bị ' + lane['name'], 'laneId': lane['id'],
            'externalDevice': DEVICE[lane['id']], 'trustedConfidence': .99, 'record24x7': False}), 'device ' + lane['id'])
    station.must(station.call('PUT', ROOT + '/cameras', {'id': 'cam-cualo-ppe', 'name': 'AI box bảo hộ cửa lò', 'laneId': 'cualo-in',
        'externalDevice': PPE_BOX, 'externalRoi': PPE_ROI, 'trustedConfidence': .99, 'record24x7': False}), 'PPE AI box')
    rng = random.Random(7)
    for person in WORKERS:
        name = f'{rng.choice(FAMILY)} {rng.choice(MIDDLE)} {rng.choice(GIVEN)}'
        station.must(station.call('PUT', ROOT + '/people', {'personId': person, 'name': name, 'code': f'CN-{person}'}), f'worker {person}')
    known = {v['normalizedPlate'] for v in station.snapshot()['vehicles']}
    for i, plate in enumerate(CARS):
        if plate not in known: station.must(station.call('POST', ROOT + '/vehicles', {'plate': plate, 'owner': f'Xe công ty {i + 1}', 'kind': 'Car'}), 'car ' + plate)
    for plate, owner in MOTOS:
        if plate not in known: station.must(station.call('POST', ROOT + '/vehicles', {'plate': plate, 'owner': f'CN-{owner}', 'kind': 'Motorcycle', 'personId': owner}), 'motorbike ' + plate)
    print(f'Set up {len(LANES)} lanes, {len(WORKERS)} workers, {len(CARS)} cars, {len(MOTOS)} motorbikes.')


class Player:
    """Keeps who is inside, so exits follow entries the way a real day goes. It also respects the station's own rules:
    one barrier command per lane every few seconds (lane cooldown), and the same plate or face on a lane is merged into one pass."""
    GAP = 10   # seconds; above the default cooldown (8 s) and merge window

    def __init__(self, station, rng):
        self.station, self.rng = station, rng
        self.mine, self.cars, self.motos = set(), set(), set()
        self.used = {}   # lane or subject → last time it was played

    def free(self, keys):
        return [k for k in keys if time.time() - self.used.get(k, 0) > self.GAP]

    def play(self, lane, subject, message, match):
        """Sends one device message, waits for the station's new decision; if it opened the (virtual) barrier, the loop reports the passage."""
        self.used[lane] = self.used[subject] = time.time()
        seen = {e['id'] for e in self.station.snapshot()['events']}
        message()
        for _ in range(40):
            time.sleep(.2)
            snapshot = self.station.snapshot()
            event = next((e for e in snapshot['events'] if e['id'] not in seen and e['laneId'] == lane and match(e)), None)
            if event is None: continue
            if event['outcome'] != 'Allow': return '→ ' + event['reason'], False
            command = next((c for c in snapshot['commands'] if c['eventId'] == event['id']), None)
            if command:
                self.station.call('POST', f"{ROOT}/commands/{command['id']}/simulate-passage", {})
                return '✓', True
        return '→ no decision', False

    def mine_in(self):
        people = self.free([w for w in WORKERS if w not in self.mine])
        if not people or not self.free(['cualo-in']): return None
        person = self.rng.choice(people)
        items = {'helmet': True, 'vest': True, 'boots': True}
        if self.rng.random() < .15: items[self.rng.choice(list(items))] = False   # the guard has to look at this one
        def send():
            self.station.send('ppe', simulator.ppe_message(PPE_BOX, {PPE_ROI: [{'track': f't-{time.time_ns()}', 'items': items}]}))
            self.station.send('generic', simulator.face_message(DEVICE['cualo-in'], person, round(self.rng.uniform(.9, .99), 2)))
        note, passed = self.play('cualo-in', person, send, lambda e: e['personId'] == person)
        if passed: self.mine.add(person)
        return f'cualo-in   #{person} {note}'

    def mine_out(self):
        people = self.free(sorted(self.mine))
        if not people or not self.free(['cualo-out']): return None
        person = self.rng.choice(people)
        note, passed = self.play('cualo-out', person, lambda: self.station.send('generic', simulator.face_message(DEVICE['cualo-out'], person, .97)), lambda e: e['personId'] == person)
        if passed: self.mine.discard(person)
        return f'cualo-out  #{person} {note}'

    def car(self, entering):
        lanes = self.free(['entry-1', 'entry-2'] if entering else ['exit-1', 'exit-2'])
        plates = self.free(([p for p in CARS if p not in self.cars] + (UNKNOWN if self.rng.random() < .12 else [])) if entering else sorted(self.cars))
        if not lanes or not plates: return None
        lane = self.rng.choice(lanes)
        plate = self.rng.choice([p for p in plates if p in UNKNOWN] or plates)
        note, passed = self.play(lane, plate, lambda: self.station.send('generic', simulator.plate_message(DEVICE[lane], plate, round(self.rng.uniform(.92, .99), 2))), lambda e: e['plate'] == plate)
        if passed: (self.cars.add if entering else self.cars.discard)(plate)
        return f'{lane:<10} {plate} {note}'

    def moto(self, entering):
        lane = 'moto-in' if entering else 'moto-out'
        bikes = [m for m in MOTOS if (m[0] in self.motos) != entering and self.free([m[0]])]
        if not bikes or not self.free([lane]): return None
        plate, owner = self.rng.choice(bikes)
        rider = owner if self.rng.random() > .15 else self.rng.choice([w for w in WORKERS if w != owner])   # someone else on the bike
        note, passed = self.play(lane, plate, lambda: self.station.send('generic', simulator.plate_message(DEVICE[lane], plate, .97, rider)), lambda e: e['plate'] == plate)
        if passed: (self.motos.add if entering else self.motos.discard)(plate)
        return f'{lane:<10} {plate} + #{rider} {note}'

    def step(self):
        """Plays one pass on a lane that is free; None when every lane is still in its cooldown."""
        actions = [(self.mine_in, 30), (self.mine_out, 18), (lambda: self.car(True), 18), (lambda: self.car(False), 12),
                   (lambda: self.moto(True), 12), (lambda: self.moto(False), 10)]
        while actions:
            action = self.rng.choices(actions, [w for _, w in actions])[0]
            if (line := action[0]()) is not None: return line
            actions.remove(action)
        return None


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--station', default='http://localhost:5085')
    parser.add_argument('--user', default='admin')
    parser.add_argument('--password', default=os.environ.get('STATION_PASSWORD'), help='or STATION_PASSWORD')
    parser.add_argument('--key', default=os.environ.get('RECEIVER_KEY'), help='the station receiver key (Receiver__Key), or RECEIVER_KEY')
    parser.add_argument('--interval', type=float, default=3, help='seconds between two passes')
    parser.add_argument('--warmup', type=int, default=25, help='passes played at once before the steady pace')
    parser.add_argument('--setup-only', action='store_true')
    parser.add_argument('--seed', type=int)
    args = parser.parse_args()
    if not args.password or not args.key: parser.error('--password (or STATION_PASSWORD) and --key (or RECEIVER_KEY) are required')
    station = Station(args.station, args.user, args.password, args.key)
    setup(station)
    if args.setup_only: return
    player = Player(station, random.Random(args.seed))
    print('Playing devices; Ctrl+C to stop.')
    played = 0
    while True:
        line = player.step()
        if line is None: time.sleep(1); continue   # every lane is in its cooldown
        print(time.strftime('%H:%M:%S'), line, flush=True)
        played += 1
        time.sleep(.2 if played < args.warmup else args.interval)


if __name__ == '__main__':
    try: main()
    except KeyboardInterrupt: pass
