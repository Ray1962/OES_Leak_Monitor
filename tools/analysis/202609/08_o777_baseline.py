"""Step 8: can O I 777.2 carry a baseline in process B and C, and do N2/O and NO/O work in B?

    python3 08_o777_baseline.py                  # 09-03, 09-08, 09-10 (SEPT_DAYS)
    python3 08_o777_baseline.py 202609/08 ...    # just these

Reads the step classes from out/cache.json (run 01_extract.py first) and the C / C' labels
from common.cp_labels, then re-reads every B and C recording for per-frame numbers, since
the cache only holds one median per step. Writes out/o777_steps.csv, one row per step.

Three different spreads are reported, because they answer different questions and the
analysis documents have already once confused them (leak-test-20260920 section 6.2):
  frame m/s  -- mean/sigma of the frames inside one step's window. What the live EWMA sees.
  pooled m/s -- mean/sigma of all frames of all steps pooled. What MinBaselineMeanToSigma (10)
                is applied to by FinalizeCapture / BaselineBuilder.
  step CV    -- spread of the per-step window medians. What Batch Trend reads.

Window is gate-open + 10..30 s (common.WIN). Drift compares that window with a late one:
C 60..70 s (the step is 81-89 s here), B 120..140 s (155 s). Within-batch drift is the
first normal C of a batch against the last, which is the viewport fouling.

SNR follows LineIntensityExtractor: peak height over the pooled scatter of the two
baseline windows about their own means. MinSnr is 5; below that a ratio reads LowSignal.

B steps shorter than B_MIN_OPEN_S are left out and listed: 09-03 22:14-22:16 carries three
33 s steps the A/B/C rule calls B that are not the production B (see 07_line_points.py).
"""
import csv
import json
import os
import sys
from statistics import mean, median, pstdev

from common import (CACHE, DATA_ROOT, LINES, OUT_DIR, SEPT_DAYS, WIN, cp_labels, day_of,
                    gate_open, in_other_recipe_batch, peak_height, read_recording)

L = {
    'O777':  LINES['O_777'],
    'Ar750': LINES['Ar_750'],
    'N2337': LINES['N2_337'],
    'CO330': LINES['CO_330'],
    'NO237': LINES['NO_237'],
}
# Ratios of interest per process. C: O777 against the other near-IR line (fouling cancels).
# B: the two nitrogen candidates, over O777 and over the current denominator CO 330, plus NO.
RATIOS = {
    'B': [('N2337', 'O777'), ('N2337', 'CO330'), ('NO237', 'O777')],
    'C': [('O777', 'Ar750'), ('N2337', 'O777'), ('N2337', 'CO330')],
}
LATE = {'B': (120.0, 140.0), 'C': (60.0, 70.0)}
B_MIN_OPEN_S = 60.0
BATCH_GAP_S = 240.0    # BatchTracker's default gap
MIN_SNR = 5.0


def pooled_noise(wl, y, c, half, gap, width):
    """LineIntensityExtractor.PooledNoise: baseline-window scatter about each window's own mean."""
    lo, hi = c - half, c + half
    import bisect

    def seg(a, b):
        return y[bisect.bisect_left(wl, a):bisect.bisect_right(wl, b)]
    left, right = seg(lo - gap - width, lo - gap), seg(hi + gap, hi + gap + width)
    if len(left) + len(right) < 3:
        return float('nan')
    ss = sum((v - mean(left)) ** 2 for v in left) + sum((v - mean(right)) ** 2 for v in right)
    return (ss / (len(left) + len(right) - 2)) ** 0.5


def ms(xs):
    sd = pstdev(xs)
    return mean(xs) / sd if sd > 0 else float('inf')


def cv(xs):
    return pstdev(xs) / mean(xs) * 100


def stamp_seconds(key):
    s = key.rsplit('_', 1)[-1][:10]          # MMddHHmmss
    return int(s[4:6]) * 3600 + int(s[6:8]) * 60 + int(s[8:10])


def scan_step(key, cls):
    wl, frames = read_recording(os.path.join(DATA_ROOT, key))
    op = gate_open(frames)
    lo, hi = LATE[cls]
    early = [y for t, y in op if WIN[0] <= t <= WIN[1]]
    late = [y for t, y in op if lo <= t <= hi]
    if not early or not late:
        return dict(open_s=op[-1][0] if op else 0.0, ok=False)
    v = {n: [peak_height(wl, y, *g) for y in early] for n, g in L.items()}
    vl = {n: [peak_height(wl, y, *g) for y in late] for n, g in L.items()}
    snr = {n: median(peak_height(wl, y, *g) / pooled_noise(wl, y, *g) for y in early)
           for n, g in L.items()}
    ratios = {f'{a}/{b}': [x / y for x, y in zip(v[a], v[b])] for a, b in RATIOS[cls]}
    late_r = {f'{a}/{b}': median(x / y for x, y in zip(vl[a], vl[b])) for a, b in RATIOS[cls]}
    return dict(open_s=op[-1][0], ok=True, v=v, snr=snr, ratios=ratios,
                drift={**{n: median(vl[n]) / median(v[n]) - 1 for n in L},
                       **{k: late_r[k] / median(r) - 1 for k, r in ratios.items()}})


def main():
    days = [a for a in sys.argv[1:] if not a.startswith('-')] or list(SEPT_DAYS)
    cache = json.load(open(CACHE))
    cp = cp_labels(cache)
    steps, skipped = [], []
    for day in days:
        keys = sorted(k for k, v in cache.items()
                      if day_of(k) == day and v and v['class'] and not in_other_recipe_batch(k))
        batch, prev_end = 0, None
        for k in keys:
            v = cache[k]
            t0 = stamp_seconds(k)
            if prev_end is None or t0 - prev_end > BATCH_GAP_S:
                batch += 1
            prev_end = t0 + v['dur']
            if v['class'] not in ('B', 'C'):
                continue
            label = cp.get(k, 'C') if v['class'] == 'C' else 'B'
            s = scan_step(k, v['class'])
            if not s['ok'] or (label == 'B' and s['open_s'] < B_MIN_OPEN_S):
                skipped.append((k, label, s['open_s']))
                continue
            s.update(key=k, day=day, batch=batch, label=label, cls=v['class'])
            steps.append(s)
            print(f"  {k.rsplit('/', 1)[-1]}  batch {batch}  {label:2}  O777 "
                  f"{median(s['v']['O777']):7.0f}  frame m/s {ms(s['v']['O777']):6.1f}", flush=True)

    write_csv(steps)
    for day in days + ['all']:
        report(day, [s for s in steps if day == 'all' or s['day'] == day])
    if skipped:
        print('\nLeft out (short B or no window):')
        for k, lab, t in skipped:
            print(f'  {k}  {lab}  gate-open {t:.0f} s')
        print('  (a step shorter than the late window has no drift reading and is not scored)')


def report(day, steps):
    print(f'\n===== {day} =====')
    for label in ('B', 'C', "C'"):
        ss = [s for s in steps if s['label'] == label]
        if not ss:
            continue
        cls = ss[0]['cls']
        print(f'-- {label}: {len(ss)} steps')
        for name in ['O777'] + list(ss[0]['ratios']):
            series = [s['v'][name] if name in s['v'] else s['ratios'][name] for s in ss]
            meds = [median(x) for x in series]
            frame = [ms(x) for x in series]
            pooled = ms([x for xs in series for x in xs])
            drift = [s['drift'][name] * 100 for s in ss]
            line = (f'   {name:12} median {min(meds):.5g}-{max(meds):.5g}  frame m/s {min(frame):6.1f}-{max(frame):6.1f}'
                    f'  pooled m/s {pooled:6.1f}  step CV {cv(meds) if len(meds) > 1 else 0:5.2f}%'
                    f'  drift {LATE[cls][0]:.0f}s {min(drift):+.1f}..{max(drift):+.1f}%')
            print(line)
        for n in ('N2337', 'NO237') if cls == 'B' else ():
            snr = [s['snr'][n] for s in ss]
            print(f'   SNR {n}: {min(snr):.1f}-{max(snr):.1f}  (MinSnr {MIN_SNR:g} -> '
                  f"{'passes' if min(snr) >= MIN_SNR else 'LowSignal'})")
        if label == 'C':
            by_batch = {}
            for s in ss:
                by_batch.setdefault((s['day'], s['batch']), []).append(s)
            falls = [(median(b[-1]['v']['O777']) / median(b[0]['v']['O777']) - 1) * 100
                     for b in by_batch.values() if len(b) >= 3]
            firsts = [median(b[0]['v']['O777']) for b in by_batch.values() if len(b) >= 3]
            if falls:
                print(f'   within-batch O777 first->last C: {min(falls):+.1f}..{max(falls):+.1f}%'
                      f' ({len(falls)} batches); first-C CV {cv(firsts) if len(firsts) > 1 else 0:.2f}%')


def write_csv(steps):
    os.makedirs(OUT_DIR, exist_ok=True)
    path = os.path.join(OUT_DIR, 'o777_steps.csv')
    names = list(L) + sorted({r for s in steps for r in s['ratios']})
    with open(path, 'w', newline='') as fh:
        w = csv.writer(fh)
        w.writerow(['file', 'day', 'batch', 'label', 'gate_open_s']
                   + [f'{n}_{q}' for n in names for q in ('median', 'frame_ms', 'drift_pct')]
                   + [f'{n}_snr' for n in L])
        for s in steps:
            row = [s['key'].rsplit('/', 1)[-1], s['day'], s['batch'], s['label'], f"{s['open_s']:.1f}"]
            for n in names:
                xs = s['v'].get(n) or s['ratios'].get(n)
                row += ([f'{median(xs):.6g}', f'{ms(xs):.1f}', f"{s['drift'][n] * 100:.2f}"]
                        if xs else ['', '', ''])
            row += [f"{s['snr'][n]:.1f}" for n in L]
            w.writerow(row)
    print(f'\nwrote {path}')


if __name__ == '__main__':
    main()
