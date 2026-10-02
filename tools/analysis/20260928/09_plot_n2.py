#!/usr/bin/env python3
"""Plot N2 337.1 nm per frame, one panel per (process, DV), one line per recording.

Two figures:
  out/n2_337_raw.png   the raw intensity of the pixel nearest 337.1 nm -- what the
                       Recordings tab plots, continuum and fixed pattern included
  out/n2_337_head.png  the band-averaged head (06_n2_head.py), continuum removed

x = seconds from gate open (the head of the file before it is negative). Each row shares
one y scale so the three DV panels can be compared directly; the dashed line is the DV 0
median over gate-open + 10..30 s.

Needs matplotlib, which the WSL python here lacks; run it with the Windows interpreter:
    OES_DATA_ROOT='C:\\DualOES' WSLENV=OES_DATA_ROOT python.exe 09_plot_n2.py
"""
import bisect
import json
import os
import sys
from statistics import median

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt  # noqa: E402

from common928 import CACHE, DATA_ROOT, DAY, GATE, OUT_DIR, p99, read_recording, win_mean  # noqa: E402

HI, LO = (337.45, 338.95), (334.85, 336.35)       # same as 06_n2_head.py
SERIES = ['#2a78d6', '#eb6834', '#1baf7a', '#eda100', '#e87ba4', '#008300', '#4a3aa7', '#e34948']
INK, INK2, GRID = '#0b0b0b', '#52514e', '#e4e3df'
plt.rcParams['font.family'] = ['Microsoft JhengHei', 'DejaVu Sans']


def process_of(r):
    f = r['fixed']
    return 'A' if f['Ar_750'] / f['O_777'] > 0.5 else 'B'


def traces(stamp):
    wl, fr = read_recording(os.path.join(DATA_ROOT, DAY, f'P_OES1_{stamp}.csv'))
    k = min(range(len(wl)), key=lambda i: abs(wl[i] - 337.1))
    g0 = next(t for t, y in fr if p99(y) > GATE)
    t = [ti - g0 for ti, _ in fr]
    raw = [y[k] for _, y in fr]
    head = [win_mean(wl, y, *HI) - win_mean(wl, y, *LO) for _, y in fr]
    return t, raw, head, wl[k]


def main():
    cache = json.load(open(CACHE))
    recs = sorted([r for r in cache.values() if r['fixed'] and not r['test_mode']
                   and r['fixed_n'] >= 20 and r['valve'] is not None], key=lambda r: r['stamp'])
    data = {}
    pix = None
    for r in recs:
        t, raw, head, pix = traces(r['stamp'])
        data[r['stamp']] = (process_of(r), r['valve'], t, raw, head)

    titles = {'A': '製程 A(LDR400,63 s)', 'B': '製程 B(HDR1730,97 s)'}
    for idx, (fname, label) in enumerate((('n2_337_raw.png', f'N₂ 337.1 nm 原始強度(像素 {pix:.2f} nm)'),
                                          ('n2_337_head.png', 'N₂ 337 帶頭高度(338.2 ± 0.75 減 335.6 ± 0.75 nm)'))):
        fig, axes = plt.subplots(2, 3, figsize=(16, 8.5), sharex='row', sharey='row')
        for row, proc in enumerate(('A', 'B')):
            ref_vals = [v for st, (p, dv, t, raw, head) in data.items() if p == proc and dv == 0
                        for ti, v in zip(t, (raw, head)[idx]) if 10 <= ti <= 30]
            ref = median(ref_vals)
            for col, dv in enumerate((0, 100, 200)):
                ax = axes[row][col]
                runs = [(st, d) for st, d in data.items() if d[0] == proc and d[1] == dv]
                for i, (st, (_, _, t, raw, head)) in enumerate(runs):
                    ax.plot(t, (raw, head)[idx], color=SERIES[i % 8], lw=1.4,
                            label=f'{st[4:6]}:{st[6:8]}:{st[8:10]}')
                ax.axhline(ref, color=INK2, lw=1, ls='--')
                ax.axvspan(10, 30, color=GRID, alpha=0.5, lw=0)
                ax.set_title(f'{titles[proc]} · DV {dv}  (n={len(runs)})   虛線 {ref:.0f}', color=INK, fontsize=11,
                             loc='left')
                ax.grid(True, color=GRID, lw=0.6)
                for s in ('top', 'right'):
                    ax.spines[s].set_visible(False)
                ax.tick_params(colors=INK2, labelsize=9)
                ax.legend(fontsize=8, frameon=False, loc='lower center', ncol=4)
                if col == 0:
                    ax.set_ylabel('counts', color=INK2)
                if row == 1:
                    ax.set_xlabel('閘門開啟後秒數 (s)', color=INK2)
        fig.suptitle(f'2026-09-28  {label}   虛線 = 該製程 DV 0 於 10–30 s 的中位數,灰底 = 取樣窗口', color=INK, fontsize=13,
                     x=0.01, ha='left')
        fig.tight_layout(rect=(0, 0, 1, 0.96))
        path = os.path.join(OUT_DIR, fname)
        fig.savefig(path, dpi=110, facecolor='#fcfcfb')
        print('wrote', path)


if __name__ == '__main__':
    sys.exit(main())
