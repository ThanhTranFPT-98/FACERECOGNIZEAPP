"""Evaluate a consented local JSON manifest against the configured local OCR baseline.

Manifest entries: {image, plate, vehicleType, condition}. Relative paths are relative to manifest.
Reports exact-match rate and p50/p95 latency by cohort; never invents acceptance thresholds.
"""
import argparse
import collections
import json
import statistics
import time
from pathlib import Path
from plate_service import recognize_bytes, normalize

def main():
    parser = argparse.ArgumentParser(); parser.add_argument('manifest'); args = parser.parse_args()
    path = Path(args.manifest).resolve()
    samples = json.loads(path.read_text(encoding='utf-8'))
    if not samples: raise ValueError('Manifest is empty.')
    cohorts = collections.defaultdict(list)
    for sample in samples:
        started = time.perf_counter()
        result = recognize_bytes((path.parent / sample['image']).read_bytes())
        elapsed = (time.perf_counter() - started) * 1000
        match = normalize(result['plate']) == normalize(sample['plate'])
        cohorts[(sample['vehicleType'], sample['condition'])].append((match, elapsed))
    report = []
    for (kind, condition), values in cohorts.items():
        latencies = sorted(v[1] for v in values)
        report.append({'vehicleType': kind, 'condition': condition, 'samples': len(values),
            'exactMatchRate': sum(v[0] for v in values) / len(values), 'p50Ms': statistics.median(latencies),
            'p95Ms': latencies[min(len(latencies)-1, int(.95*len(latencies)))]})
    print(json.dumps(report, indent=2))

if __name__ == '__main__': main()
