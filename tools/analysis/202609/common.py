"""Shared pieces for the 2026-09 production-data analysis.

See docs/production-data-202609-analysis-zh-TW.md. Pure Python, no dependencies.

The extraction here must stay identical to the app's LineIntensityExtractor
(src/OES_Leak_Monitor/LineIntensity.cs), or none of the numbers transfer to a
settings.json: PeakHeight = max over the signal window of
(intensity - the straight line through the two side windows' means).
"""
import bisect
import os
import re
from statistics import median

# Where the recordings live. Override with OES_DATA_ROOT.
DATA_ROOT = os.environ.get('OES_DATA_ROOT', '/mnt/c/DualOES')

# Working files (per-recording cache). Kept out of git by .gitignore.
OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out')
CACHE = os.path.join(OUT_DIR, 'cache.json')

# label: (centre nm, half width, baseline gap, baseline width).
# Site geometry from docs/leak-monitor-plan-zh-TW.md §4.2 / §4.3; centres already carry
# the +0.30 nm axis offset measured on this spectrometer.
LINES = {
    'N2_337': (337.40, 0.70, 1.30, 1.10),
    'N2_357': (358.00, 0.70, 1.30, 1.10),
    'CO_330': (329.60, 1.06, 2.10, 1.10),
    'Ar_750': (750.70, 1.06, 1.30, 1.10),
    'Ar_811': (811.80, 1.06, 1.30, 1.10),
    'O_777':  (777.60, 1.06, 1.30, 1.10),
    'Ha_656': (656.50, 1.06, 1.30, 1.10),
    'NO_237': (237.10, 0.70, 1.30, 1.10),
    'CO_608': (607.35, 0.70, 2.20, 0.80),
}

# The C' (class "Cp") rule proposed in §4.4 / §6.3.
CP_NUM = (656.48, 0.70, 1.30, 1.10)   # H-alpha 656.3
CP_DEN = (616.04, 0.70, 1.30, 1.10)   # O I 615.8
CP_THRESHOLD = 6.25

GATE = 1000.0          # site logger: SpectrumPercentile, 99th percentile > 1000
WIN = (10.0, 30.0)     # fixed sampling point: gate-open + 10..30 s (plan §3, BatchTracker)

# The three September days analysed, and what is excluded from them.
SEPT_DAYS = ('202609/03', '202609/08', '202609/10')
AUG_DAYS = ('202608/20', '202608/21')
DUPLICATE_FOLDER = '202609/0910-3'       # byte-identical copies of files in 202609/10
# 09-03 21:00-21:25 is a different recipe (A 108 s / C 78 s). Matched by time range, not a
# filename prefix: "P_OES1_09032" also covers the normal 22:xx batch after it.
OTHER_RECIPE_BATCH = ('0903210000', '0903213000')


def in_other_recipe_batch(key):
    stamp = key.rsplit('_', 1)[-1][:10]
    return key.startswith('202609/03/') and OTHER_RECIPE_BATCH[0] <= stamp < OTHER_RECIPE_BATCH[1]


def win_mean(wl, y, lo, hi):
    i0 = bisect.bisect_left(wl, lo)
    i1 = bisect.bisect_right(wl, hi)
    if i1 <= i0:
        return None
    return sum(y[i0:i1]) / (i1 - i0)


def peak_height(wl, y, c, half, gap, width):
    lo, hi = c - half, c + half
    s0 = bisect.bisect_left(wl, lo)
    s1 = bisect.bisect_right(wl, hi) - 1
    if s1 < s0:
        return float('nan')
    lhi = lo - gap; llo = lhi - width
    rlo = hi + gap; rhi = rlo + width
    lm = win_mean(wl, y, llo, lhi)
    rm = win_mean(wl, y, rlo, rhi)
    xl = (llo + lhi) / 2; xr = (rlo + rhi) / 2

    def base(w):
        if lm is not None and rm is not None:
            return lm + (rm - lm) / (xr - xl) * (w - xl)
        return lm if lm is not None else (rm if rm is not None else 0.0)

    return max(y[i] - base(wl[i]) for i in range(s0, s1 + 1))


def p99(y):
    s = sorted(y)
    return s[min(len(s) - 1, int(0.99 * (len(s) - 1) + 0.5))]


def tsec(t):
    h, m, s = t.split(':')
    return int(h) * 3600 + int(m) * 60 + float(s)


def classify_abc(ar_o, ha_o):
    """The August two-threshold rule (plan §4.3)."""
    if ar_o > 0.5:
        return 'C'
    if ha_o < 0.07:
        return 'A'
    return 'B'


def read_recording(path):
    """Return (wavelengths, [(elapsed_s, intensities), ...]) for every frame of a wide CSV."""
    with open(path, encoding='utf-8', errors='replace') as fh:
        wl = [float(x) for x in fh.readline().rstrip('\n').split(',')[1:] if x.strip()]
        frames = []
        t0 = None
        for line in fh:
            parts = line.rstrip('\n').split(',')
            if not re.match(r'\d\d:\d\d:\d\d', parts[0]):
                continue
            try:
                y = [float(v) for v in parts[1:1 + len(wl)]]
            except ValueError:
                continue
            if len(y) != len(wl):
                continue
            t = tsec(parts[0])
            if t0 is None:
                t0 = t
            if t < t0:
                t += 86400   # midnight roll
            frames.append((t - t0, y))
    return wl, frames


def gate_open(frames):
    """Frames whose 99th percentile clears the site plasma gate, elapsed time re-based to the first."""
    op = [(t, y) for t, y in frames if p99(y) > GATE]
    if not op:
        return []
    g0 = op[0][0]
    return [(t - g0, y) for t, y in op]


def median_spectrum(frames, lo=WIN[0], hi=WIN[1]):
    sub = [y for t, y in frames if lo <= t <= hi]
    if not sub:
        return None
    return [median(col) for col in zip(*sub)]


def day_of(key):
    return '/'.join(key.split('/')[:2])


def cp_labels(cache):
    """Label September C steps as C or C'.

    C' = Ar 750 below 85 % of the most recent *normal* C step on the same day. Deliberately
    not "below the previous step": two C' steps in a row (09-03 afternoon) would leave the
    second one labelled C, and that single mislabel collapses a 13 sigma separation to 0.89
    sigma (§4.3). The 09-03 evening batch is a different recipe and is left out.
    """
    keys = sorted(k for k, v in cache.items()
                  if k.startswith(SEPT_DAYS) and not in_other_recipe_batch(k)
                  and v and v['class'] == 'C' and v['fixed'])
    labels, ref = {}, {}
    for k in keys:
        day = day_of(k)
        ar = cache[k]['fixed']['Ar_750']
        base = ref.get(day)
        if base and ar < 0.85 * base:
            labels[k] = "C'"
        else:
            labels[k] = 'C'
            ref[day] = ar
    return labels


def sept_step_labels(cache):
    """C / C' labels for September C steps plus A / B for everything else."""
    labels = cp_labels(cache)
    for k, v in cache.items():
        if (k.startswith(SEPT_DAYS) and not in_other_recipe_batch(k)
                and v and v['class'] and v['class'] != 'C' and v['fixed']):
            labels[k] = v['class']
    return labels
