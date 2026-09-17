"""Step 3 (§4.3): which line ratios separate C' from normal C.

Prints the overlap of the three ratios already in use, then a full-spectrum search over every
pair of peaks in the September C steps' median spectra, ranked by the gap between the two
groups divided by normal C's own scatter (positive = fully separated).

    python3 03_search.py
"""
import json
import os
import statistics as st

from common import CACHE, DATA_ROOT, cp_labels, gate_open, median_spectrum, peak_height, read_recording

cache = json.load(open(CACHE))
labels = cp_labels(cache)
steps = sorted(labels)
n_c = sum(l == 'C' for l in labels.values())
n_p = len(labels) - n_c
print(f"labels: C={n_c}  C'={n_p}")

print('\nratios already in use (C vs C\'):')
for name, get in [('Ar750/O777', lambda v: v['ar_o']), ('Ha656/O777', lambda v: v['ha_o']),
                  ('CO608/Ar811', lambda v: v['fixed']['CO608_Ar811'])]:
    parts = []
    for lab in ('C', "C'"):
        xs = [get(cache[k]) for k in steps if labels[k] == lab]
        parts.append(f'{lab} {min(xs):.3f}-{max(xs):.3f}')
    print(f'  {name:12s} ' + ' | '.join(parts))

spec, axes = {}, set()
for k in steps:
    wl, frames = read_recording(os.path.join(DATA_ROOT, k))
    axes.add((len(wl), wl[0], wl[-1]))
    spec[k] = median_spectrum(gate_open(frames))
assert len(axes) == 1, f'recordings are on different axes: {axes}'

mean = [st.mean(spec[k][i] for k in steps) for i in range(len(wl))]
peaks = [i for i in range(6, len(wl) - 6)
         if mean[i] == max(mean[i - 3:i + 4]) and mean[i] - min(mean[i - 6:i + 7]) > 300]
print(f'\n{len(peaks)} candidate peaks')

geo = lambda i: (wl[i], 0.70, 1.30, 1.10)
vals = {k: [peak_height(wl, spec[k], *geo(i)) for i in peaks] for k in steps}
ranked = []
for a in range(len(peaks)):
    for b in range(len(peaks)):
        if a == b or any(vals[k][b] <= 50 for k in steps):
            continue
        r = {k: vals[k][a] / vals[k][b] for k in steps}
        c = [r[k] for k in steps if labels[k] == 'C']
        p = [r[k] for k in steps if labels[k] == "C'"]
        gap = max(min(p) - max(c), min(c) - max(p))
        ranked.append((gap / (st.pstdev(c) or 1e-12), wl[peaks[a]], wl[peaks[b]],
                       min(c), max(c), min(p), max(p)))
ranked.sort(reverse=True)
print('best separators (margin in sigma of normal C):')
for m, wn, wd, c0, c1, p0, p1 in ranked[:10]:
    print(f"  {m:5.1f} sigma  {wn:7.2f} / {wd:7.2f}   C [{c0:.4f}..{c1:.4f}]   C' [{p0:.4f}..{p1:.4f}]")
