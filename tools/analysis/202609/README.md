# 2026-09 production-data analysis

Scripts behind `docs/production-data-202609-analysis-zh-TW.md`. Pure Python 3, no dependencies.

```
python3 01_extract.py        # builds out/cache.json (~15 s); everything else reads it
python3 02_summary.py        # §1, §2, §3.2, §4.1, §5
python3 03_search.py         # §4.3
python3 04_rule_check.py     # §4.4
python3 05_decision_frame.py # §4.5
python3 06_site_state.py     # §1, §3
python3 07_line_points.py    # four lines at fixed time points, 08-19 and 09-03/08/10/20 (standalone)
python3 08_o777_baseline.py  # O 777.2 as a baseline in B / C; N2/O and NO/O in B (reads the cache)
```

`07_line_points.py` answers a different question from the rest and is not behind the
analysis document: for every B and C step of a day, what are Ar 750.4 / N2 337.1 / NO 286 /
O 777.2 at a fixed point in the step. It recomputes the A/B/C and C' labels rather than
reading `out/cache.json`, because `202609/20` is not in that cache; `--verify` checks the
labels it produces against `cp_labels` for the three days that are, and prints any
mismatch. Where a day carries its own `*.notes.txt` (09-20: process + dosing valve index)
those are the authoritative label and the spectral class is kept beside them -- on 09-20
they disagree for every B step. 08-19 is a different tool (the leak-valve test day) and is grouped
by the operator's three test conditions rather than A/B/C; an archived day (`DD.zip`) is read
from the archive.

`08_o777_baseline.py` asks whether O I 777.2 can carry a Golden Run in process B and C, and
whether N2 337.1 / O 777.2 and NO 237 / O 777.2 are usable leak indicators in B. It reports
three spreads per quantity -- frame m/s (the live EWMA), pooled m/s (what
`MinBaselineMeanToSigma` is applied to) and step CV (what Batch Trend reads) -- because the
analysis documents have already once confused them. B steps under 60 s of gate-open (09-03
22:14-22:16) and C steps too short for the late window are listed, not scored.

Recordings are read from `/mnt/c/DualOES` unless `OES_DATA_ROOT` says otherwise. `out/` is
git-ignored. The extraction in `common.py` must stay identical to the app's
`LineIntensityExtractor`, or the numbers stop transferring to a `settings.json`.
