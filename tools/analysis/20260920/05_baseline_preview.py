#!/usr/bin/env python3
"""Preview what a Golden Run built from this day would look like, before building it.

**This is a preview, not a second implementation.** The run itself must be built by the app's
BaselineBuilder, which shares RatioFrameSampling with the engine's own capture loop; the point
of this script is only to choose which recordings to tick and to have a number to check the
app's answer against afterwards.

A Golden Run's sigma is the scatter of the pooled *frames*, so it is dominated by within-window
per-frame scatter (3.7 % for C) rather than by how far the recordings sit from each other
(1.9 % pre-dose, 3.0 % across the day). That is why the choice of recording set moves the
thresholds much less than it looks like it should.
"""
import json
import math
import sys
from statistics import mean, pstdev

from common920 import CACHE

WIN_LO, WIN_HI = 10.0, 30.0
MIN_FRAMES = 60          # BaselineBuilder.MinFrames
MIN_MEAN_TO_SIGMA = 10   # BaselineBuilder.MinBaselineMeanToSigma
MARGINAL = 20            # BaselineBuilder.MarginalMeanToSigma

RATIOS = [
    ('R_N2CO', 'N2_337', 'CO_330'),      # the Alarm ratio, per process class
    ('R_N2Ar_C', 'N2_337', 'Ar_750'),    # guard (C only)
    ('R_COAr_C', 'CO_608', 'Ar_811'),    # guard (C only)
]

# Pre-dose = recorded before the dosing valve was first moved. Leak-free whatever section 18.1
# concluded, which is the whole reason to prefer them.
PREDOSE_C = ['0920161407', '0920162101', '0920162537', '0920163051']
PREDOSE_B = ['0920161847', '0920162319', '0920162837']


def pooled(cache, stamps, num, den):
    vals = []
    for s in stamps:
        r = cache[s]
        for f in r['frames']:
            if WIN_LO <= f['t'] <= WIN_HI and f[den]:
                vals.append(f[num] / f[den])
    return vals


def report(title, cache, stamps, ratios):
    print(f'\n{title}   n={len(stamps)} recordings')
    for key, num, den in ratios:
        v = pooled(cache, stamps, num, den)
        if not v:
            continue
        m, s = mean(v), pstdev(v)
        mts = m / s if s else float('inf')
        flag = ('REJECTED' if mts < MIN_MEAN_TO_SIGMA else
                'marginal' if mts < MARGINAL else 'ok')
        frames = 'ok' if len(v) >= MIN_FRAMES else f'TOO FEW (<{MIN_FRAMES})'
        print(f'  {key:10s} frames={len(v):4d} {frames:16s} mean={m:9.5f}  sigma={s:8.5f}'
              f'  ({100 * s / m:5.2f} %)  mean/sigma={mts:6.1f}  {flag}')
        print(f'{"":13s}-> live Warn at +{3 * 100 * s / m:5.1f} %,'
              f'  Alarm at +{6 * 100 * s / m:5.1f} %'
              f'   (plan section 16.3 designed for +9.1 / +18.2 %)')


def main():
    cache = json.load(open(CACHE))
    allC = sorted(k for k, v in cache.items() if v['process'] == 'C')
    allB = sorted(k for k, v in cache.items() if v['process'] == 'B')

    print('=' * 92)
    print('Option 1 -- pre-dose recordings only (unambiguously leak-free)')
    report('  process C', cache, PREDOSE_C, RATIOS)
    report('  process B', cache, PREDOSE_B, RATIOS[:1])

    print('\n' + '=' * 92)
    print('Option 2 -- every labelled recording of the day (relies on section 18.1 being right)')
    report('  process C', cache, allC, RATIOS)
    report('  process B', cache, allB, RATIOS[:1])

    print('\n' + '=' * 92)
    print('Where each sigma comes from (process C, R_N2CO)')
    for name, stamps in (('pre-dose', PREDOSE_C), ('all doses', allC)):
        per_rec = []
        levels = []
        for s in stamps:
            v = pooled(cache, [s], 'N2_337', 'CO_330')
            per_rec.append(pstdev(v) / mean(v))
            levels.append(mean(v))
        within = math.sqrt(sum(x * x for x in per_rec) / len(per_rec))
        between = pstdev(levels) / mean(levels)
        combined = math.sqrt(within ** 2 + between ** 2)
        pooled_cv = pstdev(pooled(cache, stamps, 'N2_337', 'CO_330')) / \
            mean(pooled(cache, stamps, 'N2_337', 'CO_330'))
        print(f'  {name:9s} within-recording {100 * within:5.2f} %   between-recording'
              f' {100 * between:5.2f} %   quadrature {100 * combined:5.2f} %'
              f'   actual pooled {100 * pooled_cv:5.2f} %')
    return 0


if __name__ == '__main__':
    sys.exit(main())
