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

**狀態：** 已實作，並有自動化測試（§13.2–13.3，38 個）。連線、S1F3、S5F5 已在真機驗證；
洩漏警報 S5F1 已用 2026-08-14 的實機錄檔重放驗證，S6F11 的三個事件已由 `SecsEventTests`
走 loopback 驗證。**尚待人工驗收的只剩 UI 到 bridge 的接線**（按鈕、Replay 分頁），逐項狀態見 §13.4。
2026-10-23 起另有**主機台製程事件**：S2F41 `TOOL_EVENT` 與 VID 030–032，見 §15；
同日實作**比值槽位 VID 101–400 與 CEID 507**（§5.3），以及 SECS 分頁的 **Add missing rows**（§5.2）。

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
| ~~比值槽位 VID 101–400（規格 §1.4(b)）~~ | **已實作（2026-10-23）**：槽位對應的穩定性以「槽位 = Ratio Setup 第 N 列、即時量以 Key 對應、身分改變送 CEID 507」解決，位移 22 = `DisplayName`、位移 25 = Key。見 §5.3。 |
| ~~遠端命令 S2F41~~ | **已推翻（2026-10-22）**：原本需求方不要任何遠端命令。後來確認洩漏判定必須知道主機台此刻在跑哪個製程，而這只有主機台知道；廠內 EAP 已經收得到主機台的 CEID，最省事的路就是由 EAP 透過既有的 OES 連線轉送。所以只做**一個**命令 `TOOL_EVENT`，而且是資訊轉送、不是控制。見 §15。其他 RCMD 仍一律 `HCACK=1`。 |
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
    { "svid": 1002700026, "name": "Frame rate",                  "units": "Hz",       "format": "F4", "bind": "oes.frameRate" },
    { "svid": 1002700027, "name": "Process class",              "units": "",         "format": "A",  "bind": "oes.processClass" },
    { "svid": 1002700028, "name": "Process class state",        "units": "",         "format": "U4", "bind": "oes.processClassState" },
    { "svid": 1002700029, "name": "Process step index",         "units": "",         "format": "U4", "bind": "oes.processStepIndex" }
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

## 5. 狀態變數（VID 001–032）

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
| 027 | `oes.processClass` | A | `Snapshot.ProcessClass`，即引擎單一入口 `CurrentFrameClass()` 的類別。製程事件生效時（視窗內／視窗外）由事件決定，見 §15.3 |
| 028 | `oes.processClassState` | U4 | `Snapshot.ProcessClassState`，直接轉 `int`。製程事件生效時：視窗內 3、視窗外有電漿 4、無電漿 1 |
| 029 | `oes.processStepIndex` | U4 | `Snapshot.ProcessStepIndex` |
| 030 | `oes.toolEventState` | U4 | `ToolEventStatus.State`（`ToolEventContext`，直接轉 `int`）。**不是快照欄位**，見 §15.4 |
| 031 | `oes.toolWindowClass` | A | `ToolEventStatus.WindowClass` |
| 032 | `oes.lastToolCeid` | U4 | `ToolEventStatus.LastCeid` |

### 5.1 列舉值對照——程式列舉順序即規格數值

已核對，**三個列舉的宣告順序與規格 §1.4(c) 的數值完全一致**，可直接 `(uint)` 轉型，不需要對照表：

| 列舉 | 順序 | 規格 |
|---|---|---|
| `LeakAlarmLevel` | Idle, Normal, Warning, Alarm | (c)-1 的 0/1/2/3 ✔ |
| `CalibrationStatus` | NotCalibrated, Active, BaselineMismatch | (c)-2 的 0/1/2 ✔ |
| `RatioState` | Normal, Warning, Alarm, NoPlasma, LowSignal, NoBaseline, Disabled, Observing, NotApplicable | (c)-3 的 0～6 ✔;7–8 為本專案追加,附加在尾端 |
| `ProcessClassState` | NotConfigured, NoStep, Deciding, Classified, Unclassified | (a) VID 028 的 0～4 ✔ |
| `ToolEventContext` | Disabled, Unknown, InWindow, OutsideWindow | (a) VID 030 的 0～3 ✔ |

> ⚠️ 這是**隱性契約**。日後在這三個列舉中間插入成員，會靜默改變送給 Host 的數值。
> 三處宣告都應加註解指回本節；`RatioState` 現在也直接上報（槽位位移 05），`MonitorMode`／`LineExtractMode`
> 同理（位移 17／19：Ratio 0、AbsoluteIntensity 1；PeakHeight 0、Integral 1、RawMean 2）。

### 5.2 profile 檔不會被覆寫——新增 VID 的副作用

`SecsProfileTemplate.EnsureExists` **永遠不覆寫既有的 profile**（它可能帶著站點自己改過的 SVID
編號、自己語言的警報文字）。這是對的,但有一個代價:**升級後新增的 VID 不會出現在已經跑過一次的
機台上**,而且沒有任何地方會說。函式庫只檢查反方向——profile 指名了程式沒有的 bind 會在啟動時
以名字報錯;程式提供了 profile 沒問的 bind 則完全沉默。

所以 `SecsBridge.BindsNotInProfile` 在啟動時做這一側的比對,把差異寫進 traffic log 與系統記錄
（`SecsProfileMissingBinds`,Information——站點自己刪掉的 SV 是刪掉了,不是錯誤）。
`SecsProfileTests.The_shipped_template_asks_for_every_binding_the_app_supplies` 則把**隨附範本**
釘住:範本一定要問到程式能提供的每一個 bind,否則新加的 VID 連新機台都讀不到。

現場處理方式:按 SECS 分頁的 **Add missing rows**（補上缺少的列；Engineer 以上,`SecsProfileRows`）。它把隨附範本有、
站點 profile 沒有的列附加進站點 profile（cc = 00 的那份,不是 `.effective`）,四個區段各自比對:
statusVariables 以 `bind`、events 以 CEID、alarms 以 ALID、remoteCommands 以 RCMD 名稱。

- **既有列一律不動**——包括現場改過的編號、名稱、單位與 JSON 註解。做法是**改文字而不是重新序列化**:
  新列另起一行接在該區段最後一個元素之後,分隔的逗號放在新列開頭,原本那一行一個位元組都不變;
  整個缺少的區段接在根物件最後一個屬性之後（越過該行的註解與既有的尾逗號）;沿用檔案原本的換行
  （CRLF／LF）與 BOM;寫入前重新解析一次,解析不了就不寫。唯一會變的是原本空著的 `{}`／`[]`,
  它必須展開才放得進一列。
- **編號衝突**:要補的 SV 的 SVID 已被站點的其他列使用（例如站點自己的常數 SV）時,該列拒絕並列出,
  其他照補。只剩被拒絕的列時按鈕停用,頁籤寫明哪一列、為什麼,讓人手動改號,而不是一直重按。
- **備份**:寫入前先複製成 `{profile}.bak-{yyyyMMddHHmmss}`（同一秒內再備份時加 `_N`,不覆蓋前一份）。
- **站點刪掉的列也會補回**——光看檔案分不出「刻意刪掉」與「舊版沒寫」,所以頁籤明講這一點,
  而這也是它是按鈕、不在啟動時自動做的原因。
- **下次啟動 SECS 時生效**（Restart interface）;執行結果寫系統記錄 `SecsProfileRowsAdded`,
  內含各區段補了幾列與被拒絕的列。

也可以把該檔刪掉讓程式重新產生一份,但站點編輯會一併失去。

### 5.3 比值槽位（VID 101–400）與 CEID 507

VID 033–099 保留（027–029 已用於製程分類、030–032 已用於主機台製程事件，見上表）。

比值槽位 VID = `100 + (N−1)×30 + 位移`（N = 1…10，`RatioSlotTable.SlotCount` =
`RatioSetupViewModel.MaxRatios`），位移 01–25，共 250 個 SVID，定義見規格 §1.4(b)。

**綁定名稱** `oes.slot{N}.{field}`。25 個位移的欄位名、名稱、單位、格式只寫在一處——`SecsRatioSlots.Fields`——
隨附範本的 250 列由它產生（不手寫進 JSON 常數），`SecsBridge` 的綁定也讀它，`SuppliedBindNames` 由它串接。

| 位移 | field | 格式 | 來源 |
|---|---|---|---|
| 01 | `monitoredValue` | F4 | 快照列 `RawRatio` |
| 02 | `smoothedValue` | F4 | `SmoothedRatio` |
| 03 | `percentOfBaseline` | F4 | `PercentOfBaseline`（σ 分數或百分比，看 18） |
| 04 | `slope` | F4 | `SlopePerMinute` |
| 05 | `state` | U4 | `RatioState`；對不到列時見下 |
| 06 | `signalSnr` | F4 | `NumeratorSnr` |
| 07 | `referenceSnr` | F4 | `DenominatorSnr`；**槽位表**為絕對強度模式時 0 |
| 08 | `signalIntensity` | F4 | `NumeratorIntensity` |
| 09 | `referenceIntensity` | F4 | `DenominatorIntensity`；同 07 |
| 10–13 | `baselineMean`／`baselineSigma`／`warnThreshold`／`alarmThreshold` | F4 | 快照列同名欄位 |
| 14 | `leakRate` | F4 | `LeakRateEstimate.PerRatio` 中同 Key 者的 `LeakRate` |
| 15 | `usedInFusion` | U4 | 同上的 `Used` |
| 16 | `enabled` | U4 | 槽位表 `Enabled` |
| 17 | `monitorMode` | U4 | 槽位表 `MonitorMode` |
| 18 | `valueSemantics` | U4 | 槽位表 `ValueHasPedestal` |
| 19 | `extractionMode` | U4 | 槽位表 `ExtractMode`（分子的 `LineExtractMode`） |
| 20／21 | `signalWavelength`／`referenceWavelength` | F4 | 槽位表，**校正後**的 `CenterNm`；絕對強度模式 21 = 0 |
| 22 | `displayName` | A | 槽位表 `DisplayName` |
| 23／24 | `signalSpecies`／`referenceSpecies` | A | 槽位表，label 開頭的 token 經 `WavelengthCalibration` 別名表（`N₂`→`N2`、`Hα`→`H`），使用者譜線保留 `u` 前綴；絕對強度模式 24 = "" |
| 25 | `key` | A | 槽位表 `Key`（不變） |

槽位對應改變的時機：在 Ratio Setup 用 **▲／▼**（Move up／Move down）**調整列順序**，或**刪除、新增一列**（刪掉第 1 列，其後每組比值都往前移一個槽位）、改譜線／模式／啟用／名稱，或改波長校正。
移動列是搬動同一個 `RatioEditViewModel`，Key 不變，所以基準、校正與鎖存警報都會延續（`RatioSetupReorderTests`）；刪掉再重建則會拿到新 Key，三者都對不上。
`settings.json` 裡直接改 `ratios` 的順序要重開 App 才生效，那時沒有「前一份」可比，不送 507。

**槽位表**（`RatioSlotTable`／`RatioSlot`）是引擎在建構子與每次 `ReloadRatios` 建立的不可變記錄，
槽位 N = 引擎比值清單第 N 筆（= Ratio Setup 第 N 列），每槽記錄位移 16–25 需要的一切。`SecsBridge`
以 volatile 參考保存（`OnSlotTable`，比照 `ToolEventStatus`），所以**身分類不讀快照**：設備剛啟動、
擷取停止、沒有任何光譜幀時都正確。

**即時量以 Key 對應，不用索引。** 位移 01–15 從最後一幀的 `LeakMonitorSnapshot.Ratios` 找 `Key` 等於該槽位
Key 的那一列。重排後、下一幀之前，手上的快照仍是舊順序——以索引取就會把甲的數值放進乙的槽位。
擷取停止後停在最後一幀，與全域 VID 一致。07／09 的「絕對強度模式為 0」依**槽位表**的模式判斷（與 17 一致），
不依快照列的模式，理由相同。

**無值規則**：F4 一律送 0（`F4()` 把 null、NaN、∞ 都轉成 0），字串送 ""。Host 依 05／18／19 判讀，
規格 §1.4(b) 有對照表。對不到列時（重載後第一幀之前）05 依槽位表回 3（啟用）或 6（停用）。
**空槽位**（N 大於比值數）照常回應：16 = 0、05 = 6，數值 0，字串空；看 25 是否為空分辨空槽與停用。

**位移 18 直接取 `ValueHasPedestal`**，不在 `SecsBridge` 依 17／19 重推——那正是 v2.1 規格寫錯的那一步。

**位移 22 = `DisplayName`、位移 25 = Key（2026-10-23 決定）。** Key 不變，跨重載、跨改名追蹤用 25；22 給人看。
A 格式是 ASCII：出廠顯示名稱 `N₂ 337 / Ar 750` 的下標會變成 `?`（實測 Host 收到 `N? 337 / Ar 750`）。

**CEID 507**：`RatioSlotTable.SameIdentityAs` 比較 10 槽（含空槽）的位移 16–25，`ReloadRatios` 發現不同時
引擎觸發 `SlotIdentityChanged`，`SecsBridge.OnSlotIdentityChanged` 先存新表再走既有的 `Enqueue` 送 S6F11，
受「回報事件」與測試模式閘門控制（與 502／508／509 相同）。只改門檻、平滑、`ConfirmSeconds`、`MinSnr` 不送；
`ProcessClass`／`Role` 不在任何位移裡，Host 看不到，所以也不送（它們影響的是 `MeasuresSameAs`——鎖存與基準的延續——
那是另一個問題）。

**這些都在 bridge 裡，與「只讀不算」的原則怎麼並存。** 以 Key 找列、05 的 3／6 規則、07／09 依槽位模式歸零，
都是把**引擎已經算好的兩份資料（快照、槽位表）對起來**，沒有重算任何量測值；把這一步放在 bridge 是 GitHub issue #45 的設計決定。

逐槽位的數值範例見 [`secs-ratio-svid-example-zh-TW.md`](secs-ratio-svid-example-zh-TW.md)。
2026-10-20 比對程式時發現的三處規格出入（位移 18、05 的 7／8、22）已在規格 v2.2／v2.4 全部關閉。

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
| 507 | Ratio configuration reloaded | `LeakMonitorEngine.SlotIdentityChanged`：`ReloadRatios` 後槽位身分（位移 16–25）改變時，見 §5.3 |
| 508 | Acquisition started | `DeviceViewModel.IsAcquiring` false→true |
| 509 | Acquisition stopped | `IsAcquiring` true→false |

`MainViewModel` 已在監看 `IsAcquiring` 的轉換（用來呼叫 `LeakMonitorEngine.ReloadRatios()`），
508/509 接在同一處，不要另開一個監看。

502 的來源是引擎新增的 `Acknowledged` 事件，**觸發條件與既有的 `LeakMonitorAcknowledged` 稽核
紀錄完全相同**（`cleared.Count > 0`）——一次沒清到任何東西的 Acknowledge 不寫紀錄，也不發事件，
所以事件的意義就是那行紀錄的意義：有人結束了一個已確認的洩漏警報。

規格 §6.1 另定義了 501、503–506、510，本期不送；日後補上時沿用該編號，不要另起一套。
隨附範本的 `events` 區段列出設備會送的四個 CEID（給讀 profile 的人看；程式依編號送出，不讀這個清單）。

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
| `RatioSlotTable.cs` | §5.3：`RatioSlot`／`RatioSlotTable`，引擎發布的槽位身分表 |
| `SecsRatioSlots.cs` | §5.3：25 個位移的唯一定義，範本列與綁定共用 |
| `SecsProfileRows.cs` | §5.2：補上缺少的列——比對、以文字插入、衝突拒絕 |
| `ToolEventSettings.cs` | §15：`ToolEventSettings`／`ToolEventMapping`／`ToolEventEdge`、`ToolEventContext`（VID 030 的編碼）、`ToolEventStatus`、`ToolEventAck`、引擎用的不可變對照表 `ToolEventTable` |

### 11.2 修改

| 檔案 | 改動 |
|---|---|
| `AppSettings.cs` / `SettingsService.cs` | 加 `secs` 區塊 |
| `MainViewModel.cs` | 建立並持有 `SecsBridge`；餵 snapshot；接 `IsAcquiring` 轉換與 Acknowledge |
| `MainWindow.xaml(.cs)` | 新增 `SecsTab`（**必須有 `x:Name`**，權限閘門以參考比對認分頁，見 `CLAUDE.md`） |
| `LeakMonitorEngine.cs` | §8 的三個快照欄位，以及 `Acknowledged` 事件 + `LeakAcknowledgedEventArgs`（§7） |
| `OES_Leak_Monitor.csproj` | `PackageReference`：`Aqusen.Secs 0.6.0`、`Secs4Net 2.4.4`；profile 範本不隨 exe 出貨（首次啟動寫到 `ConfigDirectory`） |
| `nuget.config` | 加 `aqusen` 來源（**不要動既有的 `<clear />`**，它是本專案刻意保留的） |
| `docs/Satellite_SECS_Specification_v2.md` | §5.1 補 ALID 012–014；v2.2 修訂 §1.4(b)～(d) 的位移 18 判定、Ratio state 7／8、位移 22 待決（§5.3）；v2.3 增訂 §7 S2F41 `TOOL_EVENT` 與 VID 030–032（§15）；v2.4 實作比值槽位與 CEID 507、定位移 22／25、無值與空槽位規則（§5.3） |
| `LeakMonitorEngine.cs`（§15） | `ConfigureToolEvents`（staged）、`ConfigureToolEventsLive`（hot）、`SetReplayActive`、`ReceiveToolEvent`、`ToolStatus`／`ToolStatusChanged`；`CurrentFrameClass()` 與每類別電漿門檻改看視窗 |
| `SecsBridge.cs`／`SecsViewModel.cs`／`SecsPanel.xaml`（§15） | `RemoteCommandHandler`、VID 030–032 binding、`ToolEventIgnored`；分頁的製程事件設定、狀態列、唯讀 CEID 表 |
| `BatchTracker.cs`／`BatchCsvLogger.cs`／`RatioCsvLogger.cs`／`AcquisitionSidecar.cs`／`LeakMonitorViewModel.cs`（§15） | 批次步驟類別、CSV 與 sidecar 欄位、Leak Monitor 提示條 |
| `LeakMonitorEngine.cs`／`SecsBridge.cs`／`SecsProfileTemplate.cs`／`SecsViewModel.cs`／`SecsPanel.xaml`（§5.2、§5.3） | `SlotTable`／`SlotIdentityChanged`；250 個槽位綁定與 CEID 507；範本的槽位列與 `events` 區段；補列按鈕 |
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
SECS 部分當初是 38 個測試。整個測試專案（含洩漏引擎、批次、Baseline Builder 等）到 2026-10-23
為 **271 過、7 略過**（略過的是需要不在 repo 裡的錄檔的那些）。下表列 SECS 與製程事件相關的：

| 檔案 | 涵蓋 |
|---|---|
| `SecsChamberCodingTests` | 編號運算對照規格範例、腔體表、戳記的冪等與可重戳、錯誤 `ss`／`aa`／腔體被拒、非本系統的 id 不被動到 |
| `SecsProfileTests` | 範本可載入、範本與 App 供應的 binding 雙向一致（現為 282 個）且每個都真的有登錄、`remoteCommands` 只有 `TOOL_EVENT`、既有 profile 永不被覆寫；槽位列逐槽逐位移符合 `100 + (N−1)×30 + 位移`、各位移的格式、`events` 有 507 |
| `SecsWireTests` | 真的 `SecsBridge` 對真的 `GemHost` 走 loopback：S1F3 回 282 個 SV、S5F5 的 ALID 是 ASCII 且 category 正確、Warning→Alarm→Normal 送出正確的 set/clear 順序、故障警報去抖動 |
| `SecsEventTests` | 同樣走 loopback 的 **S6F11 事件路徑**：三個 CEID 各自送達且編號正確、`Enqueue` 的順序保證（連續六則）、`reportEvents = false` 只擋事件不擋警報、測試模式閘門預設擋住事件與洩漏警報但**不擋設備故障**、介面關閉時呼叫不拋例外 |
| `AppSettingsDefaultsTests` | 全新 `settings.json` 的三個覆寫預設（觸發波長 337.1、記錄器預設啟用、`ForceTestMode` 為假），以及「已存檔的決定要被尊重」那半個契約 |
| `SecsTraceTests` | **S2F23／S6F1 trace**：取樣依 SVID 順序回傳綁定值、`SMPLN` 連號不重複；以及「上報全關時 trace 仍回得到真值」（composite 仍是 Alarm、VID 016 仍是 1，且確實沒送出任何 S5F1／S6F11） |
| `RecordedRunTests` | **實機錄檔重放**，見下 |
| `ToolEventWindowTests` | 製程事件在**引擎接縫**：視窗內才判定、視窗外 NotApplicable、未指定類別的比值不受影響、電漿閘門照舊、鎖存保留、未知時沿用舊行為、每類別電漿門檻、Golden Run 只從所屬視窗學習、事件不成對與逾時、視窗外電漿、分類器交叉驗證、測試模式與回放不套用、狀態在幀與幀之間即時發布、事件計數、「(not applied …)」日誌 |
| `ToolEventWireTests` | 製程事件在**線路接縫**：S2F41 的 HCACK 0／2／3 與 CPACK、對照表外的 CEID 回 0、`TIME` 格式、CEID 解析、VID 030–032 |
| `ToolEventOutputTests` | Ratio CSV 的 `ToolContext,ToolWindowClass` 欄（只在使用製程事件時出現）、舊檔照讀、sidecar 的視窗欄位、`ToolEventIgnored` 每個 CEID 只記一次 |
| `ToolEventUiTests` | SECS 分頁設定的存取（staged 與立即生效的欄位、`MaxWindowSeconds` 範圍檢查、CEID 表帶過存檔）、「尚未生效」提示、狀態列文字、Leak Monitor 提示條 |
| `RatioSlotTableTests` | 引擎槽位表：建構後即有、校正後波長且 `settings.json` 不變、`u` 前綴、重排後 Key→槽位正確；只改門檻／平滑不通知，改譜線／模式／擷取／啟用／名稱／增刪／重排／波長校正各通知一次；第 11 列不算槽位 |
| `SecsRatioSlotTests` | 槽位綁定（經 `SvBindings`，與 S1F3 同一條路）：沒有幀時身分正確、空槽位、重排前的快照以 Key 對應不錯槽、無值與非數字送 0、絕對模式 07／09 = 0、`PerRatio` 依 Key |
| `SecsRatioSlotWireTests` | loopback：S1F3 讀槽位 1～3 的身分（沒有任何幀）與即時量、重排後收到 CEID 507 且讀得到新身分、只改門檻收不到、S2F23 能追蹤槽位 VID（不需升級 `Aqusen.Secs`） |
| `SecsProfileRowsTests` | 補列：以 2026-09-04 的真實範本（`SecsProfileFixtures`）為舊 profile，既有每一行逐字保留、改號改名的列不動、刪掉的列補回、編號衝突拒絕且其他照補、尾逗號、行尾註解、CRLF、非清單的區段 |
| `RatioSetupReorderTests` | Ratio Setup 的 ▲／▼：Guest／Operator 不能按、首列不能上移末列不能下移、移動是暫存（Save 才寫入、Revert 還原順序）、Save + `ReloadRatios` 後槽位表依新順序且只通知一次、每組比值 `MeasuresSameAs` 不變、鎖存警報與基準在移動後保留 |
| `SecsProfileRowsButtonTests` | 補列按鈕：Guest／Operator 不能按、沒有可補時停用並說明、`.bak` 與補列前相同、`SecsProfileRowsAdded`、保留 BOM、同秒備份不覆蓋；loopback 的 `SecsProfileRowsWireTests`：舊 profile 對 `TOOL_EVENT` 回 HCACK 1，補列並重啟後不再是 1 |
| `BatchToolClassTests` | 批次步驟的類別取自取樣區間：區間內出現兩種類別時不取樣也不起批、`Unknown` 不取樣、功能關閉時與以前相同 |

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

狀態欄的意思：**⬜ = 沒有人跑過**，不是「應該會過」。跑過之後把該列改成
`✅ YYYY-MM-DD（執行者）`，並把對應的 `secs_YYYYMMDD.log` 存成驗收附件。

| # | 項目 | 狀態 |
|---|---|---|
| 1 | Replay 分頁播一段有洩漏的錄檔，勾「Raise leak alarms during replay」與「測試模式也上報」→ 應看到 S5F1 set/clear（ALID `1cc27001`／`1cc27002`）與 CEID `1cc27508`／`1cc27509`；按 Acknowledge → CEID `1cc27502` | ⬜ 未驗收 |
| 2 | 取消「測試模式也上報」→ 同一段錄檔不應再送出任何 S5F1／S6F11，但 S1F3 仍回得到值且 VID 016 = 1 | 🟡 部分自動化——閘門與「上報全關仍讀得到真值（含 VID 016）」已由 `SecsEventTests`／`SecsTraceTests` 涵蓋；剩下的是同一件事發生在真實 Replay 流程裡 |
| 3 | 拔掉光譜儀 → ALID `1cc27012` set；插回 → clear | ⬜ 未驗收 |
| 4 | 在 Host 端做一次 S2F23 Trace，確認 S6F1 依週期回傳 | ✅ 已自動化（`SecsTraceTests`，走 loopback）。仍建議在真實 Host 上做一次，因為對方的 DSPER／REPGSZ 慣例才是驗收標的 |
| 5 | 全程比對 `secs_YYYYMMDD.log` 與 `Test_SECS` 兩邊的收送紀錄 | ⬜ 未驗收 |
| 6 | 重播 2026-08-14 錄檔（勾「Raise leak alarms during replay」），Host 追蹤槽位 1～3 的位移 05（SVID `1cc2700105`／`135`／`165`）→ 隨重播走 0 → 1 → 2 | ⬜ 未驗收（線上讀值已由 `SecsRatioSlotWireTests` 涵蓋；剩真實重播流程） |
| 7 | 在 Ratio Setup 把第 1 列往下移一列（▼）、存檔、Stop → Start → Host 收到 CEID `1cc27507`，重讀位移 25 得到新順序；只改門檻再重啟則收不到 | ⬜ 未驗收（線上行為已由 `SecsRatioSlotWireTests`、▲／▼ → Save → `ReloadRatios` 已由 `RatioSetupReorderTests` 涵蓋；剩 `MainViewModel` 在 Stop → Start 時呼叫 `ReloadRatios` 的那一段） |
| 8 | 以 2026-10-15 之前的 profile 啟動 → 按「Add missing rows」→ Restart interface → Host 用 S1F3 查得到槽位 VID，`TOOL_EVENT` 不再回 HCACK 1 | ⬜ 未驗收（已由 `SecsProfileRowsWireTests` 涵蓋；剩真實按鈕與真實 profile） |

第 1 項的 S5F1 那半已由 §13.3 自動化，三個 CEID 的線上行為則由 `SecsEventTests` 自動化；
它真正剩下要驗的是 **Replay 分頁到引擎、以及 UI 按鈕到 bridge 的接線**。

#### 三個 CEID 的狀態

| CEID `1{cc}27nnn` | 事件 | 觸發點 | 自動化涵蓋 | 仍待人工的部分 |
|---|---|---|---|---|
| 502 | 警報已確認 | `LeakMonitorEngine.Acknowledged` → `SecsBridge.OnAcknowledged` | ✅ `SecsEventTests` | 按下 Acknowledge 會走到 `OnAcknowledged` |
| 508 | 開始擷取 | `IsAcquiring` false→true → `OnAcquisitionChanged(true)` | ✅ `SecsEventTests` | 按下 Start 會走到 `OnAcquisitionChanged(true)` |
| 509 | 停止擷取 | `IsAcquiring` true→false → `OnAcquisitionChanged(false)` | ✅ `SecsEventTests` | 按下 Stop 會走到 `OnAcquisitionChanged(false)` |

**線上行為已經測掉了**：三則 S6F11 真的走過 loopback 送到 `GemHost`、CEID 編號正確、
`Enqueue` 的順序在連續六則下維持、`reportEvents = false` 與測試模式閘門各自擋住事件
（前者以「警報仍送得出去」為對照，確保測到的是開關而不是一條死掉的連線）。

**剩下的人工驗收只有一件事：GUI 到 bridge 的那一段接線**——按鈕真的會呼叫到上表的方法。
那需要有人開著 App 按，所以 §13.4 第 1 項仍然是 ⬜。

#### 為什麼第 1、3、5 項到此為止

不是還沒做，是**自動化到不了**：

- **第 1 項**要有人在 Replay 分頁選檔、按 Play、按 Acknowledge。引擎以下（錄檔 → 引擎 → S5F1／S6F11）
  已由 §13.3 與 `SecsEventTests` 走完，缺的正好是按鈕到那些方法的那一步。
- **第 3 項**要有人把光譜儀的線拔掉再插回。`ReportFault` 的去抖動與「測試模式不擋設備故障」
  都有測試，`MainViewModel` 對 `IsConnected` 的監看則需要真的裝置。
- **第 5 項**是前四項的副產物：跑完之後把兩邊的日誌對一次。

驗收時照 [`secs-operation-sop-zh-TW.md`](secs-operation-sop-zh-TW.md) §9 的步驟走即可，
那份文件就是為此寫的。

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
| 4 | 比值槽位 VID | **已關閉（2026-10-23）**：已實作，槽位對應與位移 22／25 的決定見 §5.3 |
| 5 | 設備故障的來源 | 已定案：綁在 `IsConnected`／`Status`／`ErrorOccurred` 三個明確轉態上（§6.1），不依賴框架未公開的事件。012 是轉態非狀態，刻意如此 |
| 6 | 多腔體 | 一個 App 執行個體對一個 `cc`。同一台 PC 跑兩套監測不同腔體時，port 必須分開 |
| 7 | 舊 profile 沒有 `TOOL_EVENT` | profile 永不被覆寫（§5.2），所以升級前就啟用過 SECS 的機台，profile 裡沒有 `TOOL_EVENT` 這個 RCMD、也沒有 VID 030–032：函式庫會對 `TOOL_EVENT` 回 **HCACK 1**，啟動時記 `SecsProfileMissingBinds`。處理：按 SECS 分頁的 **Add missing rows**（§5.2）——會一併補上 `TOOL_EVENT`、030–032、槽位 VID 與 `events`，既有列不動、先備份——再 Restart interface |
| 8 | 製程事件的時間基準 | 視窗逾時以光譜幀的時間戳計算；`TIME` 只用於日誌與狀態。OES PC 與 EAP 的時鐘若差很多，`ToolEventClassMismatch` 是第一個看得到的徵兆 |

---

## 15. 主機台製程事件（S2F41 `TOOL_EVENT`，2026-10-23）

設計與取捨見 GitHub issue #25（spec）與子 ticket #26–#35；給 EAP 平台方看的介面在
[`eap-integration-spec-zh-TW.md`](eap-integration-spec-zh-TW.md)，Host 端欄位語意在規格 §7。

### 15.1 為什麼要有

PM3 交替跑 Chamber Process／precoat（製程 A）、Chamber Clean（製程 B）與尚未歸屬的製程 C。用同一套判定看三種電漿沒有意義。
光譜分類器只能推論，主機台本身確切知道，而廠內 EAP 已經收得到主機台的 PM3 ProcessStart／ProcessEnd CEID。
所以由 EAP 經既有的 OES 連線原樣轉送，OES 依對照表開關「製程視窗」，**事件是製程的權威來源**，分類器只做交叉驗證。

### 15.2 設定（`settings.json` → `secs.toolEvents`）

| 欄位 | 預設 | 生效時機 | 說明 |
|---|---|---|---|
| `enabled` | false | **staged**（下一次採集 Stop→Start） | 總開關 |
| `mappings[]` | 空 | **staged** | `{ ceid, className, edge: "Start"/"End", name, plasmaThreshold }`；`className` 空 = 只記錄；`plasmaThreshold` ≤ 0 = 沿用 |
| `maxWindowSeconds` | 600 | **存檔即生效**（`ConfigureToolEventsLive`） | 視窗最長存活時間；SECS 分頁限 10–86400 |
| `applyInTestMode` | false | **存檔即生效** | 測試模式的合成光譜是否套用視窗；回放一律不套用 |

staged 的理由和 Ratio Setup 相同：對照表產生的類別名稱就是比值 `ProcessClass` 所指的，兩者是同一份設定，只套用一半會讓兩邊在整段運行中對不上。
MainViewModel 在啟動時與每次 Stop→Start（和 `ReloadRatios` 同時）呼叫 `ConfigureToolEvents`；**同一份表重新套用會保留目前視窗**，表變了才回到 Unknown。
`ToolEventSettings.SameStaged` 比對「已存檔」與「生效中」，SECS 分頁據此顯示橘色的「尚未生效」提示。

### 15.3 引擎

- **收事件（SECS 接收執行緒）**：`ReceiveToolEvent(ceid, time)` 只查表、記下最後一筆（032、計數）、排進佇列（上限 1000），立刻回 `Accepted`／`Ignored`／`Disabled`，所以 HCACK 不等光譜。
- **套用（下一幀開頭，`_gate` 內）**：Start 開窗、對應 End 關窗；`className` 空的只記 `ToolEventRecorded`。同一幀的所有比值看到同一個視窗。
- **不成對**：視窗開著又來 Start → 關舊開新，`ToolEventWindowUnclosed`；End 對不上開著的視窗（含 Unknown／視窗外時）→ 忽略，`ToolEventOrphanEnd`；開窗超過 `maxWindowSeconds`（從套用該視窗的那一幀起，以幀時間計）→ 視窗作廢、回到 Unknown，`ToolEventWindowTimedOut`。
- **`CurrentFrameClass()` 的判定順序**：視窗內 → 視窗類別（逐幀，不鎖步驟）；視窗外 → 有電漿（步驟開啟中）為 `Unknown`、無電漿為無類別，兩者都「有分類」，所以指定類別的比值 NotApplicable、未指定的照常判定；Unknown 或未使用 → 與沒有製程事件時完全相同（分類器有開用分類器，否則不分類）。
- **每類別電漿門檻**：視窗內先取對照表該類別的 `plasmaThreshold` → 分類器（有開時）的門檻 → 分類器設定裡同名類別的 `PlasmaThreshold`（分類器關著也用）→ 基本門檻。
- **027／028**：視窗內 028 = 3（點火前亦然）、027 = 視窗類別；視窗外有電漿 4／`Unknown`；無電漿 1／空字串；Unknown 或未使用時照分類器。
- **視窗外電漿**：每段結束時記一筆 `ToolEventUnmappedPlasma`（開始時間、長度、觸發量的中位數與最大值）——對照表沒有的製程（目前是 C）或 EAP 漏轉的 Start 就是從這裡看出來。
- **交叉驗證**：分類器有開、且步驟判定時在視窗內，結果與視窗類別不同就記一筆 `ToolEventClassMismatch`；不影響判定。
- **暫不套用（suppressed）**：回放期間（`SetReplayActive`，由 `BeginReplayOutput`／`EndReplayOutput` 告知）一律不套用；測試模式的合成光譜除非 `applyInTestMode`。依「上一幀是否為合成光譜」決定。此時事件照收、032 照更新、背後的視窗照常追蹤（下一個實機幀直接回到正確的視窗），但判定用的狀態是 Unknown（fail-open），並記 `ToolEventsSuppressed`／`ToolEventsResumed`；期間的 `ToolEventWindowOpened`／`Closed` 日誌結尾加上 `(not applied: synthetic test-mode spectra)` 或 `(not applied: a replay is running)`，事件名稱不變。

### 15.4 狀態發布（VID 030–032 為什麼不是快照欄位）

第一版從最後一幀的快照讀 030–032，結果沒有光譜流動時（剛啟動、採集停止）已套用的表顯示「not in use」、030 = 0，EAP 轉送的事件也不會反映在 032——直到碰巧開始採集。這是 2026-10-23 實際執行 App 才發現的，引擎與線路測試都沒抓到。
現在引擎持有一份 `ToolEventStatus`（State、WindowClass、LastCeid、LastTime、ReceivedCount），在套用設定、收到事件、視窗變動、暫不套用狀態改變時立即重新發布（`ToolStatusChanged`），`SecsBridge.OnToolStatus` 存下這份不可變物件，binding 只讀它——仍然是「只讀、不計算」。快照仍保留 `ToolEventState`／`ToolWindowClass` 給逐幀的使用者（CSV、Leak Monitor 提示條）。

### 15.5 SECS

- `SecsBridge` 設定 `EquipmentSimulator.RemoteCommandHandler`（`Aqusen.Secs` 0.7.0）。函式庫先檢查：不在 REMOTE → 2、RCMD 不在 profile → 1、參數名稱不在 profile → 3／CPACK 1，通過才呼叫。
- App 的判定：RCMD 不是 `TOOL_EVENT` → 1；缺 `CEID` → 3；`CEID` 無法解讀 → 3／CPACK 2（接受 ASCII 數字或 U1／U2／U4／U8）；`TIME` 格式錯誤（`YYYYMMDDhhmmsscc` 或 `YYYYMMDDhhmmss`）→ 3／CPACK 2，事件不採用；未啟用 → 2；對照表外 → 0，系統日誌記 `ToolEventIgnored`（**每個 CEID 每次執行只記一次**，traffic log 每筆都有）；對照表內 → 0。
- 範本 profile 登記 `"remoteCommands": { "TOOL_EVENT": [ "CEID", "TIME" ] }` 與 VID 030–032。既有 profile 不會自動補上，見 §14 #7。

### 15.6 輸出與批次層

- **Ratio CSV**：使用製程事件時（以開檔當下判定，規則同分類器欄位）尾端加 `ToolContext,ToolWindowClass`（`Unknown`／`InWindow`／`OutsideWindow`、類別）。檔案中途不會換欄位：對照表只在採集重啟時套用，而停止採集會關檔。
- **`.acq.json`**：錄檔開啟時記 `toolEventContext`、`toolWindowClass`，放在採集指紋**之外**，避免被當成採集參數不符。
- **批次層**：步驟的類別取自取樣區間（開閘後 10–30 s）內各幀的類別；只有一種就是該類，兩種以上（視窗邊界落在區間內）就不取樣、不能起批，記 `BatchStepClassAmbiguous`；區間內完全沒有類別（步驟太短）時沿用「最後一個非空類別」。

### 15.7 畫面

- **SECS 分頁狀態列**（不需登入）：`Tool events: …`（not in use／in window X／outside any window／window unknown — …）、`Last tool event: CEID n at hh:mm:ss`、`Tool events received this run: n`。
- **SECS 分頁設定**（Engineer+、Save & apply）：`Use tool events — takes effect at next acquisition start`、`Max window (s) — applies on save`、`Apply tool events in test mode — applies on save`。
- **唯讀 CEID 表**（不需登入）與橘色的「Saved tool-event switch / CEID table not yet in force — restart acquisition (Stop, then Start) to apply it.」
- **Leak Monitor 提示條**：使用中且狀態 Unknown 時顯示 `PROCESS CONTEXT UNKNOWN — …`；測試模式提示已寫「tool events are not applied」時不重複顯示。

### 15.8 日誌事件一覽

| 名稱 | 等級 | 何時 |
|---|---|---|
| `ToolEventsApplied`／`ToolEventsOff` | Information | 採用／停用對照表 |
| `ToolEventWindowOpened`／`ToolEventWindowClosed` | Information | 視窗開／關（暫不套用時結尾加 `(not applied: …)`） |
| `ToolEventRecorded` | Information | 只記錄的事件（`className` 空） |
| `ToolEventIgnored` | Information | 對照表外的 CEID，每個 CEID 每次執行一次 |
| `ToolEventWindowUnclosed` | Warning | 視窗未收到 End 就來了新的 Start |
| `ToolEventOrphanEnd` | Warning | 對不上的 End |
| `ToolEventWindowTimedOut` | Warning | 視窗超過 `maxWindowSeconds` |
| `ToolEventUnmappedPlasma` | Information | 視窗外的一段電漿結束 |
| `ToolEventClassMismatch` | Warning | 分類器判定與視窗類別不同 |
| `ToolEventsSuppressed`／`ToolEventsResumed` | Information | 開始／結束暫不套用 |
| `BatchStepClassAmbiguous` | Information | 批次步驟的取樣區間出現兩種以上類別 |
