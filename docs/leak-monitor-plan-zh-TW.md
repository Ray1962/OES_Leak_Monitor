# 漏氣監測計畫 — 三製程腔體

> 依據 2026-08-20/21 兩天共 226 筆全譜錄檔的分析所擬,2026-09-03 定案。
> 分析本身與圖表在 [`process-classification-20260820-21-zh-TW.html`](process-classification-20260820-21-zh-TW.html)。
>
> 這份文件記錄的是**推理鏈**,不只是結論。其中最有價值的部分是兩個**否定結果** —— 它們在
> code 裡留不下任何痕跡,下一個人若重掃一次光譜,會重新選到同樣的假贏家。
> 這與 [`postmortem-test-mode-20260817.md`](postmortem-test-mode-20260817.md) 存在的理由相同。

## 1. 這個計畫要解的問題

這座腔體以約三分鐘的週期輪流跑三段製程:

```
B(156 s) → ( A(38–40 s) → C(84–86 s) ) × N
```

而 leak monitor 只有**一組** `Ratios`、**一個** `ActiveGoldenRun`、**一個** `ActiveCalibration`。
把每一段都拿去比同一條 baseline,等於拿三種不同的電漿去比其中恰好被擷取到的那一種。
`BaselineBuilder.cs` 自己就寫著這件事:*"a recipe with several operating points needs several
Golden Runs, which the engine cannot yet"*。

更根本的是:**出廠比值組在這台機上不能用**。`R_N2Ar` / `R_NOAr` / `R_N2357Ar` 全部除以
Ar 750.4,而 Ar **只存在於製程 C**;A 與 B 完全沒有。出廠值是另一台機的已知漏氣量測選出來的,
在那台機上是對的 —— 所以我們不動 `CreateDefault()`(見 §7)。

## 2. 量測基礎

| 事實 | 數字 | 後果 |
|---|---|---|
| 三段製程輪流跑 | 20 個批次全部符合上面的句型 | 單一 baseline 沒有意義 |
| 分類靠兩道比值 | `Ar750/O777 > 0.5` → C;否則 `Hα656/O777 < 0.07` → A;其餘 B | 226/226 零錯誤,不需 MES 整合 |
| 氮沒有製程來源 | TEOS 給 C/H/O/Si,載氣給 Ar,清洗給 O | **N₂ 是唯一乾淨的漏氣示蹤物** |
| 波長間距決定一切 | 413 nm 的組合批次內漂 57 %;7.5 nm 的組合跨批次 CV 3 % | 見 §3 |
| 視窗選擇性結垢 | 一個批次內 UV/NIR 透過率比變 1.8× | 固定取樣點是繞過它的方法 |
| 窗口位置 | gate 開後 10–30 s 在三段都最穩;尾段 CV 136–241 % | 尾段是 `StopConfirmSeconds` 的 gate-closed 尾巴 |
| 波長軸 | 以九條 Ar I / O I 原子線量得整體偏高 **+0.30 nm** | 下面所有 `centerNm` 都已含這個偏移 |

### 2.1 為什麼是「每批次每段製程的第一支 step、10–30 s」

視窗在一個批次內就結垢,而且是波長選擇性的:C 的絕對 O 777 掉到 0.70 倍,但同期
`Ar750/O777` **上升** 12 %,紫外的 `CO313/O777` 掉到 0.61 倍 —— 是鍍膜對短波長吸收較強,
不是電漿變弱。長 B 清洗後下一批又回到原值。

固定在每批次的第一支 step 取樣,等於每次都在「剛清洗完」這一個操作點上比較,結垢就被繞過去了。
10–30 s 這個窗口避開兩件事:A 的點火斜坡(0–10 s 的離散度 8.7 %,10–30 s 是 1.6 %),
以及檔案尾端 gate 已關但錄檔器還在寫的那段(CO 趨近 0,比值發散)。

**窗口必須從 gate 開啟起算,不是從檔案起算** —— 錄檔的頭尾都帶著 gate-closed 的列
(`PreTriggerSeconds` 的頭與 stop-confirm 的尾,見 `PlasmaGate` 的類別註解)。

## 3. 兩個否定結果 —— 讀這節,不要重掃

### 3.1 `N₂ 315.9 / CO 313` 的 CV 1.9 % 是假的

掃描含氮特徵時,`N₂ 315.9 / CO 313` 在製程 A 給出 CV 1.9 %、mean/σ 53.6,是所有候選裡最好的。
**它是壞的。** 把 306–324 nm 逐點列出來就看得到:313.6 → 322 nm 是一條**單調平滑衰減**,
315.9 沒有帶頭 —— 那是 CO 313 帶的紅翼。這個比值等於拿同一條帶的形狀除以自己的峰,
是一個帶型常數,對漏氣完全瞎。

**CV 低得異常本身就是壞掉的證據。** 只用 CV 挑指標,一定會挑到這一類。

### 3.2 237 nm 沒有 NO γ 帶

出廠比值組的 `R_NOAr` 以 237 nm 為 NO。以 +0.30 nm 校正後,NO γ 的帶頭序列
(227 / 236 / 247 / 259 / 271)在這份資料裡:236.6 與 271.9 兩處是**凹陷**,不是峰;
247.8、259.2 勉強對得上但弱。UV 區真正的強峰在 230.05 / 232.70 / 242.17 / 244.81 /
251.62(Si I),與 NO γ 的振動序列對不上。

**`R_NOAr` 在這台機上量的不是 NO。**

### 3.3 `C2 516 / CO 484` 的 CV 1.7 % 也是假的

找 TEOS 流量守衛量時出現,同樣不能用:C₂ 與 CO 都源自 TEOS,流量等比變化時這個比值不動 ——
它正好對「TEOS 流量變了」瞎掉,而那是它唯一的用途。

守衛量要選的是 **`CO 607.9 / Ar 811.5`**(跨批次 CV 5.1 %):CO 來自 TEOS、Ar 來自載氣,
兩條都在紅端(結垢影響最小),TEOS 流量一動它就動,而漏進來的 N₂ 不影響它。

### 3.4 `CO329/CO313` 的 CV 19.9 % 不是物理

同一個帶系的兩條帶不該差這麼多。原因是 312.8 的側窗踩到 OH 309 與 CO 的尾巴 ——
**表示 313 的 peak-height 抽取本身不可靠**,不要拿它當分母。

## 4. 站點比值組

| Key | 製程 | 定義 | 角色 |
|---|---|---|---|
| `R_N2CO_A` / `_B` / `_C` | A / B / C | N₂ 337.1 ÷ CO 329.6(皆 PeakHeight) | 主判 |
| `R_N2Ar_C` | C | N₂ 337.1 ÷ Ar 750.4 | 否決票 |

一段製程一個 key,因為 `GoldenRunRatioBaseline` 是以 key 配對的,而三段的絕對水準差到十倍。

### 4.1 通過引擎自身抽取器實測(2026-09-03)

離線分析用的是自寫的 peak-height,視窗幾何與 `LineIntensityExtractor` 不同,所以**離線的 CV
不會自動移轉**。以下是把同一批錄檔餵進引擎真正的抽取器量出來的,取每批次第一支 step 的
10–30 s 中位數:

| 指標 | n(批次) | CV | mean/σ |
|---|---|---|---|
| C `R_N2CO_C` | 13 | **3.1 %** | **32.2** |
| A `R_N2CO_A` | 13 | 13.6 % | 7.3 |
| B `R_N2CO_B` | 19 | 15.5 % | 6.5 |
| C `R_N2Ar_C` | 13 | 17.3 % | 5.8 |

兩件事從這張表定下來:

1. **N₂ 的 `halfWidthNm` 收到 0.70。** 用 1.06 時 C 是 CV 3.6 % / mean/σ 27.6;收窄後
   3.1 % / 32.2,正好重現離線分析的 3.0 % / 32.9。這是唯一一個從資料調出來的參數。
2. **A 與 B 的 mean/σ 是 7.3 與 6.5,低於引擎的 `MinBaselineMeanToSigma`(10)** ——
   `FinalizeCapture` 會直接拒收它們的 baseline。這不是設定沒調好,§3.1 的掃描已經確認
   沒有更好的含氮特徵。所以 A/B **只記錄與趨勢,不發警報**(P3 的 `TrendOnly`)。
   上了會叫的警報只會教操作員忽略它。

`R_N2Ar_C` 的 mean/σ 5.8 比預期弱,所以它是**方向性的否決票**,不是一個精確的數字:
真漏氣時它應與主判同向;TEOS 流量變動只動 CO 那一條。

### 4.2 設定片段

貼進 `settings.json` 的 `leakMonitor` 區塊。`centerNm` 已含 +0.30 nm 的軸偏移。

```jsonc
"ratios": [
  {
    "key": "R_N2CO_C", "displayName": "N2 337 / CO 330 (C)",
    "enabled": true, "processClass": "C", "monitorMode": "Ratio",
    "numerator":   { "label": "N2 337.1", "centerNm": 337.40, "halfWidthNm": 0.70,
                     "baselineGapNm": 1.30, "baselineWidthNm": 1.10,
                     "mode": "PeakHeight", "peakSearchHalfWidthNm": 0.0 },
    "denominator": { "label": "CO 329.6", "centerNm": 329.60, "halfWidthNm": 1.06,
                     "baselineGapNm": 2.10, "baselineWidthNm": 1.10,
                     "mode": "PeakHeight", "peakSearchHalfWidthNm": 0.0 },
    "warnFactor": 1.05, "alarmFactor": 1.12,
    "sigmaWarn": 3, "sigmaAlarm": 6,
    "emaTauSeconds": 5, "confirmSeconds": 15, "minSnr": 3
  }
  // R_N2CO_A / R_N2CO_B:同上,processClass 換成 "A" / "B",key 與 displayName 對應改。
  // R_N2Ar_C:processClass "C",denominator 換成
  //   { "label": "Ar 750.4", "centerNm": 750.70, "halfWidthNm": 1.06,
  //     "baselineGapNm": 1.30, "baselineWidthNm": 1.10, "mode": "PeakHeight",
  //     "peakSearchHalfWidthNm": 0.0 }
]
```

**`peakSearchHalfWidthNm` 刻意設 0。** 峰搜尋會吸收波長漂移,但 N₂ 337 夾在兩個巨大的
CO 帶之間(329.6 與 348.2),搜尋窗口很容易被拉到鄰帶的肩上;軸偏移已經直接寫進
`centerNm`,不需要再搜一次。

**守衛量 `CO 607.9 / Ar 811.5` 在 P0 不進比值組。** 引擎目前沒有「記錄但不判定」的角色,
放進去會顯示成一列綠色的 Normal,那是在騙人。P1 的分析從全譜錄檔離線算它(分析本來就是這樣做的),
它在 P3 隨 `TrendOnly` 一起進來。

### 4.3 分類器設定

```jsonc
"processClassifier": {
  "enabled": true,
  "decideAfterFrames": 3,
  "fallbackClass": "B",
  "rules": [
    { "className": "C", "displayName": "Ar750/O777",
      "numerator":   { "label": "Ar 750.4", "centerNm": 750.70, "halfWidthNm": 1.06,
                       "baselineGapNm": 1.30, "baselineWidthNm": 1.10, "mode": "PeakHeight",
                       "peakSearchHalfWidthNm": 0.0 },
      "denominator": { "label": "O 777.4",  "centerNm": 777.60, "halfWidthNm": 1.06,
                       "baselineGapNm": 1.30, "baselineWidthNm": 1.10, "mode": "PeakHeight",
                       "peakSearchHalfWidthNm": 0.0 },
      "op": "GreaterThan", "threshold": 0.5 },
    { "className": "A", "displayName": "Ha656/O777",
      "numerator":   { "label": "Ha 656.3", "centerNm": 656.50, "halfWidthNm": 1.06,
                       "baselineGapNm": 1.30, "baselineWidthNm": 1.10, "mode": "PeakHeight",
                       "peakSearchHalfWidthNm": 0.0 },
      "denominator": { "label": "O 777.4",  "centerNm": 777.60, "halfWidthNm": 1.06,
                       "baselineGapNm": 1.30, "baselineWidthNm": 1.10, "mode": "PeakHeight",
                       "peakSearchHalfWidthNm": 0.0 },
      "op": "LessThan", "threshold": 0.07 }
  ],
  "classes": [
    { "name": "A", "plasmaThreshold": 0 },
    { "name": "B", "plasmaThreshold": 0 },
    { "name": "C", "plasmaThreshold": 0 }
  ]
}
```

**規則順序是有意義的。** C 的 `Hα/O777` 實測是 0.143–0.159,大於 0.07 的門檻 —— 如果先跑
Hα 規則,C 會被判成 B。Ar 規則必須在前。

實測值(通過引擎抽取器,每段的第一支 step):

| | `Ar750/O777` | `Hα656/O777` | 判定 |
|---|---|---|---|
| C | **1.30 – 1.47** | 0.143 – 0.159 | 規則 1 命中 → C |
| A | 0.0003 | **0.041** | 規則 2 命中 → A |
| B | −0.0010 | 0.140 | 皆未命中 → fallback B |

`plasmaThreshold` 必須依站點的 logger 設定實測後填入,不能照抄:它比較的是 logger 的
trigger metric,而那取決於站點選的 trigger 波長與門檻。方法是量各段製程穩態的 trigger metric,
取其 50 % 當該段的門檻。**A 是限制條件** —— 它是三段裡最暗的(全譜平均 240–540,C 是
1670–4300),`SaveStartThresholdIntensity` 若照 C 調,A 整段的 gate 會是關的。留 0 表示沿用
logger 自己的門檻,也就是升級前的行為。

## 5. 階段

```
P0  分類器 + ProcessClass + per-process gate + 站點比值組        <- 已完成,見 §6
P1' 注入 harness:鏈路驗證 + transfer 量測(離線)                <- 已完成,見 §9
P2  批次層:10-30 s 窗口中位數、批次 CSV + 索引、Baseline Builder 分 class
P3  結構:A/B 的 TrendOnly + 守衛量 + 批次趨勢頁
P1  受控漏氣測試 -> 靈敏度
    ═══ GO / NO-GO ═══
P3' C 的警報門檻
P4  SECS 製程分類 SVID
P5  量化洩漏率校正(只有 P1 的斜率撐得住才做)
```

**gate 的位置在 2026-09-03 移動過。** 原本擺在 P2 之前,理由是不要在未驗證的前提上蓋大工程。
但 **P2 與指標無關** —— 窗口中位數、批次邊界、批次 CSV、Baseline Builder 分 class,沒有一項
取決於用哪條譜線;萬一指標要換,P2 一行都不用改。真正需要 P1 的只有 **P3 的門檻數值** 與
**P5**(沒有 calibrated leak,洩漏率校正在定義上不可能)。所以 gate 往後移到 P3' 之前。

### 5.1 P1 — 受控漏氣測試(硬性 gate)

- **漏氣量**:0 / 3×10⁻⁵ / 1×10⁻⁴ / 3×10⁻⁴ mbar·L/s,每個量跑一整批。
  一個批次同時含 A、B、C 三段,所以跑一批就同時拿到三段在同一漏氣量下的響應。
- **必須固定**:同一天;緊接在長 B 清洗之後(視窗透過率才在同一個起點);
  **TEOS 與 Ar 流量不動** —— 主判的分母來自 TEOS,那是這個指標唯一的已知弱點。
- **氣體必須是空氣**(或與待測漏氣同成分)。惰性氣體的漏不會抬升 N₂ 以外的任何東西,
  而 N₂ 正是我們在看的那一條 —— 用氮氣做這個測試會給出過度樂觀的靈敏度。

> ### GO / NO-GO
>
> **Q_min = 1×10⁻⁴ mbar·L/s。**
>
> **GO:在 Q_min 上,`R_N2CO_C` 的變化量 ≥ 9.3 %**(跨批次 σ 3.1 % 的三倍)。
>
> 換算到**帶本身**要上升多少:除以 §9 量到的 transfer,C 是 **9.5 %**、A 是 10.6 %。
>
> **NO-GO:< 9.3 %。** 選項是換指標、降低目標只抓大漏、或承認 OES 在這台機達不到這個靈敏度。
> **不是把門檻調鬆。**
>
> Q_min 是從「rate-of-rise 1 mTorr/min × 腔體約 50 L」推的。**上機前先用實際腔體體積與
> 製程規格確認這個數字**,它決定了整個計畫值不值得做下去。

P2–P3 是這個計畫裡最大的一塊工,而它們全部建立在「N₂337/CO329 對漏氣會動」這個目前**純屬推論**
的前提上。所以 P1 是硬性 gate:先用一天的機台時間換掉整個計畫最大的不確定性。

## 6. P0 已完成的部分

| 檔案 | 內容 |
|---|---|
| `ProcessClassifier.cs` | 規則、類別定義、設定型別,以及純函式的 `Evaluate` |
| `LeakMonitorSettings.cs` | `RatioDefinition.ProcessClass`;`LeakMonitorSettings.ProcessClassifier`;`MeasuresSameAs` 納入 class |
| `LeakMonitorEngine.cs` | 每 step 的分類狀態機、per-class gate、class 路由、快照欄位 |
| `PlasmaGate.cs` | 公開 `TriggerMetric`,讓同一個亮度量測能比對不同門檻 |
| `RatioMonitor.cs` | `RatioState.NotApplicable` 與 `MarkNotApplicable()` |
| `RatioCsvLogger.cs` | 尾端追加 `ProcessClass,ProcessStep,disc:*` 欄 |
| `RatioEditViewModel.cs` / `RatioSetupPanel.xaml` | class 欄位的往返與 UI |
| `ProcessClassTests.cs` | 13 個測試 |

**沒有設定分類器的安裝,行為完全不變。** `ProcessClassifierSettings.Enabled` 預設 false,
空的 `ProcessClass` 表示適用於每一段 step,分類器關閉時 ratio CSV 一個欄位都不會多。

幾個刻意的決定:

- **邊界偵測用最低的門檻,不用該類別自己的。** 類別要到 step 開始好幾個 frame 之後才知道,
  用最亮那類的門檻做邊界偵測,最暗那類的 step 永遠不會被看見 → 永遠無法分類 → 永遠是暗的。
  這與 [`leak-test-20260819-analysis.md`](leak-test-20260819-analysis.md) 記錄的失敗
  是同一件事,只是從反方向遇上。
- **判定只取一次然後鎖住。** 一段 plasma 不會中途換製程,允許答案移動只會讓點火暫態翻它。
- **分不出來的 step 是 `Unknown`,期間什麼都不判。** 與 `PlasmaGate` 對「無法評估」的處理
  同一條原則:「我們無法判斷」不是一個量測結果。
- **離開自己的 class 時 latch 保留。** 確認過的漏氣不會因為機台跑到下一段製程就結束;
  只有 Acknowledge 能結束它,而那條路徑會寫審計紀錄。
- **判別量的數值一起記錄,不只記判定。** 第一次有 step 落在門檻附近時,數字是唯一能回答
  「這個門檻還適用嗎」的東西。與 `SpectrumFrameDropout` 同一個教訓。
- **`RatioEditViewModel.ToDefinition()` 必須帶上 `ProcessClass`。** 少了它,任何人第一次
  在 Ratio Setup 按 Save 就會把三段的 class 一次清空 —— 畫面看起來毫無變化,leak monitor
  回到拿三段製程比同一條 baseline,而且沒有任何地方會說發生過這件事。有測試釘住。

## 7. 刻意不做的事

- **不動出廠 `CreateDefault()`。** 那是另一台機的已知漏氣量測選出來的,在那台機上是對的;
  而且它只對全新的 `settings.json` 生效,改了也不遷移既有安裝,只會製造第三種狀態。
  但**出廠組合的適用前提要寫進文件**(有 Ar 載氣、signal 與 reference 的波長間距限制),
  否則下一台機又會照抄。
- **不改 `RatioMonitor`。** 它有測試覆蓋、有 latch 語意、操作員看的分頁靠它。
  批次層(P2)是訂閱 `SampleProcessed` 的獨立元件,理由與 `RatioCsvLogger` 當初從
  intensity logger 獨立出來完全相同。
- **不做透過率建模。** 固定取樣點已經繞過結垢,現在建模是過度工程。
- **守衛量不抑制警報。** 它自己的 CV 是 5.1 %,拿它去否決真警報,遲早會壓掉一次真漏氣。
  警報訊息裡印出它的值與偏離量,讓工程師三秒內能判斷「這是漏氣還是 TEOS 動了」——
  比讓程式替他判斷可靠。
- **批次趨勢暫不上報 host。** 格式要先跟 host 談,而 baseline 都還沒建。

## 8. 已知風險

1. **主判的分母來自 TEOS。** `CO 607.9 / Ar 811.5` 守衛 + `R_N2Ar_C` 否決票是對策,
   但都不是硬性的,而且否決票本身只有 mean/σ 5.8。
2. **A 與 B 的 mean/σ 是 7.3 / 6.5。** 全譜掃過,沒有更好的含氮特徵(§3.1)。
   TrendOnly 是誠實的做法,不是保守。
3. **靈敏度完全未經驗證。** 這是 P1 是硬 gate 的唯一理由。
4. **N₂ 337 的 peak-height 抽取依賴側窗位置**,而它夾在兩個巨大的 CO 帶之間。
   §4.2 的視窗是實測調出來的;**改動任何一個窗口參數都必須重跑 §4.1 那張表**,
   因為指標的絕對值會跟著變,§5.1 的 9.3 % 判準是綁在 3.1 % 這個 σ 上的。

## 9. P1′ — 注入 harness 的結果(2026-09-03)

實機漏氣測試暫時排不到,所以先做能離線做的那一半。

**前提要先講清楚:8/19 的資料無法用來換算漏氣量。** 那次用的是閥門「單位」(100 / 50),
不是 mbar·L/s;而且 §6 明確拒絕把 100 vs 50 的 +11.1 % 差異歸因給閥門,因為當天每兩次 run
之間本來就在掉 9.8–13.6 %。`settings.json` 裡 `calibrations` 是 **0 筆**,從來沒做過。
另外 8/19 那個漏氣量大到讓放電本身變亮 2.1–5.3 倍 —— **我們唯一觀察過的漏氣,比要偵測的目標
大得多**,往下外插不可靠。

所以 GO/NO-GO 拆成兩半:**「多大的上升能被偵測到」可以離線回答;「多大的漏氣造成那個上升」
只有機台能回答。** `LeakInjectionTests` / `BandInjector` 做的是前者。

注入方式:把每一幀自己在局部連續光譜之上的超出量乘以 (1+x),保留該幀量到的帶型 ——
不是加高斯,因為 N₂ 二正帶有帶頭和向紅衰減的尾巴,對稱的鼓包會把光子放在帶上沒有的地方。

### 9.1 transfer:抽取器只報回一部分

| 製程 | transfer | 意義 |
|---|---|---|
| C | **0.979** | 注入 10 % 讀到 9.79 % |
| B | 0.963 | |
| A | 0.879 | |

每個製程是一個**常數**(整條掃描 0.02–0.30 都一樣,因為抽取是線性的)。損失來自 N₂ 337 帶
向紅的尾巴壓在抽取器右側連續窗底下,那部分被當成連續光譜減掉了。A 的 transfer 最低,因為它的
N₂ 帶相對自身連續光譜最弱,壓在窗下的比例最高。

**這會改變判準的算術**:9.3 % 是講在**讀數**上,帶本身要上升 9.3 % ÷ transfer ≈ **9.5 %**(C)。
偏差不大但方向是保守的那一邊,該寫進驗收算式而不是註腳。

### 9.2 harness 自己的陷阱(第一版量錯了)

第一版把注入區間設成 337.4 ± 5 nm,下緣到 332.4 —— 那裡是 **CO 329.6 的右側連續窗**
(332.76–333.86)。注入等於同時把分母的連續光譜墊高、壓低它的峰高,於是製程 B 報回**比注入更大**
的上升(transfer 1.02–1.04),而且 transfer 會隨注入量漂移。

**注入區間不能碰到分母讀到的任何東西。** 改成明確的 334.2–342.6 之後,transfer 立刻變成常數。
`transfer 隨注入量漂移` 現在是測試裡的一條斷言,就是為了再犯時會失敗。

### 9.3 鏈路通,但警報在一段 84 s 的 step 裡不會確認

`% of baseline` 在注入後從 ~97 升到 ~120,抽取 → 比值 → EMA 全部照實傳遞。但 **Warning 從未確認**。

原因不是 bug,是設計:Warn 門檻是 `mean + SigmaWarn · max(baselineσ, liveσ)`,而 **liveσ 是
raw 比值散度的 EWMA —— 階躍本身就是散度**。注入那一幀門檻跟著跳高 **+30 %**(0.02193 → 0.02850),
之後隨噪音估計衰減而下降,但衰減要約 `2τ·ln(liveσ/baseσ)`,τ = 5 s 時比 84 s step 剩下的時間還長:

```
t=40  ema=0.02098  warn=0.02850  margin -26.4 %   ← 注入
t=48  ema=0.02294  warn=0.02523  margin  -9.1 %
t=56  ema=0.02427  warn=0.02424  margin  +0.1 %   ← 只碰到一次
t=58  ema=0.02294  warn=0.02643  margin -13.2 %   ← 一個下抖就重新撐開
```

這與 `ratio-csv-sigma-score-zh-TW.md` §4.1 描述的 σ-score 現象是同一個機制,只是這裡發生在門檻上。

**對計畫的後果:R7 保留的「per-step 粗篩」在這台機的 step 長度下,用這組參數是行不通的。**
真實漏氣若以階躍到達,不會在一個 step 內確認出 Warning。**跨批次比較(計畫的主判)不受影響**
—— 它比的是窗口中位數,既不用 EMA 也不用 liveσ。

要在 P3′ 做的選擇:縮短 τ、關掉 liveσ 加寬、或直接放棄 per-step 粗篩。
`A_step_change_widens_its_own_warn_threshold` 這個測試存在的目的,就是讓這個選擇是**刻意做的**,
而不是在機台上才發現。

### 9.4 這一輪沒有回答的

- **多大的漏氣造成多大的上升。** 仍然只有機台能答,GO/NO-GO 仍然懸著。
- 注入給的是**上界**:它只動分子、不動參考線,而真實空氣漏會改變放電負載、兩者都動;
  它也不帶自己的雜訊,而真實漏氣是在波動的流量上到達的。
