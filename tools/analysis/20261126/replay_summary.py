"""Replay 產生的比值 CSV(SIMP_Ratio_*.csv)逐 step 摘要(docs/production-20261126-leak-strategy-zh-TW.md §4)。

用法: python3 -I replay_summary.py <SIMP_Ratio.csv> [GR N2 357 mean] [GR N2 337 mean] [GR N2 337 σ]
      預設是 GR「recipe-20261126」的 0.01927、0.01319、0.000336。

每個 ProcessStep 一列:類別、幀數、OverallState 計數;Recipe step 另列
N2 357 / Ar 相對 GR 的 %(中位數)與 N2 337 / Ar(RawMean)相對 GR 的 σ(中位數)。
"""
import collections, csv, statistics as st, sys

path = sys.argv[1]
g357, g337, s337 = (float(x) for x in (sys.argv[2:5] if len(sys.argv) >= 5 else ("0.01927", "0.01319", "0.000336")))
with open(path, encoding="utf-8-sig") as f:
    rows = list(csv.reader(f))
h = rows[0]
col = lambda prefix: next(i for i, c in enumerate(h) if c.startswith(prefix) and c.endswith("]"))
i357, i337 = col("N2 357 / Ar 750"), col("N2 337 / Ar 750")
iS, iC, iP = h.index("OverallState"), h.index("ProcessClass"), h.index("ProcessStep")
steps = collections.OrderedDict()
for x in rows[1:]:
    steps.setdefault(x[iP], []).append(x)
first = {}
for x in rows[1:]:
    first.setdefault(x[iS], x[0])
for n, xs in steps.items():
    cls = sorted({x[iC] for x in xs if x[iC]})
    states = dict(collections.Counter(x[iS] for x in xs))
    a = [float(x[i357]) for x in xs if x[iC] == "Recipe" and x[i357]]
    b = [float(x[i337]) for x in xs if x[iC] == "Recipe" and x[i337]]
    extra = f" N2 357 {100 * (st.median(a) / g357 - 1):+4.0f}%  N2 337 {(st.median(b) - g337) / s337:+5.1f}σ" if a and b else ""
    print(f"step {n:>3} {xs[0][0][11:19]} n={len(xs):3d} {'/'.join(cls):8s}{extra}  {states}")
print("每個 OverallState 第一次出現:", first)
