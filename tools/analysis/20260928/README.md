# 2026-09-28 production-tool controlled-leak re-run

Scripts behind `docs/leak-test-20260928-analysis-zh-TW.md`. Pure Python 3, no dependencies.

```
python3 01_extract.py                 # builds out/cache.json; everything else reads it
python3 02_dose.py                    # per-dose indicator tables, both processes
python3 03_throughput.py              # line-by-line light level against 2026-09-20
python3 04_noise.py                   # per-frame noise, SNR, between-recording scatter
python3 05_search.py                  # per-pixel dose search (B), within-dose drift (A)
python3 06_n2_head.py                 # band-averaged N2 337 head, immune to the fixed pattern
python3 07_app_lines.py               # the day's final ratio set, app geometry, plasma off/on
python3 08_shape.py [--per-recording] # head-versus-sides excess: added N2 or a brighter plasma?
```

`10_head_origin.py` answers "A's 337 head rises with DV -- is that nitrogen?": the head against
CO 349, and a fit of an N2 (0,0)-shaped component. Same interpreter as 09 (numpy + matplotlib).

`11_n2_0920.py` asks the same of 2026-09-20 (is that day's "N2 337" air from another path?): the
337 feature split into a red-degraded and an N2-shaped part, the CO2+ 288.3/289.6 doublet it travels
with, and the missing N2 (0,1) head at 357.69. It also carries the UV axis offset (+0.10 nm, not +0.30).

`09_plot_n2.py` draws N2 337.1 per frame, one panel per (process, DV), into `out/n2_337_raw.png`
and `out/n2_337_head.png`. It needs matplotlib, which only the Windows interpreter has:

```
OES_DATA_ROOT='C:\DualOES' WSLENV=OES_DATA_ROOT python.exe 09_plot_n2.py
```

Recordings are read from `/mnt/c/DualOES` unless `OES_DATA_ROOT` says otherwise. `out/` is
git-ignored. Extraction geometry is imported from `../20260920/common920.py` and
`../202609/common.py` (pinned to the app's `LineIntensityExtractor`) -- do not restate it here.

The notes files carry only the valve index (`DV=`); the process is read off the spectrum
(`Ar750/O777 > 0.5` is the TEOS recipe the operator calls A). `0928155258` is a test-mode
recording (synthetic spectra) and is excluded everywhere.

The headline is a second null: nothing nitrogen-bearing responds to the valve in either
process. `05_search.py` settles it for B; for A, where dose and clock are the same variable,
`08_shape.py` is the argument.
