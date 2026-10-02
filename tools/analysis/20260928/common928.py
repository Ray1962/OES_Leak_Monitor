"""Shared pieces for the 2026-09-28 production-tool controlled-leak re-run.

Extraction geometry comes from the 2026-09-20 toolkit (itself pinned to the app's
LineIntensityExtractor through ../202609/common.py). Nothing here may redefine it.

What is new: the notes files carry only the dosing valve index ("DV=100"), not the
process, so the process is read off the spectrum instead.
"""
import glob
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(_HERE, '..', '20260920'))
sys.path.insert(0, os.path.join(_HERE, '..', '202609'))

from common920 import LINES, VALVE_Q, extract_all  # noqa: E402,F401
from common import (  # noqa: E402,F401
    DATA_ROOT, GATE, WIN, gate_open, median_spectrum, p99, peak_height, read_recording,
    tsec, win_mean,
)

DAY = '202609/28'
OUT_DIR = os.path.join(_HERE, 'out')
CACHE = os.path.join(OUT_DIR, 'cache.json')

# 15:52:58 connected in TEST MODE ("No OES hardware devices found"); its recording is the
# synthetic generator, 1000-point 200-799 nm axis. Never a measurement.
TEST_MODE = {'0928155258'}

NOTE_RE = re.compile(r'DV\s*=\s*(\d+)')


def read_notes():
    """{recording stamp: valve index} from the day's *.notes.txt files."""
    out = {}
    for path in sorted(glob.glob(os.path.join(DATA_ROOT, DAY, 'P_*.notes.txt'))):
        stamp = os.path.basename(path).split('.')[0].split('_')[1]
        m = NOTE_RE.search(open(path, encoding='utf-8', errors='replace').read())
        if m:
            out[stamp] = int(m.group(1))
    return out


def recordings():
    out = []
    for path in sorted(glob.glob(os.path.join(DATA_ROOT, DAY, 'P_OES1_*.csv'))):
        base = os.path.basename(path)
        if base.endswith('.summary.csv'):
            continue
        out.append((base[len('P_OES1_'):-len('.csv')], path))
    return out
