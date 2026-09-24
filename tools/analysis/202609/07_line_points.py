"""Step 7: four lines at fixed time points in every B / C step of a day.

Ar 750.4, N2 337.1, NO 286, O I 777.2 read at gate-open + 10 s and + 30 s (process B)
or + 10 s and + 70 s (process C), one row per recording. 2026-08-19 is grouped by its
test conditions instead of A/B/C (see AUG19_GROUPS). A day compressed to DD.zip by
DataRetentionService is read straight out of the archive.

    python3 07_line_points.py                  # every day in DAYS
    python3 07_line_points.py 202609/03 ...    # just these

Writes out/lines_{yymmdd}_{group}.csv (B, C; O2, Pblk, P on 08-19) and out/lines_{yymmdd}.json per day, plus
out/artifact_data.json for the review page.

t = 0 is the plasma gate opening (p99 > GATE), the same zero BatchTracker's fixed sampling
point uses -- not the first row of the CSV, which carries the recorder's pre-trigger
back-fill and start-confirm head.

Two numbers per line, because they are not interchangeable:
  raw  -- mean over the +-0.5 nm nearest the centre. Includes the continuum pedestal.
  peak -- the app's PeakHeight: max over the signal window of (intensity - the straight
          line through the two side windows). Meaningful only where there is a peak.
NO 286 has no peak here: 286 nm is the valley between the 282.5 and 288.1 nm bands, and
its right-hand baseline window lands on 288.1, so the subtraction is meaningless. Its peak
column is written anyway and flagged, so nobody re-derives it and believes it.

Classification is recomputed here rather than read from out/cache.json, because 202609/20
is not in that cache. It is the same rule (common.classify_abc, and the C' rule of
common.cp_labels restated over this script's own rows); `--verify` checks the labels of
the three cached days against cp_labels and refuses to differ silently.
"""
import glob
import io
import json
import os
import re
import sys
import zipfile
from statistics import median

from common import (CACHE, DATA_ROOT, GATE, LINES, OUT_DIR, WIN, classify_abc, cp_labels,
                    gate_open, in_other_recipe_batch, p99, peak_height, read_recording, tsec,
                    win_mean)

DAYS = ['202608/19', '202609/03', '202609/08', '202609/10', '202609/20']

# label: (centre nm, half width, baseline gap, baseline width). Centres carry the
# +0.30 nm axis offset measured on this spectrometer (plan section 4.2).
TARGETS = {
    'Ar_750.4': (750.70, 1.06, 1.30, 1.10),
    'N2_337.1': (337.40, 0.70, 1.30, 1.10),
    'NO_286':   (286.30, 0.70, 1.30, 1.10),   # peak column not trustworthy, see above
    'O_777.2':  (777.60, 1.06, 1.30, 1.10),
}
NO_PEAK = 'NO_286'

# 10 s and 30 s (B) / 70 s (C) are the points asked for. 60 s is carried as well because
# the C step is not the same length on every recipe: 81-89 s on 09-03 (afternoon), 09-08 and
# 09-10, but 71 s on 09-20 and on 09-03's 21:00-21:25 batch, where the discharge ends at
# t ~ 64 s and the 70 s point falls in the afterglow. 60 s is on the plateau of both.
POINTS = {'B': (10.0, 30.0), 'C': (10.0, 60.0, 70.0)}

# 2026-08-19 is not the three-process production chamber and the A/B/C rule does not
# describe it: it is the leak-valve test day on a tool at 540 torr, 2.0 s per frame, whose
# recordings fall into three conditions the operator's own notes name. The first two ran
# with an acrylic plate over the viewport, which blocks 180-400 nm outright -- measured as
# 33-44 counts of mean 200-400 nm signal against 1060-2717 once it came off -- so N2 337
# and NO 286 are not present in them at all. Groups are those conditions, not A/B/C; the
# spectral class is still recorded beside them, and means nothing here.
# Each group takes the sampling points that fit its recordings: the O2 runs hold a 28-30 s
# discharge, so they get process B's 10 s / 30 s; the others hold 76-80 s and get process
# C's 10 s / 60 s / 70 s. No day mixes more than three points, which is also what keeps a
# chart's series inside the categorical palette's all-pairs cap.
AUG19 = '202608/19'
AUG19_GROUPS = [
    ('O2',    '0819000000', '0819165625', 'Dummy wafer + O2（壓克力板在，無氬）', POINTS['B']),
    ('P-blk', '0819165625', '0819173409', '空跑製程 P 540 torr（壓克力板在）', POINTS['C']),
    ('P',     '0819173409', '0819999999', '製程 P（17:34 拆除壓克力板之後）', POINTS['C']),
]
AUG19_POINTS = {k: pts for k, _, _, _, pts in AUG19_GROUPS}


def aug19_group(stamp):
    for key, lo, hi, _, _ in AUG19_GROUPS:
        if lo <= stamp < hi:
            return key
    return None
NEAR = 2.0          # half width of the robustness median, seconds
CP_FRACTION = 0.85  # the C' rule of common.cp_labels

# One representative step per (day, process) for the time-trace chart: the second full
# step of the day's longest batch, so it is neither the batch's first step nor its tail.
TRACE_PICK = {
    ('202609/03', 'B'): 'P_OES1_0903221045.csv', ('202609/03', 'C'): 'P_OES1_0903153619.csv',
    ('202609/08', 'B'): 'P_OES1_0908221610.csv', ('202609/08', 'C'): 'P_OES1_0908222349.csv',
    ('202609/10', 'B'): 'P_OES1_0910163236.csv', ('202609/10', 'C'): 'P_OES1_0910164015.csv',
    ('202609/20', 'B'): 'P_OES1_0920162319.csv', ('202609/20', 'C'): 'P_OES1_0920162537.csv',
    ('202608/19', 'O2'): 'P_OES1_0819163450.csv',
    ('202608/19', 'P-blk'): 'P_OES1_0819170225.csv',
    ('202608/19', 'P'): 'P_OES1_0819173611.csv',
}

NOTE_RE = re.compile(r'製程\s*([ABC]).*?index\s*=\s*(\d+)', re.S)

# The production B, on every one of 09-03 / 09-08 / 09-10: 154-155 s of gate-open plasma at
# H-alpha/O 0.139-0.141. A step the A/B/C rule calls B without that signature is flagged
# rather than dropped -- 09-03 22:14-22:16 (33 s, 0.093-0.108) and 09-20 15:48-16:07
# (33 s, 0.099) are steps of some other kind, and averaging them into B would be wrong.
B_MIN_OPEN_S = 60.0
B_MIN_HA_O = 0.12

# A sampling point can land after the discharge has already gone out: on the 33 s steps of
# 09-03 22:14-22:16 and 09-20 15:48-16:07 the nitrogen band collapses at t ~ 25 s while the
# gate stays open, because O 777's afterglow alone holds the 99th percentile above GATE.
# Such a point is marked rather than dropped -- the numbers are real, they just are not a
# measurement of the plasma.
# Half the step's own 10 s reading. Well clear of the real within-step decline, which is
# 10-15 % over 60 s on every recipe here, and it catches the 71 s C step's 70 s point
# (19 % of the 10 s value) that a 10 % rule let through.
TAIL_FRACTION = 0.50   # indicator peak below this share of the step's 10 s value

# Which line says "the discharge is still running". N2 337 wherever it is present, Ar 750
# where the viewport blocked the UV, and nothing at all for the O2 runs, whose only strong
# feature is O 777 -- which *rises* as the discharge dies, so it cannot serve. A recording
# with no indicator gets no flag rather than a wrong one.
INDICATOR_ORDER = ('N2_337.1', 'Ar_750.4')
INDICATOR_MIN = 50.0   # peak counts at the 10 s point for the line to count as present


def archive_of(day):
    """`{baseDir}/YYYYMM/DD.zip` if this day has been compressed, else None.

    DataRetentionService compresses a day folder in place and the app reads recordings
    straight back out of the archive (Recording.FromArchive); this does the same, so an
    archived day is not silently missing.
    """
    zpath = os.path.join(DATA_ROOT, day + '.zip')
    return zpath if not os.path.isdir(os.path.join(DATA_ROOT, day)) and os.path.exists(zpath) else None


def recordings_of(day):
    """[(basename, open_text_callable)] for every full-spectrum recording of `day`."""
    zpath = archive_of(day)
    if zpath:
        z = zipfile.ZipFile(zpath)
        names = sorted(n for n in z.namelist()
                       if os.path.basename(n).startswith('P_OES1_') and n.endswith('.csv')
                       and not n.endswith('.summary.csv'))
        return [(os.path.basename(n),
                 (lambda n=n: io.TextIOWrapper(z.open(n), encoding='utf-8', errors='replace')))
                for n in names]
    paths = sorted(p for p in glob.glob(os.path.join(DATA_ROOT, day, 'P_OES1_*.csv'))
                   if not p.endswith('.summary.csv'))
    return [(os.path.basename(p),
             (lambda p=p: open(p, encoding='utf-8', errors='replace'))) for p in paths]


def read_stream(fh):
    """common.read_recording over an already-open text stream (a zip entry has no path)."""
    with fh:
        wl = [float(x) for x in fh.readline().rstrip('\n').split(',')[1:] if x.strip()]
        frames, t0 = [], None
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
                t += 86400
            frames.append((t - t0, y))
    return wl, frames


def read_notes(day):
    """{recording stamp: (process, dosing valve index)} from a day's *.notes.txt files."""
    out = {}
    zpath = archive_of(day)
    if zpath:
        z = zipfile.ZipFile(zpath)
        items = [(os.path.basename(n), z.read(n).decode('utf-8', 'replace'))
                 for n in z.namelist() if os.path.basename(n).endswith('.notes.txt')]
    else:
        items = [(os.path.basename(p), open(p, encoding='utf-8', errors='replace').read())
                 for p in sorted(glob.glob(os.path.join(DATA_ROOT, day, 'P_*.notes.txt')))]
    for base, text in items:
        m = NOTE_RE.search(text)
        if m:
            out[base.split('.')[0].split('_')[1]] = (m.group(1), int(m.group(2)))
    return out


def at_time(wl, op, target):
    """Nearest gate-open frame to `target`, plus a +-NEAR s median of each line."""
    t, y = min(op, key=lambda f: abs(f[0] - target))
    if abs(t - target) > 5.0:
        return None
    row = {'t': round(t, 2), 'dt': round(t - target, 2)}
    for name, (c, half, gap, width) in TARGETS.items():
        row[name + '_raw'] = round(win_mean(wl, y, c - 0.5, c + 0.5), 1)
        row[name + '_peak'] = round(peak_height(wl, y, c, half, gap, width), 1)
    near = [f for f in op if abs(f[0] - target) <= NEAR]
    for name, (c, half, gap, width) in TARGETS.items():
        row[name + '_raw_med'] = round(median(win_mean(wl, ny, c - 0.5, c + 0.5) for _, ny in near), 1)
        row[name + '_peak_med'] = round(median(peak_height(wl, ny, c, half, gap, width) for _, ny in near), 1)
    row['n_med'] = len(near)
    return row


def trace(wl, op):
    """Every gate-open frame's four line values. Rounded to whole counts: this feeds a
    chart, and the fractional part doubles the page's size for nothing."""
    tr = {'t': [round(t, 1) for t, _ in op], 'raw': {}, 'peak': {}}
    for n, (c, h, g, w) in TARGETS.items():
        tr['raw'][n] = [round(win_mean(wl, y, c - 0.5, c + 0.5)) for _, y in op]
        tr['peak'][n] = [round(peak_height(wl, y, c, h, g, w)) for _, y in op]
    return tr


def scan_day(day):
    """Every recording of `day`: class, the two sampling points, and the trace if picked."""
    notes = read_notes(day)
    steps, traces = [], {}
    for base, opener in recordings_of(day):
        key = f'{day}/{base}'
        wl, frames = read_stream(opener())
        op = gate_open(frames) if frames else []
        if len(op) < 5:
            continue
        # gate_open() re-bases elapsed time to the first gate-open frame, so recover that
        # frame's offset from the recording's own start -- the head before it is the
        # recorder's pre-trigger back-fill and start-confirm, which is several seconds.
        gate_offset = next(t for t, y in frames if p99(y) > GATE)
        cls_rows = []
        for t, y in op:
            cls_rows.append({'t': t, **{k: peak_height(wl, y, *g) for k, g in LINES.items()}})
        ar_o = median(r['Ar_750'] / r['O_777'] for r in cls_rows if r['O_777'] > 0)
        ha_o = median(r['Ha_656'] / r['O_777'] for r in cls_rows if r['O_777'] > 0)
        fixed = [r for r in cls_rows if WIN[0] <= r['t'] <= WIN[1]]
        stamp = base.rsplit('_', 1)[-1].split('.')[0]
        rec = {'file': base, 'key': key, 'stamp': stamp,
               'clock': f'{stamp[4:6]}:{stamp[6:8]}:{stamp[8:10]}',
               'spectral': classify_abc(ar_o, ha_o),
               'alt': in_other_recipe_batch(key),
               'ar_o': round(ar_o, 4), 'ha_o': round(ha_o, 4),
               'open_s': round(op[-1][0], 1), 'frames': len(frames),
               'ar750_fixed': round(median(r['Ar_750'] for r in fixed), 1) if fixed else None,
               'gate_sod': round(tsec(f'{stamp[4:6]}:{stamp[6:8]}:{stamp[8:10]}') + gate_offset, 1),
               'note': notes.get(stamp)}
        steps.append((rec, wl, op))

    # The day's own notes are the authoritative label where they exist (202609/20 only);
    # the spectral class is kept beside them, because on 09-20 they disagree for every
    # single B step and that disagreement is the finding, not a nuisance.
    for rec, _, _ in steps:
        if day == AUG19:
            rec['cls'] = aug19_group(rec['stamp'])
        else:
            rec['cls'] = rec['note'][0] if rec['note'] else rec['spectral']
        rec['note_disagrees'] = (day != AUG19 and bool(rec['note'])
                                 and rec['note'][0] != rec['spectral'])
        rec['odd_b'] = (rec['cls'] == 'B'
                        and (rec['open_s'] < B_MIN_OPEN_S or rec['ha_o'] < B_MIN_HA_O))

    # C' -- the same rule as common.cp_labels, restated over this script's own rows.
    ref = None
    for rec, _, _ in steps:
        if rec['cls'] != 'C' or rec['alt'] or rec['ar750_fixed'] is None:
            continue
        if ref is not None and rec['ar750_fixed'] < CP_FRACTION * ref:
            rec['cls'] = "C'"
        else:
            ref = rec['ar750_fixed']

    out = []
    for rec, wl, op in steps:
        proc = rec['cls'].rstrip("'") if rec['cls'] else None
        pts = AUG19_POINTS.get(proc) if day == AUG19 else POINTS.get(proc)
        if not pts:
            continue
        rec['points'] = {str(int(t)): at_time(wl, op, t) for t in pts}
        ref = rec['points'].get('10')
        rec['indicator'] = next((n for n in INDICATOR_ORDER
                                 if ref and ref[n + '_peak'] > INDICATOR_MIN), None)
        for pt in rec['points'].values():
            if pt is not None:
                pt['tail'] = bool(rec['indicator'] and
                                  pt[rec['indicator'] + '_peak']
                                  < TAIL_FRACTION * ref[rec['indicator'] + '_peak'])
        rec['trace'] = trace(wl, op)
        if TRACE_PICK.get((day, rec['cls'])) == rec['file']:
            traces[rec['cls']] = {'file': rec['file'], **rec['trace']}
        out.append(rec)
    return out, traces


def verify(all_steps):
    """The three cached days must get the labels out/cache.json's cp_labels gives."""
    cache = json.load(open(CACHE))
    want = cp_labels(cache)
    bad = 0
    for day, steps in all_steps.items():
        for s in steps:
            if s['key'] not in cache or s['alt']:
                continue
            expect = want.get(s['key'], cache[s['key']]['class'] if cache[s['key']] else None)
            mine = s['cls'] if not s['note'] else s['spectral']
            if expect and expect != mine:
                print(f"  MISMATCH {s['key']}: here {mine}, cp_labels {expect}")
                bad += 1
    print(f'  verify: {bad} mismatch(es)')
    return bad


def groups_of(day, steps):
    """The groups a day is presented in, with the sampling points each one carries."""
    if day == AUG19:
        return [{'key': k, 'label': lab, 'points': [int(t) for t in pts]}
                for k, _, _, lab, pts in AUG19_GROUPS
                if any(s['cls'] == k for s in steps)]
    return [{'key': k, 'label': f'製程 {k}', 'points': [int(t) for t in POINTS[k]]}
            for k in ('B', 'C') if any(s['cls'].rstrip("'") == k for s in steps)]


def main():
    days = [a for a in sys.argv[1:] if not a.startswith('-')] or DAYS
    os.makedirs(OUT_DIR, exist_ok=True)
    cols = [f'{n}_{k}' for n in TARGETS for k in ('raw', 'peak', 'raw_med', 'peak_med')]
    art = {'targets': list(TARGETS), 'no_peak_untrustworthy': NO_PEAK, 'points': POINTS,
           'days': {}}
    all_steps = {}
    for day in days:
        steps, traces = scan_day(day)
        all_steps[day] = steps
        tag = day.replace('/', '')[2:]
        groups = groups_of(day, steps)
        print(f"{day}: {len(steps)} steps  "
              + '  '.join(f'{c}={sum(1 for s in steps if s["cls"] == c)}'
                          for c in [g['key'] for g in groups] + (["C'"] if day != AUG19 else []))
              + f"  note-disagrees={sum(1 for s in steps if s['note_disagrees'])}"
              + f"  odd-B={sum(1 for s in steps if s['odd_b'])}"
              + f"  after-plasma points={sum(1 for s in steps for p in s['points'].values() if p and p['tail'])}")
        for s in steps:
            miss = [t for t, p in s['points'].items() if p is None]
            if miss:
                print(f"    {s['file']} {s['cls']}: no frame at {'/'.join(miss)} s "
                      f"(gate open {s['open_s']} s)")
        json.dump({'day': day, 'steps': steps, 'traces': traces, 'groups': groups},
                  open(os.path.join(OUT_DIR, f'lines_{tag}.json'), 'w'), indent=1)
        for g in groups:
            rows = [s for s in steps if s['cls'].rstrip("'") == g['key']]
            if not rows:
                continue
            fname = f"lines_{tag}_{g['key'].replace('-', '')}.csv"
            with open(os.path.join(OUT_DIR, fname), 'w', encoding='utf-8') as fh:
                fh.write('file,group,spectral_class,clock,alt_recipe,note_process,valve_index,'
                         'odd_b,indicator,gate_open_s,point_s,frame_t_s,after_plasma,'
                         + ','.join(cols) + '\n')
                for s in rows:
                    for target, p in s['points'].items():
                        n = s['note'] or ('', '')
                        fh.write(f"{s['file']},{s['cls']},{s['spectral']},{s['clock']},"
                                 f"{int(s['alt'])},{n[0]},{n[1]},{int(s['odd_b'])},"
                                 f"{s['indicator'] or ''},{s['open_s']},{target},"
                                 f"{'' if p is None else p['t']},"
                                 f"{'' if p is None else int(p['tail'])},"
                                 + ','.join('' if p is None else str(p[c]) for c in cols) + '\n')
        art['days'][day] = {'steps': steps, 'traces': traces, 'groups': groups}
    json.dump(art, open(os.path.join(OUT_DIR, 'artifact_data.json'), 'w'), separators=(',', ':'))
    if '--verify' in sys.argv:
        verify(all_steps)
    print(f"out/artifact_data.json: {os.path.getsize(os.path.join(OUT_DIR, 'artifact_data.json'))} bytes")


if __name__ == '__main__':
    main()
