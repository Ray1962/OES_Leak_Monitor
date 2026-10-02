#!/usr/bin/env python3
"""Did anything in the spectrum respond to the valve? Per-pixel search, process B.

B is the process where this can be asked: its throughput held still all day (CO 330 CV
1.2 %) and its dose order is not monotonic in time (DV 100 at 19:22-19:28 and again at
19:56, DV 200 in between), so dose and clock are not the same variable. The three B steps
before the test (16:52, 16:58, 17:06) are taken as DV 0: the first two carry no notes but
precede the only labelled DV 0, with no valve change logged.

Per pixel: I = a + c * DV on the 10..30 s median spectra; ranked by |t(c)|. A delivered
air leak has to put the N2 second-positive band heads at the top, together.

Process A cannot be asked this way: its DV rises strictly with time while its NIR brightens
2.4x over the same half hour, so the within-dose drift is printed instead -- if the drift
inside one valve setting is as large as the drift between settings, the clock is doing it.
"""
import json
import math
import os
import sys
from statistics import mean

from common928 import CACHE, DATA_ROOT, DAY, gate_open, median_spectrum, read_recording

NITROGEN = [337.40, 358.00, 380.80, 375.80, 354.00, 316.20, 391.70, 428.10, 388.60]


def process_of(r):
    f = r['fixed']
    return 'A' if f['Ar_750'] / f['O_777'] > 0.5 else 'B'


def fit(x, y):
    n = len(x); mx = mean(x); my = mean(y)
    sxx = sum((a - mx) ** 2 for a in x)
    c = sum((a - mx) * (b - my) for a, b in zip(x, y)) / sxx
    res = [b - my - c * (a - mx) for a, b in zip(x, y)]
    s2 = sum(r * r for r in res) / (n - 2)
    return c, c / math.sqrt(s2 / sxx) if s2 > 0 else float('nan')


def main():
    cache = json.load(open(CACHE))
    recs = sorted([r for r in cache.values() if r['fixed'] and not r['test_mode']
                   and r['fixed_n'] >= 20], key=lambda r: r['stamp'])
    B = [r for r in recs if process_of(r) == 'B']
    dv = [r['valve'] if r['valve'] is not None else 0 for r in B]
    spectra = []
    for r in B:
        wl, fr = read_recording(os.path.join(DATA_ROOT, DAY, f"P_OES1_{r['stamp']}.csv"))
        spectra.append(median_spectrum(gate_open(fr), 10, 30))
    out = []
    for i in range(len(wl)):
        y = [s[i] for s in spectra]
        lv = mean(y)
        if lv < 15:
            continue
        c, t = fit(dv, y)
        out.append((abs(t), wl[i], lv, 200 * c / lv * 100, t))
    out.sort(reverse=True)
    print(f'process B, n={len(B)}, DV = {dv}')
    print(f'{len(out)} pixels above 15 counts; top 25 by |t|  (change at DV 200, % of level)')
    for _, w, lv, pct, t in out[:25]:
        near = min(NITROGEN, key=lambda c: abs(c - w))
        tag = '  <- N2/N2+/CN' if abs(near - w) < 0.6 else ''
        print(f'  {w:7.2f} nm  level {lv:7.1f}  {pct:+6.1f} %  t={t:+5.2f}{tag}')
    print('\nnitrogen band heads, their own rank:')
    rank = {round(w, 2): k for k, (_, w, *_) in enumerate(out)}
    for c in NITROGEN:
        cands = [(abs(w - c), w) for _, w, *_ in out if abs(w - c) < 0.6]
        if not cands:
            print(f'  {c:7.2f}  below 15 counts'); continue
        w = min(cands)[1]
        k = rank[round(w, 2)]
        _, _, lv, pct, t = out[k]
        print(f'  {c:7.2f} -> {w:7.2f} nm  level {lv:6.1f}  {pct:+6.1f} %  t={t:+5.2f}  rank {k + 1}/{len(out)}')

    A = [r for r in recs if process_of(r) == 'A' and r['valve'] is not None]
    print('\nprocess A, drift inside one valve setting vs between settings')
    for ln in ('Ar_750', 'O_777', 'CO_330', 'N2_337'):
        by = {}
        for r in A:
            by.setdefault(r['valve'], []).append(r['fixed'][ln])
        cells = ' '.join(f"DV{k}: {v[0]:7.1f}->{v[-1]:7.1f} ({100 * (v[-1] / v[0] - 1):+5.1f}%)"
                         for k, v in sorted(by.items()) if len(v) > 1)
        print(f'  {ln:7}  {cells}')


if __name__ == '__main__':
    sys.exit(main())
