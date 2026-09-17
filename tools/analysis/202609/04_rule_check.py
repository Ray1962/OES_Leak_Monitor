"""Step 4 (§4.4): can a candidate C' rule go first in the classifier's decision list?

A rule placed ahead of the C rule must not fire on any A or B step, so each candidate is
evaluated on A, B, C and C' (September, fixed sampling point) with a leave-one-day-out
threshold test. The proposed rule (Ha/O616) is then checked against every A and B step of
August and September at the engine's single decision frame (gate-open frame 8).

    python3 04_rule_check.py
"""
import json
import os

from common import (AUG_DAYS, CACHE, CP_DEN, CP_NUM, CP_THRESHOLD, DATA_ROOT, SEPT_DAYS,
                    day_of, gate_open, median_spectrum, peak_height, read_recording,
                    sept_step_labels)

cache = json.load(open(CACHE))
labels = sept_step_labels(cache)
geo = lambda w: (w, 0.70, 1.30, 1.10)
CANDIDATES = [(656.48, 616.04), (411.70, 419.76), (265.95, 251.62),
              (656.48, 668.12), (656.48, 675.63), (607.33, 603.49)]

spectra, axes = {}, set()
for k in labels:
    wl, frames = read_recording(os.path.join(DATA_ROOT, k))
    axes.add((len(wl), wl[0], wl[-1]))
    spectra[k] = median_spectrum(gate_open(frames))
assert len(axes) == 1, f'recordings are on different axes: {axes}'

for num, den in CANDIDATES:
    by = {}
    for k, lab in labels.items():
        d = peak_height(wl, spectra[k], *geo(den))
        r = peak_height(wl, spectra[k], *geo(num)) / d if d > 50 else None
        by.setdefault(lab, []).append((r, day_of(k)))
    print(f'\n{num} / {den}')
    for lab in ('A', 'B', 'C', "C'"):
        xs = [r for r, _ in by.get(lab, []) if r is not None]
        weak = sum(r is None for r, _ in by.get(lab, []))
        rng = f'[{min(xs):.4f} .. {max(xs):.4f}]' if xs else '-'
        print(f'  {lab:2s} {rng}  n={len(xs)}' + (f'  (+{weak} with denominator <= 50)' if weak else ''))
    for hold in SEPT_DAYS:
        tr_c = [r for r, d in by['C'] if d != hold and r is not None]
        tr_p = [r for r, d in by["C'"] if d != hold and r is not None]
        te_c = [r for r, d in by['C'] if d == hold and r is not None]
        te_p = [r for r, d in by["C'"] if d == hold and r is not None]
        up = min(tr_p) > max(tr_c)
        thr = (max(tr_c) + min(tr_p)) / 2 if up else (min(tr_c) + max(tr_p)) / 2
        e_c = sum((x > thr) if up else (x < thr) for x in te_c)
        e_p = sum((x <= thr) if up else (x >= thr) for x in te_p)
        print(f"  hold out {hold}: threshold {'>' if up else '<'} {thr:.4f}"
              f"  errors C->C' {e_c}/{len(te_c)}  C'->C {e_p}/{len(te_p)}")

print(f'\nproposed rule Ha/O616 > {CP_THRESHOLD} on every August + September A and B step, frame 8:')
worst = {'A': 0.0, 'B': 0.0}
count = {'A': 0, 'B': 0}
fired = 0
for k, v in cache.items():
    if not v or v['class'] not in ('A', 'B') or not k.startswith(AUG_DAYS + SEPT_DAYS):
        continue
    wl, frames = read_recording(os.path.join(DATA_ROOT, k))
    op = gate_open(frames)
    if not op:
        continue
    y = op[min(7, len(op) - 1)][1]
    d = peak_height(wl, y, *CP_DEN)
    r = peak_height(wl, y, *CP_NUM) / d if d > 0 else float('inf')
    worst[v['class']] = max(worst[v['class']], r)
    count[v['class']] += 1
    fired += r > CP_THRESHOLD
print(f"  A: n={count['A']} max {worst['A']:.2f}   B: n={count['B']} max {worst['B']:.2f}   fired: {fired}")

print('\nexisting Ha656/O777 on B steps (why it cannot go first):')
for grp, days in (('August', AUG_DAYS), ('September', SEPT_DAYS)):
    xs = [v['ha_o'] for k, v in cache.items() if k.startswith(days) and v and v['class'] == 'B']
    print(f'  {grp}: n={len(xs)} range {min(xs):.3f}..{max(xs):.3f}  >= 0.145: {sum(x >= 0.145 for x in xs)}')
