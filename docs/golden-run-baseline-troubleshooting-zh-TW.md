# Golden Run baseline 建不起來：判讀與處置

> **用途**：Golden Run 擷取完成後，某些 ratio 顯示 **No Baseline**（或整次擷取被丟棄）時，判斷它是**故障**還是**這個工具的正常狀態**，並決定要不要動、動哪裡。
>
> **這份文件寫給誰**
> - **Operator**：只看第 [0](#0-開始之前一條原則) 節。你要做的只有一件事——**把對話框上那句英文原文抄下來**（連同時間），交給 Engineer。那句話是唯一保證存在的證據，按下「確定」之後畫面上不會再有它。
> - **Engineer／設備工程師**：全部。所有補救動作都在 Engineer 權限的 Ratio Setup／Configuration 分頁。
>
> **相關文件**
> - 名詞定義（Golden Run baseline、Absent-line baseline、Reactant-gas regime、Pedestal）：[`CONTEXT-zh-TW.md`](CONTEXT-zh-TW.md)
> - App 操作與參數表：[`user-manual-zh-TW.md`](user-manual-zh-TW.md)
> - 巡檢分工（L1／L2）：[`daily-inspection-plan-zh-TW.md`](daily-inspection-plan-zh-TW.md)
> - 洩漏率校正的數學：[`leak-rate-calibration.md`](leak-rate-calibration.md)

---

## 目錄

- [0. 開始之前：一條原則](#0-開始之前一條原則)
- [1. 先分辨：這條線屬於哪一種 regime](#1-先分辨這條線屬於哪一種-regime)
- [2. 兩個孿生失敗](#2-兩個孿生失敗)
- [3. 對照表：擷取後跳出的那句話](#3-對照表擷取後跳出的那句話)
- [4. 交叉表：同一句話在不同模式下的意義](#4-交叉表同一句話在不同模式下的意義)
- [5. regime 1 的建議做法](#5-regime-1-的建議做法)
- [6. regime 2 的判斷準則與調校](#6-regime-2-的判斷準則與調校)
- [7. 已知盲點：目前診斷不到的東西](#7-已知盲點目前診斷不到的東西)
- [附錄 A：本機現況核對與處方](#附錄-a本機現況核對與處方)
- [附錄 B：常數與它們的出處](#附錄-b常數與它們的出處)

---

## 0. 開始之前：一條原則

**先問「這條線在無洩漏時該不該在？」，再問「為什麼沒有 baseline」。**

順序反過來的話，你會把一個正確的拒絕當成故障來排除，而排除故障的動作（把 `Min SNR` 調到 0、把記錄器的觸發門檻調低、把波長容差放寬）確實會讓紅字消失——它會給你一份**通過每一道檢查、但量的是背景雜訊**的 baseline，而且之後不會再有任何警告。那比 No Baseline 危險得多，詳見 [2.2](#22-假-baseline通過每一道-gate量到的是背景)。

給 Operator 的三件事：

1. 對話框標題是 **`Golden Run captured with warnings`**（部分項目沒拿到）或 **`Golden Run capture failed`**（整次被丟棄，原本的基準值沒被換掉）。
2. **把 `•` 開頭的每一行原文抄下來**，包含 ratio 名稱與後面那句英文。同一份紀錄也會寫進 Logs 分頁與系統紀錄 CSV（事件代碼 `GoldenRunRatioDropped`／`GoldenRunRatioLowSnr`／`GoldenRunRatioUnstableBaseline`）。
3. **不要重按一次擷取就當作解決了**。同名擷取會覆蓋，覆蓋前那次的證據就沒了。

---

## 1. 先分辨：這條線屬於哪一種 regime

這個工具偵測的是**空氣漏進真空腔**，也就是氮與氧。所以被監看的元素在腔體裡的正常存量，取決於製程本身：

| | **regime 1：製程不通被監看元素** | **regime 2：製程刻意通入 N₂／O₂ 當反應氣體** |
|---|---|---|
| 無洩漏時，訊號線 | 不存在，讀數就是雜訊水準 | 明確存在，而且很強 |
| 要偵測的是 | 譜線的**出現** | 大訊號上的**小增量** |
| 正確的判準 | 高於雜訊 N 個 σ | 佔基準值的百分之幾（% of baseline） |
| Golden Run baseline 的真面目 | **零基線**（Absent-line baseline） | 真實訊號 |
| 擷取時會發生什麼 | 減背景的萃取模式**必然**被 `mean > 10 σ` 拒絕 | 正常通過 |
| **No Baseline 代表** | **預期行為，不是故障** | **故障，要查** |
| 靈敏度 | 最好（零背景） | 差，且與氣體流量本身的變動競爭 |

**判斷方式**：看這台機器該 recipe 的氣體表。被監看的元素（N 或 O）有沒有出現在 MFC 的設定裡？有 → regime 2；沒有 → regime 1。同一台機器的不同 step 可以分屬不同 regime，這時以**要監測的那個 step** 為準。

> **兩個 regime 是兩個不同的偵測問題，不是同一題的難易差別。** 把 regime 1 的項目套上 regime 2 的判準（% of baseline），得到的不是比較不準的答案，而是沒有意義的答案——分母本來就該是零。

---

## 2. 兩個孿生失敗

兩者同源：**Golden Run baseline 同時被當成「參考點」和「除數」用**。只有除數需要平均值明顯離零。

### 2.1 No Baseline：誠實的拒絕

程式在擷取結束時對每一筆 ratio 做這個檢查（`LeakMonitorEngine.FinalizeCapture`）：

```
mean <= 0  或  mean < 10 × σ   →  拒絕，記 GoldenRunRatioUnstableBaseline
```

它保護的是**除法**，不是偵測：`% of baseline`、`warn 1.05×`／`alarm 1.12×` 這種倍率門檻、以及洩漏率校正的 `x = value / baseMean − 1`，全都要除以這個平均值。平均值若不明顯離零，這些量算出來只是雜訊的正負號。

所以在 regime 1，一條**沒有洩漏就不存在的譜線**，用 `PeakHeight` 或 `Integral`（會減掉連續背景）萃取時，平均值本來就在零附近，甚至因為連續背景是凸的、線性內插畫出來的基線壓在上面而**系統性為負**——它一定被拒絕。這時：

- 程式邏輯是對的（它不該讓你除以零）；
- 但**模型是錯位的**（這個場景根本不該用除法）；
- 正確的處置在 [第 5 節](#5-regime-1-的建議做法)，不是放寬檢查。

### 2.2 假 baseline：通過每一道 gate，量到的是背景

`RawMean` 模式**什麼都不減**，所以它讀到的是「連續背景 + 譜線」。在 regime 1，譜線不存在，讀到的就是純連續背景——一個又大又穩的數字（實測案例：440.7 nm 無譜線處為 `3745 ± 19`）。於是：

- `mean > 10 σ` **輕鬆通過**（3745 / 19 ≈ 197）；
- SNR 檢查**完全不作用**（`RawMean` 不估雜訊，`LineMeasurement.Snr` 回傳 `NaN`，而低訊號判斷只在 `!IsNaN(Snr)` 時成立）；
- 畫面上得到一筆平均值漂亮、σ 極小的 baseline；
- **它量的是背景，不是那個元素。而這件事不會亮任何紅字。**

**現場檢查法**（不需要停機）：

1. 到 **Recordings** 分頁，載入一段含電漿的錄製。
2. 在 line view 同時加入 **該監看波長** 與 **一段確定沒有譜線的鄰近波長**（例如 440.7 nm；catalog 給不出這種波長，要用手打，這正是手打欄位存在的理由）。
3. 勾選 **Normalize**（各自除以自身平均）。
4. **兩條線若形狀一致、相對散度也一致 → 這筆 ratio 沒有在量該元素**，它量的是連續背景。

**數值判準**：該 ratio 的 baseline 平均值，與鄰近無譜線波長的 `RawMean` 是否落在同一量級且同步變動。是 → 假 baseline。

---

## 3. 對照表：擷取後跳出的那句話

程式只會給出下面這幾句（`LeakMonitorEngine.ReportDroppedRatio` 及兩個後續檢查）。找到你抄下來的那一句，往右讀。

| 對話框／紀錄裡的句子（節錄） | 它實際在說什麼 | 動作 | 怎麼確認有效 |
|---|---|---|---|
| `fell outside the spectrometer wavelength range in every frame` | 監看波長不在這次的光譜軸上 | 查光譜軸。紀錄裡若同時有 `axis 1904 → 1000, axis 179.8–850.3 → 200–799.4 nm`，代表這是**測試模式的合成軸**，不是真的量測 | 重新擷取後這句消失；`DevicePanel` 頁尾沒有紅色 `Test Mode` |
| `no frame passed the plasma-present gate` | **不是「沒有光」**，是記錄器的觸發量測沒跨過門檻。電漿閘刻意與記錄器觸發同一個量測 | 對照 Configuration 的 `TriggerMode`／`TriggerPercentile`／`SaveStartThresholdIntensity` | 資料夾 `YYYYMM\DD\` 下有 `P_OES1_*.csv` 正在長大——這等同於閘門是開的 |
| `no frame produced a usable value while the gate was open` | 絕對強度模式：閘門開著但算不出值 | 檢查該譜線的萃取視窗與波長是否在軸上 | 同上 |
| `the reference line X never registered` | 比值模式的**分母** NaN 或 ≤ 0。凸型連續背景會讓減背景的分母系統性為負 | 分母改用 `RawMean`，或換一條參考線（`ReferenceLineCatalog`） | 下次擷取該項有 baseline，且分母平均值為正 |
| `stayed below the SNR floor … in every usable frame` | **只可能來自分母**——`RawMean` 分子的 SNR 是 `NaN`，不參與判斷 | 先動分母的萃取模式，**不要先動 `Min SNR`** | 下一句（`only k of m`）的比例過半 |
| `only k of m frames cleared the SNR floor` | 譜線在雜訊邊緣游走，通過率不足一半（`MinBaselineAcceptFraction = 0.5`） | 同上；若確認是分子太弱，回到 [第 5 節](#5-regime-1-的建議做法)，不是調低門檻 | 同上 |
| `no frame had both lines valid` | 混合型：分子與分母各有一部分無效 | 先把分母解決，再看剩下什麼 | 句子換成更明確的另一句 |
| `the ratio was disabled for the whole capture` | 設定問題，不是量測問題 | Ratio Setup 勾選啟用，重新擷取 | — |
| `no spectrum frames were processed during the capture window` | 整段沒有任何幀進到引擎：沒在 acquiring，或 Leak Monitor 未啟用 | 確認 Start 已按下、`leakMonitor.enabled = true` | 這屬於「整次失敗」，不是部分失敗 |
| **`mean X ± Y is not clear of zero (needs mean > 10 σ)`** | **在 regime 1 這是預期結果**，不是故障 | 到 [第 5 節](#5-regime-1-的建議做法)。在 regime 2 出現才要查訊號為何消失 | — |

> **`Min SNR` 幾乎永遠不是第一個該動的東西。** 你目前的設定裡分子全是 `RawMean`，它的 SNR 是 `NaN`，所以 `Min SNR` 對分子完全沒有作用——調它只會改變分母的判斷。

---

## 4. 交叉表：同一句話在不同模式下的意義

| | `PeakHeight` / `Integral`（減連續背景） | `RawMean`（什麼都不減） |
|---|---|---|
| SNR 是否存在 | 有（值 ÷ 局部連續背景雜訊） | **沒有**（`NaN`）——SNR 相關的兩句話對它**不可能出現** |
| regime 1 的平均值 | 約為零，或系統性為負 | 大而穩，但那是**連續背景** |
| `mean > 10 σ` 檢查 | **必然失敗** → No Baseline | **必然通過** → 有 baseline，但可能是假的 |
| 帶 pedestal？ | 否 | 是 |
| `ValueHasPedestal` 判定 | 否 | **僅在 `AbsoluteIntensity` 模式下為真**（見 [第 7 節](#7-已知盲點目前診斷不到的東西)） |
| 門檻怎麼算 | `max(倍率×mean, mean + 3σ)` | pedestal 成立時只用 σ 項；不成立時仍走倍率 |

**兩個直接可用的推論**：

- 看到 SNR 相關的句子 → 問題**一定**在分母（比值模式），或該項不是 `RawMean`。
- 看到 `mean … not clear of zero` → 分子**一定**是減背景的模式；在 regime 1 這是正確拒絕。

---

## 5. regime 1 的建議做法

**主推：`MonitorMode = AbsoluteIntensity` + 分子 `LineExtractMode = RawMean`。**

這是目前引擎中**唯一從擷取、判讀、顯示到洩漏率校正都一致**的路徑。它讓 `RatioDefinition.ValueHasPedestal` 成立，於是：

- **門檻**：忽略 `WarnFactor`／`AlarmFactor`（對 pedestal 而言 1.05× 是幾十個 σ），只用 `mean + SigmaWarn·σ`（預設 3σ／6σ）；
- **顯示**：% -of-baseline 欄位改成 σ 分數 `100 + (20/SigmaWarn)·(ema − baseMean)/σ`——基準仍是 100，一個 warn-σ 落在 120，所以 100/120/150 三條參考線照用；
- **趨勢斜率**：讀數為 σ/min；
- **洩漏率校正**：擬合**絕對增量** `Δ = value − baseMean`，而不是分數增量；
- **CSV 欄位名**：`_sigmaScore`（不是 `_pctBaseline`），所以一年後打開檔案的人知道那一欄是什麼。

**代價，要寫在交接文件上**：

- 不再除以 Ar 參考線，**失去電漿條件漂移的抵消**。需要穩定的操作點，並且要比比值模式**更常重新擷取 Golden Run**。
- 曝光時間／平均次數／背景移除開關一改，絕對強度就整個重新縮放。引擎會記 `LeakMonitorAcquisitionMismatch` 並在 Leak Monitor 橫幅提示，看到就要重新擷取。

**明確不建議：`MonitorMode = Ratio` + 分子 `RawMean`。**
它可以設定、也會通過所有檢查，但它落在 `ValueHasPedestal` 的判斷式之外（該判斷式只涵蓋絕對強度模式），因此得到的是「連續背景 ÷ Ar」的百分比：數字看得懂，語意卻不是「該元素相對基準上升了幾 %」。實務後果是門檻被 pedestal 稀釋——以實測基準 `0.02895 ± 0.00024`（mean/σ ≈ 120）為例，`warn 1.05×` 等於 `mean + 6σ`，比設定上寫的 `SigmaWarn = 3` 嚴格一倍，而你不會從任何畫面看出這件事。

**`AbsoluteIntensity` + `PeakHeight`**（真的減掉連續背景、平均值誠實地趨近零）在目前版本**必定**被 `mean > 10 σ` 拒絕。這是已知限制，見 [第 7 節](#7-已知盲點目前診斷不到的東西)。

---

## 6. regime 2 的判斷準則與調校

製程刻意通入被監看元素時，該譜線的存在不構成漏氣證據。要先回答一個問題：

> **漏氣預期造成的增量，有沒有大過製程氣體本身造成的變動？**

用一個可以現場估的比值：

```
判斷比 =  漏氣預期增量 ÷ (該譜線在正常運轉下的 σ)
```

分母直接讀 Golden Run 擷取後那筆 baseline 的 σ；分子若沒有校正漏，用「可接受漏率造成的分壓」除以「製程氣體分壓」乘上目前讀數估算即可，量級對就夠了。

| 判斷比 | 結論 | 做法 |
|---|---|---|
| 明顯 > 6 | 可以監測 | 用 % of baseline，門檻照常 |
| 3 – 6 | 勉強，容易誤報／漏報 | 收緊 `WarnFactor`／`AlarmFactor`，同時**拉長** `ConfirmSeconds`（預設 15 s），倚賴引擎的即時 σ 加寬去吸收氣體流量抖動 |
| < 3 | **這個 step 不要用這條線判漏** | 改看**另一個元素**（通 N₂ 就看 O，通 O₂ 就看 N），或只在**不通該氣體的 step** 監測 |

**兩個常犯的錯**：

- 用同一組 ratio 涵蓋整個 recipe。通氣與不通氣的 step 屬於不同 regime，同一個基準值不可能同時對兩者成立。正確做法是為要監測的 step 各自擷取 Golden Run，並在切換 recipe 時一併切換基準值（引擎會自動配對綁定的校正）。
- 只監測氮。空氣是氮**加**氧，只有氮的項目時，通 N₂ 的 step 就完全沒有第二意見。至少配一條氧或 OH 的項目當獨立判斷。

---

## 7. 已知盲點：目前診斷不到的東西

以下是目前版本**測不到或無法區分**的事。列出來是為了讓現場知道哪一格只能靠人工判讀，不是待辦清單。

| 盲點 | 後果 | 現在只能怎麼辦 |
|---|---|---|
| 擷取診斷把「分母系統性為負」與「分母 NaN」併成同一個計數（`ReferenceMissing`） | 兩種完全不同的原因給出同一句話 | 到 Recordings 看該分母波長的原始值是負的還是缺的 |
| `mean > 10 σ` 的檢查只在整個擷取窗結束後才做 | 設 120 秒就要等滿 120 秒才知道失敗 | 先用短擷取（10 秒）試一次，確認會過再擷取正式的 |
| 沒有任何地方記錄「這次擷取電漿閘開了幾成」 | 「部分幀被丟掉」看不出來 | 對照同時段 `P_OES1_*.csv` 的列數與擷取秒數 × 幀率 |
| **`ValueHasPedestal` 不涵蓋 `Ratio` + `RawMean`** | 帶 pedestal 的量走一般百分比與倍率門檻，語意與門檻都被稀釋（見 [第 5 節](#5-regime-1-的建議做法)） | 改用 `AbsoluteIntensity` + `RawMean`；或知道那個百分比不是「該元素上升幾 %」 |
| `AbsoluteIntensity` + `PeakHeight` 在 regime 1 必定被拒 | 誠實的零基線目前無法被接受 | 用 `RawMean` 走 pedestal 路徑 |

---

## 附錄 A：本機現況核對與處方

依 `%AppData%\OES_Leak_Monitor\settings.json`（作用中基準值 `Recipe Ar`，2026-08-14 擷取，8 筆 baseline）。

> **照做之後必須重新擷取 Golden Run。** 更動 `MonitorMode` 會讓 `Recipe Ar` 裡對應那筆 baseline 被判定為「量的是不同的東西」，開機時會記 `LeakMonitorBaselineMismatch`，該項在重新擷取前不參與警報。紀錄裡已有兩筆同類前例（2026-08-14 22:03，`uN2 335.5`／`uN2 324.7`）。**這是預期後果，不是新的故障。**

| key | 名稱 | 現況 | 啟用 | 判讀語意 | 建議 | 立即後果 |
|---|---|---|---|---|---|---|
| `R_ec3adb8f` | N2 337.1 / Ar 750.4 | `Ratio` + 分子 `RawMean` | 是 | **pedestal 走一般百分比**，門檻實為 6σ 而非 3σ | 改 `AbsoluteIntensity`（分子保持 `RawMean`） | 該項 baseline 失效，須重擷取 |
| `R_54c914c8` | N2 337.1 (abs) | `AbsoluteIntensity` + `RawMean` | 是 | **正確**：σ 分數、σ 門檻、Δ 校正 | 維持。這是 337.1 目前唯一語意正確的來源 | 無 |
| `R_7a6c5d0f` | uN2 335.5 / Ar 750.4 | `Ratio` + 分子 `RawMean` | 是 | 同 `R_ec3adb8f` | 改 `AbsoluteIntensity`，或另建一條 abs 版本並停用本項 | 同上 |
| `R_353b7e10` | N2 353.7 / Ar 750.4 | `Ratio` + 分子 `RawMean` | 是 | 同上 | 同上 | 同上 |
| `R_7c40d2f9` | NO 237 / Ar 750.4 | `Ratio` + 分子 `RawMean` | 否 | 同上 | 若要啟用，直接建成 `AbsoluteIntensity` | — |
| `R_54858314` | NO 237 (abs) | `AbsoluteIntensity` + `RawMean` | 否 | 正確 | 需要第二個元素的獨立意見時啟用這條 | — |
| `R_a2ff1794` | N2+ 391.4 / Ar 750.4 | `Ratio` + 分子 `RawMean` | 否 | 同上 | 同 `R_7c40d2f9` | — |
| `R_df502a98` | N2+ 391.4 (abs) | `AbsoluteIntensity` + `RawMean` | 否 | 正確 | 需要時啟用 | — |
| `R_e3b669d5` | uN2 324.7 / Ar 750.4 | `Ratio` + 分子 `RawMean` | 否 | 同上 | 同 `R_7c40d2f9` | — |

**兩個與上表無關、但一起看才看得出來的觀察**：

- 這九筆的 `MinSnr`（2 或 5）**目前幾乎不起作用**：分子全是 `RawMean`（SNR 為 `NaN`），所以它只在比值模式下作用於分母 `Ar 750.4 PeakHeight`，在絕對強度模式下完全不作用。調它之前先確認你想影響的是哪一條線。
- **沒有任何一筆 ratio 監測氧。** `MonitoredWavelengths` 有 308.9／309.3 nm（OH），但那只進到強度 CSV 與趨勢圖，不構成判斷。空氣漏是氮加氧；目前所有判斷都建立在氮上，通 N₂ 的 step 會完全失去獨立意見。建議至少建一條 `AbsoluteIntensity` + `RawMean` 的 O 或 OH 項目。

---

## 附錄 B：常數與它們的出處

| 常數 | 值 | 在哪 | 它保護什麼 |
|---|---|---|---|
| `MinBaselineMeanToSigma` | 10 | `LeakMonitorEngine` | 除法：`% of baseline`、倍率門檻、洩漏率擬合的分母 |
| `MinBaselineAcceptFraction` | 0.5 | `LeakMonitorEngine` | 防止只用「向上雜訊」那一小撮通過的幀去組成基準 |
| `RatioDefinition.MinSnr` | 預設 5 | `LeakMonitorSettings` | 兩條線是否可信；對 `RawMean` 無效 |
| `SigmaWarn` / `SigmaAlarm` | 3 / 6 | `LeakMonitorSettings` | σ 門檻；pedestal 成立時是**唯一**的門檻 |
| `WarnFactor` / `AlarmFactor` | 本機 1.05 / 1.12 | `settings.json` | 倍率門檻；pedestal 成立時被忽略 |
| `ConfirmSeconds` | 15 s | `LeakMonitorSettings` | 把突波和洩漏分開 |
| `GoldenRunCaptureSeconds` | 本機 120 s | `settings.json` | 擷取窗長度 |
| 擷取期間的電漿地板 | 站下（floor = 0） | `LeakMonitorEngine.ProcessSample` | 讓上一份 Golden Run 的地板不會擋住新的擷取 |

門檻的完整式子：`Warning` 觸發於 `max(WarnFactor × mean, mean + SigmaWarn × σ)`；`ValueHasPedestal` 成立時倍率項被丟棄。
