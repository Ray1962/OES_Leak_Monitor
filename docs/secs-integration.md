# SECS/GEM 設備端整合規格（OES Leak Monitor）

> 目的：讓 fab 的 Host／MES 能以 SECS-II over HSMS 連進 OES Leak Monitor，
> 查詢洩漏監測狀態、接收洩漏警報與設備事件。
>
> 本文件是**實作規格**——寫給要動這段程式的人。欄位語意的上位文件是
> [`Satellite_SECS_Specification_v2.md`](Satellite_SECS_Specification_v2.md)（`ss=27` 章節、
> §5 ALID、§6 CEID）；兩者衝突時以該份為準，且必須回頭修正本文件。
>
> 相關程式：`LeakMonitorEngine`、`MainViewModel`、`AppSettings`（見 `CLAUDE.md`）。
> 通訊函式庫：`Aqusen.Secs`（維護於 `Ray1962/Test_SECS`，整合方式見該 repo 的 `整合指南.md`）。

**狀態：** 已實作，並有自動化測試（§13.2–13.3，25 個）。連線、S1F3、S5F5 已在真機驗證；
洩漏警報 S5F1 已用 2026-08-14 的實機錄檔重放驗證。**尚待人工驗收的只剩三個 CEID 事件**
與 Replay 分頁的接線（§13.4）。

---

## 0. 範圍

### 0.1 本期要做

- HSMS 設備端（passive）常駐監聽，可被 Host 連上、完成 S1F13/S1F14 建立通訊。
- 狀態查詢 **S1F3/S1F4**，供應規格 §1.4 `ss=27` 的**全域量測項目 VID 001–026**。
- 警報 **S5F1**：洩漏 Warning／Alarm，加上設備故障三條。
- 事件 **S6F11**：擷取開始／停止、操作者確認警報。
- 一個獨立的 **SECS 分頁**：狀態任何人可看，設定 Engineer+ 才能改。
- 三段式上報開關 + 測試模式閘門。
- 獨立的 SECS 收送日誌檔。

### 0.2 本期不做（但編碼規則預留）

| 項目 | 理由 |
|---|---|
| 比值槽位 VID 101–400（規格 §1.4(b)） | 10 槽 × 30 位移 = 300 個 SVID，且需 slot↔ratio 的穩定對應。先讓機台級結論通到 Host。 |
| 遠端命令 S2F41 | 需求方明確不要。profile 的 `remoteCommands` 留空，Host 下命令一律 `HCACK=1`。 |
| Spooling（S2F43／S6F23） | `Aqusen.Secs` 預設不留。斷線期間送出的警報會失敗並記在日誌。客戶要求時再由 Host 送 S2F43 開啟。 |
| 製程程式 S7、終端訊息 S10 | `Aqusen.Secs 0.6.0` 尚未支援。 |

> Trace（S2F23／S6F1）**不必額外實作**——`Aqusen.Secs` 的設備端已內建，SVID 綁好即可使用。
> 規格 §6.2 也建議連續趨勢走 Trace，不要用事件報告逐幀推送。

---

## 1. 已定案決策

| # | 項目 | 決定 | 理由 |
|---|---|---|---|
| 1 | 範圍 | 最小可對測版 | 先用 `Test_SECS` 當 Host 驗收，不等 fab MES |
| 2 | SVID | 只做全域量 001–026 | 見 §0.2 |
| 3 | `cc`（腔體） | SECS 分頁輸入，程式算出全部編號 | 現場不會手改十位數字而打錯 |
| 4 | profile | 外部 JSON 為主，程式只覆寫 `cc` | 出貨後客戶要換編號／改文字不必重編譯 |
| 5 | profile 位置 | `OesAppPaths.ConfigDirectory\profiles\` | 與 `settings.json` 同一棵樹，一台機的設定在同一處 |
| 6 | 分頁權限 | 狀態人人可看、設定 Engineer+ | 沿用 Monitor 分頁「錄製狀態條」的先例：看狀態是 Operator 的事，改設定不是 |
| 7 | 開關 | 總開關 + 上報警報／上報事件／測試模式也上報 | 現場調試時要能只連線不發訊息，避免誤發警報到 MES |
| 8 | 警報 | ALID 001／002 + 新增 012～014 | 規格全域 ALID 沒有「設備故障」，用保留段補 |
| 9 | 事件 | CEID 502／508／509 | |
| 10 | 日誌 | 獨立 `secs_YYYYMMDD.log` + 分頁即時顯示 | 每行含完整訊息內容，量大，不該淹掉既有的稽核 CSV |
| 11 | 套件來源 | nupkg 複製進 `LocalPackages`，另加 `aqusen` 來源 | 前者保建置可重現，後者方便開發時追新版 |
| 12 | ALID 型別 | 改 `Aqusen.Secs` 支援 ASCII | 規格 §5.3 寫 ASCII，套件送 U4；改套件才能與 Satellite 其他感測器一致 |

---

## 2. 架構

```
  SDK 取樣執行緒                          UI 執行緒
  ─────────────                          ─────────
  DeviceViewModel
       │ SpectrumAvailable
       ▼
  MainViewModel.FanOutSpectrum
       │
       ├─► DualIntensityLogger ──► 全譜 CSV
       ├─► WavelengthTrendViewModel
       └─► LeakMonitorEngine
                │ SampleProcessed(LeakMonitorSnapshot)
                ├─► RatioCsvLogger ──► 比值 CSV
                ├─► LeakMonitorViewModel ──► Leak Monitor 分頁
                └─► SecsBridge
                        │  存下最新 snapshot（volatile 參考）
                        │
                        ├─ SvBindings（被動：Host 查詢時才讀）──► S1F3 / S6F1 Trace
                        ├─ 警報邊緣偵測 ──► SendAlarmAsync ──► S5F1
                        └─ 事件觸發點   ──► SendEventAsync ──► S6F11
                                 │
                          EquipmentSimulator（Aqusen.Secs）
                                 │ HSMS passive, TCP
                                 ▼
                              Host / MES
```

**設計要點：`SecsBridge` 只讀不算。** `SvBindings` 的讀取委派在 S1F3、每一筆 trace 取樣、
每一次事件報告時都會被呼叫，且可能同時發生，所以它必須輕量且執行緒安全。做法是
`SampleProcessed` 時把整個 `LeakMonitorSnapshot` 存成一個 volatile 參考（該型別本身不可變），
binding 只做欄位讀取與型別轉換，不做統計、不取鎖。

---

## 3. 編碼與 `cc` 注入

### 3.1 編碼規則（摘自規格 §1、§5、§6）

```
SVID = 1 + cc + 27 + 00 + vvv      （10 碼）   例：cc=02, vvv=001 → 1022700001
ALID = 1 + cc + 27 + nnn           （ 8 碼）   例：cc=02, nnn=002 → 10227002
CEID = 1 + cc + 27 + nnn           （ 8 碼）   nnn 自 501 起
```

OES 直接觀測腔體內電漿發光，不透過 slit valve 調節訊號，故 `aa` 一律 `00`。
`cc` 的可用代號見規格 §1.1（01=Ch_1、02=Ch_2、…、11=Buffer/Buffer 1…）。

### 3.2 注入機制

`JsonDeviceProfile.Load` 只接受檔案路徑，沒有樣板參數，因此採「**範本 → 有效檔**」兩段式：

1. **範本**：`{ConfigDirectory}\profiles\oes-leak-monitor.json`，一律以 **`cc=00`** 寫成完整編號
   （`"svid": 1002700001`、`"alid": 10027001`）。檔案不存在時由 `SecsProfileTemplate` 寫出一份。
2. **覆寫**：讀成 `JsonNode`，把每個 `svid`（10 碼）與 `alid`／`ceid`（8 碼）的第 2–3 位換成設定的 `cc`。
3. **校驗**（覆寫後、載入前）：位數必須正確、`ss` 必須仍是 `27`、`aa` 必須仍是 `00`。
   任一項不符就**拒絕啟動**並在分頁顯示原因——現場手改 JSON 打錯一位數，比連不上更難查。
4. **有效檔**：寫到 `{ConfigDirectory}\profiles\.effective\oes-leak-monitor.json`，
   再 `JsonDeviceProfile.Load` 這一份。
5. 兩個路徑都印進 SECS 日誌（`整合指南` §三.4 的要求：現場人員要找得到）。

`.effective` 是衍生物，每次啟動重寫，現場要改的是範本那份。改 `cc` 或改範本後需重啟 SECS
（分頁上的 Restart 按鈕，不必重開 App）。

`SecsChamberCoding` 是純函式（`(json, cc) → json`），不碰檔案系統，可單獨測試。

---

## 4. Profile 範本

```jsonc
{
  "name": "OES Leak Monitor (ss=27)",

  // svid 一律以 cc=00 書寫；程式啟動時依 SECS 分頁的腔體號覆寫第 2-3 位。
  // 客戶要改編號或改顯示名稱，改這個檔即可，不必重新編譯。
  "statusVariables": [
    { "svid": 1002700001, "name": "Leak rate",                   "units": "mbar-L/s", "format": "F4", "bind": "oes.leakRate" },
    { "svid": 1002700002, "name": "Leak rate sigma",             "units": "mbar-L/s", "format": "F4", "bind": "oes.leakRateSigma" },
    { "svid": 1002700003, "name": "Leak rate confidence",        "units": "",         "format": "F4", "bind": "oes.leakRateConfidence" },
    { "svid": 1002700004, "name": "Leak rate valid",             "units": "",         "format": "U4", "bind": "oes.leakRateValid" },
    { "svid": 1002700005, "name": "Out of calibrated range",     "units": "",         "format": "U4", "bind": "oes.outOfCalibratedRange" },
    { "svid": 1002700006, "name": "Calibration status",          "units": "",         "format": "U4", "bind": "oes.calibrationStatus" },
    { "svid": 1002700007, "name": "Composite leak level",        "units": "",         "format": "U4", "bind": "oes.compositeLevel" },
    { "svid": 1002700008, "name": "Enabled ratio count",         "units": "",         "format": "U4", "bind": "oes.enabledRatios" },
    { "svid": 1002700009, "name": "Warning ratio count",         "units": "",         "format": "U4", "bind": "oes.warningRatios" },
    { "svid": 1002700010, "name": "Alarm ratio count",           "units": "",         "format": "U4", "bind": "oes.alarmRatios" },
    { "svid": 1002700011, "name": "Low-signal ratio count",      "units": "",         "format": "U4", "bind": "oes.lowSignalRatios" },
    { "svid": 1002700012, "name": "Baseline available",          "units": "",         "format": "U4", "bind": "oes.baselineAvailable" },
    { "svid": 1002700013, "name": "Active Golden Run name",      "units": "",         "format": "A",  "bind": "oes.goldenRunName" },
    { "svid": 1002700014, "name": "Active calibration name",     "units": "",         "format": "A",  "bind": "oes.calibrationName" },
    { "svid": 1002700015, "name": "Acquisition mismatch",        "units": "",         "format": "U4", "bind": "oes.acquisitionMismatch" },
    { "svid": 1002700016, "name": "Test / replay mode",          "units": "",         "format": "U4", "bind": "oes.testMode" },
    { "svid": 1002700017, "name": "Golden Run capture active",   "units": "",         "format": "U4", "bind": "oes.captureActive" },
    { "svid": 1002700018, "name": "Golden Run capture progress", "units": "%",        "format": "F4", "bind": "oes.captureProgress" },
    { "svid": 1002700019, "name": "Calibration capture active",  "units": "",         "format": "U4", "bind": "oes.calCaptureActive" },
    { "svid": 1002700020, "name": "Calibration capture progress","units": "%",        "format": "F4", "bind": "oes.calCaptureProgress" },
    { "svid": 1002700021, "name": "Plasma present",              "units": "",         "format": "U4", "bind": "oes.plasmaPresent" },
    { "svid": 1002700022, "name": "Plasma gate available",       "units": "",         "format": "U4", "bind": "oes.plasmaGateAvailable" },
    { "svid": 1002700023, "name": "Frame dropout count",         "units": "",         "format": "U4", "bind": "oes.dropoutCount" },
    { "svid": 1002700024, "name": "Integration time",            "units": "ms",       "format": "F4", "bind": "oes.integrationTime" },
    { "svid": 1002700025, "name": "Average count",               "units": "",         "format": "U4", "bind": "oes.averageCount" },
    { "svid": 1002700026, "name": "Frame rate",                  "units": "Hz",       "format": "F4", "bind": "oes.frameRate" }
  ],

  // category 依 SEMI E5：4 = 參數控制錯誤、5 = 不可回復錯誤、
  //                      6 = 設備狀態警告、8 = 資料完整性。
  "alarms": [
    { "alid": 10027001, "category": 6, "text": "OES LEAK WARNING" },
    { "alid": 10027002, "category": 4, "text": "OES LEAK ALARM" },
    { "alid": 10027012, "category": 5, "text": "OES CONNECTION LOST" },
    { "alid": 10027013, "category": 5, "text": "OES ACQUISITION ERROR" },
    { "alid": 10027014, "category": 8, "text": "OES DATA WRITE FAILURE" }
  ],

  "remoteCommands": {},
  "hostActions": [],
  "equipmentActions": []
}
```

`alarms` 的 `text` 是 S5F5 列表用的靜態文字；實際送 S5F1 時會以帶實值的 ALTX 覆蓋（見 §6.2）。

---

## 5. 狀態變數（VID 001–026）

單位、型別、語意以規格 §1.4 `ss=27`(a) 為準。下表補上**本 App 的資料來源**。

| VID | bind 名稱 | 格式 | 來源 |
|---|---|---|---|
| 001 | `oes.leakRate` | F4 | `Snapshot.LeakRate.LeakRate`；無估計時 0 |
| 002 | `oes.leakRateSigma` | F4 | `LeakRateEstimate.Sigma` |
| 003 | `oes.leakRateConfidence` | F4 | `LeakRateEstimate.Confidence` |
| 004 | `oes.leakRateValid` | U4 | `LeakRate?.HasEstimate == true` → 1 |
| 005 | `oes.outOfCalibratedRange` | U4 | `LeakRateEstimate.OutOfCalibratedRange` |
| 006 | `oes.calibrationStatus` | U4 | `Snapshot.CalibrationStatus`，直接轉 `int` |
| 007 | `oes.compositeLevel` | U4 | `Snapshot.Overall`，直接轉 `int` |
| 008 | `oes.enabledRatios` | U4 | `Ratios` 中 `State != Disabled` 的數量 |
| 009 | `oes.warningRatios` | U4 | `State == Warning` 的數量 |
| 010 | `oes.alarmRatios` | U4 | `State == Alarm` 的數量 |
| 011 | `oes.lowSignalRatios` | U4 | `State == LowSignal` 的數量 |
| 012 | `oes.baselineAvailable` | U4 | 任一 ratio `HasBaseline` → 1 |
| 013 | `oes.goldenRunName` | A | `Snapshot.ActiveGoldenRun ?? ""` |
| 014 | `oes.calibrationName` | A | `Snapshot.ActiveCalibration ?? ""` |
| 015 | `oes.acquisitionMismatch` | U4 | `AcquisitionWarning` 非空 → 1 |
| 016 | `oes.testMode` | U4 | `Snapshot.TestMode`（含 Replay） |
| 017 | `oes.captureActive` | U4 | `Snapshot.CaptureActive` |
| 018 | `oes.captureProgress` | F4 | `CaptureProgress01 × 100` |
| 019 | `oes.calCaptureActive` | U4 | `CalibrationCaptureActive` |
| 020 | `oes.calCaptureProgress` | F4 | `CalibrationCaptureProgress01 × 100` |
| 021 | `oes.plasmaPresent` | U4 | **需新增**，見 §8 |
| 022 | `oes.plasmaGateAvailable` | U4 | **需新增**，見 §8 |
| 023 | `oes.dropoutCount` | U4 | **需新增**，見 §8 |
| 024 | `oes.integrationTime` | F4 | `DeviceSettings` 的積分時間（ms） |
| 025 | `oes.averageCount` | U4 | `DeviceSettings` 的平均次數 |
| 026 | `oes.frameRate` | F4 | `MainViewModel` 量測的實際幀率 |

### 5.1 列舉值對照——程式列舉順序即規格數值

已核對，**三個列舉的宣告順序與規格 §1.4(c) 的數值完全一致**，可直接 `(uint)` 轉型，不需要對照表：

| 列舉 | 順序 | 規格 |
|---|---|---|
| `LeakAlarmLevel` | Idle, Normal, Warning, Alarm | (c)-1 的 0/1/2/3 ✔ |
| `CalibrationStatus` | NotCalibrated, Active, BaselineMismatch | (c)-2 的 0/1/2 ✔ |
| `RatioState` | Normal, Warning, Alarm, NoPlasma, LowSignal, NoBaseline, Disabled | (c)-3 的 0～6 ✔ |

> ⚠️ 這是**隱性契約**。日後在這三個列舉中間插入成員，會靜默改變送給 Host 的數值。
> 三處宣告都應加註解指回本節；`RatioState` 雖然本期只用來統計數量，將來做槽位 VID 就會直接上報。

### 5.2 尚未實作但已保留

VID 027–099 保留。比值槽位 VID = `100 + (N−1)×30 + 位移`（N = 1…10，對應 `RatioSetupViewModel.MaxRatios`），
定義見規格 §1.4(b)。做這一段時要一併處理槽位↔ratio 的對應穩定性：ratio 的 key 是隨機 GUID stub，
槽位號則是設定畫面的第 N 列，設定重載後可能改變——這正是規格保留 CEID 507 的原因。

---

## 6. 警報（S5F1）

### 6.1 清單

| ALID `1{cc}27nnn` | 名稱 | category | 觸發 | 解除 |
|---|---|---|---|---|
| 001 | Leak warning | 6 | `Overall` 升到 `Warning` | 降回 `Normal`／`Idle` |
| 002 | Leak alarm | 4 | `Overall` 升到 `Alarm`（鎖存） | 操作者 Acknowledge 後降回 |
| **012** | OES connection lost | 5 | 裝置連線中斷 | 重新連上 |
| **013** | Acquisition error | 5 | 擷取錯誤 | 恢復擷取 |
| **014** | Data write failure | 8 | CSV 寫檔失敗 | 下一次寫檔成功 |

012–014 是本案新增，已同步補進 `Satellite_SECS_Specification_v2.md` §5.1 的保留段。

**觸發來源**（實作後定案）。原先設想掛在 `SystemLogger` 的條目上，但那需要猜一個框架沒有公開的
事件；改成掛在 App 自己就知道的三個明確轉態上：

| ALID | 來源 | 解除 |
|---|---|---|
| 012 | `DeviceViewModel.IsConnected` 由真變假 | 重新連上 |
| 013 | `DeviceViewModel.Status == DeviceConnectionStatus.Error` | 離開 Error |
| 014 | `DualIntensityLogger.ErrorOccurred` | 下次 `FilesChanged` 有寫檔器開啟 |

012 刻意是**轉態**而非狀態：開機時本來就沒連線，一個工具每次啟動都對 Host 抱怨自己，Host 端很快
就學會忽略它。`SecsBridge.ReportFault` 做去抖動——已在 set 狀態的 ALID 不重送，沒 set 過的不送
clear，否則 Host 的紀錄裡會出現從未發生過的事件。

**這三條不受測試模式閘門影響**（§9.3 只擋資料衍生的警報）。光譜儀掉線、CSV 寫不進去，是關於這台
機器的事實，與畫面上跑的是不是合成頻譜無關。

### 6.2 ALCD 與 ALTX

- ALCD：bit 7 = 1 表示發生、= 0 表示解除，低 7 位是 category。此為 `Aqusen.Secs` 既有行為。
- ALTX 依規格 §5.3 慣例帶入腔體與當下數值，讓 Host 的警報紀錄本身可讀：
  - `CH2 OES LEAK ALARM composite=3, 2 of 4 ratios in alarm`
  - `CH2 OES leak rate 1.8e-004 mbar-L/s +/-3.0e-005 (conf 0.72)`
  - `CH2 OES CONNECTION LOST at 2026-08-16 14:03:11`
- 送出時用 `SendAlarmAsync(alid, text, set)` 這個帶文字的多載，profile 的靜態 `text` 只給 S5F5 列表用。

### 6.3 ALID 型別（需改函式庫）

規格 §5.3 的 S5F1 範例把 ALID 寫成 **ASCII**（`<A[16] "10227112 ">`），
而 `Aqusen.Secs 0.5.0` 的 `EquipmentSimulator.SendAlarmAsync` 固定送 **U4**。
8 碼數值放得進 U4，但型別不同，Host 照規格解析會失敗。處理方式見 §10。

---

## 7. 事件（S6F11）

| CEID `1{cc}27nnn` | 事件 | 觸發點 |
|---|---|---|
| 502 | Alarm acknowledged | `LeakMonitorEngine.Acknowledged`（本案新增的事件），帶操作者帳號與被清除的比值 |
| 508 | Acquisition started | `DeviceViewModel.IsAcquiring` false→true |
| 509 | Acquisition stopped | `IsAcquiring` true→false |

`MainViewModel` 已在監看 `IsAcquiring` 的轉換（用來呼叫 `LeakMonitorEngine.ReloadRatios()`），
508/509 接在同一處，不要另開一個監看。

502 的來源是引擎新增的 `Acknowledged` 事件，**觸發條件與既有的 `LeakMonitorAcknowledged` 稽核
紀錄完全相同**（`cleared.Count > 0`）——一次沒清到任何東西的 Acknowledge 不寫紀錄，也不發事件，
所以事件的意義就是那行紀錄的意義：有人結束了一個已確認的洩漏警報。

規格 §6.1 另定義了 501、503–507、510，本期不送；日後補上時沿用該編號，不要另起一套。

---

## 8. 對 `LeakMonitorEngine` 的改動

VID 021–023 目前拿不到，需要在 `LeakMonitorSnapshot` 補三個**全域**欄位：

| 欄位 | 型別 | 說明 |
|---|---|---|
| `PlasmaPresent` | `bool` | 本幀 `PlasmaGate` 判定電漿開啟。原先只有 per-ratio 的 `RatioSnapshot.PlasmaPresent` |
| `PlasmaGateAvailable` | `bool` | 閘門可用（對應既有的 `_gateWarned` / `LeakMonitorPlasmaGateUnavailable` 那個判斷） |
| `DropoutCount` | `int` | 累計掉幀數。引擎已在 `TrackGateDropouts` 統計（`_dropoutEvents`），原先只寫進日誌 |

三者都是把引擎**已經算好**的內部狀態暴露出來，不新增計算、不改變任何判斷邏輯。
實作上多存一個 `_lastGateOpen`（`bool?`，null = 該幀無法判定），並在 `ConfigureTrigger` 清掉——
換了閘門之後、還沒判過任何一幀時，該回報「無法判定」而不是沿用舊答案。
這是本案唯一動到量測核心的地方。

> ⚠️ **VID 023 的計數範圍與規格措辭不符。** 規格寫「本次採集累計」，引擎的 `_dropoutEvents`
> 實際上是**本次執行累計**（`ResetRuntimeState` 不清它，日誌的 `EventsThisSession` 也是這個意思）。
> 沒有跟著改，是因為改了會一併改掉既有日誌欄位的意義，而那是量測行為。Host 端應把它當單調遞增
> 的計數器用差值判讀。

---

## 9. 設定與開關

### 9.1 `settings.json` 新增區塊

```jsonc
"secs": {
  "enabled": false,              // 總開關：false 時完全不啟動監聽
  "reportAlarms": true,
  "reportEvents": true,
  "reportInTestMode": false,     // 預設關，見 §9.3
  "chamberCode": 2,              // cc，01-15 / 21-25 / 31-34（規格 §1.1）
  "isActive": false,             // 設備端為 passive，等 Host 連進來
  "ipAddress": "0.0.0.0",        // 監聽所有網卡；本機對測用 127.0.0.1
  "port": 5000,
  "deviceId": 0,                 // 必須與 Host 設定一致
  "modelName": "OESLM",          // MDLN，回在 S1F2 / S1F14
  "softwareRevision": "1.0.0",   // SOFTREV
  "t3": 45, "t5": 10, "t6": 10, "t7": 10, "t8": 10,   // 逾時（秒），規格 §2
  "profileFileName": "oes-leak-monitor.json",
  "logRetentionDays": 30
}
```

沿用既有的原子存檔（暫存檔 + `File.Move(overwrite:true)`）與 `AccessControl` 重讀慣例。
舊的 `settings.json` 沒有 `secs` 區塊時取上述預設值，即**預設不啟動**。

### 9.2 開關語意

| 開關 | false 時 |
|---|---|
| `enabled` | 完全不啟動監聽；分頁顯示 `Disabled` |
| `reportAlarms` | 不送 S5F1。狀態照樣進 UI 與稽核日誌，S1F3 的 VID 007 也照樣反映 |
| `reportEvents` | 不送 S6F11 |
| `reportInTestMode` | test mode／Replay 期間不送警報與事件（見下） |

`reportAlarms` / `reportEvents` 只擋主動送出，**不擋被動查詢**——Host 隨時能用 S1F3 讀到真實狀態。

### 9.3 測試模式閘門

App 在沒有硬體時會落到 test mode（合成頻譜），Replay 分頁還會播放錄檔。兩者都不是量測值——
現有機制會把 CSV 檔名標成 `SIM` 前綴，SECS 這邊的對應機制是：

- `reportInTestMode = false`（預設）：`Snapshot.TestMode` 為真時，**警報與事件一律不送**。
- 不論開關如何，**VID 016 恆為 1**，Host 自己就能判斷這批數據不可用於製程判定（規格 §1.4(d)）。
- SECS 分頁明顯標示目前處於測試模式、以及是否正在上報。

對測階段把這個開關勾起來，就能用 Replay 播一段真實錄檔來驗證整條警報路徑。

---

## 10. 對 `Aqusen.Secs` 的改動（先做，版本 0.6.0）

| 檔案 | 改動 |
|---|---|
| `GemModels.cs` | 新增 `AlarmIdFormat` 列舉與 `GemOptions.AlarmIdFormat`，**預設 `U4`** 以免影響既有消費端 |
| `EquipmentSimulator.cs` | 新增私有 `AlarmId(uint)`，`SendAlarmAsync` 與 `BuildAlarmEntries` 都改用它——S5F1 與 S5F6/S5F8 用同一種編碼，否則 Host 解析得了警報卻解析不了清單 |
| `SecsItems.cs` | `ToU8` 新增 ASCII/JIS8 分支（含前後空白修剪）。**入向必須容忍**：設備用 ASCII 宣告 ALID，Host 在 S5F3/S5F5 就會用 ASCII 回指同一條，原本會被讀成 0 |
| `README.md` | 補這個選項 |
| `tests/` | 新增 7 個測試（4 個 `SecsItemsTests`、3 個 ALID 編碼），全套 141 個通過 |

`dotnet pack` 出 `Aqusen.Secs 0.6.0`，複製 nupkg 到 `C:\Users\infor\source\repos\Ray1962\LocalPackages`。
本 App 設 `AlarmIdFormat = Ascii`。

> 0.x 期間 minor 版本可能有破壞性變更；升級前看 `Test_SECS` 的 commit 訊息。

---

## 11. 檔案清單

### 11.1 新增（`src/OES_Leak_Monitor/`）

| 檔案 | 職責 |
|---|---|
| `SecsSettings.cs` | §9.1 的設定模型，掛進 `AppSettings.Secs` |
| `SecsProfileTemplate.cs` | 內建範本；`profiles\` 沒有檔案時寫出一份 |
| `SecsChamberCoding.cs` | `cc` 覆寫與校驗，純函式，不碰檔案系統 |
| `SecsBridge.cs` | 生命週期、`SvBindings` 註冊、警報／事件橋接、測試模式閘門 |
| `SecsLogFile.cs` | 獨立日誌檔，依日輪替、保留 `logRetentionDays` 天 |
| `SecsViewModel.cs` | 分頁的 VM：連線／通訊／控制狀態、設定、日誌 ring buffer |
| `SecsPanel.xaml(.cs)` | 分頁 UI |

### 11.2 修改

| 檔案 | 改動 |
|---|---|
| `AppSettings.cs` / `SettingsService.cs` | 加 `secs` 區塊 |
| `MainViewModel.cs` | 建立並持有 `SecsBridge`；餵 snapshot；接 `IsAcquiring` 轉換與 Acknowledge |
| `MainWindow.xaml(.cs)` | 新增 `SecsTab`（**必須有 `x:Name`**，權限閘門以參考比對認分頁，見 `CLAUDE.md`） |
| `LeakMonitorEngine.cs` | §8 的三個快照欄位，以及 `Acknowledged` 事件 + `LeakAcknowledgedEventArgs`（§7） |
| `OES_Leak_Monitor.csproj` | `PackageReference`：`Aqusen.Secs 0.6.0`、`Secs4Net 2.4.4`；profile 範本不隨 exe 出貨（首次啟動寫到 `ConfigDirectory`） |
| `nuget.config` | 加 `aqusen` 來源（**不要動既有的 `<clear />`**，它是本專案刻意保留的） |
| `docs/Satellite_SECS_Specification_v2.md` | §5.1 補 ALID 012–014 |
| `CLAUDE.md` | 補 SECS 段落 |

> 註：`整合指南` §一.1 說「不要加 `<clear />`」，那是針對全新專案的建議。本專案的
> `nuget.config` 早已有 `<clear />` 且列了 nuget.org，只是多加一個來源，不受影響。

---

## 12. 分頁與權限

| 區塊 | Guest / Operator | Engineer+ |
|---|---|---|
| 連線狀態（Disabled／Listening／Selected／Communicating）、控制狀態 | 可看 | 可看 |
| 目前 `cc`、實際監聽位址、profile 路徑（範本與 `.effective` 兩個） | 可看 | 可看 |
| 測試模式標示、是否正在上報 | 可看 | 可看 |
| 最近數百行收送日誌 | 可看 | 可看 |
| 全部設定（§9.1）、Restart、Enable/Disable | 唯讀 | 可改 |

分頁本身不設權限閘門（人人可進），控制項以 `IsEnabled` 綁角色。這與 Configuration／Replay
分頁的做法不同——那兩個是整個分頁擋掉——理由見 §1 決策 6。

---

## 13. 驗收

不必等 fab 的 MES，用 `Test_SECS.exe` 當對測 Host。

### 13.1 已驗證（實作時實際跑過）

以 `cc = 02`、`127.0.0.1:5000`、Device ID 0 啟動 App，再從外部接一個 `GemHost` 進來：

- App 啟動時寫出 profile 範本、戳上 `cc=02` 產生 `.effective` 副本，兩個路徑都進了
  `secs_YYYYMMDD.log`；系統日誌留下 `SecsStarted`。
- 兩端 `Communicating`（**兩次 S1F13 是正常的**，兩端都會主動建立通訊）。
- **S1F17** → `ONLACK=2, already OnlineRemote`。
- **S1F3** → `S1F4 (26 SV)`，SVID 為 `1022700001`…`1022700026`，型別與 §5 相符
  （`F4` 洩漏率、`U4` 列舉、`A` 名稱），VID 024/025 回的是當下裝置的 150 ms／6 次。
- **S5F5** → 5 條警報，**ALID 為 ASCII**（`<A [8] "10227002">`）、category 6/4/5/5/8。
### 13.2 自動化測試（`tests/OES_Leak_Monitor.Tests`）

`dotnet test tests/OES_Leak_Monitor.Tests/OES_Leak_Monitor.Tests.csproj -c Debug` —
25 個測試，在有實機錄檔的機器上全過（沒有錄檔時 22 過、3 略過）：

| 檔案 | 涵蓋 |
|---|---|
| `SecsChamberCodingTests` | 編號運算對照規格範例、腔體表、戳記的冪等與可重戳、錯誤 `ss`／`aa`／腔體被拒、非本系統的 id 不被動到 |
| `SecsProfileTests` | 範本可載入、26 個 binding 全被 App 供應、既有 profile 永不被覆寫 |
| `SecsWireTests` | 真的 `SecsBridge` 對真的 `GemHost` 走 loopback：S1F3 回 26 個 SV、S5F5 的 ALID 是 ASCII 且 category 正確、Warning→Alarm→Normal 送出正確的 set/clear 順序、故障警報去抖動 |
| `RecordedRunTests` | **實機錄檔重放**，見下 |

### 13.3 實機錄檔重放（`RecordedRunTests`）

用 2026-08-14 的 `P_OES1_0814220358.csv`（10 分鐘、322 幀、1904 點軸，當天工具自己走過
Normal → Warning → Alarm），從錄檔自身前一分鐘擷取基準，跑**原廠比值組**——刻意不是當天
那組設定（已被重新調校，無法還原）：

| | 當天實際記錄 | 重放結果 | 差 |
|---|---|---|---|
| Normal → Warning | +414 s（22:10:52.98） | +416 s | 2 s |
| Warning → Alarm | +443 s（22:11:21.79） | +448 s | 5 s |

換了比值組與基準仍落在數秒內，表示那段資料的洩漏事件遠離調校邊界。測試斷言順序
（Warning 必在 Alarm 之前）與 ±90 s 容差；**容差被撞破時要查的是偵測或原廠比值組變了，
不是把容差調寬**。第三個測試把同一次重放接到 SecsBridge → GemHost，驗證 Host 依序收到
`10227001+`、`10227001-`、`10227002+`。

**錄檔不進 repo**（5.5 MB／壓縮後 2.5 MB，而整個 repo 歷史約 5 MB、最大檔 128 KB）。
測試預設讀 `C:\DualOES\202608\14\`，可用 `OES_TEST_RECORDING` 指到別的全譜 CSV；
找不到就報 *skipped*，不會假裝通過。

### 13.4 需人工驗收（要有電漿或 GUI）

1. Replay 分頁播一段有洩漏的錄檔，勾「Raise leak alarms during replay」與「測試模式也上報」
   → 應看到 S5F1 set/clear（ALID `1cc27001`／`1cc27002`）與 CEID `1cc27508`／`1cc27509`；
   按 Acknowledge → CEID `1cc27502`。
   （S5F1 那半已由 §13.3 自動化；這一步驗的是 **Replay 分頁到引擎的接線**與**三個 CEID**，
   後者目前只有人工能觸發。）
2. 取消「測試模式也上報」→ 同一段錄檔不應再送出任何 S5F1／S6F11，但 S1F3 仍回得到值且
   VID 016 = 1。
3. 拔掉光譜儀 → ALID `1cc27012` set；插回 → clear。
4. 在 Host 端做一次 S2F23 Trace，確認 S6F1 依週期回傳。
5. 全程比對 `secs_YYYYMMDD.log` 與 `Test_SECS` 兩邊的收送紀錄。

`Test_SECS` 的 `使用手冊.md` 有完整的雙視窗驗收流程（使用例 1～14），可整套照走。

> 註：`GemHost.RequestStatusAsync` 只把 S1F4 印進日誌、**不回傳值**，所以自動化驗收要嘛比對
> 日誌文字，要嘛直接評估 profile 的 `SvDefinition.Read()`。不是缺陷，但寫驗收腳本時會撞到。

---

## 14. 風險與待決

| # | 項目 | 現況 |
|---|---|---|
| 1 | ALID 型別 | 已決定改函式庫（§10）。若 Satellite 其他感測器實際送的是 U4，則要反過來改規格——上線前應向 Host 端確認一次實測封包 |
| 2 | 列舉順序的隱性契約 | 見 §5.1。三處宣告（`LeakAlarmLevel`／`CalibrationStatus`／`RatioState`）已加註解指回本文件；仍應列入 code review 清單 |
| 3 | 斷線期間的資料 | 本期不做 spooling。客戶若要求「MES 重開後資料不能掉」，需 Host 送 S2F43，並評估 `MaxSpooledMessages`（預設 100）是否夠 |
| 4 | 比值槽位 VID | 本期不做。做之前要先解決槽位↔ratio key 的對應穩定性（§5.2） |
| 5 | 設備故障的來源 | 已定案：綁在 `IsConnected`／`Status`／`ErrorOccurred` 三個明確轉態上（§6.1），不依賴框架未公開的事件。012 是轉態非狀態，刻意如此 |
| 6 | 多腔體 | 一個 App 執行個體對一個 `cc`。同一台 PC 跑兩套監測不同腔體時，port 必須分開 |
