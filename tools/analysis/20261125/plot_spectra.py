"""11/25 新機台 precoat / recipe 的電漿光譜圖(docs/production-20261125-precoat-recipe-leak-strategy-zh-TW.md 圖 1–3)。

需要 numpy 與 matplotlib —— WSL 的 python3 沒有,用 Windows 的 Python 跑:
    python.exe tools\\analysis\\20261125\\plot_spectra.py [資料夾] [輸出資料夾]
預設 C:\\DualOES\\202611\\25 與 docs\\images。

譜是各幀的中位數,再扣暗場錄檔(11:28:17)的中位數:
- precoat:P_OES1_1125113613 的穩態幀(N2 337 > 0.9 × 最大值)
- recipe 穩態:precoat 之後第 10–19 步(11:54:47 起)的穩態幀,選法與 step_stats.py 相同
- recipe 頭幀:11:58:54 以後 7 個 step 的第一幀(每個 step 開頭含 N2 / N2O 的轉換幀)
"""
import glob, os, sys
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

D = sys.argv[1] if len(sys.argv) > 1 else r"C:\DualOES\202611\25"
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "..", "..", "..", "docs", "images")
plt.rcParams["font.sans-serif"] = ["Microsoft JhengHei", "DejaVu Sans"]
plt.rcParams["axes.unicode_minus"] = False


def load(path):
    with open(path, encoding="utf-8-sig") as f:
        wl = np.array([float(x) for x in f.readline().strip().split(",")[1:]])
        rows = [[float(x) for x in l.rstrip().split(",")[1:]] for l in f if l.count(",") == len(wl)]
    return wl, np.array(rows)


def peak(wl, X, c, hw=1.0, bg=1.5):
    m = (wl > c - hw) & (wl < c + hw)
    fl = ((wl > c - hw - bg) & (wl <= c - hw)) | ((wl >= c + hw) & (wl < c + hw + bg))
    return X[:, m].max(axis=1) - np.median(X[:, fl], axis=1)


rec = lambda name: os.path.join(D, f"P_OES1_{name}.csv")
wl, X = load(rec("1125112817"))
dark = np.median(X, axis=0)

_, X = load(rec("1125113613"))
n2 = peak(wl, X, 336.7)
pre = np.median(X[n2 > 0.9 * n2.max()], axis=0) - dark

steady, head = [], []
for p in sorted(glob.glob(os.path.join(D, "P_OES1_1125*.csv"))):
    name = os.path.basename(p)[7:17]
    if p.endswith("summary.csv") or name < "1125115447":
        continue
    _, X = load(p)
    ar, n2 = peak(wl, X, 750.8), peak(wl, X, 336.7)
    steady.extend(X[(ar > 0.8 * ar.max()) & (n2 < 150)])
    if name >= "1125115854":
        head.append(X[0])
recipe = np.median(np.array(steady), axis=0) - dark
headf = np.median(np.array(head), axis=0) - dark
os.makedirs(OUT, exist_ok=True)


def label(ax, s, marks, ymax):
    for w, txt in marks:
        i = np.argmin(abs(wl - w))
        y = s[max(0, i - 3):i + 4].max()
        ax.annotate(txt, (wl[i], y), xytext=(0, 4), textcoords="offset points",
                    ha="center", va="bottom", fontsize=7.5, rotation=90)
    ax.set_ylim(-0.02 * ymax, ymax)


# 圖 1:全譜,precoat 與 recipe 穩態
fig, axs = plt.subplots(2, 1, figsize=(11, 7.2), sharex=True)
axs[0].plot(wl, pre, lw=0.8, color="#1f5fa8")
axs[0].set_title("precoat(SiH4 750 / N2 1 700 / N2O 16 000 sccm,1 800 W)— 11:36:13,58 幀中位數", fontsize=10)
label(axs[0], pre, [(236.0, "NO γ"), (315.1, "N2 315.9"), (336.5, "N2 337.1"), (353.3, "N2 353.7"),
                    (357.1, "N2 357.7"), (379.9, "N2 380.5"), (399.4, "N2 399.8"), (405.3, "N2 405.9"),
                    (750.8, "Ar 750(無)"), (777.4, "O 777(無)")], 7000)
axs[1].plot(wl, recipe, lw=0.8, color="#b5431f")
axs[1].set_title("recipe 穩態(C2H2 250 / He 5 000 / Ar 10 000 sccm,500 W)— 第 10–19 步,穩態幀中位數", fontsize=10)
label(axs[1], recipe, [(388.4, "CN 388"), (419.6, "Ar I 419.8"), (476.5, "Ar II"), (516.3, "C2 516"),
                       (588.0, "He 587.6"), (656.4, "Hα"), (668.1, "He 667.8"), (696.7, "Ar 696.5"),
                       (706.8, "He 706.5"), (738.3, "Ar 738.4"), (750.8, "Ar 750.4"), (763.7, "Ar 763.5"),
                       (777.4, "O 777(無)"), (801.2, "Ar 801"), (811.5, "Ar 811.5"), (842.4, "Ar 842.5")], 26000)
for ax in axs:
    ax.set_ylabel("counts(扣暗場)")
    ax.grid(alpha=0.25)
axs[1].set_xlabel("讀值波長 (nm)")
axs[1].set_xlim(180, 850)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "production-20261125-spectra-overview.png"), dpi=110)

# 圖 2:recipe 頭幀 vs 穩態
fig, ax = plt.subplots(figsize=(11, 4.2))
ax.plot(wl, headf, lw=0.8, color="#7a3fa0", label="頭幀(11:58 以後 7 個 step 的第一幀,中位數)")
ax.plot(wl, recipe, lw=0.8, color="#b5431f", label="穩態(第 10–19 步)")
label(ax, headf, [(336.5, "N2 337"), (357.1, "N2 357"), (388.4, "CN 388"), (421.6, "CN 421.6"),
                  (750.8, "Ar 750"), (777.4, "O 777")], 26000)
ax.set_xlim(180, 850)
ax.set_xlabel("讀值波長 (nm)")
ax.set_ylabel("counts(扣暗場)")
ax.legend(fontsize=8.5, loc="upper left")
ax.grid(alpha=0.25)
fig.tight_layout()
fig.savefig(os.path.join(OUT, "production-20261125-recipe-head-vs-steady.png"), dpi=110)

# 圖 3:漏氣指標的放大 —— recipe 穩態對暗場雜訊,頭幀 ×0.1 標出 N / O 出現時線在哪裡
_, X = load(rec("1125112817"))
dsd = (X - np.median(X, axis=0)).std(axis=0)          # 暗場單幀雜訊
wins = [(328, 344, "N2 337(帶峰讀在 336.3–336.7)"), (302, 318, "OH 309"), (380, 394, "CN 388(帶頭 388.3)"),
        (774, 781, "O 777.2(讀在 777.4)")]
fig, axs = plt.subplots(1, 4, figsize=(12, 3.8))
for ax, (lo, hi, t) in zip(axs, wins):
    m = (wl >= lo) & (wl <= hi)
    ax.fill_between(wl[m], -2 * dsd[m], 2 * dsd[m], color="0.8", label="暗場 ±2σ(單幀)")
    ax.plot(wl[m], recipe[m], color="#b5431f", lw=1.3, label="recipe 穩態")
    ax.plot(wl[m], 0.1 * headf[m], color="#7a3fa0", lw=0.9, ls="--", label="recipe 頭幀 ×0.1")
    ax.set_ylim(-40, 1.15 * max(recipe[m].max(), 0.1 * headf[m].max()))
    ax.set_title(t, fontsize=9)
    ax.grid(alpha=0.25)
    ax.set_xlabel("nm")
axs[0].set_ylabel("counts(扣暗場)")
axs[3].legend(fontsize=7.5, loc="upper right")
fig.tight_layout()
fig.savefig(os.path.join(OUT, "production-20261125-leak-lines-zoom.png"), dpi=110)
print("wrote 3 figures to", os.path.abspath(OUT))
