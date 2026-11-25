"""11/24 生產錄檔(DV=0)的序列位置分析,並與 11/23 對照。

用法: python3 -I sequence_stats.py [11/24 資料夾] [11/23 資料夾]
      預設 /mnt/c/DualOES/202611/24 與 /mnt/c/DualOES/202611/23(11/23 只取 15:45:07 以後,同 ratio_stats.py)

譜線抽取、穩態幀選取與 Ar/無 Ar 判別都和 tools/analysis/20261123/ratio_stats.py 相同,數字可以直接互比。
離線 peak-height,不是引擎的 LineIntensityExtractor —— 上機前要用 Baseline Builder 重量。

序列 = 相鄰錄檔間隔不超過 240 s(與 Batch.BatchGapSeconds 相同)的一串錄檔;
位置 = 該類別(Ar / 無 Ar)在序列中的第幾個 step。
"""
import csv, glob, os, statistics as st, sys

D24 = sys.argv[1] if len(sys.argv) > 1 else "/mnt/c/DualOES/202611/24"
D23 = sys.argv[2] if len(sys.argv) > 2 else "/mnt/c/DualOES/202611/23"
GAP = 240.0

def idx(wl, w):
    return min(range(len(wl)), key=lambda i: abs(wl[i] - w))

def mean(xs):
    return sum(xs) / len(xs)

def peak(s, wl, c, hw=0.5, search=1.0, gl=1.0, bl=1.0, gr=1.0, br=1.0, left=True, right=True):
    """與 ratio_stats.py 相同:c±search 找峰、±hw 取最大值、扣兩側窗的連續光。"""
    i0, i1 = idx(wl, c - search), idx(wl, c + search)
    ic = max(range(i0, i1 + 1), key=lambda i: s[i])
    cc = wl[ic]
    pk = max(s[idx(wl, cc - hw):idx(wl, cc + hw) + 1])
    L = s[idx(wl, cc - hw - gl - bl):idx(wl, cc - hw - gl) + 1]
    R = s[idx(wl, cc + hw + gr):idx(wl, cc + hw + gr + br) + 1]
    sides = ([mean(L)] if left else []) + ([mean(R)] if right else [])
    return pk - mean(sides)

LINES = {
    "N2 337.1": dict(c=337.1, hw=0.5),
    "N2 380.5": dict(c=380.5, hw=0.5),
    "CN 388.3": dict(c=388.3, hw=0.6),
    "NO 237":   dict(c=237.8, hw=0.6),
    "Si 251.6": dict(c=251.6, hw=0.5),
    "Ha 656.3": dict(c=656.3, hw=0.6),
    "He 667.8": dict(c=667.8, hw=0.5),
    "Ar 750.4": dict(c=750.4, hw=0.5),
    "O 777.2":  dict(c=777.2, hw=0.7),
    "O 844.6":  dict(c=844.6, hw=0.6),
}
RATIOS = {
    "Ar":   [("N2 337.1", "Ar 750.4"), ("CN 388.3", "Ar 750.4"), ("NO 237", "Ar 750.4"),
             ("N2 380.5", "Ar 750.4"), ("Si 251.6", "Ar 750.4"), ("O 777.2", "Ar 750.4"),
             ("He 667.8", "Ar 750.4")],
    "noAr": [("N2 337.1", "O 777.2"), ("CN 388.3", "O 777.2"), ("NO 237", "O 777.2"),
             ("Si 251.6", "O 777.2"), ("Ha 656.3", "O 777.2"), ("O 844.6", "O 777.2")],
}

def secs(t):
    h, m, s = t.split(":"); return int(h) * 3600 + int(m) * 60 + float(s)

def load(d, first="000000"):
    files = sorted(f for f in glob.glob(os.path.join(d, "P_OES1_*.csv"))
                   if not f.endswith(".summary.csv") and os.path.basename(f)[11:17] >= first)
    out = []
    for f in files:
        rows = list(csv.reader(open(f, encoding="utf-8-sig")))
        wl = [float(x) for x in rows[0][1:]]
        fr = [(r[0], [float(v) for v in r[1:]]) for r in rows[1:] if len(r) > 10]
        tot = [sum(s) for _, s in fr]
        mx = max(tot)
        on = [(t, s) for (t, s), v in zip(fr, tot) if v > 0.6 * mx][2:-3]
        kind = "Ar" if peak(on[0][1], wl, **LINES["Ar 750.4"]) > 0.5 * peak(on[0][1], wl, **LINES["O 777.2"]) else "noAr"
        vals = {k: [peak(s, wl, **g) for _, s in on] for k, g in LINES.items()}
        out.append(dict(name=os.path.basename(f)[:-4], t0=on[0][0], t1=on[-1][0], n=len(on), kind=kind, v=vals))
    # 依間隔切序列,並標每類的位置
    seq, last, pos = 0, None, {}
    for r in out:
        if last is not None and secs(r["t0"]) - secs(last) > GAP:
            seq += 1; pos = {}
        pos[r["kind"]] = pos.get(r["kind"], 0) + 1
        r["seq"], r["pos"], last = seq, pos[r["kind"]], r["t1"]
    return out

def ratio(r, a, b):
    x = [p / q for p, q in zip(r["v"][a], r["v"][b]) if abs(q) > 50]
    return (mean(x), st.stdev(x) / abs(mean(x))) if len(x) >= 5 else (None, None)

def fmt(x):
    return "—" if x is None else f"{x:.4f}"

def table(rr, kind):
    print(f"| 位置 | 錄檔 | 穩態幀 | " + " | ".join(f"{a} / {b}" for a, b in RATIOS[kind]) + " |")
    print("|---" * (3 + len(RATIOS[kind])) + "|")
    for r in rr:
        print(f"| {r['pos']} | `{r['name'][-10:]}` | {r['n']} | " +
              " | ".join(fmt(ratio(r, a, b)[0]) for a, b in RATIOS[kind]) + " |")
    first, lastr = rr[0], rr[-1]
    print(f"| 1→末 | | | " + " | ".join(
        f"{(ratio(lastr, a, b)[0] / ratio(first, a, b)[0] - 1) * 100:+.0f} %" if ratio(first, a, b)[0] else "—"
        for a, b in RATIOS[kind]) + " |")

r24 = load(D24)
print("## 錄檔與序列")
print("| 序列 | 位置 | 類別 | 錄檔 | 穩態 | 幀數 | He 667.8 / Ar 750.4 |")
print("|---|---|---|---|---|---|---|")
for r in r24:
    he = ratio(r, "He 667.8", "Ar 750.4")[0] if r["kind"] == "Ar" else None
    print(f"| {r['seq']} | {r['pos']} | {r['kind']} | `{r['name']}` | {r['t0'][:8]}–{r['t1'][:8]} | {r['n']} | {fmt(he)} |")

seqs = sorted({r["seq"] for r in r24})
for s in seqs:
    for kind in ("noAr", "Ar"):
        rr = [r for r in r24 if r["seq"] == s and r["kind"] == kind]
        if not rr: continue
        print(f"\n## 序列 {s} / {kind}(平均比值)")
        table(rr, kind)

print("\n## 幀間 CV(各序列各位置的平均)")
for s in seqs:
    for kind in ("noAr", "Ar"):
        rr = [r for r in r24 if r["seq"] == s and r["kind"] == kind]
        if not rr: continue
        cv = lambda a, b: [c for c in (ratio(r, a, b)[1] for r in rr) if c is not None]
        print(f"- 序列 {s} / {kind}: " + ", ".join(
            f"{a}/{b} {mean(cv(a, b)) * 100:.1f} %" for a, b in RATIOS[kind][:4] if cv(a, b)))

# 序列 1、2 是同一配方的兩批:同位置互比
b1 = [r for r in r24 if r["seq"] == 1]; b2 = [r for r in r24 if r["seq"] == 2]
if len(b1) == len(b2) and b1:
    print("\n## 批次 1 → 批次 2,同位置差異")
    for x, y in zip(b1, b2):
        print(f"- {x['kind']} 位置 {x['pos']}: " + ", ".join(
            f"{a}/{b} {(ratio(y, a, b)[0] / ratio(x, a, b)[0] - 1) * 100:+.1f} %" for a, b in RATIOS[x["kind"]][:4]
            if ratio(x, a, b)[0]))

# 11/23(15:45:07 以後)對照 11/24 序列 0
if os.path.isdir(D23):
    r23 = [r for r in load(D23, "154507") if r["kind"] == "Ar"]
    a0 = [r for r in r24 if r["seq"] == 0 and r["kind"] == "Ar"]
    print("\n## 11/23 的 4 次 Ar run 對 11/24 序列 0 的 6 次 Ar run")
    for a, b in RATIOS["Ar"][:3]:
        print(f"- {a}/{b}: 11/23 " + " ".join(fmt(ratio(r, a, b)[0]) for r in r23) +
              " | 11/24 " + " ".join(fmt(ratio(r, a, b)[0]) for r in a0))
