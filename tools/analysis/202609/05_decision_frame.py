"""Step 5 (§4.5): does the classifier decide correctly at the frame the engine actually uses?

The engine classifies from ONE frame — gate-open frame DecideAfterFrames — not from a median.
That setting is a frame count, so the time it represents moves with the frame rate. This
replays the decision list (C' first, then the August C / A / B rules) on single frames and
reports errors by frame number, split by frame rate, with the C' rule's margins.

    python3 05_decision_frame.py
"""
import json
import os

from common import (CACHE, CP_DEN, CP_NUM, CP_THRESHOLD, DATA_ROOT, gate_open, peak_height,
                    read_recording, sept_step_labels)

cache = json.load(open(CACHE))
labels = sept_step_labels(cache)
G = lambda w: (w, 1.06, 1.30, 1.10)


def cp_ratio(wl, y):
    d = peak_height(wl, y, *CP_DEN)
    return peak_height(wl, y, *CP_NUM) / d if d > 0 else float('inf')


def decide(wl, y, with_cp):
    if with_cp and cp_ratio(wl, y) > CP_THRESHOLD:
        return "C'"
    o = peak_height(wl, y, *G(777.60))
    if o <= 0:
        return 'Unknown'
    if peak_height(wl, y, *G(750.70)) / o > 0.5:
        return 'C'
    if peak_height(wl, y, *G(656.50)) / o < 0.07:
        return 'A'
    return 'B'


def fps_group(k):
    return '1.66 fps' if k.startswith('202609/10') and k.split('_')[-1][4:8] >= '1636' else '1.24 fps'


steps = {}
for k in labels:
    wl, frames = read_recording(os.path.join(DATA_ROOT, k))
    steps[k] = (wl, [f for f in gate_open(frames) if f[0] <= 12])

print('existing C / A / B rules alone (C\' counted as C):')
for n in (1, 2, 3, 5, 8):
    err = {'1.24 fps': [0, 0], '1.66 fps': [0, 0]}
    for k, (wl, frames) in steps.items():
        if len(frames) < n:
            continue
        want = 'C' if labels[k] == "C'" else labels[k]
        e = err[fps_group(k)]
        e[1] += 1
        e[0] += decide(wl, frames[n - 1][1], False) != want
    print(f"  frame {n:2d}: errors 1.24 fps {err['1.24 fps'][0]}/{err['1.24 fps'][1]}"
          f"   1.66 fps {err['1.66 fps'][0]}/{err['1.66 fps'][1]}")

print(f"\nC' rule first (threshold {CP_THRESHOLD}):")
margin_c, margin_p = 0.0, float('inf')
for n in (3, 4, 5, 6, 7, 8, 10):
    parts = []
    for grp in ('1.24 fps', '1.66 fps'):
        err = tot = 0
        times = []
        for k, (wl, frames) in steps.items():
            if fps_group(k) != grp or len(frames) < n:
                continue
            t, y = frames[n - 1]
            times.append(t)
            tot += 1
            err += decide(wl, y, True) != labels[k]
            if n >= 5:
                if labels[k] == 'C':
                    margin_c = max(margin_c, cp_ratio(wl, y))
                elif labels[k] == "C'":
                    margin_p = min(margin_p, cp_ratio(wl, y))
        times.sort()
        parts.append(f'{grp} ~{times[len(times) // 2]:.1f} s errors {err}/{tot}')
    print(f'  frame {n:2d}: ' + '   '.join(parts))
print(f"\nframes 5-10, single frame: normal C max {margin_c:.2f}, C' min {margin_p:.2f}")
