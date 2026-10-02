#!/usr/bin/env python3
"""The N2 337 band head measured so that the detector's fixed pattern cannot pose as it.

At this day's light level the engine's PeakHeight for N2 337 (max over 336.7-338.1 nm of
intensity minus the side-window line) lands on a pixel at 337.1 nm that reads ~45 counts
above its neighbours with the plasma OFF -- a fixed pattern, identical on 09-20. Same for
NO 237: the 236.9 nm pixel. So here the head is the band-averaged step across it:

    head = mean(337.5..338.9 nm) - mean(334.9..336.3 nm)

The pixel pattern is the same in every recording, so it cancels in any between-recording
comparison, and averaging five pixels a side shrinks it for the absolute level.
On 09-20 process C, index 0, the same quantity is ~411 counts.
"""
import json
import os
import sys
from statistics import mean, pstdev

from common928 import CACHE, DATA_ROOT, DAY, gate_open, median_spectrum, read_recording, win_mean

HI, LO = (337.45, 338.95), (334.85, 336.35)
EXPECT = (0.77, 1.78)    # plan section 16.1: R_N2CO_C rise at 1.0e-2 mbar.L/s


def head(wl, y):
    return win_mean(wl, y, *HI) - win_mean(wl, y, *LO)


def process_of(r):
    f = r['fixed']
    return 'A' if f['Ar_750'] / f['O_777'] > 0.5 else 'B'


def main():
    ref_wl, ref_fr = read_recording(os.path.join(DATA_ROOT, '202609/20', 'P_OES1_0920162537.csv'))
    ref = median_spectrum(gate_open(ref_fr), 10, 30)
    print(f'09-20 process C index 0 (16:25): head {head(ref_wl, ref):.1f}\n')
    cache = json.load(open(CACHE))
    recs = sorted([r for r in cache.values() if r['fixed'] and not r['test_mode']
                   and r['fixed_n'] >= 20], key=lambda r: r['stamp'])
    for proc in ('B', 'A'):
        g = [r for r in recs if process_of(r) == proc]
        rows = []
        for r in g:
            wl, fr = read_recording(os.path.join(DATA_ROOT, DAY, f"P_OES1_{r['stamp']}.csv"))
            h = head(wl, median_spectrum(gate_open(fr), 10, 30))
            rows.append((r['stamp'], r['valve'], h, h / r['fixed']['CO_330']))
        print(f'process {proc}')
        for st, dv, h, hc in rows:
            print(f"  {st}  DV={str(dv if dv is not None else '-'):>4}  head {h:6.1f}  head/CO330 {hc:.4f}")
        # DV 0 group: labelled DV 0 plus the unlabelled steps of the same process before the test
        base = [h for st, dv, h, _ in rows if dv in (0, None)]
        b0, s0 = mean(base), pstdev(base)
        print(f'  DV0 (n={len(base)}, incl. unlabelled pre-test steps): {b0:.1f} +/- {s0:.1f}')
        for dv in sorted({dv for _, dv, *_ in rows if dv}):
            hs = [h for _, d, h, _ in rows if d == dv]
            print(f'  DV{dv:<4} n={len(hs)}  {mean(hs):6.1f}  ({100 * (mean(hs) / b0 - 1):+5.1f} %)'
                  f'   expected at 1e-2 if delivered: +{EXPECT[0] * b0:.0f}..+{EXPECT[1] * b0:.0f} counts')
        print()


if __name__ == '__main__':
    sys.exit(main())
