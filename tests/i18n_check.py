"""GC-39: every UI text passed to t('...') and every column key used with cols([...]) / tk() must have a Vietnamese entry.

    python tests/i18n_check.py            # exit 1 and list what is missing
    python tests/i18n_check.py --list     # print the missing keys as JSON (to start a translation)
"""
import json, re, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / 'ui' / 'src'
FILES = ['pages/Parking/Parking.jsx', 'pages/Parking/DeviceInputs.jsx', 'components/Layout/MainLayout.jsx', 'contexts/AuthContext.jsx']
# Values shown through t() at runtime (codes from the API), which can't be found by reading the source.
RUNTIME_CODES = ['Allow', 'Deny', 'ManualReview', 'Open', 'Acknowledged', 'Resolved', 'FalseAlarm', 'Critical', 'High', 'Medium', 'Low',
    'Entry', 'Exit', 'Vehicle', 'Person', 'Car', 'Motorcycle', 'Registered', 'Guest', 'Delivery', 'Visit', 'WalkIn',
    'PlateOnly', 'FaceOnly', 'Either', 'Both', 'Mine', 'VehicleGate', 'ParkingLot', 'Other',
    'Admin', 'Guard', 'Auditor', 'Viewer', 'SafetyOfficer', 'Manager', 'Technician', 'Ok', 'Warn', 'Review',
    'Simulation', 'Camera', 'CameraBurst', 'ThirdPartyCamera', 'Pending', 'Simulated', 'Unknown', 'Reconciled',
    'POLICY_MATCH', 'ACTIVE_VISITOR', 'GUARD_APPROVED', 'GUARD_REJECTED', 'BLOCKED_VEHICLE', 'INACTIVE_VEHICLE', 'INVALID_SCORES',
    'PERSON_VEHICLE_CONFLICT', 'INSUFFICIENT_IDENTITY_EVIDENCE', 'GUARD_APPROVAL_REQUIRED', 'REGISTRATION_EXPIRED', 'CATEGORY_REVIEW',
    'EXIT_WITHOUT_ENTRY', 'EXIT_PERSON_MISMATCH', 'DUPLICATE_ENTRY', 'PPE_MISSING', 'PPE_UNCONFIRMED', 'PPE_WARNING', 'PPE_ROI_NOT_SINGLE',
    'PPE_NO_DATA', 'PPE_OK', 'AUTO_OPEN_HELD', 'BARRIER_OUTCOME_UNKNOWN', 'DEVICE_OFFLINE', 'TAILGATING', 'SAFETY_FAULT', 'UNKNOWN_CAMERA', 'UNKNOWN_PERSON',
    'CAMERA_SILENT', 'CAMERA_NOT_RECORDING', 'OVERSTAY', 'UPLOAD_BACKLOG', 'VIDEO_DISK_FULL', 'CLOCK_DRIFT']

def keys():
    found = set(RUNTIME_CODES)
    for rel in FILES:
        src = (ROOT / rel).read_text(encoding='utf-8')
        found |= {m.replace("\\'", "'") for m in re.findall(r"\bt\('((?:[^'\\]|\\.)*)'\)", src)}
        for block in re.findall(r"cols\(\[([^\]]*)\]\)", src):
            found |= {'col.' + k for k in re.findall(r"'(\w+)'", block)}
        # field('name', 'Label', …) labels are translated inside field().
        found |= set(re.findall(r"field\('\w+', '((?:[^'\\]|\\.)*)'", src))
        # opts([...]) values are translated as labels.
        for block in re.findall(r"opts\(\[([^\]]*)\]\)", src):
            found |= set(re.findall(r"'([^']+)'", block))
        # timingFields labels, passed to field() through a variable.
        found |= set(re.findall(r"\['\w+', '([^']+)', \d+, \d+\]", src))
    found |= {'Required', 'Yes', 'No', 'Language'}
    return found

def main():
    vi = json.loads((ROOT / 'locales' / 'vi.json').read_text(encoding='utf-8'))
    missing = sorted(k for k in keys() if k not in vi)
    if '--list' in sys.argv:
        print(json.dumps({k: '' for k in missing}, ensure_ascii=False, indent=1)); return 0
    unused = sorted(k for k in vi if k not in keys() and not k.startswith('srv.'))
    print(f'{len(vi)} Vietnamese entries, {len(missing)} missing, {len(unused)} not used by the source')
    for k in missing: print('  missing:', k)
    return 1 if missing else 0

if __name__ == '__main__':
    sys.exit(main())
