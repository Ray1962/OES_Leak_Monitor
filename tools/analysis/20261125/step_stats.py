"""11/25 新機台(precoat SiH4/N2/N2O + recipe C2H2/He/Ar)錄檔的逐幀、逐 step 統計。

用法: python3 -I step_stats.py [資料夾]      預設 /mnt/c/DualOES/202611/25

離線 peak-height:中心 ±hw 取最大值,扣兩側 hw..hw+1.5 nm 的中位數。
不是引擎的 LineIntensityExtractor —— 絕對值不同,同一套抽法之內的相對變化才可比;上機前用 Baseline Builder 重量。
線中心已含這台光譜儀的偏移(Ar 750.39 讀在 750.8–750.9;N2 337.13 的帶峰讀在 336.3–336.7)。

穩態幀 = Ar 750 > 0.8 × 該 step 最大值,且 N2 337 < 150(排除每個 step 頭尾含 N2/N2O 的轉換幀)。
"""
import glob, os, statistics as st, sys
from datetime import datetime

D = sys.argv[1] if len(sys.argv) > 1 else "/mnt/c/DualOES/202611/25"

LINES = {  # 名稱: (讀值中心 nm, 半寬 nm)
    "Ar750": (750.8, 1.0), "He706": (706.8, 1.0), "Ha": (656.4, 1.0), "C2": (516.3, 1.0),
    "CN388": (388.4, 0.8), "N2_337": (336.7, 1.0), "OH309": (309.2, 1.0), "O777": (777.4, 1.0),
    "NO237": (236.0, 1.5),
}

def load(path):
    with open(path, encoding="utf-8-sig") as f:
        wl = [float(x) for x in f.readline().strip().split(",")[1:]]
        t, rows = [], []
        for line in f:
            p = line.rstrip().split(",")
            if len(p) != len(wl) + 1:
                continue
            t.append((datetime.strptime(p[0], "%H:%M:%S.%f") - datetime(1900, 1, 1)).total_seconds())
            rows.append([float(x) for x in p[1:]])
    return wl, t, rows

def peak(wl, s, c, hw, bg=1.5):
    core = [s[i] for i, w in enumerate(wl) if c - hw < w < c + hw]
    fl = [s[i] for i, w in enumerate(wl) if c - hw - bg < w <= c - hw or c + hw <= w < c + hw + bg]
    return max(core) - st.median(fl)

def cv(xs):
    return 100 * st.stdev(xs) / st.mean(xs)

files = sorted(p for p in glob.glob(os.path.join(D, "P_OES1_*.csv")) if not p.endswith("summary.csv"))
print("== 每筆錄檔:幀數、幀距、穩態中位數(peak-height, counts)")
steps = []
for p in files:
    name = os.path.basename(p)[7:17]
    wl, t, X = load(p)
    P = [{k: peak(wl, s, *LINES[k]) for k in LINES} for s in X]
    dt = st.median([b - a for a, b in zip(t, t[1:])]) if len(t) > 1 else 0
    amax = max(f["Ar750"] for f in P)
    nmax = max(f["N2_337"] for f in P)
    kind = "recipe" if amax > 15000 else ("precoat" if nmax > 2000 else ("recipe-weak" if amax > 2000 else "dark"))
    ok = [f for f in P if f["Ar750"] > 0.8 * amax and f["N2_337"] < 150] if kind == "recipe" else \
         [f for f in P if f["N2_337"] > 0.9 * nmax] if kind == "precoat" else P
    m = {k: st.mean(f[k] for f in ok) for k in LINES}
    print(f"{name} {kind:11s} n={len(t):3d} dt={dt:4.2f}s steady={len(ok):3d} | " +
          " ".join(f"{k}={m[k]:.0f}" for k in LINES))
    if kind == "recipe":
        steps.append((name, len(t), m, ok, P))

print("\n== recipe step 的比值(穩態幀平均),依 precoat 之後的位置")
pairs = [("CN388", "Ar750"), ("CN388", "Ha"), ("N2_337", "Ar750"), ("OH309", "Ar750"),
         ("C2", "Ar750"), ("Ha", "Ar750"), ("He706", "Ar750")]
for a, b in pairs:
    r = [s[2][a] / s[2][b] for s in steps]
    diffs = [(y - x) / x for x, y in zip(r, r[1:])]
    print(f"{a}/{b}: 位置1={r[0]:.4f} 最後={r[-1]:.4f} 最後/位置1={r[-1] / r[0]:.2f} "
          f"CV(後10步)={cv(r[-10:]):.1f}% 相鄰差sd/√2={100 * st.stdev(diffs) / 2 ** 0.5:.1f}%")
    print("   " + " ".join(f"{x:.4f}" for x in r))

print("\n== 雜訊(counts,後 10 步)")
last = steps[-10:]
for k in ("CN388", "N2_337", "O777", "OH309", "Ha"):
    ms = [s[2][k] for s in last]
    sf = st.median(st.stdev([f[k] for f in s[3]]) for s in last)
    print(f"{k}: step 平均 {st.mean(ms):.1f}  step 間 sd {st.stdev(ms):.2f}  單幀 sd {sf:.2f}")

print("\n== step 內 CN/Ar ×1e3(逐幀,頭尾轉換幀用 * 標出)、O777")
for name, n, m, ok, P in steps[-10:]:
    cells = []
    for f in P:
        if f["Ar750"] < 2000:
            continue
        tag = "*" if f["N2_337"] >= 150 or f["Ar750"] < 0.8 * max(g["Ar750"] for g in P) else " "
        cells.append(f"{1e3 * f['CN388'] / f['Ar750']:6.2f}{tag}")
    print(name, " ".join(cells), "| O777", " ".join(f"{f['O777']:.0f}" for f in P if f["Ar750"] >= 2000))
