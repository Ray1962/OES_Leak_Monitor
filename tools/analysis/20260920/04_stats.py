#!/usr/bin/env python3
"""The numbers the plan needs back from this day, whatever the dose test concluded.

  1. R_N2CO_C's per-frame and between-recording scatter on the production tool, measured
     through the engine's own geometry -- the sigma both alarm paths divide by.
  2. The bound this null result puts on the delivered leak.
  3. Classifier headroom: the discriminants against the thresholds settings.json carries.
  4. The viewport fouling rate, which is the day's largest effect by far.
"""
import json
import math
import sys
from statistics import mean, median, pstdev

from common920 import CACHE, VALVE_Q

SIGMA_S = (86.6, 200.0)      # plan section 16.2: x = s * Q, per (mbar.L/s)
CRITERION = 0.093            # plan section 5.1: 3 x 3.1 % cross-batch sigma
SPEC_Q = 8.89e-3             # plan section 16.1: 1 mTorr/min x 400 L


def cv(xs):
    return pstdev(xs) / abs(mean(xs)) * 100 if len(xs) > 1 else float('nan')


def main():
    cache = json.load(open(CACHE))
    recs = sorted([v for v in cache.values() if v['process']], key=lambda r: r['stamp'])
    C = [r for r in recs if r['process'] == 'C']
    B = [r for r in recs if r['process'] == 'B']

    print('=' * 78)
    print('1. R = N2 337 / CO 330 scatter on the production tool (engine geometry)\n')
    for name, group in (('C', C), ('B', B)):
        per_frame = []
        for r in group:
            win = [f for f in r['frames'] if 10.0 <= f['t'] <= 30.0]
            vals = [f['N2_337'] / f['CO_330'] for f in win if f['CO_330']]
            per_frame.append(cv(vals))
        fixed = [r['fixed']['N2_337'] / r['fixed']['CO_330'] for r in group]
        base = [r['fixed']['N2_337'] / r['fixed']['CO_330'] for r in group if r['valve'] == 0]
        print(f'  process {name}  n={len(group)} recordings, {len(win)} frames per window')
        print(f'    per-frame CV inside the window : median {median(per_frame):5.2f} %'
              f'  (range {min(per_frame):.2f} - {max(per_frame):.2f} %)')
        print(f'    between-recording CV, all doses: {cv(fixed):5.2f} %   mean {mean(fixed):.5f}')
        print(f'    between-recording CV, index 0  : {cv(base):5.2f} %   mean {mean(base):.5f}'
              f'   (n={len(base)})')
        print(f'    mean/sigma (index 0)           : {1 / (pstdev(base) / mean(base)):5.1f}'
              f'   engine MinBaselineMeanToSigma = 10')
        print()

    print('=' * 78)
    print('2. What the null result bounds the delivered leak to\n')
    c0 = [r['fixed']['N2_337'] / r['fixed']['CO_330'] for r in C if r['valve'] == 0]
    c200 = [r['fixed']['N2_337'] / r['fixed']['CO_330'] for r in C if r['valve'] == 200]
    obs = mean(c200) / mean(c0) - 1
    print(f'  index 200 vs index 0, process C : {100 * obs:+.1f} %'
          f'   (detection criterion {100 * CRITERION:.1f} %)')
    print(f'  valve table says index 200 =      {VALVE_Q[200]:.1e} mbar.L/s'
          f'   (spec target {SPEC_Q:.2e})')
    print(f'  plan section 16.1 expects there   +77 % to +178 %\n')
    print('  Reading A -- the indicator behaves as calibrated, so the leak was not delivered:')
    for s in SIGMA_S:
        print(f'    with s = {s:5.1f} : delivered Q < {CRITERION / s:.2e} mbar.L/s'
              f'  = {100 * (CRITERION / s) / VALVE_Q[200]:5.1f} % of the table value')
    print('\n  Reading B -- the leak was delivered, so the indicator is this insensitive:')
    s_obs = obs / VALVE_Q[200]
    q_det = CRITERION / s_obs if s_obs > 0 else float('inf')
    print(f'    s = {s_obs:.1f} per (mbar.L/s)  -> Q_det = {q_det:.2e}'
          f'  = {q_det / SPEC_Q:.1f} x the spec target  -> NO-GO')
    print(f'    that is {SIGMA_S[0] / s_obs:.0f} x below the extrapolated sensitivity\n')

    print('=' * 78)
    print('3. Classifier headroom (settings.json thresholds: Ar750/O777 > 0.5 -> C,'
          ' then Ha656/O777 < 0.05 -> A)\n')
    print(f"{'':>26}{'Ar750/O777':>14}{'Ha656/O777':>14}{'verdict':>10}")
    for name, group in (('C (labelled)', C), ('B (labelled)', B)):
        ar = [r['fixed']['Ar_750'] / r['fixed']['O_777'] for r in group]
        ha = [r['fixed']['Ha_656'] / r['fixed']['O_777'] for r in group]
        print(f'  {name:<24}{min(ar):6.4f}-{max(ar):<7.4f}{min(ha):6.4f}-{max(ha):<7.4f}'
              f"{('C' if min(ar) > 0.5 else 'B'):>10}")
    # every unlabelled recording, so the A step and the tail pair are visible too
    print('\n  unlabelled recordings of the same day:')
    for r in sorted(cache.values(), key=lambda r: r['stamp']):
        if r['process'] or not r['fixed']:
            continue
        f = r['fixed']
        ar = f['Ar_750'] / f['O_777']; ha = f['Ha_656'] / f['O_777']
        cls = 'C' if ar > 0.5 else ('A' if ha < 0.05 else 'B')
        mark = '  <- within 15 % of the 0.05 threshold' if cls != 'C' and abs(ha / 0.05 - 1) < 0.15 else ''
        print(f"    {r['stamp']}  {r['duration']:6.1f}s  {ar:9.4f}  {ha:9.4f}   -> {cls}{mark}")
    print(f'\n  plan section 17.2 measured in August: A 0.0306-0.0333, B 0.0740-0.1146.')

    print('\n' + '=' * 78)
    print('4. Viewport fouling: UV/NIR = CO 330 / O 777 against time\n')
    for name, group in (('C', C), ('B', B)):
        after = [r for r in group if r['stamp'] > '0920170000']
        t0 = None
        print(f'  process {name}:')
        for r in after:
            t = int(r['stamp'][4:6]) * 3600 + int(r['stamp'][6:8]) * 60 + int(r['stamp'][8:10])
            t0 = t0 if t0 is not None else t
            f = r['fixed']
            print(f"    {r['stamp']}  +{(t - t0) / 60:5.1f} min   UV/NIR {f['CO_330'] / f['O_777']:.4f}")
        first, last = after[0]['fixed'], after[-1]['fixed']
        ta = int(after[0]['stamp'][4:6]) * 60 + int(after[0]['stamp'][6:8])
        tb = int(after[-1]['stamp'][4:6]) * 60 + int(after[-1]['stamp'][6:8])
        ratio = (last['CO_330'] / last['O_777']) / (first['CO_330'] / first['O_777'])
        half = (tb - ta) * math.log(2) / -math.log(ratio)
        print(f'    -> {ratio:.3f} over {tb - ta} min   half-life {half:.0f} min\n')
    return 0


if __name__ == '__main__':
    sys.exit(main())
