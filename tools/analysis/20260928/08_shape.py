#!/usr/bin/env python3
"""Is process A's UV rise an added nitrogen spectrum, or the whole UV brightening?

Process A's dose order is strictly monotonic in time, so a dose table cannot separate the
two. The spectrum can: an air leak ADDS nitrogen emission at the band heads, so the relative
change at a head exceeds the relative change of the continuum on either side of it. A plasma
that simply gets brighter (or a spectral tilt from the optics) moves the head and its sides
by the same fraction.

    excess = dI/I at the head  -  mean(dI/I on the two sides)

compared between DV0 (first A step of the test, 19:50) and the mean of DV 200 + DV 250.
Same for B (DV0 pre-test steps against DV 200). A delivered leak at the plan's C
sensitivity would put +0.77..+1.78 at 337.
"""
import json
import os
import sys
from statistics import mean

from common928 import CACHE, DATA_ROOT, DAY, gate_open, median_spectrum, read_recording, win_mean

# (label, head window, blue side, red side) -- centres carry the +0.30 nm offset
FEATURES = [
    ('N2 SPS 315.9', (315.9, 316.6), (314.3, 315.2), (317.3, 318.2)),
    ('N2 SPS 337.1', (337.2, 337.9), (334.9, 335.9), (339.6, 340.6)),
    ('N2 SPS 357.7', (357.7, 358.4), (359.4, 360.4), (361.2, 362.2)),
    ('N2 SPS 380.5', (380.5, 381.2), (378.6, 379.5), (382.2, 383.1)),
    ('N2+ FNS 391.4', (391.4, 392.1), (389.8, 390.7), (393.2, 394.1)),
    ('CN 388.3', (388.3, 389.0), (386.2, 387.1), (390.0, 390.9)),
]


def process_of(r):
    f = r['fixed']
    return 'A' if f['Ar_750'] / f['O_777'] > 0.5 else 'B'


def avg_spectrum(stamps):
    ss = []
    for st in stamps:
        wl, fr = read_recording(os.path.join(DATA_ROOT, DAY, f'P_OES1_{st}.csv'))
        ss.append(median_spectrum(gate_open(fr), 10, 30))
    return wl, [mean(c) for c in zip(*ss)]


def main():
    cache = json.load(open(CACHE))
    recs = [r for r in cache.values() if r['fixed'] and not r['test_mode'] and r['fixed_n'] >= 20]
    sets = {
        'A': ([r['stamp'] for r in recs if process_of(r) == 'A' and r['valve'] == 0],
              [r['stamp'] for r in recs if process_of(r) == 'A' and (r['valve'] or 0) >= 200]),
        'B': ([r['stamp'] for r in recs if process_of(r) == 'B' and not r['valve']],
              [r['stamp'] for r in recs if process_of(r) == 'B' and r['valve'] == 200]),
    }
    for proc, (lo, hi) in sets.items():
        wl, s0 = avg_spectrum(sorted(lo))
        _, s1 = avg_spectrum(sorted(hi))
        print(f'process {proc}: DV0 {sorted(lo)}  vs  high {len(hi)} recordings')
        print(f"  {'feature':14} {'head':>8} {'blue':>8} {'red':>8} {'excess':>8}"
              f"   (337 only: expected excess if the head part rose +77..+178 %)")
        for name, h, b, r in FEATURES:
            rel = []
            for w in (h, b, r):
                a0, a1 = win_mean(wl, s0, *w), win_mean(wl, s1, *w)
                rel.append(a1 / a0 - 1)
            ex = rel[0] - (rel[1] + rel[2]) / 2
            extra = ''
            if '337' in name:
                h0 = win_mean(wl, s0, *h)
                side0 = (win_mean(wl, s0, *b) + win_mean(wl, s0, *r)) / 2
                frac = (h0 - side0) / h0      # the part of the head window above its sides
                extra = (f'   head part {100 * frac:.0f} % of window -> expected'
                         f' +{77 * frac:.0f}..+{178 * frac:.0f} %')
            print(f'  {name:14} {100 * rel[0]:+7.1f}% {100 * rel[1]:+7.1f}% {100 * rel[2]:+7.1f}%'
                  f' {100 * ex:+7.1f}%{extra}')
        print()


if __name__ == '__main__' and len(sys.argv) == 1:
    sys.exit(main())


def per_recording():
    """Excess at N2 337 for every A step against the first DV0 step, in time order."""
    cache = json.load(open(CACHE))
    A = sorted([r for r in cache.values() if r['fixed'] and not r['test_mode']
                and r['fixed_n'] >= 20 and process_of(r) == 'A'], key=lambda r: r['stamp'])
    _, h, b, rd = FEATURES[1]
    wl, s0 = avg_spectrum(['0928195053'])
    print('process A, N2 337 excess per recording vs 19:50 DV0')
    for r in A:
        _, s1 = avg_spectrum([r['stamp']])
        rel = [win_mean(wl, s1, *w) / win_mean(wl, s0, *w) - 1 for w in (h, b, rd)]
        print(f"  {r['stamp']}  DV={str(r['valve'] if r['valve'] is not None else '-'):>4}"
              f"  UV +{100 * (rel[1] + rel[2]) / 2:5.1f} %   excess {100 * (rel[0] - (rel[1] + rel[2]) / 2):+5.1f} %")


if __name__ == '__main__' and len(sys.argv) > 1 and sys.argv[1] == '--per-recording':
    per_recording()
