"""11/28 新機台 recipe 第二次 DV 加量測試(DV 0 → 500 → 100 → 500)逐 step 統計與判定模擬。

用法: python3 -I dv_sim.py [錄檔資料夾]
      預設 /mnt/c/DualOES/202611/28;只讀旁邊有 P_<時間>.notes.txt(DV 點位)的 25 個 recipe step,
      另外讀 15:10:43 那筆(沒有 notes,GR 之前的一步)當位置 0。

取線與判定模擬照 20261126/head_tail_n2.py(11/26 策略 §2.1 的取線、§2.2 分類器、RatioMonitor 的 EMA / 門檻 / 確認):
  N2 357 = 356.4 ±1.4 Integral、N2 337 = 337.1 ±1.5 RawMean、Ar = 750.8 ±1.0 Integral、分類比值 RawMean 336.7/750.8。
另外照「現場實際設定」(R_41779f99:337.1 ±0.8918 RawMean / Ar 750.4 Integral gap 0.7134 寬 1.0701,分類器關閉、3σ/6σ)
重算 σ-score,用 EWMA 即時 σ(EffectiveSigma = max(基準 σ, 即時 σ))說明現場為什麼 DV 500 也只到 101–104。
"""
import glob, math, os, statistics as st, sys

D = sys.argv[1] if len(sys.argv) > 1 else "/mnt/c/DualOES/202611/28"
PLASMA, DECIDE, TAU, CONFIRM = 3000, 10, 5.0, 15.0


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
    ar_site = integral(wl, s, 750.4, 1.0, 0.7134, 1.0701)  # 不加 0.4 修正比較貼近引擎 P_Ratio(引擎 Ar 有 ±0.71 搜峰)
    v = sorted(s)
    return dict(t=t, p99=v[int(0.99 * (len(v) - 1))], ar=ar,
                n357=integral(wl, s, 356.4, 1.4, 2.0, 1.5, 1.5, 1.5) / ar,
                n337=rawmean(wl, s, 337.1, 1.5) / ar,
                site=rawmean(wl, s, 337.1, 0.8918) / ar_site,
                cn=integral(wl, s, 388.3, 0.6, 1.2, 1.8) / ar,
                cls=rawmean(wl, s, 336.7, 0.5) / rawmean(wl, s, 750.8, 0.5))


notes = {os.path.basename(p)[2:12]: open(p, encoding="utf-8-sig").read().split("\n")[0].strip().replace(" ", "")
         + ("*" if "pressice" in open(p, encoding="utf-8-sig").read() else "")
         for p in glob.glob(os.path.join(D, "P_*.notes.txt"))}
notes.setdefault("1128151043", "(pre)")
steps = []
for p in sorted(glob.glob(os.path.join(D, "P_OES1_*.csv"))):
    name = os.path.basename(p)[7:17]
    if p.endswith("summary.csv") or name not in notes:
        continue
    wl, t, X = load(p)
    F = [frame(wl, s, tt - t[0]) for s, tt in zip(X, t)]
    on = [f for f in F if f["p99"] > PLASMA]
    steps.append(dict(name=name, dv=notes[name], F=F, on=on, t0=on[0]["t"] if on else 0))

KEYS = ("n357", "n337", "site", "cn")


def plateau(s, key, lo=15, hi=38):
    xs = [f[key] for f in s["on"] if lo <= f["t"] - s["t0"] <= hi]
    return st.mean(xs) if xs else float("nan")


print("== 1. 每步:電漿幀數、亮起後 15–38 s 平台(×1e3);* = DV open to process")
print(f"{'#':>2} {'錄檔':10s} {'DV':8s} {'幀':>3s} {'亮@檔':>6s} {'長s':>5s} {'N2357':>7s} {'N2337':>7s} {'現場337':>7s} {'CN':>6s} {'Ar':>8s}")
for k, s in enumerate(steps):
    on = s["on"]
    if not on:
        print(f"{k:2d} {s['name']} {s['dv']:8s} 無電漿"); continue
    ar = st.mean(f["ar"] for f in on if 15 <= f["t"] - s["t0"] <= 38)
    print(f"{k:2d} {s['name']} {s['dv']:8s} {len(on):3d} {s['t0']:6.1f} {on[-1]['t'] - s['t0']:5.1f} "
          + " ".join(f"{1e3 * plateau(s, key):7.3f}" for key in KEYS) + f" {ar:8.0f}")


def grstats(sel, key, lo=15, hi=40):
    xs = [f[key] for k in sel for f in steps[k]["on"] if lo <= f["t"] <= hi]
    return st.mean(xs), st.stdev(xs), len(xs)


def judge(on, key, m, s, mode):
    """mode 'strategy': 前 9 幀不判(分類器)、門檻不含即時 σ(同 head_tail_n2.py)。
       mode 'site': 每幀都判(分類器關)、EffectiveSigma = max(base, live EWMA σ)、σ-score = 100 + 20 z / 3。"""
    if key == "n357":
        W, A = max(1.3 * m, m + 3 * s), max(1.4 * m, m + 6 * s)
    elif mode == "site":
        W, A = 3, 6
    else:
        W, A = m + 5 * s, m + 10 * s
    ema = var = last = ws = as_ = None
    bw = ba = 0.0
    pk = -1e9
    first = None
    for j, f in enumerate(on):
        if mode == "strategy" and j < DECIDE - 1:
            continue
        x = f[key]
        if ema is None:
            ema, var = x, 0.0
        else:
            a = 1 - math.exp(-(f["t"] - last) / TAU)
            d = x - ema
            ema += a * d
            var = (1 - a) * (var + a * d * d)
        last = f["t"]
        if mode == "site":
            z = (ema - m) / max(s, math.sqrt(var))
            v = z
        else:
            v = ema
        pk = max(pk, v if mode == "site" else (ema / m if key == "n357" else (ema - m) / s))
        ws = (ws if ws is not None else f["t"]) if v >= W else None
        as_ = (as_ if as_ is not None else f["t"]) if v >= A else None
        if ws is not None:
            bw = max(bw, f["t"] - ws)
        if as_ is not None:
            ba = max(ba, f["t"] - as_)
            if ba >= CONFIRM and first is None:
                first = f["t"] - on[0]["t"]
    return bw, ba, pk, first


# GR 1:跟 11/26 一樣,用當天平台區 DV 0–400 的 6 步(位置 5–10:DV 200–400),檔案時間 15–40 s
SEL = [k for k, s in enumerate(steps) if s["dv"].startswith(("DV=200", "DV=300", "DV=400"))][:6]
print("\nGR 步驟:", " ".join(f"{k}:{steps[k]['name']}({steps[k]['dv']})" for k in SEL))
G = {key: grstats(SEL, key)[:2] for key in ("n357", "n337")}
for key, (m, s) in G.items():
    print(f"  {key}: {m:.5f} ± {s:.6f} (mean/σ {m / s:.1f})")
# 11/26 GR 用同一套離線取線算(20261126/head_tail_n2.py 的位置 7–12、15–40 s);
# 引擎的 recipe-20261126(0.01927 / 0.01319)是另一套 Ar 取線,絕對值約大 1.2 倍,不能直接跟這裡的離線值比。
G26 = {"n357": (0.01603, 0.000585), "n337": (0.01119, 0.000277)}
print("  11/26 GR(離線,head_tail_n2.py):n357 0.01603 ± 0.000585、n337 0.01119 ± 0.000277")
SITE = (0.013763811242623814, 0.00026707390583060133)  # 現場 GR「Recipe 3」的 R_41779f99


def show(title, GG):
    print(f"\n== {title}:最長連續超 Warn/Alarm 秒數、EMA 峰值、Alarm 確認時間(亮起後)")
    for k, s in enumerate(steps):
        if not s["on"]:
            continue
        cells = []
        for key in ("n357", "n337"):
            m, sg = GG[key]
            bw, ba, pk, first = judge(s["on"], key, m, sg, "strategy")
            u = f"×{pk:.2f}" if key == "n357" else f"{pk:+.0f}σ"
            cells.append(f"{key} W{bw:4.1f} A{ba:4.1f} 峰{u:>7s}" + (f" 確認@{first:4.1f}s" if first else "            "))
        both = all(judge(s["on"], key, *GG[key], "strategy")[3] for key in ("n357", "n337"))
        print(f"{k:2d} {s['dv']:8s} " + " | ".join(cells) + ("  → ALARM" if both else ""))


show("2a. 11/26 策略、當天 GR", G)
show("2b. 11/26 策略、11/26 的 GR(跨日)", G26)

print("\n== 3. 現場設定(分類器關、R_41779f99 3σ/6σ、即時 σ 放寬):σ 分數峰值與 σ 分數 120 的秒數")
for k, s in enumerate(steps):
    if not s["on"]:
        continue
    bw, ba, pk, first = judge(s["on"], "site", *SITE, "site")
    bw2, ba2, pk2, _ = judge(s["on"][9:], "site", *SITE, "site")
    raw = (plateau(s, "site") - SITE[0]) / SITE[1]
    print(f"{k:2d} {s['dv']:8s} 平台 {raw:+6.1f}σ | 含頭幀:峰 z {pk:5.2f}(分數 {100 + 20 * pk / 3:6.1f})W{bw:4.1f}s A{ba:4.1f}s"
          f" | 去頭 9 幀:峰 z {pk2:5.1f} W{bw2:4.1f}s A{ba2:4.1f}s")

# 4. 引擎自己的 P_Ratio:每步 R_41779f99(現場 N2 337 RawMean)/ R_40cb92c0(N2 357 RawMean)/ O 777 的原始值與 σ 分數
import csv
RAT = sorted(glob.glob(os.path.join(D, "P_Ratio_*.csv")))
rows = []
for p in RAT:
    with open(p, encoding="utf-8-sig") as f:
        r = list(csv.reader(f))
    h = r[0]
    for x in r[1:]:
        hh, mm, ss = x[0][11:].split(":")
        rows.append((int(hh) * 3600 + int(mm) * 60 + float(ss), dict(zip(h, x))))
GRS = {"R_41779f99": SITE, "R_40cb92c0": (0.019282963973170066, 0.00032365560086266415),
       "R_58006cc7": (0.0020068464827862663, 7.743788517109976e-05)}
print("\n== 4. 引擎 P_Ratio:亮起後 15–38 s 原始值對 GR「Recipe 3」(σ);第 3 幀以後 σ 分數最大值;整步 OverallState")
for k, s in enumerate(steps):
    if not s["on"]:
        continue
    t0 = s["on"][0]["t"]
    # 錄檔時間 t 是檔案時間;換回時鐘時間
    base = sec0 = None
    with open(os.path.join(D, f"P_OES1_{s['name']}.csv"), encoding="utf-8-sig") as f:
        f.readline()
        h, m, x = f.readline().split(",", 1)[0].split(":")
        sec0 = int(h) * 3600 + int(m) * 60 + float(x)
    on_clock = sec0 + t0
    sel = [d for tt, d in rows if on_clock - 0.6 <= tt <= on_clock + 45]
    cells = []
    for key, (m, sg) in GRS.items():
        col = next(c for c in sel[0] if c.endswith(f"[{key}]")) if sel else None
        vals = [(tt, float(d[col]), float(d[col + "_sigmaScore"])) for tt, d in rows
                if on_clock - 0.6 <= tt <= on_clock + 45 and d.get(col) and d.get(col + "_sigmaScore")]
        pl = [v for tt, v, _ in vals if on_clock + 15 <= tt <= on_clock + 38]
        mxs = max((sc for _, _, sc in vals[2:]), default=float("nan"))
        cells.append(f"{key[2:6]} {(st.mean(pl) - m) / sg:+6.1f}σ 分數max {mxs:6.1f}" if pl else f"{key[2:6]} —")
    states = sorted({d["OverallState"] for _, d in [(tt, d) for tt, d in rows if on_clock <= tt <= on_clock + 45]})
    print(f"{k:2d} {s['dv']:8s} " + " | ".join(cells) + "  " + "/".join(states))
