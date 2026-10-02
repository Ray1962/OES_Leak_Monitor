#!/usr/bin/env python3
"""Process A's "N2 337 head" rises with DV. Is that nitrogen?

Three independent readings of the same recordings:

  1. What the step is made of. The N2 second-positive (0,0) band has its head at 337.13 nm
     and is degraded to the VIOLET: a sharp red edge, a tail toward 334 nm. The feature in
     these spectra is the opposite -- flat on the blue side, a step up at ~337.4 that stays
     up toward the red. 09-20 16:45 (long O2 clean) shows the same feature thirty times
     brighter, so its shape can be read off directly and used as a template ("band X").

  2. A three-component fit of (recording - first DV 0 recording) over 332.5-344.5 nm:
         a * (the DV 0 plasma spectrum)   -- everything brightening together
       + x * band X template              -- the red-degraded step growing on its own
       + n * N2 (0,0) template            -- a violet-degraded head at 337.13 (+0.30 axis)
       + c
     n is the only term a leak can feed. The detector's fixed pattern cancels in the
     difference.

  3. What the step travels with: CO 349, and the clock.

Needs numpy + matplotlib (Windows interpreter):
    OES_DATA_ROOT='C:\\DualOES' WSLENV=OES_DATA_ROOT python.exe 10_head_origin.py
"""
import json
import os
import sys

import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt  # noqa: E402

from common928 import (CACHE, DATA_ROOT, DAY, GATE, OUT_DIR, gate_open, median_spectrum,  # noqa: E402
                       p99, peak_height, read_recording)

AXIS_OFFSET = 0.30
N2_HEAD = 337.13 + AXIS_OFFSET
FIT = (332.5, 344.5)
HI, LO = (337.45, 338.95), (334.85, 336.35)
plt.rcParams['font.family'] = ['Microsoft JhengHei', 'DejaVu Sans']
C = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100']
INK, INK2, GRID = '#0b0b0b', '#52514e', '#e4e3df'


def load(day, stamp):
    wl, fr = read_recording(os.path.join(DATA_ROOT, day, f'P_OES1_{stamp}.csv'))
    on = np.array(median_spectrum(gate_open(fr), 10, 30))
    off = [y for _, y in fr if p99(y) <= GATE]
    off = np.median(np.array(off), axis=0) if off else None
    return np.array(wl), on, off


def band(wl, y, w):
    m = (wl >= w[0]) & (wl <= w[1])
    return float(y[m].mean())


def fwhm_nm(wl, y, centre):
    """Instrument width from an isolated atomic line (Ar 763.5)."""
    m = (wl > centre - 3) & (wl < centre + 3)
    w, v = wl[m], y[m] - np.percentile(y[m], 10)
    half = v.max() / 2
    above = w[v >= half]
    return float(above.max() - above.min()) + float(np.mean(np.diff(w)))


def n2_template(wl, fwhm):
    """Violet-degraded band: exp tail to the blue of the head, nothing to the red, blurred."""
    fine = np.arange(325, 350, 0.02)
    shape = np.where(fine <= N2_HEAD, np.exp((fine - N2_HEAD) / 1.3), 0.0)
    s = fwhm / 2.3548
    k = np.exp(-0.5 * (np.arange(-3, 3.001, 0.02) / s) ** 2)
    blurred = np.convolve(shape, k / k.sum(), mode='same')
    t = np.interp(wl, fine, blurred)
    return t / t.max()


def main():
    cache = json.load(open(CACHE))
    A = sorted([r for r in cache.values() if r['fixed'] and not r['test_mode'] and r['fixed_n'] >= 20
                and r['fixed']['Ar_750'] / r['fixed']['O_777'] > 0.5], key=lambda r: r['stamp'])
    wl, s0, off0 = load(DAY, '0928195053')
    fw = fwhm_nm(wl, s0, 763.8)
    print(f'instrument FWHM from Ar 763.5: {fw:.2f} nm')

    # band X: the 09-20 long clean, local baseline (334-336.3) removed, peak-normalised
    wr, clean, _ = load('202609/20', '0920164507')
    x_t = clean - band(wr, clean, (334.0, 336.3))
    x_t = np.where(wr < 336.3, 0.0, x_t)
    x_t = x_t / x_t[(wr > 337) & (wr < 340)].max()
    n_t = n2_template(wl, fw)
    m = (wl >= FIT[0]) & (wl <= FIT[1])
    p0 = (s0 - off0)                      # the DV 0 plasma spectrum, fixed pattern removed

    print(f"\n{'rec':9} {'DV':>4} {'head':>6} {'CO349':>6} {'head/CO349':>10} {'CO330':>6}"
          f" {'a (bright)':>11} {'x (band X)':>11} {'n (N2)':>13}")
    rows = []
    for r in A:
        _, s, _ = load(DAY, r['stamp'])
        head = band(wl, s, HI) - band(wl, s, LO)
        co349 = peak_height(list(wl), list(s), 349.6, 0.7, 1.3, 1.1)
        y = (s - s0)[m]
        Bm = np.column_stack([p0[m], x_t[m], n_t[m], np.ones(m.sum())])
        coef, res, *_ = np.linalg.lstsq(Bm, y, rcond=None)
        dof = m.sum() - 4
        s2 = float(((y - Bm @ coef) ** 2).sum() / dof)
        se = np.sqrt(np.diag(s2 * np.linalg.inv(Bm.T @ Bm)))
        rows.append((r, head, co349, coef, se))
        dv = '-' if r['valve'] is None else r['valve']
        print(f"{r['stamp'][4:8]+':'+r['stamp'][8:10]:9} {dv:>4} {head:6.1f} {co349:6.1f} {head / co349:10.3f}"
              f" {r['fixed']['CO_330']:6.0f} {coef[0]:+11.3f} {coef[1]:+7.1f}±{se[1]:3.1f}"
              f" {coef[2]:+7.1f}±{se[2]:3.1f}")

    hc = [h / c for _, h, c, *_ in rows]
    print(f'\nhead / CO 349 over the {len(rows)} A steps: mean {np.mean(hc):.3f}, CV {np.std(hc) / np.mean(hc) * 100:.1f} %'
          f'   (head alone: CV {np.std([h for _, h, *_ in rows]) / np.mean([h for _, h, *_ in rows]) * 100:.1f} %)')

    # ---- figure: shapes
    fig, ax = plt.subplots(1, 2, figsize=(15, 5.6))
    w = (wl > 330.5) & (wl < 346)
    _, late, _ = load(DAY, '0928202206')
    d = late - s0
    a = ax[0]
    a.plot(wl[w], x_t[w], color=C[0], lw=2, label='09-20 16:45 長清洗的同一特徵(亮 30 倍)')
    a.plot(wl[w], (d[w] - d[(wl > 334) & (wl < 336.3)].mean()) / (band(wl, d, (338, 340)) - d[(wl > 334) & (wl < 336.3)].mean()),
           color=C[1], lw=2, label='09-28 A:20:22(DV 200)減 19:50(DV 0)')
    a.plot(wl[w], n_t[w], color=C[2], lw=2, ls='--', label='N₂ 二正系 (0,0) 應有的形狀(帶頭 337.13,向紫端遞減)')
    a.axvline(N2_HEAD, color=INK2, lw=0.8, ls=':')
    a.set_title('337 nm 附近「多出來的東西」的形狀(各自正規化)', loc='left', fontsize=11, color=INK)
    a.set_xlabel('波長 (nm)', color=INK2); a.set_ylabel('相對強度', color=INK2)
    a.legend(fontsize=9, frameon=False, loc='upper left'); a.set_ylim(-0.3, 1.9)
    b = ax[1]
    t = [r['stamp'][4:6] + ':' + r['stamp'][6:8] for r, *_ in rows]
    xs = np.arange(len(rows))
    b.plot(xs, [h for _, h, *_ in rows], color=C[1], lw=2, marker='o', ms=7, label='337 帶頭高度(圖上那條)')
    b.plot(xs, [c * np.mean(hc) for _, _, c, *_ in rows], color=C[0], lw=2, marker='s', ms=6,
           label=f'CO 349 × {np.mean(hc):.3f}')
    b.errorbar(xs, [co[2] for *_, co, _ in rows], yerr=[2 * se[2] for *_, se in rows], color=C[2], lw=2,
               marker='^', ms=7, capsize=3, label='N₂ 形狀的擬合量 ±2σ')
    b.axhline(0, color=INK2, lw=0.8)
    b.set_xticks(xs)
    b.set_xticklabels([f"{ti}\nDV {'-' if r['valve'] is None else r['valve']}" for ti, (r, *_) in zip(t, rows)], fontsize=8)
    b.set_ylabel('counts', color=INK2)
    b.set_title('製程 A 各段(依時間):帶頭跟著 CO 349 走;N₂ 形狀的成分沒有隨 DV 增加', loc='left', fontsize=11, color=INK)
    b.legend(fontsize=9, frameon=False, loc='center right')
    for q in ax:
        q.grid(True, color=GRID, lw=0.6)
        for sp in ('top', 'right'):
            q.spines[sp].set_visible(False)
        q.tick_params(colors=INK2)
    fig.tight_layout()
    path = os.path.join(OUT_DIR, 'n2_337_head_origin.png')
    fig.savefig(path, dpi=110, facecolor='#fcfcfb')
    print('wrote', path)


if __name__ == '__main__':
    sys.exit(main())
