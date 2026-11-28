"""11/28 DV 測試的圖(docs/production-20261128-dv-test-zh-TW.md 圖 1–3)。

需要 numpy / matplotlib —— 用 Windows 的 Python 跑:
    py -3.13 tools\\analysis\\20261128\\plot_dv28.py [錄檔資料夾] [輸出資料夾]
預設 C:\\DualOES\\202611\\28 與 docs\\images。圖 2–3 的計算借用同資料夾的 dv_sim.py(執行時不印它的輸出)。
圖 1 延續 20261126/plot_dv.py 圖 1 的做法:每個 DV 的譜 = 該 DV 所有 step 穩態幀的平均
(穩態幀 = Ar 750 > 0.8 × 該 step 第 75 百分位,再去掉前後各 3 幀);step 中途開閥的 3 步(notes 寫 open to process)不算。
"""
import contextlib, csv, glob, io, math, os, runpy, sys
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

D = sys.argv[1] if len(sys.argv) > 1 else r"C:\DualOES\202611\28"
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "..", "..", "..", "docs", "images")
plt.rcParams["font.sans-serif"] = ["Microsoft JhengHei", "DejaVu Sans"]
plt.rcParams["axes.unicode_minus"] = False

argv, sys.argv = sys.argv, [os.path.join(os.path.dirname(__file__), "dv_sim.py"), D]
with contextlib.redirect_stdout(io.StringIO()):
    g = runpy.run_path(sys.argv[0])
sys.argv = argv
steps, G, SITE, plateau = g["steps"], g["G"], g["SITE"], g["plateau"]
steps = [s for s in steps if s["on"]]


# 圖 1:N2 337、357(358)、CN 388 / N2 380 三段的平均穩態譜
DVS = ["DV=0", "DV=100", "DV=200", "DV=300", "DV=400", "DV=500"]
COL = dict(zip(DVS, ["#7f7f7f", "#c6dbef", "#9ecae1", "#4292c6", "#08519c", "#d62728"]))


def load_np(path):
    with open(path, encoding="utf-8-sig") as f:
        wl = np.array([float(x) for x in f.readline().strip().split(",")[1:]])
        rows = [l.rstrip().split(",") for l in f]
    return wl, np.array([[float(x) for x in r[1:]] for r in rows if len(r) == len(wl) + 1])


def band(wl, X, lo, hi, b1, b2):
    c = X[:, (wl >= lo) & (wl <= hi)].max(axis=1)
    b = X[:, ((wl >= b1[0]) & (wl <= b1[1])) | ((wl >= b2[0]) & (wl <= b2[1]))].mean(axis=1)
    return c - b


acc = {dv: [] for dv in DVS}
for npath in sorted(glob.glob(os.path.join(D, "P_*.notes.txt"))):
    with open(npath, encoding="utf-8-sig") as f:
        txt = f.read()
    dv = txt.split("\n")[0].strip().replace(" ", "")
    if dv not in acc or "pressice" in txt:
        continue
    wl, X = load_np(os.path.join(D, "P_OES1_" + os.path.basename(npath)[2:12] + ".csv"))
    ar = band(wl, X, 750.0, 751.6, (747.5, 748.5), (753.5, 754.5))
    acc[dv].append(X[np.where(ar > 0.8 * np.percentile(ar, 75))[0][3:-3]])
spec = {dv: np.vstack(v).mean(axis=0) for dv, v in acc.items()}
nstep = {dv: len(v) for dv, v in acc.items()}
fig, axs = plt.subplots(1, 3, figsize=(13, 4.2))
for ax, (lo, hi, title) in zip(axs, [(329, 341, "N2 337(0,0)"), (350, 361, "N2 357–358(0,1)"),
                                     (374, 394, "N2 380(0,2) + CN 388")]):
    m = (wl >= lo) & (wl <= hi)
    for dv in DVS:
        ax.plot(wl[m], spec[dv][m], color=COL[dv], lw=1.6 if dv == "DV=500" else 1.1, label=f"{dv}({nstep[dv]} 步)")
    ax.set_title(title); ax.set_xlabel("波長 (nm)"); ax.grid(alpha=0.3)
axs[0].set_ylabel("counts(穩態幀平均)"); axs[0].legend(fontsize=8)
fig.suptitle("圖 1  11/28:DV 0–400 疊在一起,DV 500 長出 N2 第二正帶;CN 388 帶尾整片抬高")
fig.tight_layout(); fig.savefig(os.path.join(OUT, "production-20261128-n2-bands.png"), dpi=110); plt.close(fig)
d = spec["DV=500"] - spec["DV=400"]
for lo, hi in ((334, 338), (353, 359), (379, 382), (383, 387.5), (387.5, 389)):
    m = (wl >= lo) & (wl <= hi)
    i = np.where(m)[0][int(np.argmax(d[m]))]
    print(f"{lo}–{hi} nm:DV500 − DV400 最大 {d[i]:+.0f} @ {wl[i]:.1f} nm(DV400 {spec['DV=400'][i]:.0f}、DV0 {spec['DV=0'][i]:.0f})")

# 圖 2:逐步平台
x = list(range(len(steps)))
n357 = [plateau(s, "n357") / G["n357"][0] for s in steps]
site = [(plateau(s, "site") - SITE[0]) / SITE[1] for s in steps]
col = ["#d62728" if s["dv"].startswith("DV=500") else "#4292c6" for s in steps]
fig, ax = plt.subplots(2, 1, figsize=(10, 6.2), sharex=True)
ax[0].bar(x, n357, color=col)
ax[0].axhline(1.4, ls="--", c="k", lw=1); ax[0].text(len(x) - 0.5, 1.42, "Alarm ×1.4", ha="right", fontsize=9)
ax[0].axhline(1.3, ls=":", c="k", lw=1); ax[0].text(len(x) - 0.5, 1.23, "Warn ×1.3", ha="right", fontsize=9)
ax[0].set_ylabel("N2 357 Integral / Ar\n(× 當天 GR)"); ax[0].set_ylim(0.8, 2.0)
ax[1].bar(x, site, color=col)
ax[1].axhline(6, ls="--", c="k", lw=1); ax[1].text(len(x) - 0.5, 7, "現場 Alarm 6σ", ha="right", fontsize=9)
ax[1].axhline(0, c="k", lw=0.6)
ax[1].set_ylabel("N2 337 RawMean / Ar\n(對現場 GR,σ)")
ax[1].set_xticks(x)
ax[1].set_xticklabels([s["dv"].replace("DV=", "") if s["dv"] != "(pre)" else "pre" for s in steps], fontsize=8)
ax[1].set_xlabel("step(依時間;數字 = DV,* = step 中途開閥)")
ax[0].set_title("圖 2:11/28 逐 step 平台(亮起後 15–38 s)。紅 = DV 500")
fig.tight_layout(); fig.savefig(os.path.join(OUT, "production-20261128-step-trend.png"), dpi=110)

# 圖 3:引擎 P_Ratio 一個 DV 500 step(15:47:56)的 R_41779f99:原始值 σ 與 σ 分數
rows = []
for name in sorted(os.listdir(D)):
    if name.startswith("P_Ratio_") and name.endswith(".csv"):
        with open(os.path.join(D, name), encoding="utf-8-sig") as f:
            r = list(csv.reader(f))
        h = r[0]
        c = next(i for i, k in enumerate(h) if k.endswith("[R_41779f99]"))
        for q in r[1:]:
            if q[c] and q[c + 1] and q[0][11:16] in ("15:47", "15:48"):
                hh, mm, ss = q[0][11:].split(":")
                rows.append((int(hh) * 3600 + int(mm) * 60 + float(ss), float(q[c]), float(q[c + 1])))
t0 = rows[0][0]
tt = [t - t0 for t, _, _ in rows]
z = [(v - SITE[0]) / SITE[1] for _, v, _ in rows]
sc = [s for _, _, s in rows]
# 同一串原始值、跳過前 9 幀再起 EMA(分類器 decideAfterFrames 10):引擎若不吃到頭幀會給的分數
ema = var = last = None
sim = []
for (t, v, _), j in zip(rows, range(len(rows))):
    if j < 9:
        sim.append(float("nan")); continue
    if ema is None:
        ema, var = v, 0.0
    else:
        a = 1 - math.exp(-(t - last) / 5.0); d = v - ema; ema += a * d; var = (1 - a) * (var + a * d * d)
    last = t
    sim.append(100 + 20 * (ema - SITE[0]) / max(SITE[1], math.sqrt(var)) / 3)
fig, ax = plt.subplots(figsize=(10, 4.4))
ax.plot(tt, [100 + 20 * v / 3 for v in z], "o-", ms=3, c="#999", label="原始值換成分數(100 + 20·z/3,z 用 GR σ)")
ax.plot(tt, sc, "o-", ms=3, c="#d62728", label="引擎寫的 _sigmaScore(頭幀撐大即時 σ)")
ax.plot(tt, sim, "--", c="#2ca02c", label="同一串值、前 9 幀不判(分類器 decideAfterFrames 10)")
ax.axhline(120, ls=":", c="k"); ax.text(tt[-1], 123, "Warn 120(3σ)", ha="right", fontsize=9)
ax.axhline(140, ls="--", c="k"); ax.text(tt[-1], 143, "Alarm 140(6σ)", ha="right", fontsize=9)
ax.set_ylim(90, 300); ax.set_xlabel("電漿亮起後(s)"); ax.set_ylabel("σ 分數")
ax.set_title("圖 3:DV 500(15:47:56)現場 N2 337 RawMean。原始值 +20σ 以上,分數卻停在 101–104")
ax.legend(fontsize=8, loc="upper center")
fig.tight_layout(); fig.savefig(os.path.join(OUT, "production-20261128-sigma-score.png"), dpi=110)
print("ok")
