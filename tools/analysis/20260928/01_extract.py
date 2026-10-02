#!/usr/bin/env python3
"""Build out/cache.json: every 2026-09-28 recording through the engine's geometry.

Same two extractions as 2026-09-20: `fixed` (per-point median over gate-open + 10..30 s,
what BatchTracker samples) and `frames` (every gate-open frame on its own).
"""
import json
import os
import sys

from common928 import (CACHE, GATE, OUT_DIR, TEST_MODE, WIN, extract_all, gate_open,
                       median_spectrum, p99, read_notes, read_recording, recordings)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    notes = read_notes()
    cache = {}
    for stamp, path in recordings():
        wl, frames = read_recording(path)
        rec = {'stamp': stamp, 'valve': notes.get(stamp), 'test_mode': stamp in TEST_MODE,
               'n_frames': len(frames),
               'duration': round(frames[-1][0], 2) if frames else 0.0,
               'axis': [wl[0], wl[-1], len(wl)]}
        op = gate_open(frames)
        rec['n_gate_open'] = len(op)
        rec['gate_duration'] = round(op[-1][0], 2) if op else 0.0
        rec['fps'] = round((len(op) - 1) / op[-1][0], 3) if len(op) > 1 and op[-1][0] else None
        rec['frames'] = []
        for t, y in op:
            row = {'t': round(t, 3), 'p99': round(p99(y), 1)}
            row.update({k: round(v, 4) for k, v in extract_all(wl, y).items()})
            rec['frames'].append(row)
        med = median_spectrum(op, *WIN)
        rec['fixed'] = ({k: round(v, 4) for k, v in extract_all(wl, med).items()}
                        if med is not None else None)
        rec['fixed_n'] = sum(1 for t, _ in op if WIN[0] <= t <= WIN[1])
        cache[stamp] = rec
        f = rec['fixed'] or {}
        cls = (f"Ar/O={f['Ar_750'] / f['O_777']:.4f} Ha/O={f['Ha_656'] / f['O_777']:.4f}"
               if f else '')
        print(f"{stamp} DV={str(rec['valve']):>4} {'TEST' if rec['test_mode'] else '    '}"
              f" frames={len(frames):4d} gate={len(op):4d} gdur={rec['gate_duration']:6.1f}s"
              f" fps={rec['fps']} win={rec['fixed_n']:3d} axis={len(wl)}  {cls}", flush=True)
    with open(CACHE, 'w') as fh:
        json.dump(cache, fh)
    print(f"\n{len(cache)} recordings -> {CACHE}")


if __name__ == '__main__':
    sys.exit(main())
