"""Shared pieces for the 2026-09-20 production-tool controlled-leak test.

Extraction geometry and the PeakHeight definition are imported wholesale from the
2026-09 toolkit (tools/analysis/202609/common.py), which is itself pinned to the app's
LineIntensityExtractor. Nothing here may redefine them: the whole point of this test is
that its numbers transfer to a settings.json.

What is new here:
  * the day's own notes files (process + dosing valve index) as the label source,
  * a few extra lines the September set did not need (He, OH, CN, N2 380, O 616),
  * ArUV, the 415-436 nm Ar I multiplet used as the 2026-09-04 bridge.
"""
import glob
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(_HERE, '..', '202609'))

from common import (  # noqa: E402
    DATA_ROOT, GATE, WIN, LINES as SEPT_LINES, gate_open, median_spectrum,
    p99, peak_height, read_recording, tsec, win_mean,
)

DAY = '202609/20'
OUT_DIR = os.path.join(_HERE, 'out')
CACHE = os.path.join(OUT_DIR, 'cache.json')

# Site geometry (plan section 4.2 / 17.1) plus the extras this test needs.
# centre, half width, baseline gap, baseline width -- all carry the +0.30 nm axis offset.
LINES = dict(SEPT_LINES)
LINES.update({
    'N2_380':  (380.80, 0.70, 1.30, 1.10),   # N2 SPS 380.5, the weak third band
    'N2p_391': (391.70, 0.70, 1.30, 1.10),   # N2+ FNS 391.4
    'OH_309':  (309.30, 0.70, 1.30, 1.10),
    'CN_388':  (388.60, 0.70, 1.30, 1.10),
    'CO_313':  (313.10, 1.06, 2.10, 1.10),
    'O_616':   (616.04, 0.70, 1.30, 1.10),   # the Cp discriminant's denominator
    'He_587':  (587.86, 0.70, 1.30, 1.10),   # process C admits He
    'He_667':  (668.10, 0.70, 1.30, 1.10),
})

# ArUV: the 415-436 nm Ar I multiplet, the 2026-09-04 bridge (that analysis section 4.1).
# Band mean minus the straight line through two side continuum windows. The 09-04 scripts
# were not kept, so this is a restatement, not the identical code -- see the doc.
ARUV = (415.0, 436.0, (411.5, 413.5), (438.0, 440.0))


def aruv(wl, y):
    lo, hi = ARUV[0], ARUV[1]
    (llo, lhi), (rlo, rhi) = ARUV[2], ARUV[3]
    lm = win_mean(wl, y, llo, lhi)
    rm = win_mean(wl, y, rlo, rhi)
    bm = win_mean(wl, y, lo, hi)
    if bm is None:
        return float('nan')
    if lm is None or rm is None:
        base = lm if lm is not None else (rm if rm is not None else 0.0)
    else:
        base = (lm + rm) / 2.0          # band centre sits midway between the side windows
    return bm - base


def extract_all(wl, y):
    """Every tracked line plus ArUV, from one spectrum."""
    out = {name: peak_height(wl, y, *geom) for name, geom in LINES.items()}
    out['ArUV'] = aruv(wl, y)
    return out


# ---------------------------------------------------------------- the day's own labels

NOTE_RE = re.compile(r'製程\s*([ABC]).*?index\s*=\s*(\d+)', re.S)


def read_notes():
    """{recording stamp: (process, valve index)} from the day's *.notes.txt files."""
    out = {}
    for path in sorted(glob.glob(os.path.join(DATA_ROOT, DAY, 'P_*.notes.txt'))):
        stamp = os.path.basename(path).split('.')[0].split('_')[1]
        text = open(path, encoding='utf-8', errors='replace').read()
        m = NOTE_RE.search(text)
        if m:
            out[stamp] = (m.group(1), int(m.group(2)))
    return out


# Dosing valve index -> leak rate, mbar*L/s. The table the operator supplied for the
# 2026-09-04 experimental-tool test (that analysis section 1.1); the same valve was used
# here. Every point except index 300 was read off a curve, so the absolute scale may be
# shifted as a whole -- linearity and relative comparisons are unaffected.
VALVE_Q = {0: 0.0, 20: 5.0e-6, 25: 6.3e-6, 50: 2.0e-5, 75: 6.7e-5,
           100: 3.0e-4, 125: 7.2e-4, 150: 1.7e-3, 175: 4.2e-3, 200: 1.0e-2}

# Process gas flow, used to turn Q into airFrac. Plan section 16.1: process C runs 500 sccm.
# 1 sccm = 1.01325/60 mbar*L/s.
SCCM = 1.01325 / 60.0
FLOW_SCCM = {'C': 500.0, 'B': 500.0}


def air_frac(q, process):
    f = FLOW_SCCM.get(process)
    if not f:
        return None
    return q / (f * SCCM)


def recordings():
    """(stamp, path) for every full-spectrum recording of the day, in time order."""
    out = []
    for path in sorted(glob.glob(os.path.join(DATA_ROOT, DAY, 'P_OES1_*.csv'))):
        base = os.path.basename(path)
        if base.endswith('.summary.csv'):
            continue
        out.append((base[len('P_OES1_'):-len('.csv')], path))
    return out
