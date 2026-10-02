#!/usr/bin/env python3
"""Per-recording and per-dose table of every indicator, for both processes of the day.

Process is read off the spectrum: Ar750/O777 > 0.5 is the TEOS recipe the operator calls
A (LDR400, 1.8 Torr, ~63 s); below that is B (HDR1730, 3.8 Torr, ~97 s, Ar 3000 sccm).
"""
import json
import sys
from statistics import mean, pstdev

from common928 import CACHE

IND = [
    ('N2 337/CO 330', 'N2_337', 'CO_330'),
    ('N2 357/CO 330', 'N2_357', 'CO_330'),
    ('N2 380/CO 330', 'N2_380', 'CO_330'),
    ('N2+391/CO 330', 'N2p_391', 'CO_330'),
    ('CN 388/CO 330', 'CN_388', 'CO_330'),
    ('NO 237/CO 330', 'NO_237', 'CO_330'),
    ('OH 309/CO 330', 'OH_309', 'CO_330'),
    ('N2 337/Ar 750', 'N2_337', 'Ar_750'),
    ('N2 337/O 777 ', 'N2_337', 'O_777'),
    ('Ha 656/O 777 ', 'Ha_656', 'O_777'),
    ('Ha 656/Ar750 ', 'Ha_656', 'Ar_750'),
    ('Ar 750/O 777 ', 'Ar_750', 'O_777'),
    ('CO 330/O 777 ', 'CO_330', 'O_777'),
    ('CO 608/Ar811 ', 'CO_608', 'Ar_811'),
]
RAW = ['N2_337', 'N2_357', 'CO_330', 'NO_237', 'OH_309', 'Ha_656', 'O_777', 'Ar_750', 'Ar_811']


def process_of(r):
    f = r['fixed']
    return 'A' if f['Ar_750'] / f['O_777'] > 0.5 else 'B'


def main():
    cache = json.load(open(CACHE))
    recs = sorted([r for r in cache.values() if r['fixed'] and not r['test_mode']
                   and r['fixed_n'] >= 20], key=lambda r: r['stamp'])
    for proc in ('B', 'A'):
        g = [r for r in recs if process_of(r) == proc]
        print('=' * 110)
        print(f'process {proc}: {len(g)} recordings (unlabelled shown as DV=-)')
        print('stamp        DV  ' + ' '.join(f'{k:>8}' for k in RAW))
        for r in g:
            print(f"{r['stamp']} {str(r['valve'] if r['valve'] is not None else '-'):>4}  "
                  + ' '.join(f"{r['fixed'][k]:8.1f}" for k in RAW))
        print()
        print('stamp        DV  ' + ' '.join(f'{lbl[:13]:>13}' for lbl, *_ in IND))
        for r in g:
            print(f"{r['stamp']} {str(r['valve'] if r['valve'] is not None else '-'):>4}  "
                  + ' '.join(f"{r['fixed'][a] / r['fixed'][b]:13.5f}" for _, a, b in IND))
        by = {}
        for r in g:
            if r['valve'] is not None:
                by.setdefault(r['valve'], []).append(r)
        base = by[min(by)]
        print(f"\nper dose, % vs DV={min(by)} (n={len(base)})")
        print('indicator        ' + ' '.join(f'DV{k:>4}(n{len(v)})' for k, v in sorted(by.items())))
        for lbl, a, b in IND:
            b0 = mean(r['fixed'][a] / r['fixed'][b] for r in base)
            cells = []
            for k, v in sorted(by.items()):
                xs = [r['fixed'][a] / r['fixed'][b] for r in v]
                m = mean(xs)
                sd = pstdev(xs) / abs(m) * 100 if len(xs) > 1 else 0
                cells.append(f'{100 * (m / b0 - 1):+6.1f}%±{sd:3.1f}')
            print(f'{lbl}  ' + ' '.join(f'{c:>12}' for c in cells))
        print()


if __name__ == '__main__':
    sys.exit(main())
