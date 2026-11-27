"""11/26 新機台 recipe 的 DV 加量測試(DV 0 → 500)逐 step 統計。

用法: python3 -I dv_stats.py [錄檔資料夾] [P_Ratio.csv]
      預設 /mnt/c/DualOES/202611/26 與該資料夾裡的 P_Ratio_1126165108.csv
      (diag 包 diag_DESKTOP-4JD00UD_cc03_20261126_175656.zip 的 data/ 裡也有同一個檔)

DV 點位讀自每筆錄檔旁的 P_<時間>.notes.txt("DV = 300")。
離線 peak-height:中心 ±hw 取最大值,扣兩側 hw..hw+1.5 nm 的中位數(與 20261125/step_stats.py 同一套)。
N2 337 另外用較寬的背景窗(331–332.5、338.3–339.7 nm)量一次 —— 漏氣時 N2 帶的兩翼也一起升,
貼身的 ±1.5 nm 背景會把一部分訊號扣掉,寬背景比較接近建議上機的取線(文件 §5)。

穩態幀 = Ar 750 > 0.8 × 該 step 最大值,再去掉前後各 3 幀(頭尾含 N2 的轉換幀)。
"""
import csv, glob, os, statistics as st, sys

D = sys.argv[1] if len(sys.argv) > 1 else "/mnt/c/DualOES/202611/26"
RATIO = sys.argv[2] if len(sys.argv) > 2 else os.path.join(D, "P_Ratio_1126165108.csv")

LINES = {  # 名稱: (讀值中心 nm, 半寬 nm)
    "Ar750": (750.8, 1.0), "He706": (706.8, 1.0), "Ha": (656.4, 1.0), "CN388": (388.4, 0.8),
    "N2_337": (336.7, 1.0), "N2_357": (357.4, 1.0), "OH309": (309.2, 1.0), "O777": (777.4, 1.0),
    "NO237": (236.0, 1.5),
}


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        wl = [float(x) for x in f.readline().strip().split(",")[1:]]
        t, rows = [], []
        for line in f:
            p = line.rstrip().split(",")
            if len(p) == len(wl) + 1:
                t.append(p[0])
                rows.append([float(x) for x in p[1:]])
    return wl, t, rows


def peak(wl, s, c, hw, bg=1.5):
    core = [s[i] for i, w in enumerate(wl) if c - hw < w < c + hw]
    fl = [s[i] for i, w in enumerate(wl) if c - hw - bg < w <= c - hw or c + hw <= w < c + hw + bg]
    return max(core) - st.median(fl)


def band(wl, s, lo, hi, b1, b2):
    c = max(s[i] for i, w in enumerate(wl) if lo <= w <= hi)
    b = st.mean(s[i] for i, w in enumerate(wl) if b1[0] <= w <= b1[1] or b2[0] <= w <= b2[1])
    return c - b


def frame(wl, s):
    f = {k: peak(wl, s, *LINES[k]) for k in LINES}
    f["N2w"] = band(wl, s, 335.5, 337.2, (331.0, 332.5), (338.3, 339.7))
    return f


def sec(hms):
    h, m, x = hms.split(":")
    return int(h) * 3600 + int(m) * 60 + float(x)


notes = {os.path.basename(p)[2:12]: open(p, encoding="utf-8-sig").read().strip().replace(" ", "")
         for p in glob.glob(os.path.join(D, "P_*.notes.txt"))}
files = sorted(p for p in glob.glob(os.path.join(D, "P_OES1_*.csv"))
               if not p.endswith("summary.csv") and os.path.basename(p)[7:17] in notes)

steps = []
for p in files:
    name = os.path.basename(p)[7:17]
    wl, t, X = load(p)
    P = [frame(wl, s) for s in X]
    amax = max(f["Ar750"] for f in P)
    idx = [i for i, f in enumerate(P) if f["Ar750"] > 0.8 * amax][3:-3]
    ok = [P[i] for i in idx]
    m = {k: st.mean(f[k] for f in ok) for k in ok[0]}
    steps.append(dict(name=name, dv=notes[name], t0=t[0], m=m, ok=ok, P=P))

print("== 每個 recipe step:穩態平均(peak-height, counts)")
keys = ["Ar750", "He706", "CN388", "N2_337", "N2w", "N2_357", "OH309", "O777", "NO237", "Ha"]
print(f"{'位置':>4} {'錄檔':10s} {'DV':7s} {'穩態幀':>4} " + " ".join(f"{k:>7s}" for k in keys))
for i, s in enumerate(steps, 1):
    print(f"{i:4d} {s['name']} {s['dv']:7s} {len(s['ok']):4d} " + " ".join(f"{s['m'][k]:7.1f}" for k in keys))

print("\n== 比值 / Ar 750 (×1e3)")
rk = ["CN388", "N2_337", "N2w", "N2_357", "OH309", "O777", "NO237", "Ha", "He706"]
print(f"{'位置':>4} {'DV':7s} " + " ".join(f"{k:>7s}" for k in rk))
for i, s in enumerate(steps, 1):
    print(f"{i:4d} {s['dv']:7s} " + " ".join(f"{1e3 * s['m'][k] / s['m']['Ar750']:7.2f}" for k in rk))

print("\n== 各 DV 的平均與 step 間 sd(N2w / Ar ×1e3);雜訊用 DV=300 的 3 步")
groups = {}
for s in steps:
    groups.setdefault(s["dv"], []).append(1e3 * s["m"]["N2w"] / s["m"]["Ar750"])
for dv, xs in groups.items():
    print(f"{dv:7s} n={len(xs)} 平均={st.mean(xs):6.2f} sd={st.stdev(xs) if len(xs) > 1 else 0:5.2f}")

print("\n== 逐幀 N2w / Ar ×1e3(Ar 750 > 2000 的幀;頭尾轉換幀很大)")
for s in steps:
    if s["dv"] in ("DV=300", "DV=400", "DV=500"):
        cells = [f"{1e3 * f['N2w'] / f['Ar750']:.1f}" for f in s["P"] if f["Ar750"] > 2000]
        print(s["name"], s["dv"], " ".join(cells))

if os.path.exists(RATIO):
    print("\n== 引擎 P_Ratio:開段後 12–40 s 的平均(引擎自己的取線,不是離線抽法)")
    with open(RATIO, encoding="utf-8-sig") as f:
        r = list(csv.reader(f))
    h = r[0]
    cols = [i for i, c in enumerate(h) if c.endswith("]")]
    rows = [(sec(x[0][11:]), x) for x in r[1:]]
    print(f"{'DV':7s} {'幀':>3s} " + " ".join(f"{h[i][:15]:>15s}" for i in cols) + "  狀態")
    for s in steps:
        t0 = sec(s["t0"])
        sel = [x for tt, x in rows if t0 + 12 <= tt <= t0 + 40 and x[cols[0]] != ""]
        vals = [st.mean(float(x[i]) for x in sel if x[i] != "") for i in cols]
        states = sorted({x[h.index("OverallState")] for x in sel})
        print(f"{s['dv']:7s} {len(sel):3d} " + " ".join(f"{v:15.5f}" for v in vals) + "  " + "/".join(states))
