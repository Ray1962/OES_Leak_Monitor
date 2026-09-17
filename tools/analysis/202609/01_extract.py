"""Step 1: per-recording extraction cache (everything else reads out/cache.json).

For each full-spectrum recording: gate every frame, classify the step A/B/C from the
gate-open frames, and store line intensities as medians over (a) the fixed sampling point
and (b) all gate-open frames. Re-running skips recordings already in the cache.

    python3 01_extract.py
"""
import glob
import json
import os
from statistics import median

from common import (AUG_DAYS, CACHE, DATA_ROOT, DUPLICATE_FOLDER, LINES, OUT_DIR, SEPT_DAYS,
                    WIN, classify_abc, gate_open, peak_height, read_recording)

RATIOS = {
    'N2337_CO330': ('N2_337', 'CO_330'),
    'N2337_Ar750': ('N2_337', 'Ar_750'),
    'CO608_Ar811': ('CO_608', 'Ar_811'),
    'Ar750_O777':  ('Ar_750', 'O_777'),
}


def stats(rows):
    if len(rows) < 3:
        return None
    out = {k: median(r[k] for r in rows) for k in LINES}
    for name, (n, d) in RATIOS.items():
        vals = [r[n] / r[d] for r in rows if r[d] > 0]
        out[name] = median(vals) if vals else float('nan')
    out['n'] = len(rows)
    return out


def process(path):
    wl, frames = read_recording(path)
    if not frames:
        return None
    op = gate_open(frames)
    base = {'file': os.path.basename(path), 'frames': len(frames), 'open': len(op),
            'dur': frames[-1][0]}
    if len(op) < 5:
        return {**base, 'class': None}
    rows = []
    for t, y in op:
        rec = {'t': t}
        for k, geo in LINES.items():
            rec[k] = peak_height(wl, y, *geo)
        rows.append(rec)
    ar_o = median(r['Ar_750'] / r['O_777'] for r in rows if r['O_777'] > 0)
    ha_o = median(r['Ha_656'] / r['O_777'] for r in rows if r['O_777'] > 0)
    fixed = [r for r in rows if WIN[0] <= r['t'] <= WIN[1]]
    return {**base, 'class': classify_abc(ar_o, ha_o), 'ar_o': ar_o, 'ha_o': ha_o,
            'fixed': stats(fixed), 'all_open': stats(rows)}


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    cache = json.load(open(CACHE)) if os.path.exists(CACHE) else {}
    paths = []
    for day in AUG_DAYS + SEPT_DAYS + (DUPLICATE_FOLDER,):
        paths += sorted(p for p in glob.glob(os.path.join(DATA_ROOT, day, 'P_OES1_*.csv'))
                        if not p.endswith('.summary.csv'))
    todo = [p for p in paths if os.path.relpath(p, DATA_ROOT).replace(os.sep, '/') not in cache]
    print(f'{len(paths)} recordings, {len(todo)} to extract')
    for i, p in enumerate(todo, 1):
        cache[os.path.relpath(p, DATA_ROOT).replace(os.sep, '/')] = process(p)
        if i % 25 == 0:
            json.dump(cache, open(CACHE, 'w'))
            print(f'  {i}/{len(todo)}')
    json.dump(cache, open(CACHE, 'w'))
    print(f'cache: {CACHE} ({len(cache)} recordings)')


if __name__ == '__main__':
    main()
