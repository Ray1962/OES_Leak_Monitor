#!/usr/bin/env python3
"""How much light reached the spectrometer, 09-28 against 09-20, line by line.

Same spectrometer (OS361AC55036076), same exposure (80 ms x 5, HWAvg; boxcar 1 -> 2 only).
The TEOS recipe of 09-28 ("A", LDR400) and 09-20's process C are the same kind of plasma
(Ar 750/O 777 1.73 vs 1.52), so the ratio of a line's height on the two days is mostly the
optical path, with the recipe difference as a second-order term. If the ratio is a smooth
function of wavelength it is the optics; if it scatters line by line it is the plasma.
"""
import os
import sys

from common928 import DATA_ROOT, gate_open, median_spectrum, peak_height, read_recording

# (label, centre incl. +0.30 nm offset). Lines present in both TEOS plasmas.
PAIRS = [('OH/CO 297', 297.0), ('CO 313', 313.1), ('CO 330', 329.6), ('N2 337', 337.4),
         ('CO 349', 349.6), ('CN 388', 388.6), ('Hb 486', 486.4), ('C2 516', 516.8),
         ('He 587', 587.86), ('O 616', 616.04), ('Ha 656', 656.5), ('He 667', 668.1),
         ('Ar 696', 696.8), ('Ar 706', 707.0), ('Ar 750', 750.7), ('Ar 763', 763.8),
         ('O 777', 777.6), ('Ar 794', 794.1), ('Ar 811', 811.8), ('O 844', 844.9)]
REF = ('202609/20', '0920162537')   # process C, index 0, 16:25
DAY28 = [('202609/28', '0928195053'), ('202609/28', '0928202206')]


def spectrum(day, stamp):
    wl, fr = read_recording(os.path.join(DATA_ROOT, day, f'P_OES1_{stamp}.csv'))
    return wl, median_spectrum(gate_open(fr), 10, 30)


def main():
    wr, sr = spectrum(*REF)
    rows = {}
    for day, st in DAY28:
        w, s = spectrum(day, st)
        rows[st] = (w, s)
    print(f"{'line':10} {'09-20 C':>9} " + ' '.join(f'{st:>18}' for _, st in DAY28))
    for lbl, c in PAIRS:
        ref = peak_height(wr, sr, c, 0.7, 1.3, 1.1)
        cells = []
        for _, st in DAY28:
            w, s = rows[st]
            v = peak_height(w, s, c, 0.7, 1.3, 1.1)
            cells.append(f'{v:8.0f} (1/{ref / v if v > 0 else float("nan"):5.1f})')
        print(f'{lbl:10} {ref:9.0f} ' + ' '.join(f'{x:>18}' for x in cells))
    return 0


if __name__ == '__main__':
    sys.exit(main())
