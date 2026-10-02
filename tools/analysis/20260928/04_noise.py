#!/usr/bin/env python3
"""What the nitrogen lines are worth at this light level: per-frame scatter, SNR, and the
smallest rise a fixed-point comparison could have seen.

The app's SNR is signal / continuum noise from the side windows; here the per-frame scatter
inside the 10..30 s window stands in for it (it includes the continuum noise and anything
the plasma adds), and the between-recording scatter at one valve setting is what a
cross-batch comparison divides by.
"""
import json
import sys
from statistics import mean, median, pstdev

from common928 import CACHE

LINES = ['N2_337', 'N2_357', 'N2p_391', 'CN_388', 'NO_237', 'CO_330', 'Ha_656', 'O_777', 'Ar_750']


def process_of(r):
    f = r['fixed']
    return 'A' if f['Ar_750'] / f['O_777'] > 0.5 else 'B'


def main():
    cache = json.load(open(CACHE))
    recs = sorted([r for r in cache.values() if r['fixed'] and not r['test_mode']
                   and r['fixed_n'] >= 20], key=lambda r: r['stamp'])
    for proc in ('B', 'A'):
        g = [r for r in recs if process_of(r) == proc]
        print(f'\nprocess {proc}  ({len(g)} recordings)')
        print(f"{'line':8} {'level':>8} {'frame sd':>9} {'frame SNR':>9} {'33-frame':>9}"
              f" {'between-rec CV':>15}")
        for ln in LINES:
            lv, sd = [], []
            for r in g:
                w = [f[ln] for f in r['frames'] if 10 <= f['t'] <= 30]
                lv.append(median(w)); sd.append(pstdev(w))
            fixed = [r['fixed'][ln] for r in g]
            m, s = median(lv), median(sd)
            print(f'{ln:8} {m:8.1f} {s:9.2f} {m / s if s else 0:9.1f} {m / (s / 33 ** .5):9.1f}'
                  f" {pstdev(fixed) / abs(mean(fixed)) * 100:14.1f}%")
        # N2 337 / CO 330 at the fixed point, B only has usable replication
        r_ = [r['fixed']['N2_337'] / r['fixed']['CO_330'] for r in g]
        print(f'  N2 337/CO 330 fixed-point: mean {mean(r_):.5f}  CV {pstdev(r_) / mean(r_) * 100:.1f} %'
              f'  -> 3-sigma detectable rise {3 * pstdev(r_) / mean(r_) * 100:.1f} %')


if __name__ == '__main__':
    sys.exit(main())
