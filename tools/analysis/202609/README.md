# 2026-09 production-data analysis

Scripts behind `docs/production-data-202609-analysis-zh-TW.md`. Pure Python 3, no dependencies.

```
python3 01_extract.py        # builds out/cache.json (~15 s); everything else reads it
python3 02_summary.py        # §1, §2, §3.2, §4.1, §5
python3 03_search.py         # §4.3
python3 04_rule_check.py     # §4.4
python3 05_decision_frame.py # §4.5
python3 06_site_state.py     # §1, §3
```

Recordings are read from `/mnt/c/DualOES` unless `OES_DATA_ROOT` says otherwise. `out/` is
git-ignored. The extraction in `common.py` must stay identical to the app's
`LineIntensityExtractor`, or the numbers stop transferring to a `settings.json`.
