"""11/24 引擎逐幀輸出(P_Ratio_1124000000.csv)的 step 位置分析,涵蓋 00:00–17:30。

用法: python3 -I engine_positions.py <P_Ratio_1124000000.csv>
      (檔案在 diag_DESKTOP-4JD00UD_cc03_20261124_173001.zip 的 data/ 裡)

這是 App 自己的 LineIntensityExtractor 算出來的值(工具上的設定:N2 337 / Ar750 PeakHeight、
NO 237 / Ar750 RawMean、N2 357 / Ar750 PeakHeight),所以可以直接拿來定門檻;
但這份設定沒有 CN,也不記 Ar 或 He 的強度。無 Ar 的 step 在這份檔案裡是 Idle(Ar 被閘門擋掉),
看不到,所以只分析有 Ar 的 step,種類用電漿時間分:
  B = 約 98 s(含 He 的配方)
  A = 約 82 s
  S = 約 33 s(只在 11:39–11:45 出現)
序列 = 相鄰 Ar step 間隔 > 240 s 就切(同 Batch.BatchGapSeconds)。
「完整序列」= 第一個 Ar step 的 NO/Ar > 0.04,也就是前面跑過完整的無 Ar 沉積段(錄檔核對過 00:00–04:44)。
"""
import csv, statistics as st, sys
from datetime import datetime

COL_N2, COL_NO = 1, 3
GAP = 240.0

def ts(s):
    return datetime.strptime(s, "%Y-%m-%d %H:%M:%S.%f").timestamp()

rows = csv.reader(open(sys.argv[1], encoding="utf-8-sig"))
next(rows)
segs, cur, last = [], None, None
for r in rows:
    if len(r) < 14:
        continue
    t = ts(r[0])
    if r[13] != "Normal":          # OverallState:Normal = 閘門開(有電漿)
        continue
    if cur is None or t - last > 3:
        cur = dict(t0=r[0][11:19], a=t, n2=[], no=[], n=0); segs.append(cur)
    cur["n"] += 1; cur["b"] = t; last = t
    if r[COL_N2]: cur["n2"].append(float(r[COL_N2]))
    if r[COL_NO]: cur["no"].append(float(r[COL_NO]))

steps = []
for s in segs:
    dur = s["b"] - s["a"]
    if dur < 15:
        continue
    core = lambda v: v[5:-5] if len(v) > 15 else v
    n2 = st.median(core(s["n2"])) if s["n2"] else None
    no = st.median(core(s["no"])) if len(s["no"]) > 10 else None
    if no is None:
        continue
    kind = "B" if dur > 90 else "A" if dur > 60 else "S"
    steps.append(dict(t0=s["t0"], a=s["a"], b=s["b"], dur=dur, kind=kind, n2=n2, no=no))

seqs, prev = [], None
for s in steps:
    if prev is None or s["a"] - prev["b"] > GAP:
        seqs.append([])
    seqs[-1].append(s); prev = s

def sig(q):
    return "".join(s["kind"] for s in q)

def full(q):
    return q[0]["no"] > 0.04

print("## 每個序列(括號內是 N2/Ar、NO/Ar 的中位數;* = 完整序列)\n")
for i, q in enumerate(seqs):
    ar = q
    print(f"- {i:2d} {q[0]['t0']} `{sig(q)}`{'*' if full(q) else ''}: " +
          " ".join(f"{s['kind']}({s['n2']:.4f}, {s['no']:.4f})" for s in ar))

# 同結構、以 D 開頭的序列:同位置互比
def table(pattern, label):
    group = [q for q in seqs if sig(q) == pattern and full(q)]
    if len(group) < 2:
        return
    print(f"\n## `{pattern}` 共 {len(group)} 批({label}):" + "、".join(q[0]["t0"][:5] for q in group))
    print("\n| 位置 | 種類 | N2/Ar 平均 | N2/Ar 批間 CV | NO/Ar 平均 | NO/Ar 批間 CV | NO/Ar 對前一步 |")
    print("|---|---|---|---|---|---|---|")
    cols = list(zip(*group))
    prevno = None
    for k, c in enumerate(cols, 1):
        n2 = [s["n2"] for s in c]; no = [s["no"] for s in c]
        m2, m0 = st.mean(n2), st.mean(no)
        dec = f"×{m0 / prevno:.3f}" if prevno else "—"
        print(f"| {k} | {c[0]['kind']} | {m2:.4f} | {st.stdev(n2) / m2 * 100:.1f} % | {m0:.5f} | "
              f"{st.stdev(no) / m0 * 100:.1f} % | {dec} |")
        prevno = m0

table("A" * 9, "9 個 A")
table("B" * 7, "7 個 B")
table("A" * 6 + "B" * 2, "6 個 A + 2 個 B")

# 參考曲線:D + 9 A 的每個位置平均。每個 Ar step 對「同位置平均」的偏差
ref = {}
for pattern in ("A" * 9, "B" * 7):
    group = [q for q in seqs if sig(q) == pattern and full(q)]
    cols = list(zip(*group))
    ref[pattern[1]] = [(st.mean(s["n2"] for s in c), st.mean(s["no"] for s in c)) for c in cols]

print("\n## N2/Ar 偏高的 step(完整序列,對同種類同位置的參考值,N2 > +15 %)\n")
print("| 時間 | 序列結構 | 位置 | 種類 | N2/Ar 偏差 | NO/Ar 偏差 | 是序列/同種類的最後一步 |")
print("|---|---|---|---|---|---|---|")
cnt = tot = 0
for q in seqs:
    if not full(q):
        continue
    ar = q
    for k, s in enumerate(ar):
        # 位置 = 序列裡第幾個 Ar step(A、B 混合的序列裡,B 接在 A 後面時位置照算)
        r = ref.get(s["kind"])
        if not r or k >= len(r):
            continue
        tot += 1
        d2 = s["n2"] / r[k][0] - 1; d0 = s["no"] / r[k][1] - 1
        if d2 > 0.15:
            cnt += 1
            lastof = "是" if k == len(ar) - 1 or ar[k + 1]["kind"] != s["kind"] else "否"
            print(f"| {s['t0']} | `{sig(q)}` | {k + 1} | {s['kind']} | {d2 * 100:+.0f} % | {d0 * 100:+.0f} % | {lastof} |")
print(f"\n共 {cnt} / {tot} 個 step。")
