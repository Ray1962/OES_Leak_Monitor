#!/usr/bin/env python3
"""Build out/cache.json: every 2026-09-20 recording, extracted through the engine's geometry.

Two extractions per recording, because they answer different questions:

  fixed  -- the per-point median spectrum over gate-open + 10..30 s, then extracted.
            This is what BatchTracker samples and what the plan's cross-batch numbers mean.
  frames -- every gate-open frame extracted on its own, kept so that per-frame scatter
            (the sigma the live Warn threshold uses) can be measured rather than assumed.
"""
import json
import os
import sys

from common920 import (CACHE, GATE, OUT_DIR, WIN, extract_all, gate_open,
                       median_spectrum, p99, read_notes, read_recording, recordings)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    notes = read_notes()
    cache = {}
    for stamp, path in recordings():
        wl, frames = read_recording(path)
        rec = {
            'stamp': stamp,
            'process': notes.get(stamp, (None, None))[0],
            'valve': notes.get(stamp, (None, None))[1],
            'n_frames': len(frames),
            'duration': round(frames[-1][0], 2) if frames else 0.0,
            'axis': [wl[0], wl[-1], len(wl)],
        }
        op = gate_open(frames)
        rec['n_gate_open'] = len(op)
        rec['gate_duration'] = round(op[-1][0], 2) if op else 0.0
        # how far into the recording the gate opened (the pre-trigger head, plan PlasmaGate)
        rec['gate_start'] = round(next((t for t, y in frames if p99(y) > GATE), float('nan')), 2)

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
        lbl = f"{rec['process']}/{rec['valve']}" if rec['process'] else '-'
        print(f"{stamp}  {lbl:>6}  frames={len(frames):4d}  gate={len(op):4d}"
              f"  win={rec['fixed_n']:3d}  dur={rec['duration']:6.1f}s", flush=True)

    with open(CACHE, 'w') as fh:
        json.dump(cache, fh)
    print(f"\n{len(cache)} recordings -> {CACHE}")
    unlabelled = [k for k, v in cache.items() if not v['process']]
    print(f"{len(unlabelled)} without notes: {' '.join(unlabelled)}")


if __name__ == '__main__':
    sys.exit(main())
