"""11/26 recipe step 頭尾 N2 突波對測漏的影響(docs/production-20261126-leak-strategy-zh-TW.md §6)。

用法: python3 -I head_tail_n2.py [錄檔資料夾]
      預設 /mnt/c/DualOES/202611/26;只讀旁邊有 P_<時間>.notes.txt(DV 點位)的 16 個 recipe step。

取線照 strategy-settings-fragment.json 近似引擎:
  N2 357 = 356.4 ±1.4 Integral(背景左 gap 2.0 寬 1.5、右 gap 1.5 寬 1.5);
  N2 337 = 337.1 ±1.5 RawMean;Ar = 750.8 ±1.0 Integral(gap 1.2 寬 1.8,即 750.4 + 波長修正 0.4);
  分類比值 = RawMean 336.7 ±0.5 / RawMean 750.8 ±0.5;電漿 = 全譜 p99 > 3000(Recipe 類別門檻)。
GR 用位置 7–12 的檔案時間 15–40 s(與 §2.3 原本的做法相同),離線 mean/σ 27.4 對引擎的 27.9。

判定模擬照 RatioMonitor:前 9 幀 NotApplicable(MarkNotApplicable 清掉 EMA),第 10 幀起 EMA τ 5 s;
門檻 = max(factor × mean, mean + kσ)(N2 357 ×1.3/×1.4、3σ/6σ;N2 337 只用 5σ/10σ);
確認 = 連續超過門檻的秒數(confirmSeconds 15)。LiveSigma 放寬門檻的效果沒有模擬(會讓尾幀更難越線)。
"""
import glob, math, os, statistics as st, sys

D = sys.argv[1] if len(sys.argv) > 1 else "/mnt/c/DualOES/202611/26"
PLASMA = 3000
DECIDE = 10
TAU, CONFIRM = 5.0, 15.0


def load(p):
    with open(p, encoding="utf-8-sig") as f:
        wl = [float(x) for x in f.readline().strip().split(",")[1:]]
        t, X = [], []
        for line in f:
            q = line.rstrip().split(",")
            if len(q) == len(wl) + 1:
                h, m, s = q[0].split(":")
                t.append(int(h) * 3600 + int(m) * 60 + float(s))
                X.append([float(v) for v in q[1:]])
    return wl, t, X


def idx(wl, lo, hi):
    return [i for i, w in enumerate(wl) if lo <= w <= hi]


def rawmean(wl, s, c, hw):
    ii = idx(wl, c - hw, c + hw)
    return sum(s[i] for i in ii) / len(ii)


def integral(wl, s, c, hw, gl, wdl, gr=None, wdr=None):
    gr = gl if gr is None else gr
    wdr = wdl if wdr is None else wdr
    L, R = idx(wl, c - hw - gl - wdl, c - hw - gl), idx(wl, c + hw + gr, c + hw + gr + wdr)
    xl, yl = st.mean(wl[i] for i in L), st.mean(s[i] for i in L)
    xr, yr = st.mean(wl[i] for i in R), st.mean(s[i] for i in R)
    dx = wl[1] - wl[0]
    return sum((s[i] - (yl + (yr - yl) * (wl[i] - xl) / (xr - xl))) * dx for i in idx(wl, c - hw, c + hw))


def frame(wl, s, t):
    ar = integral(wl, s, 750.8, 1.0, 1.2, 1.8)
    v = sorted(s)
    return dict(t=t, p99=v[int(0.99 * (len(v) - 1))], ar=ar,
                n357=integral(wl, s, 356.4, 1.4, 2.0, 1.5, 1.5, 1.5) / ar,
                n337=rawmean(wl, s, 337.1, 1.5) / ar,
                cls=rawmean(wl, s, 336.7, 0.5) / rawmean(wl, s, 750.8, 0.5))


notes = {os.path.basename(p)[2:12]: open(p, encoding="utf-8-sig").read().strip().replace(" ", "")
         for p in glob.glob(os.path.join(D, "P_*.notes.txt"))}
steps = []
for p in sorted(glob.glob(os.path.join(D, "P_OES1_*.csv"))):
    name = os.path.basename(p)[7:17]
    if p.endswith("summary.csv") or name not in notes:
        continue
    wl, t, X = load(p)
    F = [frame(wl, s, tt - t[0]) for s, tt in zip(X, t)]  # t = 檔案時間(Baseline Builder 的視窗用這個)
    on = [f for f in F if f["p99"] > PLASMA]
    steps.append(dict(name=name, dv=notes[name], F=F, on=on, t0=on[0]["t"]))


def gr(lo, hi, key, sel=range(6, 12)):
    xs = [f[key] for k in sel for f in steps[k]["on"] if lo <= f["t"] <= hi]
    return st.mean(xs), st.stdev(xs), len(xs)


G = {k: gr(15, 40, k)[:2] for k in ("n357", "n337")}
print("GR(位置 7–12,檔案時間 15–40 s):" + "  ".join(f"{k} {m:.5f}±{s:.6f} (mean/σ {m / s:.1f})" for k, (m, s) in G.items()))

print("\n== 1. 頭尾幀(t = 電漿亮起後秒數;×平台 = N2 357 / 位置 7–12 平台;σ = N2 337 相對 GR)")
print(f"{'#':>2} {'DV':7s} {'亮起@檔':>7s} {'最後@檔':>7s} | {'頭幀 t / ×平台 / σ / 分類比值':40s} | 尾幀(t / Ar 對平台 / ×平台 / σ / p99)")
for k, s in enumerate(steps, 1):
    on, t0 = s["on"], s["t0"]
    arp = st.mean(f["ar"] for f in on if 15 <= f["t"] <= 40)
    head = " ".join(f"{f['t'] - t0:.1f}/×{f['n357'] / G['n357'][0]:.0f}/{(f['n337'] - G['n337'][0]) / G['n337'][1]:.0f}σ/{f['cls']:.2f}"
                    for f in on[:2])
    tail = " ".join(f"{f['t'] - t0:.1f}/{f['ar'] / arp:.2f}/×{f['n357'] / G['n357'][0]:.1f}/{(f['n337'] - G['n337'][0]) / G['n337'][1]:.0f}σ/{f['p99']:.0f}"
                    for f in on[-2:])
    print(f"{k:2d} {s['dv']:7s} {t0:6.1f}s {on[-1]['t']:6.1f}s | {head:40s} | {tail}")

print("\n== 2. 頭段退場:回到自己平台(電漿 15–38 s)±5 %(N2 357)/ ±3 %(N2 337)的時間,與判定開始後 11–15 s 的偏高")
for k, s in enumerate(steps, 1):
    on, t0 = s["on"], s["t0"]
    pl = [f for f in on if 15 <= f["t"] - t0 <= 38]
    m7, m3 = st.mean(f["n357"] for f in pl), st.mean(f["n337"] for f in pl)
    r7 = next(f["t"] - t0 for f in on if f["n357"] <= 1.05 * m7)
    r3 = next(f["t"] - t0 for f in on if f["n337"] <= 1.03 * m3)
    e = [f for f in on if 10.5 <= f["t"] - t0 <= 15]
    print(f"{k:2d} {s['dv']:7s} N2 357 @{r7:4.1f}s 11–15 s ×{st.mean(f['n357'] for f in e) / m7:.3f} | "
          f"N2 337 @{r3:4.1f}s 11–15 s ×{st.mean(f['n337'] for f in e) / m3:.3f} | 第 {DECIDE} 幀分類比值 {on[DECIDE - 1]['cls']:.3f}")


def judge(on, key, drop_tail):
    m, s = G[key]
    W, A = (max(1.3 * m, m + 3 * s), max(1.4 * m, m + 6 * s)) if key == "n357" else (m + 5 * s, m + 10 * s)
    fr = on[:-2] if drop_tail else on
    ema = last = ws = as_ = None
    bw = ba = 0.0
    peak = -1e9
    for j, f in enumerate(fr):
        if j < DECIDE - 1:
            continue
        ema = f[key] if ema is None else ema + (1 - math.exp(-(f["t"] - last) / TAU)) * (f[key] - ema)
        last = f["t"]
        peak = max(peak, ema)
        ws = (ws if ws is not None else f["t"]) if ema >= W else None
        as_ = (as_ if as_ is not None else f["t"]) if ema >= A else None
        bw = max(bw, f["t"] - ws) if ws is not None else bw
        ba = max(ba, f["t"] - as_) if as_ is not None else ba
    pk = peak / m if key == "n357" else (peak - m) / s
    return bw, ba, pk


print(f"\n== 3. 判定模擬:最長連續超過 Warn / Alarm 的秒數(確認要 {CONFIRM:.0f} s),含尾幀 vs 去掉最後 2 幀;EMA 峰值")
for k, s in enumerate(steps, 1):
    cells = []
    for key, lab, u in (("n357", "N2 357", "×"), ("n337", "N2 337", "σ")):
        a, b = judge(s["on"], key, False), judge(s["on"], key, True)
        pk = f"×{a[2]:.1f}" if u == "×" else f"{a[2]:+.0f}σ"
        cells.append(f"{lab} 含尾 W{a[0]:4.1f}/A{a[1]:4.1f} 峰{pk:>6s} 去尾 W{b[0]:4.1f}/A{b[1]:4.1f}")
    print(f"{k:2d} {s['dv']:7s} " + " | ".join(cells))

print("\n== 4. Baseline Builder 視窗(檔案時間)對 GR 的影響;DV 500 位置 13–16 的電漿 15–38 s 對 GR")
for lo, hi in ((15, 40), (20, 40), (20, 38), (15, 45)):
    row = []
    for key in ("n357", "n337"):
        m, s, n = gr(lo, hi, key)
        d = st.mean(f[key] for k in range(12, 16) for f in steps[k]["on"] if 15 <= f["t"] - steps[k]["t0"] <= 38)
        row.append(f"{key} mean/σ {m / s:5.1f}  DV500 ×{d / m:.2f} {(d - m) / s:+5.1f}σ")
    print(f"{lo:2d}–{hi:2d} s (n={n:3d}): " + " | ".join(row))
