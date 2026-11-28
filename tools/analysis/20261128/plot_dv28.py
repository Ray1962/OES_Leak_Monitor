"""11/28 DV 測試的圖(docs/production-20261128-dv-test-zh-TW.md 圖 1–2)。

需要 numpy / matplotlib —— 用 Windows 的 Python 跑:
    py -3.13 tools\\analysis\\20261128\\plot_dv28.py [錄檔資料夾] [輸出資料夾]
預設 C:\\DualOES\\202611\\28 與 docs\\images。計算全部借用同資料夾的 dv_sim.py(執行時不印它的輸出)。
"""
import contextlib, csv, io, math, os, runpy, sys
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

# 圖 1:逐步平台
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
ax[0].set_title("圖 1:11/28 逐 step 平台(亮起後 15–38 s)。紅 = DV 500")
fig.tight_layout(); fig.savefig(os.path.join(OUT, "production-20261128-step-trend.png"), dpi=110)

# 圖 2:引擎 P_Ratio 一個 DV 500 step(15:47:56)的 R_41779f99:原始值 σ 與 σ 分數
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
ax.set_title("圖 2:DV 500(15:47:56)現場 N2 337 RawMean。原始值 +20σ 以上,分數卻停在 101–104")
ax.legend(fontsize=8, loc="upper center")
fig.tight_layout(); fig.savefig(os.path.join(OUT, "production-20261128-sigma-score.png"), dpi=110)
print("ok")
