"""Step 2: the tables in §1, §2, §3.2 and §5 of the analysis.

    python3 02_summary.py
"""
import collections
import filecmp
import json
import os
import statistics as st

from common import (AUG_DAYS, CACHE, DATA_ROOT, DUPLICATE_FOLDER, LINES, SEPT_DAYS, WIN,
                    cp_labels, day_of, gate_open, median_spectrum, peak_height, read_recording)

cache = json.load(open(CACHE))


def cv(xs):
    return st.pstdev(xs) / st.mean(xs) * 100 if len(xs) > 1 else float('nan')


def section(title):
    print(f'\n=== {title}')


section('§1 duplicate folder check')
for k in sorted(x for x in cache if x.startswith(DUPLICATE_FOLDER)):
    twin = os.path.join(DATA_ROOT, '202609/10', os.path.basename(k))
    same = os.path.exists(twin) and filecmp.cmp(os.path.join(DATA_ROOT, k), twin, shallow=False)
    print(f'  {os.path.basename(k)}: byte-identical to 202609/10 copy = {same}')

section('§1 classes and frame rate per day')
for day in AUG_DAYS + SEPT_DAYS:
    vs = [v for k, v in cache.items() if day_of(k) == day]
    cls = collections.Counter(v['class'] if v else None for v in vs)
    fps = [v['frames'] / v['dur'] for v in vs if v and v.get('dur')]
    print(f'  {day}: {len(vs)} recordings  A/B/C = {cls["A"]}/{cls["B"]}/{cls["C"]}'
          f'  no-plasma = {cls[None]}  median fps = {st.median(fps):.2f}')
pre = [v['frames'] / v['dur'] for k, v in cache.items()
       if k.startswith('202609/10/') and v and v.get('dur') and k.split('_')[-1][4:8] < '1636']
post = [v['frames'] / v['dur'] for k, v in cache.items()
        if k.startswith('202609/10/') and v and v.get('dur') and k.split('_')[-1][4:8] >= '1636']
print(f'  §5.2 09-10 fps before 16:36 = {st.median(pre):.2f}, after = {st.median(post):.2f}')

section('§2.1 N2 337 / CO 330, normal C at the fixed sampling point')
labels = cp_labels(cache)
for day in AUG_DAYS + SEPT_DAYS:
    if day in AUG_DAYS:
        rows = [v['fixed'] for k, v in cache.items() if day_of(k) == day and v and v['class'] == 'C'
                and v['fixed'] and v['fixed']['Ar_750'] >= 40000]
    else:
        rows = [cache[k]['fixed'] for k, l in labels.items() if day_of(k) == day and l == 'C']
    r = [f['N2337_CO330'] for f in rows]
    print(f'  {day}: n={len(r):3d}  N2/CO = {st.mean(r):.5f}  CV {cv(r):.1f} %')

section("§4.1 normal C vs C' (September, final labels)")
for lab in ('C', "C'"):
    g = [cache[k]['fixed'] for k, l in labels.items() if l == lab]
    r = [f['N2337_CO330'] for f in g]
    a = [f['Ar_750'] for f in g]
    print(f"  {lab:2s} n={len(g)}  Ar750 {min(a)/1e3:.1f}-{max(a)/1e3:.1f} k"
          f"  N2/CO {st.mean(r):.5f} ({min(r):.4f}-{max(r):.4f})  CV {cv(r):.1f} %")
cm = st.mean(cache[k]['fixed']['N2337_CO330'] for k, l in labels.items() if l == 'C')
pm = st.mean(cache[k]['fixed']['N2337_CO330'] for k, l in labels.items() if l == "C'")
print(f"  C' vs C: {100 * (pm / cm - 1):+.1f} %")

section('§2.2 batch anchors (first C after a B longer than 140 s)')
for day in SEPT_DAYS:
    seq = sorted((k.split('_')[-1][:10], v) for k, v in cache.items() if day_of(k) == day and v and v['class'])
    anchors, after_long_b = [], False
    for stamp, v in seq:
        if v['class'] == 'B' and v['dur'] > 140:
            after_long_b = True
        elif v['class'] == 'C' and after_long_b and v['fixed']:
            anchors.append(f"{stamp[4:10]} {v['fixed']['N2337_CO330']:.5f}")
            after_long_b = False
    print(f'  {day}: ' + ', '.join(anchors))

section('§2.3 the 09-08 22:20 batch, C steps in order')
for k in sorted(cache):
    stamp = k.split('_')[-1][:10]
    v = cache[k]
    if k.startswith('202609/08/') and '0908222035' <= stamp <= '0908224332' and v['class'] == 'C':
        f = v['fixed']
        print(f"  {stamp[4:10]}  N2 {f['N2_337']:6.1f}  CO {f['CO_330']:8.1f}  Ar {f['Ar_750']:8.1f}"
              f"  N2/CO {f['N2337_CO330']:.5f}")

section('§3.2 recordings around the 09-03 21:01 re-capture')
for k in ('202609/03/P_OES1_0903210004.csv', '202609/03/P_OES1_0903210217.csv'):
    v = cache[k]
    print(f"  {os.path.basename(k)}  class {v['class']}  Ar750 {v['all_open']['Ar_750']:.1f}")

section('§5.1 cross-check against 09-04 §4.2 (74 August C steps)')
aug_c = [k for k, v in cache.items() if day_of(k) in AUG_DAYS and v and v['class'] == 'C']
r = [cache[k]['all_open']['N2337_CO330'] for k in aug_c]
n = [cache[k]['all_open']['N2_337'] for k in aug_c]
print(f'  per-frame, median:           n={len(r)} N2337 {st.mean(n):.1f} ({cv(n):.0f} %)'
      f'  N2/CO {st.mean(r):.5f} ({cv(r):.1f} %)')
n2, r2 = [], []
for k in aug_c:
    wl, frames = read_recording(os.path.join(DATA_ROOT, k))
    a, b = int(len(frames) * 0.2), int(len(frames) * 0.9)
    spec = median_spectrum(frames[a:b], lo=float('-inf'), hi=float('inf'))
    x = peak_height(wl, spec, *LINES['N2_337'])
    n2.append(x)
    r2.append(x / peak_height(wl, spec, *LINES['CO_330']))
print(f'  09-04 method (20-90 % spec): n={len(r2)} N2337 {st.mean(n2):.1f} ({cv(n2):.0f} %)'
      f'  N2/CO {st.mean(r2):.5f} ({cv(r2):.1f} %)')
print('  09-04 document §4.2:          n=74 N2337 467.1 (33 %)  N2/CO 0.02162 (7.0 %)')
