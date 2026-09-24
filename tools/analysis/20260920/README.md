# 2026-09-20 production-tool controlled-leak test

Scripts behind `docs/leak-test-20260920-analysis-zh-TW.md`. Pure Python 3, no dependencies.

```
python3 01_extract.py   # builds out/cache.json (~4 s); everything else reads it
python3 02_dose.py      # section 3 -- the dose curves for process C and B
python3 03_search.py    # section 4 -- whole-spectrum search for an index-correlated feature
python3 04_stats.py     # sections 2, 6, 7.1 -- scatter, the bound on the delivered leak,
                        #                       classifier headroom, viewport fouling
```

Recordings are read from `/mnt/c/DualOES` unless `OES_DATA_ROOT` says otherwise. `out/` is
git-ignored. The extraction geometry is imported from `../202609/common.py`, which is pinned
to the app's `LineIntensityExtractor` -- do not restate it here, or the numbers stop
transferring to a `settings.json`.

The headline result is a null: no feature anywhere in the spectrum responded to the dosing
valve, so the leak was not delivered. `03_search.py` is the script that establishes it.
