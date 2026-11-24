"""11/23 生產錄檔(15:45:07 以後)兩段製程的候選 Ratio 穩定度。

用法: python3 -I ratio_stats.py "C:/DualOES/202611/23" 的 WSL 路徑
離線 peak-height(峰搜尋 ±1 nm、線性連續光扣除),幾何接近 LineIntensityExtractor 的 PeakHeight,
但不是同一支程式 —— 上機前要用 Baseline Builder 以引擎抽取器重量。
"""
import csv, glob, os, statistics as st, sys

D = sys.argv[1] if len(sys.argv) > 1 else "/mnt/c/DualOES/202611/23"
FIRST = "154507"          # 使用者指定:15:45:07 以後
DT_TAU = 5.0              # EMA tau(秒),與目前設定相同

def idx(wl, w):
    return min(range(len(wl)), key=lambda i: abs(wl[i] - w))

def mean(xs):
    return sum(xs) / len(xs)

def peak(s, wl, c, hw=0.5, search=1.0, gl=1.0, bl=1.0, gr=1.0, br=1.0, left=True, right=True):
    """PeakHeight:在 c±search 內找峰,重新置中後取 ±hw 的最大值,扣兩側窗的線性連續光。"""
    i0, i1 = idx(wl, c - search), idx(wl, c + search)
    ic = max(range(i0, i1 + 1), key=lambda i: s[i])
    cc = wl[ic]
    pk = max(s[idx(wl, cc - hw):idx(wl, cc + hw) + 1])
    L = s[idx(wl, cc - hw - gl - bl):idx(wl, cc - hw - gl) + 1]
    R = s[idx(wl, cc + hw + gr):idx(wl, cc + hw + gr + br) + 1]
    sides = ([mean(L)] if left else []) + ([mean(R)] if right else [])
    return pk - mean(sides)

LINES = {
    "N2 337.1":  dict(c=337.1, hw=0.5),
    "N2 357.7":  dict(c=357.7, hw=0.5),                       # 原設定:左窗踩在 Ar 355.4 上
    "N2 357.7R": dict(c=357.7, hw=0.5, left=False, gr=0.8),   # 只用右窗,避開 Ar 355.4
    "N2 380.5":  dict(c=380.5, hw=0.5),
    "CN 388.3":  dict(c=388.3, hw=0.6),
    "NO 237":    dict(c=237.8, hw=0.6),
    "Ar 750.4":  dict(c=750.4, hw=0.5),
    "O 777.2":   dict(c=777.2, hw=0.7),
    "O 844.6":   dict(c=844.6, hw=0.6),
    "CO 329.6":  dict(c=329.6, hw=0.7),
}
RATIOS = {
    "Ar": [("N2 337.1", "Ar 750.4"), ("CN 388.3", "Ar 750.4"), ("NO 237", "Ar 750.4"),
           ("N2 357.7", "Ar 750.4"), ("N2 357.7R", "Ar 750.4"), ("N2 380.5", "Ar 750.4"),
           ("O 777.2", "Ar 750.4"), ("N2 337.1", "CO 329.6")],
    "noAr": [("N2 337.1", "O 777.2"), ("CN 388.3", "O 777.2"), ("NO 237", "O 777.2"),
             ("Ar 750.4", "O 777.2"), ("O 844.6", "O 777.2"), ("N2 337.1", "CO 329.6")],
}

files = sorted(f for f in glob.glob(os.path.join(D, "P_OES1_*.csv"))
               if not f.endswith(".summary.csv") and os.path.basename(f)[11:17] >= FIRST)
runs = {"Ar": [], "noAr": []}
for f in files:
    rows = list(csv.reader(open(f, encoding="utf-8-sig")))
    wl = [float(x) for x in rows[0][1:]]
    fr = [(r[0], [float(v) for v in r[1:]]) for r in rows[1:] if len(r) > 10]
    tot = [sum(s) for _, s in fr]
    mx = max(tot)
    on = [(t, s) for (t, s), v in zip(fr, tot) if v > 0.6 * mx][2:-3]   # 去掉點火與收尾過渡
    kind = "Ar" if peak(on[0][1], wl, **LINES["Ar 750.4"]) > 0.5 * peak(on[0][1], wl, **LINES["O 777.2"]) else "noAr"
    vals = {k: [peak(s, wl, **g) for _, s in on] for k, g in LINES.items()}
    runs[kind].append((os.path.basename(f), on[0][0], on[-1][0], len(on), vals))

def secs(t):
    h, m, s = t.split(":"); return int(h) * 3600 + int(m) * 60 + float(s)

for kind, rr in runs.items():
    dt = mean([(secs(e) - secs(b)) / (n - 1) for _, b, e, n, _ in rr])
    a = 1 - __import__("math").exp(-dt / DT_TAU)
    ema = (a / (2 - a)) ** 0.5
    print(f"\n## {kind}: {len(rr)} runs, frame dt {dt:.2f} s, EMA sigma factor {ema:.2f}")
    for _, b, e, n, _ in rr: print(f"   {b}–{e}  steady frames {n}")
    print("| ratio | run means | frame CV | run CV | drift 1→4 | numerator counts | in-step 3σ_EMA |")
    for n_, d_ in RATIOS[kind]:
        ms, cvs, nums = [], [], []
        for *_, v in rr:
            x = [p / q for p, q in zip(v[n_], v[d_]) if abs(q) > 50]
            if len(x) < 5: break
            ms.append(mean(x)); cvs.append(st.stdev(x) / abs(mean(x))); nums.append(mean(v[n_]))
        if len(ms) < len(rr):
            print(f"| {n_} / {d_} | 分母≈0,不可用 |"); continue
        print(f"| {n_} / {d_} | {' '.join(f'{m:.4f}' for m in ms)} | {mean(cvs)*100:.1f} % | "
              f"{st.stdev(ms)/abs(mean(ms))*100:.1f} % | {(ms[-1]/ms[0]-1)*100:+.0f} % | {mean(nums):.0f} | "
              f"{3*ema*mean(cvs)*100:.1f} % |")
