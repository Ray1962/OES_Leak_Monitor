"""11/26 DV 加量測試的圖(docs/production-20261126-dv-test-zh-TW.md 圖 1–3)。

需要 numpy 與 matplotlib —— WSL 的 python3 沒有,用 Windows 的 Python 跑:
    py -3.13 tools\\analysis\\20261126\\plot_dv.py [錄檔資料夾] [輸出資料夾]
預設 C:\\DualOES\\202611\\26 與 docs\\images。

每個 DV 的譜 = 該 DV 所有 step 穩態幀的平均(選幀與 dv_stats.py 相同;DV=0 只取位置 2–3,
位置 1 的 CN / OH 還在 precoat 之後的衰減高點)。
"""
import csv, glob, os, sys
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

D = sys.argv[1] if len(sys.argv) > 1 else r"C:\DualOES\202611\26"
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "..", "..", "..", "docs", "images")
plt.rcParams["font.sans-serif"] = ["Microsoft JhengHei", "DejaVu Sans"]
plt.rcParams["axes.unicode_minus"] = False
DVS = ["DV=0", "DV=200", "DV=300", "DV=400", "DV=500"]
COL = dict(zip(DVS, ["#7f7f7f", "#9ecae1", "#4292c6", "#08519c", "#d62728"]))


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        wl = np.array([float(x) for x in f.readline().strip().split(",")[1:]])
        t, rows = [], []
        for l in f:
            p = l.rstrip().split(",")
            if len(p) == len(wl) + 1:
                t.append(p[0]); rows.append([float(x) for x in p[1:]])
    return wl, t, np.array(rows)


def band(wl, X, lo, hi, b1, b2):
    c = X[:, (wl >= lo) & (wl <= hi)].max(axis=1)
    b = X[:, ((wl >= b1[0]) & (wl <= b1[1])) | ((wl >= b2[0]) & (wl <= b2[1]))].mean(axis=1)
    return c - b


notes = {os.path.basename(p)[2:12]: open(p, encoding="utf-8-sig").read().strip().replace(" ", "")
         for p in glob.glob(os.path.join(D, "P_*.notes.txt"))}
steps = []
for name in sorted(notes):
    wl, t, X = load(os.path.join(D, f"P_OES1_{name}.csv"))
    ar = band(wl, X, 750.0, 751.6, (747.5, 748.5), (753.5, 754.5))
    idx = np.where(ar > 0.8 * np.percentile(ar, 75))[0][3:-3]  # 75 百分位:個別尖峰幀不拉高門檻
    steps.append(dict(name=name, dv=notes[name], t=t, X=X, ar=ar, idx=idx))

spec = {}
for dv in DVS:
    ss = [s for s in steps if s["dv"] == dv]
    if dv == "DV=0":
        ss = ss[1:]
    spec[dv] = np.vstack([s["X"][s["idx"]] for s in ss]).mean(axis=0)

# 圖 1:N2 第二正帶的三個帶頭
fig, axs = plt.subplots(1, 3, figsize=(13, 4.2))
for ax, (lo, hi, title) in zip(axs, [(329, 341, "N2 337(0,0)"), (350, 361, "N2 357(0,1)"), (374, 394, "N2 380(0,2) + CN 388")]):
    m = (wl >= lo) & (wl <= hi)
    for dv in DVS:
        ax.plot(wl[m], spec[dv][m], color=COL[dv], lw=1.6 if dv == "DV=500" else 1.1, label=dv)
    ax.set_title(title); ax.set_xlabel("波長 (nm)"); ax.grid(alpha=0.3)
axs[0].set_ylabel("counts(穩態幀平均)"); axs[0].legend(fontsize=8)
fig.suptitle("圖 1  N2 第二正帶:DV 0–400 疊在一起,DV 500 才長出來(UV 端整體下降是視窗/電漿漂移)")
fig.tight_layout(); fig.savefig(os.path.join(OUT, "production-20261126-n2-bands.png"), dpi=110); plt.close(fig)

# 圖 2:777 nm 附近,沒有 O 峰
fig, (a1, a2) = plt.subplots(2, 1, figsize=(9, 6), sharex=True, gridspec_kw={"height_ratios": [2, 1]})
m = (wl >= 768) & (wl <= 792)
for dv in ("DV=300", "DV=400", "DV=500"):
    a1.plot(wl[m], spec[dv][m], color=COL[dv], label=dv)
a1.set_ylim(0, 400); a1.set_ylabel("counts"); a1.legend(); a1.grid(alpha=0.3)
a1.annotate("← Ar 772.4(超出刻度)", (774.3, 370), ha="left", fontsize=9)
d = spec["DV=500"] - spec["DV=400"]
a2.plot(wl[m], d[m], color="k"); a2.axhline(0, color="gray", lw=0.6)
for x, lab in [(777.4, "O 777.4"), (775.3, "N2 1PG")]:
    a2.axvline(x, color="#d62728" if x > 777 else "#2ca02c", ls="--", lw=0.9)
    a2.annotate(lab, (x, a2.get_ylim()[1] * 0.85 if a2.get_ylim()[1] > 0 else 40), ha="center", fontsize=9)
a2.set_ylabel("DV500 - DV400"); a2.set_xlabel("波長 (nm)"); a2.grid(alpha=0.3)
fig.suptitle("圖 2  777 nm:DV500 的增量是 774–776 nm 的 N2 第一正帶斜坡,777.4 沒有 O 峰")
fig.tight_layout(); fig.savefig(os.path.join(OUT, "production-20261126-o777-zoom.png"), dpi=110); plt.close(fig)

# 圖 3:逐 step 的比值(離線 N2 337 寬背景 + 引擎 P_Ratio)
ratio = os.path.join(D, "P_Ratio_1126165108.csv")
eng = {}
if os.path.exists(ratio):
    with open(ratio, encoding="utf-8-sig") as f:
        r = list(csv.reader(f))
    h = r[0]
    sec = lambda s: sum(float(v) * k for v, k in zip(s.split(":"), (3600, 60, 1)))
    rows = [(sec(x[0][11:]), x) for x in r[1:] if x[1] != ""]
    for s in steps:
        t0 = sec(s["t"][0])
        sel = [x for tt, x in rows if t0 + 12 <= tt <= t0 + 40]
        eng[s["name"]] = {c: np.mean([float(x[h.index(c)]) for x in sel if x[h.index(c)] != ""])
                          for c in h if c.endswith("]")}
pos = np.arange(1, len(steps) + 1)
n2 = [1e3 * np.median(band(wl, s["X"][s["idx"]], 335.5, 337.2, (331, 332.5), (338.3, 339.7)) / s["ar"][s["idx"]]) for s in steps]
panels = [("N2 337 / Ar ×1e-3\n(離線,寬背景,逐幀中位數)", n2)]
for c in ("N2 337 / Ar 750 [R_82ecc466]", "CN 388 / Ar 750 [R_d1cfada5]", "O 777 / Ar 750 [R_58006cc7]"):
    if eng:
        panels.append((f"{c.split(' [')[0]}(引擎)", [eng[s['name']][c] for s in steps]))
fig, axs = plt.subplots(len(panels), 1, figsize=(9, 2.3 * len(panels)), sharex=True)
for ax, (title, y) in zip(np.atleast_1d(axs), panels):
    ax.plot(pos, y, color="#bbbbbb", lw=0.8, zorder=1)
    for p, yy, s in zip(pos, y, steps):
        ax.scatter(p, yy, color=COL[s["dv"]], s=36, zorder=2)
    ax.set_ylabel(title, fontsize=8.5); ax.grid(alpha=0.3)
np.atleast_1d(axs)[-1].set_xlabel("recipe step 位置(17:00:57 起連續 16 步,沒有 precoat)")
np.atleast_1d(axs)[-1].set_xticks(pos)
handles = [plt.Line2D([], [], marker="o", ls="", color=COL[d], label=d) for d in DVS]
np.atleast_1d(axs)[0].legend(handles=handles, ncol=5, fontsize=8, loc="upper left")
fig.suptitle("圖 3  逐 step:只有 DV500 讓 N2 跳起來;CN/Ar 前段是位置衰減")
fig.tight_layout(); fig.savefig(os.path.join(OUT, "production-20261126-step-trend.png"), dpi=110); plt.close(fig)
print("done", OUT)
