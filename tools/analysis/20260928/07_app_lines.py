#!/usr/bin/env python3
"""The day's final ratio set, read with its own geometry, plasma off against plasma on.

The four ratios left in settings.json at 20:23 are NO 237 (abs), NO 237/Ar 750,
NO 237/CO 329.6 and CO 329.6 (abs). NO 237 is configured centre 237.0, half 0.5,
gap 1, width 1, peak search +/-1 nm, no wavelength correction. This emulates
LineIntensityExtractor.Extract for PeakHeight: re-centre on the brightest pixel within the
search range (3-pixel sum), then max of (intensity - side-window line) over centre +/- half.

"Plasma off" = the recording's own frames below the gate (pre-trigger head + stop tail).
They are not dark -- O 777 is lit in some of them -- but the UV is (CO 330 < 50), so a
UV reading that is the same off and on is a detector pattern, not an emission.
"""
import bisect
import os
import sys
from statistics import median

from common928 import DATA_ROOT, DAY, GATE, p99, peak_height, read_recording

APP = {  # label: (centre, half, gap, width, search)
    'NO 237':   (237.0, 0.5, 1.0, 1.0, 1.0),
    'N2 337.1': (337.1, 0.5, 1.0, 1.0, 1.0),
    'CO 329.6': (329.6, 0.5, 1.0, 1.0, 1.0),
    'Ar 750.4': (750.4, 0.5, 1.0, 1.0, 1.0),
}
STAMPS = ['0928170638', '0928194838', '0928195053', '0928200720', '0928202206']


def app_peak(wl, y, c, half, gap, width, search):
    i0 = bisect.bisect_left(wl, c - search); i1 = bisect.bisect_right(wl, c + search)
    # LineIntensityExtractor.FindPeakWavelength: brightest 3-pixel sum, not brightest pixel
    k = max(range(i0, i1), key=lambda i: y[i - 1] + y[i] + y[i + 1])
    return peak_height(wl, y, wl[k], half, gap, width), wl[k]


def main():
    print(f"{'recording':12} {'':4} " + ' '.join(f'{k:>18}' for k in APP))
    for st in STAMPS:
        wl, fr = read_recording(os.path.join(DATA_ROOT, DAY, f'P_OES1_{st}.csv'))
        for tag, sel in (('off', lambda y: p99(y) <= GATE), ('on', lambda y: p99(y) > GATE)):
            g = [y for _, y in fr if sel(y)]
            if not g:
                continue
            m = [median(c) for c in zip(*g)]
            cells = []
            for geom in APP.values():
                v, at = app_peak(wl, m, *geom)
                cells.append(f'{v:7.1f} @{at:6.1f}')
            print(f'{st:12} {tag:4} ' + ' '.join(f'{c:>18}' for c in cells))
    return 0


if __name__ == '__main__':
    sys.exit(main())
