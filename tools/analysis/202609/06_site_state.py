"""Step 6 (§1, §3): what the site's own files say — config snapshots and ratio CSVs.

Golden Run baselines in every _config_*.json of the September days, the capture length, the
absence of the classifier section (site build older than b3d00fe), and the per-ratio
sigma-score distribution by overall state in the ratio CSVs.

    python3 06_site_state.py
"""
import collections
import csv
import glob
import json
import os

from common import DATA_ROOT, SEPT_DAYS

print('=== config snapshots')
for p in sorted(glob.glob(os.path.join(DATA_ROOT, '202609', '[0-9][0-9]', '_config_*.json'))):
    if not p.replace(os.sep, '/').split('DualOES/')[-1].startswith(SEPT_DAYS):
        continue
    d = json.load(open(p, encoding='utf-8'))
    lm = d.get('leakMonitor', {})
    print(f"\n{os.path.relpath(p, DATA_ROOT)}  captureSeconds={lm.get('goldenRunCaptureSeconds')}"
          f"  processClassifier section present={'processClassifier' in lm}"
          f"  wavelengthCorrections={len(lm.get('wavelengthCorrections') or [])}")
    for g in lm.get('goldenRuns', []):
        if g.get('name') != lm.get('activeGoldenRun'):
            continue
        floors = g.get('plasmaFloors') or g.get('plasmaPresentFloor')
        floor = floors[0]['floor'] if isinstance(floors, list) and floors else floors
        src = (g.get('source') or {}).get('kind')
        print(f"  active {g['name']!r}  source={src}  plasma floor={floor:.1f}")
        for b in g.get('baselines', []):
            print(f"    {b['key']:14s} {b['mode']:17s} mean {b['mean']:<10.5g} sigma {b['sigma']:<9.4g}"
                  f" mean/sigma {b['mean'] / b['sigma']:6.1f}  frames {b.get('sampleCount')}")

print('\n=== ratio CSVs: score columns by overall state (p5 / median / p95)')
for p in sorted(glob.glob(os.path.join(DATA_ROOT, '202609', '[0-9][0-9]', 'P_Ratio_*.csv'))):
    rel = os.path.relpath(p, DATA_ROOT).replace(os.sep, '/')
    if not rel.startswith(SEPT_DAYS):
        continue
    with open(p, encoding='utf-8-sig', errors='replace') as fh:
        rd = csv.reader(fh)
        hdr = next(rd)
        si = hdr.index('OverallState')
        cols = [i for i, h in enumerate(hdr) if h.endswith(('_pctBaseline', '_sigmaScore'))]
        agg = collections.defaultdict(lambda: collections.defaultdict(list))
        first = last = None
        for row in rd:
            if len(row) < len(hdr) or not row[0][:2].isdigit():
                continue
            first = first or row[0]
            last = row[0]
            for i in cols:
                if row[i]:
                    try:
                        agg[row[si]][i].append(float(row[i]))
                    except ValueError:
                        pass
    print(f'\n{rel}  {first} .. {last}')
    for state in ('Normal', 'Warning', 'Alarm'):
        if state not in agg:
            continue
        print(f'  {state}')
        for i in cols:
            v = sorted(agg[state][i])
            if not v:
                print(f'    {hdr[i][:48]:48s} (blank)')
                continue
            q = lambda f: v[min(len(v) - 1, int(f * len(v)))]
            print(f'    {hdr[i][:48]:48s} n={len(v):6d}  {q(.05):8.1f} / {q(.5):8.1f} / {q(.95):8.1f}')
