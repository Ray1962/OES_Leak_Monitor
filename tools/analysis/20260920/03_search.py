#!/usr/bin/env python3
"""Did anything at all respond to the dosing valve? A whole-spectrum search.

The day confounds dose with time: the valve was opened in increasing steps while viewport
transmission decayed. One accident breaks the confound -- the 16:45 long clean restored UV
throughput by 3.3x *after* the index-0 group, so index 0 (UV/NIR 0.32-0.44) and index 200
(0.29-0.35) overlap in throughput. That makes a two-predictor fit identifiable.

Per wavelength, over the 11 process-C recordings:

    log(I / O777) = a + b * log(UV/NIR) + c * index

b absorbs viewport transmission, c is what a leak would move. The output is the ranked
|t(c)|, so a nitrogen band that responded cannot hide: it would stand at the top with the
SPS progression beside it.
"""
import json
import math
import os
import sys

from common920 import (CACHE, DATA_ROOT, DAY, WIN, gate_open, median_spectrum,
                       read_recording)

# N2 second positive system band heads, N2+ first negative, CN violet, NO gamma -- every
# feature a real air leak would have to raise. Centres carry the +0.30 nm axis offset.
NITROGEN = [('N2 SPS 337.1', 337.40), ('N2 SPS 357.7', 358.00), ('N2 SPS 380.5', 380.80),
            ('N2 SPS 375.5', 375.80), ('N2 SPS 353.7', 354.00), ('N2 SPS 315.9', 316.20),
            ('N2+ FNS 391.4', 391.70), ('N2+ FNS 427.8', 428.10),
            ('CN violet 388', 388.60), ('CN violet 359', 359.30),
            ('NO gamma 236.6', 236.90), ('NO gamma 247.8', 248.10)]


def ols3(ys, x1, x2):
    """y = a + b*x1 + c*x2. Returns (b, c, t_b, t_c, resid_sd). Plain normal equations."""
    n = len(ys)
    mx1 = sum(x1) / n; mx2 = sum(x2) / n; my = sum(ys) / n
    u = [v - mx1 for v in x1]; w = [v - mx2 for v in x2]; z = [v - my for v in ys]
    suu = sum(a * a for a in u); sww = sum(a * a for a in w)
    suw = sum(a * b for a, b in zip(u, w))
    suz = sum(a * b for a, b in zip(u, z)); swz = sum(a * b for a, b in zip(w, z))
    det = suu * sww - suw * suw
    if abs(det) < 1e-18:
        return (float('nan'),) * 5
    b = (sww * suz - suw * swz) / det
    c = (suu * swz - suw * suz) / det
    resid = [zi - b * ui - c * wi for zi, ui, wi in zip(z, u, w)]
    dof = n - 3
    s2 = sum(r * r for r in resid) / dof
    se_b = math.sqrt(s2 * sww / det); se_c = math.sqrt(s2 * suu / det)
    return b, c, b / se_b, c / se_c, math.sqrt(s2)


def main():
    cache = json.load(open(CACHE))
    recs = sorted([v for v in cache.values() if v['process'] == 'C' and v['fixed']],
                  key=lambda r: r['stamp'])
    idx = [r['valve'] for r in recs]
    uvnir = [math.log(r['fixed']['CO_330'] / r['fixed']['O_777']) for r in recs]

    # How badly are the two predictors confounded?
    n = len(idx)
    mi = sum(idx) / n; mu = sum(uvnir) / n
    num = sum((a - mi) * (b - mu) for a, b in zip(idx, uvnir))
    den = math.sqrt(sum((a - mi) ** 2 for a in idx) * sum((b - mu) ** 2 for b in uvnir))
    print(f'process C, n={n} recordings')
    print(f'corr(index, log UV/NIR) = {num / den:+.3f}   '
          f'(1.0 would make the fit unidentifiable; the 16:45 clean is what lowers it)\n')

    # Load the fixed-window median spectra once.
    spectra = []
    for r in recs:
        wl, frames = read_recording(os.path.join(DATA_ROOT, DAY, f"P_OES1_{r['stamp']}.csv"))
        spectra.append(median_spectrum(gate_open(frames), *WIN))
    o777 = [r['fixed']['O_777'] for r in recs]

    # Per-wavelength fit.
    out = []
    for i in range(len(wl)):
        col = [s[i] / o for s, o in zip(spectra, o777)]
        if min(col) <= 0:
            continue
        ys = [math.log(v) for v in col]
        b, c, tb, tc, sd = ols3(ys, uvnir, idx)
        if math.isnan(c):
            continue
        # c is per index unit; report the modelled change from index 0 to 200.
        out.append((abs(tc), wl[i], 100 * (math.exp(c * 200) - 1), tc, sd))

    out.sort(reverse=True)
    print('Strongest index-correlated wavelengths (throughput already removed)')
    print(f"{'nm':>9}{'change 0->200':>15}{'t':>8}{'resid sd':>10}")
    for t, w, ch, tc, sd in out[:20]:
        print(f'{w:9.2f}{ch:14.1f}%{tc:8.2f}{100 * sd:9.1f}%')

    print('\nThe features a real air leak must raise')
    print(f"{'feature':>16}{'nm':>9}{'change 0->200':>15}{'t':>8}{'resid sd':>10}")
    lookup = {round(w, 2): (ch, tc, sd) for _, w, ch, tc, sd in out}
    for name, centre in NITROGEN:
        best = min(lookup.items(), key=lambda kv: abs(kv[0] - centre))
        ch, tc, sd = best[1]
        print(f'{name:>16}{best[0]:9.2f}{ch:14.1f}%{tc:8.2f}{100 * sd:9.1f}%')

    # Matched-throughput comparison, the assumption-free version of the same question.
    print('\nMatched-throughput check: index 0 vs index 200, R = N2 337 / CO 330')
    for r in recs:
        if r['valve'] in (0, 200):
            f = r['fixed']
            print(f"  {r['stamp']}  index {r['valve']:>3}  UV/NIR {f['CO_330'] / f['O_777']:.3f}"
                  f"   R {f['N2_337'] / f['CO_330']:.5f}   CN/CO {f['CN_388'] / f['CO_330']:.4f}")
    return 0


if __name__ == '__main__':
    sys.exit(main())
