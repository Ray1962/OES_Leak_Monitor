# 上機前比值重量 SOP(11/23 兩段製程)

目的:用 App 本身的譜線抽取,重量 `production-20261123-leak-strategy-zh-TW.md` §4 的比值,確認與離線數字一致。

| 方法 | 誰 | 何時 |
|---|---|---|
| **A. Baseline Builder**(主要) | Operator 可做 | 隨時;不影響量測 |
| **B. Replay**(選用) | Engineer | 只在**不量測時**(要斷開光譜儀) |

> ⚠️ 全程**不按** `Save as active baseline`。

---

## 0. 開始前確認(Engineer 已完成)

- [ ] Ratio Setup 已有策略文件 §5.2 的 9 組比值
- [ ] Process Classifier 已啟用:`Ar 750.4 / O 777.2 > 0.5 → P1_Ar`,其餘 `P2_NoAr`
- [ ] 做完本 SOP 之前,不改 Ratio Setup、Wavelength Calibration、Logger 觸發門檻

---

## A. Baseline Builder

### A1. 列出錄檔

1. 進 **Baseline Builder** 分頁。
2. **From** 設成 `2026-11-23`,**To** 設成 `2026-11-23` → 按 **Refresh**。
   → 狀態列:`{n} recording(s) in …`
3. 在 **Use** 欄勾選下列 8 筆(其他不勾):

| 錄檔 | 預期 class | 選取視窗(s) |
|---|---|---|
| `P_OES1_1123154507` | `P2_NoAr` | 5.5 – 33.0 |
| `P_OES1_1123154602` | `P1_Ar` | 5.6 – 74.9 |
| `P_OES1_1123154822` | `P2_NoAr` | 5.5 – 31.9 |
| `P_OES1_1123154917` | `P1_Ar` | 5.5 – 74.8 |
| `P_OES1_1123155141` | `P2_NoAr` | 5.6 – 31.9 |
| `P_OES1_1123155236` | `P1_Ar` | 5.6 – 74.8 |
| `P_OES1_1123155443` | `P2_NoAr` | 6.3 – 32.6 |
| `P_OES1_1123155536` | `P1_Ar` | 6.7 – 74.9 |

### A2. 掃描

4. 按 **Scan ticked**,等按鈕恢復可按。
   → **State** 欄,每筆應為:
   - `P1_Ar · 約 80 frames, 約 89 s`
   - `P2_NoAr · 約 40 frames, 約 43 s`
   - 若出現 `Unknown ·`,或 class 跟上表不同:**停**,通知 Engineer(分類器設定有問題)。

### A3. 選視窗

5. 逐筆點選 → 在 **Baseline window:** 兩格輸入上表的秒數(按 Tab 離開欄位才生效)。
   → 該列 **Window** 欄出現數字。
   - 不要用 `Whole file`:每段結尾都有幾秒是碳源已經關掉的過渡段。
6. 確認 8 筆的 **Window** 欄都有數字。

### A4. Build(只算,不存)

7. **Golden Run name** 填 `remeasure-20261123` → 按 **Build**。
   → 畫面最下方的結果面板出現文字。

### A5. 讀結果

8. 看 `Baselines (n):` 與 `No baseline for:`,對照下表:

| # | 比值 | class | 預期 mean | 預期結果 |
|---|---|---|---|---|
| 1 | N₂ 337.1 / Ar 750.4 | P1_Ar | 約 0.0060 | **預期被拒**(`mean/σ` 約 7.7 < 10) |
| 2 | CN 388.3 / Ar 750.4 | P1_Ar | 約 0.021 | **預期被拒或剛好過**(約 9.6) |
| 3 | NO 237 / Ar 750.4 | P1_Ar | 約 0.0093 | 預期被拒(約 3.7) |
| 4 | N₂ 380.5 / Ar 750.4 | P1_Ar | 約 0.0042 | 剛好過(約 10.4),行尾 `<< only just clears the floor` |
| 5 | O 777.2 / Ar 750.4 | P1_Ar | 約 0.83 | 通過(約 100) |
| 6 | N₂ 337.1 / O 777.2 | P2_NoAr | 約 0.0016 | 預期被拒(約 7.6) |
| 7 | CN 388.3 / O 777.2 | P2_NoAr | 約 0.0024 | 預期被拒(約 8.7) |
| 8 | NO 237 / O 777.2 | P2_NoAr | 約 0.0040 | 預期被拒(約 7.4) |
| 9 | O 844.6 / O 777.2 | P2_NoAr | 約 0.17 | 通過(約 140) |

   - 第 4 組若設成 N₂ 357.7(只用右側窗):預期 mean 約 0.015,預期被拒(約 5.9)。
   - 「預期被拒」是**正常結果**:4 次 run 的含氮指標一路在降,合併後的 σ 被拉大。
   - 被拒的比值,看 `How far the recordings disagree with each other` 那一段 → 應顯示 run 間差距是檔內散度的數倍。

9. 看 `How steady each chosen window is`:
   → 每筆 `σ/mean` 應在 10 % 以內。超過 → 視窗可能沒對準,回 A3 重設。

### A6. 單筆再量一次(只量比值 1、2)

10. 取消勾選其他錄檔,只留 `P_OES1_1123155536`(最後一次長段) → **Build**。
    → 預期比值 1 的 `mean/σ` 約 14,比值 2 約 26,兩者都通過。
    - 若比值 1、2 顯示幀數不足(少於 60 幀):把視窗起點改成 5.0 s 再 Build。
    - 單筆短段只有約 25 幀,不夠 60 幀,比值 6–9 會被拒:正常。

---

## B. Replay(Engineer,選用)

> ⚠️ 只在**機台不跑製程、光譜儀已斷線**時做。有接光譜儀時 Play 按不下去。

1. 斷開光譜儀,App 進入測試模式。
2. 進 **Replay** 分頁 → **Choose CSV…** → 選 `C:\DualOES\202611\23\P_OES1_1123155536.csv`。
3. 速度選 `20×` → **Play**,等到狀態變成 Finished。
4. 依序再播 `P_OES1_1123155443.csv`(短段)。
   → 今天的日資料夾會多出 `SIMP_OES1_*.csv`、`SIMP_Ratio_*.csv`。
5. 用 **Ratio Review** 開 `SIMP_Ratio_*.csv`。
   → 各比值的原始值應接近 A5 表的「預期 mean」;沒有 Golden Run 的比值不會有 % 值。
6. 收尾:
   - 刪除今天日資料夾裡的 `SIMP_*` 檔。
   - `C:\DualOES\_batches.csv` 會多出 Replay 的批次列,把它們刪掉。
   - 重新連上光譜儀,確認 Monitor 分頁回到即時量測。

---

## C. 紀錄

1. A5、A6 每次 Build 完,把結果面板全文複製出來,存成:
   `C:\DualOES\202611\23\_remeasure_20261123_{今天 yyyyMMdd}.txt`
   (檔名要用底線開頭,才不會被列進錄檔清單)
2. 在同一個檔案最後補上:
   - 做的人、日期
   - A2 每筆的 class
   - 與 A5 表不同的地方
3. 交給 Engineer。

---

## D. 判定

| 結果 | 判定 |
|---|---|
| class 全對,各比值的 `mean` 與預期差 ≤ 30 %,被拒和通過的情形與 A5 表一致 | **一致**,可以上機 |
| `mean` 與預期差 > 30 % | 通知 Engineer:App 的視窗設定與策略文件不同 |
| 比值 1 或 2 在 A6 單筆也被拒 | 通知 Engineer:主判比值在這台機的 App 上不穩 |
| 合併後比值 1、2 **反而通過** | 通知 Engineer:比預期穩定,門檻可以重新評估 |
