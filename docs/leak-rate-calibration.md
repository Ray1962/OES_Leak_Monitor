# 洩漏率校正曲線（Leak-Rate Calibration）設計筆記

> 目的：把現行「分級警報（Normal / Warning / Alarm）」的洩漏監測，擴充成
> **定量估算**——輸出一個帶不確定度的洩漏率 `Q̂ ± σ_Q`（單位 **mbar·L/s**），
> 方法是建立「比值上升量 ↔ 已知洩漏率」的校正曲線。
>
> 既有的 Golden Run + 雙閾值 + 持續確認三級警報**維持不變**；定量估算是並列的
> 輔助讀數，不取代警報判斷。

相關程式：`LeakMonitorEngine`、`RatioMonitor`、`LineIntensityExtractor`、
`LeakMonitorSettings`（見 `CLAUDE.md` 的 Leak Monitor 段落）。

---

## 0. 設計選擇（已與使用者確認）

| 項目 | 決定 | 影響 |
|---|---|---|
| 參考漏源 | **標準漏孔（calibrated leak element）** | 校正點離散、少數幾顆；模型須低階、可單點起步 |
| 洩漏率單位 | **mbar·L/s** | 漏孔額定值需溫度修正後輸入 |
| 模型 | **多比值加權融合** | 每條比值各擬合靈敏度，逆變異加權成單一估計 |

### ⚠️ 關鍵前提：漏孔氣種必須是空氣

本程式偵測的化學指紋是 O(777)、OH(309)、NO(237)，必須由**空氣/水氣漏入**激發。
**He 標準漏孔不會抬升這些比值**，校正曲線無法對應真實空氣洩漏。

- ✅ 用**空氣（或 N₂+O₂ 混合）校正漏孔**。
- ⚠️ 只有 He 漏孔時，僅能當總漏導物理參考再換算等效空氣漏率，誤差大，須在報告標註。

---

## 1. 量化鏈路

到 `RatioMonitor.Snapshot()` 為止，每條比值已算出 `PercentOfBaseline`。校正只是在後面
接一段「反推」：

```
每條比值的相對上升量  xᵢ = (平滑比值 / 基線mean) − 1     ← 已有 (PercentOfBaseline/100 − 1)
        │  套用該比值靈敏度 sᵢ（校正曲線）
每條比值各自估的漏率  Qᵢ = xᵢ / sᵢ
        │  逆變異加權融合
融合漏率  Q̂ = Σ wᵢ Qᵢ / Σ wᵢ        不確定度  σ_Q = 1/√(Σ wᵢ)
```

用 `xᵢ`（相對上升量）當自變數的理由：它已除掉基線、抵消電漿亮度波動，是與漏率最線性的量；
且 `Q=0` 時 `x=0`，曲線天然過原點。

---

## 2. 校正模型與融合數學（核心）

標準漏孔是離散、少數幾點（可能 1～3 顆 + 基線零點），模型須低階、可單點起步。

### 2.1 每條比值擬合「過原點」靈敏度 sᵢ

單位：每 mbar·L/s 的相對上升量。

```
sᵢ = Σ_k (Q_k · x_ik) / Σ_k (Q_k²)
```

- 校正點 k = 各漏孔的已知漏率 `Q_k` 與當下量到的 `x_ik`。
- 只有一顆漏孔也能定出一條斜率；多顆 / 可疊加開啟可加點，並用殘差 / R² 評估線性度。
- 高漏率若飽和（次線性），超過最高校正點即標記「外插，僅供參考」。

### 2.2 反推 + 逆變異加權融合（多比值融合）

```
Qᵢ = xᵢ / sᵢ
wᵢ ≈ 1 / Var(Qᵢ) ,  Var(Qᵢ) ≈ (σ_xᵢ / sᵢ)² + (xᵢ · δsᵢ / sᵢ²)²
Q̂  = Σ wᵢ Qᵢ / Σ wᵢ
σ_Q = 1 / √(Σ wᵢ)
```

權重自動處理「哪條比值該信」：

- 靈敏度 `sᵢ` 大、雜訊 `σ_xᵢ` 小 → 權重高。
- `NoPlasma / NoBaseline / Disabled`、`sᵢ` 太小（對此漏不敏感）、或 `xᵢ` 仍在雜訊內 → 權重歸零。
- 各 `Qᵢ` 彼此偏離過大 → 一致性檢查失敗，標記低信賴（可能單一譜線異常或飽和）。

---

## 3. 資料結構與綁定關係

校正曲線**綁定操作點（recipe）與基線**，與 Golden Run 同樣「只在當時條件成立」。
持久化進同一個 `settings.json`（`OesAppPaths.ConfigDirectory`），沿用原子寫入。

```csharp
// 加進 LeakMonitorSettings.cs，與 GoldenRuns 並列
public sealed class LeakCalibration
{
    public string Name;                 // 操作員命名，對應某 recipe / 操作點
    public string GoldenRunName;        // 綁定的基線；reference label 須一致才有效
    public string LeakRateUnit = "mbar·L/s";
    public DateTime CapturedUtc;
    public List<LeakCalPoint> Points;   // 每個漏孔一點：Q + 每條比值的 x 平均/σ
    public List<RatioSensitivity> Fits; // 每條比值的 sᵢ、δsᵢ、R²、最大校正Q（外插界線）
}
```

有效性閘門（任一不符 → 顯示「未校正 / 校正失效」）：

1. 有作用中的 Golden Run，且**參考線 label 與校正時一致**（沿用 `ReferenceLabel` 比對）。
2. recipe / 操作點識別碼相符。
3. 電漿存在（plasma gate 通過）。

---

## 4. 校正流程（引導式 Wizard）

大量重用 `LeakMonitorEngine` 既有的 Golden Run 擷取機制（每比值 `Accum` 平均/σ、
`ProcessSample` 累積）。

1. 穩定 recipe、無漏 → 擷取 / 確認 Golden Run（基線）。
2. 逐顆開啟標準漏孔：操作員輸入溫度修正後的 `Q (mbar·L/s)` → 等待穩定（`ConfirmSeconds`）
   → App 在擷取窗口內平均每條比值得 `xᵢ` 與 `σ` → 存成一個校正點。
3. （可選）兩顆漏孔同時開 = 多一個高漏率點，改善斜率與飽和判斷。
4. 自動帶入 `Q=0 / x=0` 原點。
5. 全部擷取完 → 擬合 `sᵢ`、算權重、算 R² / 殘差 → 持久化。

新增 API（與 `BeginGoldenRunCapture` 對稱）：

```csharp
engine.BeginCalibrationPointCapture(double knownLeakRate, double seconds);
// 完成 raise CalibrationPointCaptured(xᵢ均值/σ)；全部點到齊後 FinalizeCalibration() 擬合
```

---

## 5. Runtime 估算與輸出

- `LeakRateEstimator`：持有作用中校正，吃 `RatioSnapshot` 列表 → 算 `Q̂` 與 `σ_Q`。引擎每幀呼叫。
- `LeakMonitorSnapshot` 新增欄位：`EstimatedLeakRate`、`LeakRateSigma`、`PerRatioEstimates`、
  `OutOfCalibratedRange`、`Confidence`。
- **Leak Monitor 面板**：總警報旁顯示「估計漏率 = X.X ± Y mbar·L/s」；外插 / 低信賴時變灰並標註。
- **`RatioCsvLogger`**：新增欄位記錄 `Q̂`、`σ_Q`（與既有 raw ratio / % baseline 同一檔）。
- **Ratio Review**：可選疊加估計漏率趨勢線。

> **P3 實作細節**：`RatioCsvLogger` 每列尾端多寫 `LeakRate,LeakRateSigma`（無校正/無估值時留空）。
> `RatioCsvReader` 改用**表頭欄名定位**（找 `OverallState` 算 ratioCount、找 `LeakRate`/`LeakRateSigma`），
> 舊檔（無漏率欄）與新檔皆可解析，`RatioTrendData.HasLeakRate` 標記是否有漏率資料。Ratio Review 新增
> 第三個檢視模式「Leak rate」：以 `mbar·L/s` 軸畫 `Q̂` 折線 + 半透明 ±1σ 帶，狀態色帶仍在背後；
> 舊檔在此模式顯示「no leak-rate data in this file」。PNG 檔名後綴 `_leakrate.png`。

> **P2 實作細節**：每幀 `xᵢ` 來自各 `RatioMonitor` 的 % -of-baseline；`σ_xᵢ` 來自比值的 EWMA
> 變異（`RatioMonitor` 新增 `_emaVar`，`σ_raw = √emaVar`，再除以基線 mean 換成 `σ_x`）餵進權重。
> 引擎持有一個由作用中校正建構的 `LeakRateEstimator`，存進 `LeakMonitorSnapshot.LeakRate`。
> 存檔後呼叫 `engine.ReloadCalibration()` 即時生效，**不需重啟 acquisition**（與 ratio-set 變更不同）。
> 面板在合成狀態橫幅下方顯示「Leak rate ≈ Q̂ ± σ · NN% confidence」，外插時標 `extrapolated`、
> 無校正時標 `not calibrated`。

---

## 6. 必須在 UI / 報告中提醒的限制

1. **操作點相依**：壓力、功率、氣種、流量一變，`sᵢ` 就變 → 每個 recipe 各自一組校正。
2. **時間漂移**：視窗霧化會改變絕對靈敏度；重抓 Golden Run 只還原基線、**不還原 sᵢ** → 建議定期重校。
3. **外插警示**：超過最高校正漏率即標「僅供參考」。
4. **氣種一致性**：漏孔氣種要等於要偵測的漏（空氣）——見第 0 節。

---

## 7. 分階段落地

| 階段 | 內容 | 產出 |
|---|---|---|
| **P0** ✅ | `LeakCalibration` 資料模型 + `LeakRateEstimator` 數學核心 | 可離線用合成資料驗證融合公式 |
| **P1** ✅ | 校正 Wizard（重用 Golden Run 擷取）+ 持久化 | 跑完一次校正、寫進 settings.json |
| **P2** ✅ | Snapshot 估算欄位 + Leak Monitor 面板顯示 | 即時看到 `Q̂ ± σ` |
| **P3** ✅ | RatioCsv 記錄 + Ratio Review 疊圖 + 匯出 | 可回放 / 稽核 |
| **P4** ✅ | 有效性閘門、recipe 綁定、失效 / 外插警示、SystemLogger 記錄 | 防誤用 |

> **P4 實作細節**：引擎新增 `CalibrationStatus`（`NotCalibrated` / `Active` / `BaselineMismatch`）。
> 校正只在其綁定的 Golden Run（`GoldenRunName`）等於目前作用中基線時才 `Active` —— 漏率上升量
> `xᵢ` 是相對該基線定義的，換基線即 `BaselineMismatch` 並**暫停估算**（不會悄悄給錯值）。所有基線
> 變更都經過 `ApplyGoldenRun` → `BuildEstimator` 重新評估有效性；參考線層級的失效仍由
> `LeakRateEstimator.Estimate` 的 per-ratio 閘門處理（"reference line changed since calibration"）。
> 狀態轉換寫進 SystemLogger（`LeakCalibrationActive` / `LeakCalibrationSuspended` / `LeakCalibrationCleared`）。
> Leak Monitor 讀數帶在 `BaselineMismatch` 時顯示「calibration "X" needs its baseline — select that
> Golden Run」（橘色），外插時標 `extrapolated`。

主要新增 / 改動檔案：`LeakMonitorSettings.cs`（模型）、新 `LeakRateEstimator.cs`、
`LeakMonitorEngine.cs`（擷取 + 估算掛載）、`LeakMonitorSnapshot` / `RatioMonitor`（欄位）、
新 `LeakCalibrationViewModel` + 面板（Engineer+ 權限，比照 Ratio Setup 暫存式）、
`RatioCsvLogger.cs`、`LeakMonitorPanel`。
