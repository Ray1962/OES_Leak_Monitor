#!/usr/bin/env python3
"""The dose curves: every indicator against dosing valve index, for process C and B.

Printed both raw and normalised, because the day's dominant effect is not the leak: UV
throughput moves 3.3x over the session (viewport fouling, restored by the 16:45 long
clean) while the NIR barely moves. Anything not normalised inside the UV measures that.
"""
import json
import sys
from statistics import mean, pstdev

from common920 import CACHE, VALVE_Q, air_frac

# (label, numerator, denominator) -- denominators chosen in the same wavelength region as
# the numerator, so that viewport transmission divides out (plan section 2, the 7.5 nm rule).
INDICATORS = [
    ('R_N2CO   N2 337 / CO 330', 'N2_337', 'CO_330'),
    ('N2 357   / CO 330       ', 'N2_357', 'CO_330'),
    ('N2 380   / CO 330       ', 'N2_380', 'CO_330'),
    ('N2+ 391  / CO 330       ', 'N2p_391', 'CO_330'),
    ('CN 388   / CO 330       ', 'CN_388', 'CO_330'),
    ('NO 237   / CO 330       ', 'NO_237', 'CO_330'),
    ('R_N2Ar   N2 337 / Ar 750', 'N2_337', 'Ar_750'),
    ('guard    CO 608 / Ar 811', 'CO_608', 'Ar_811'),
    ('UV/NIR   CO 330 / O 777 ', 'CO_330', 'O_777'),
]


def groups(cache, process):
    out = {}
    for v in cache.values():
        if v['process'] == process and v['fixed']:
            out.setdefault(v['valve'], []).append(v)
    return {k: sorted(v, key=lambda r: r['stamp']) for k, v in sorted(out.items())}


def main():
    cache = json.load(open(CACHE))
    for process in ('C', 'B'):
        g = groups(cache, process)
        print(f"\n{'=' * 100}\nprocess {process}   n per dose: "
              + '  '.join(f'{k}:{len(v)}' for k, v in g.items()))
        base_idx = min(g)
        print(f"baseline = index {base_idx}  (n={len(g[base_idx])})\n")
        for label, num, den in INDICATORS:
            vals = {k: [r['fixed'][num] / r['fixed'][den] for r in rs] for k, rs in g.items()}
            base = mean(vals[base_idx])
            cells = []
            for k in sorted(g):
                m = mean(vals[k])
                cells.append(f'{100 * (m / base - 1):+7.1f}%')
            sd = pstdev(vals[base_idx]) / abs(base) * 100 if len(vals[base_idx]) > 1 else float('nan')
            print(f'{label}  base={base:9.5f} sd={sd:4.1f}%   ' + ' '.join(cells))
        print('  index:                                          '
              + ' '.join(f'{k:>8}' for k in sorted(g)))
        print('  Q mbar.L/s (2026-09-04 valve table):            '
              + ' '.join(f'{VALVE_Q[k]:8.1e}' for k in sorted(g)))
        print('  airFrac at 500 sccm:                            '
              + ' '.join(f'{air_frac(VALVE_Q[k], process):8.1e}' for k in sorted(g)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
