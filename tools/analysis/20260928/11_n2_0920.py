#!/usr/bin/env python3
"""Is the "N2 337" seen on 2026-09-20 nitrogen at all -- i.e. air reaching the chamber by
some other path (a TEOS line, a fitting) with the dosing valve shut?

The reading called N2 337 is a PeakHeight over 336.7-338.1 nm. Whatever emits there is
counted. So the spectrum around it is decomposed, per recording, into

    const + slope
  + x * (red-degraded doublet, heads 337.0 / 338.1 nm)   <- what a CO2+ A-X band looks like
  + n * (violet-degraded band, head 337.13 nm)           <- what N2 C-B (0,0) looks like

over 333.5-344.5 nm (the UV axis offset, +0.10 nm, applied to both). The red-degraded shape's decay
length and the instrument width are fitted once, on the brightest example (09-20 16:45, the
long O2 clean), and then held. n is the only term nitrogen can feed.

The same violet-degraded template is then fitted, alone, at the other two second-positive
heads (357.69, 380.49): real N2 must show all three in the ratio ~1 : 0.65 : 0.25.

n is turned into an air fraction with the 2026-09-04 lab sensitivity (plan section 16.1,
PeakHeight): N2 counts = k * ArUV * airFrac, k = 424.5 (E2) .. 980.3 (E3).

Windows interpreter (numpy):
    OES_DATA_ROOT='C:\\DualOES' WSLENV=OES_DATA_ROOT python.exe 11_n2_0920.py
"""
import json
import os
import sys

import numpy as np

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(_HERE, '..', '20260920'))
sys.path.insert(0, os.path.join(_HERE, '..', '202609'))
from common import DATA_ROOT, gate_open, median_spectrum, read_recording  # noqa: E402
from common920 import aruv  # noqa: E402

OFF = 0.10          # UV axis offset: Si 288.16 reads +0.09, H-beta -0.05. The +0.30 of the plan
                    # was measured on Ar I / O I lines beyond 690 nm and does not hold down here.
K = (424.5, 980.3)
FLOW_SCCM = 30500.0
SCCM = 1.01325 / 60.0
FINE = np.arange(320, 395, 0.02)


def blur(shape, fwhm):
    s = fwhm / 2.3548
    k = np.exp(-0.5 * (np.arange(-4, 4.001, 0.02) / s) ** 2)
    return np.convolve(shape, k / k.sum(), mode='same')


def violet(wl, head, fwhm, tau=1.3):
    t = np.interp(wl, FINE, blur(np.where(FINE <= head + OFF, np.exp((FINE - head - OFF) / tau), 0.0), fwhm))
    return t / t.max()


def red_doublet(wl, fwhm, tau):
    sh = np.zeros_like(FINE)
    for h in (337.0, 338.1):
        sh += np.where(FINE >= h + OFF, np.exp(-(FINE - h - OFF) / tau), 0.0)
    t = np.interp(wl, FINE, blur(sh, fwhm))
    return t / t.max()


def lsq(B, y):
    coef, *_ = np.linalg.lstsq(B, y, rcond=None)
    r = y - B @ coef
    s2 = float(r @ r) / max(1, len(y) - B.shape[1])
    se = np.sqrt(np.diag(s2 * np.linalg.inv(B.T @ B)))
    return coef, se, float(np.sqrt(np.mean(r ** 2)))


def load(day, stamp):
    wl, fr = read_recording(os.path.join(DATA_ROOT, day, f'P_OES1_{stamp}.csv'))
    med = median_spectrum(gate_open(fr), 10, 30)
    return np.array(wl), (np.array(med) if med is not None else None)


def fit337(wl, y, fwhm, tau, with_n2=True, with_x=True):
    m = (wl >= 333.5) & (wl <= 344.5)
    cols = [np.ones(m.sum()), wl[m] - 339.0]
    if with_x:
        cols.append(red_doublet(wl, fwhm, tau)[m])
    if with_n2:
        cols.append(violet(wl, 337.13, fwhm)[m])
    return lsq(np.column_stack(cols), y[m])


def fit_head(wl, y, head, fwhm):
    m = (wl >= head + OFF - 4.5) & (wl <= head + OFF + 3.0)
    B = np.column_stack([np.ones(m.sum()), wl[m] - head, violet(wl, head, fwhm)[m]])
    c, se, _ = lsq(B, y[m])
    return c[2], se[2]


def band(wl, y, lo, hi):
    m = (wl >= lo) & (wl <= hi)
    return float(y[m].mean())


def co2p_doublet(wl, y):
    """CO2+ B-X doublet 288.3 / 289.6 nm above the line through its two sides."""
    return band(wl, y, 287.9, 289.8) - (band(wl, y, 285.0, 286.4) + band(wl, y, 291.3, 292.4)) / 2


def n2_01_head(wl, y):
    """N2 C-B (0,1) at 357.69: band just blue of the head minus the mean of both sides."""
    return band(wl, y, 356.9, 357.8) - (band(wl, y, 355.6, 356.4) + band(wl, y, 358.6, 359.4)) / 2


def main():
    wl, clean = load('202609/20', '0920164507')
    best = None
    for fw in np.arange(0.9, 2.01, 0.1):
        for tau in np.arange(1.0, 8.01, 0.5):
            _, _, rms = fit337(wl, clean, fw, tau)
            if best is None or rms < best[0]:
                best = (rms, fw, tau)
    _, fw, tau = best
    c, se, rms = fit337(wl, clean, fw, tau)
    _, _, rms_n2only = fit337(wl, clean, fw, tau, with_x=False)
    _, _, rms_xonly = fit337(wl, clean, fw, tau, with_n2=False)
    print(f'09-20 16:45 long clean: instrument FWHM {fw:.1f} nm, red decay {tau:.1f} nm')
    print(f'  fit residual RMS: red-degraded only {rms_xonly:.0f}   N2-shaped only {rms_n2only:.0f}'
          f'   both {rms:.0f}   (feature height {c[2]:.0f} counts)')
    print(f'  N2-shaped term when both are allowed: {c[3]:+.0f} +/- {se[3]:.0f} counts'
          f'  = {100 * c[3] / c[2]:+.1f} % of the red-degraded band\n')

    cache = json.load(open(os.path.join(_HERE, '..', '20260920', 'out', 'cache.json')))
    print(f"{'rec':6} {'proc/idx':>8} {'band X':>8} {'N2-shaped 337':>14} {'CO2+ 289':>9} {'X/CO2+289':>10}"
          f" {'357.7 head':>11} {'if X were N2':>13}")
    ratios = []
    for stamp in sorted(cache):
        r = cache[stamp]
        wl, y = load('202609/20', stamp)
        if y is None:
            continue
        c, se, _ = fit337(wl, y, fw, tau)
        d = co2p_doublet(wl, y)
        h01 = n2_01_head(wl, y)
        lab = f"{r['process'] or '-'}/{r['valve'] if r['valve'] is not None else '-'}"
        if c[2] > 30 and d > 30:
            ratios.append(c[2] / d)
        print(f"{stamp[4:8]:6} {lab:>8} {c[2]:8.0f} {c[3]:+8.0f}±{se[3]:4.0f} {d:9.0f} {c[2] / d if d > 30 else float('nan'):10.3f}"
              f" {h01:+11.0f} {0.67 * c[2]:+13.0f}")
    ratios = np.array(ratios)
    print(f'\nband X / CO2+ 289 over {len(ratios)} recordings, four plasma types, band X from 35 to 1553 counts:'
          f' median {np.median(ratios):.3f}, range {ratios.min():.3f}-{ratios.max():.3f}')
    print('"if X were N2": the (0,1) head at 357.69 shares its upper level with (0,0) and must be ~0.67 of it.')

if __name__ == '__main__':
    sys.exit(main())
