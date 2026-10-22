# 全域量測項目 VID 001～009 設定與判讀範例

> 本文以 Ch_2 一台裝了出廠比值組的 OES Leak Monitor 為例，說明全域 VID 001～009 怎麼設定（profile）、各項的值從哪裡來，以及在不同機台情境下 Host 會讀到什麼、該怎麼解讀。
> 依據：`docs/Satellite_SECS_Specification_v2.md` §1.4(a)、(c)、(d)；設備端實作見 `docs/secs-integration.md` §4、§5 與 `SecsBridge.BuildBindings`。

> ✅ **實作狀態：** VID 001～032 **已實作**，可用 S1F3 查詢、S2F23 追蹤。（比值槽位 VID 101～400 尚未實作，見 `secs-ratio-svid-example-zh-TW.md`。）
> 本文情境中的數值是示意，用來說明怎麼解讀，不是實測數據。

## 目錄

- [1. 九個 VID 一覽](#1-九個-vid-一覽)
- [2. Profile 設定例](#2-profile-設定例)
- [3. 各 VID 的來源與讀法](#3-各-vid-的來源與讀法)
- [4. 情境範例：六種機台狀態下的讀值](#4-情境範例六種機台狀態下的讀值)
- [5. S1F3 查詢範例](#5-s1f3-查詢範例)
- [6. S2F23 追蹤範例](#6-s2f23-追蹤範例)
- [7. 判讀注意事項](#7-判讀注意事項)
- [附錄：列舉值對照](#附錄列舉值對照)

## 1. 九個 VID 一覽

SVID = `1` + `cc` + `27` + `00` + `vvv`。Ch_2（`cc = 02`）的 VID 001～009 如下：

| VID | SVID（Ch_2） | 項目 | 單位 | 型別 | 一句話 |
|---|---|---|---|---|---|
| 001 | `1022700001` | Leak rate | mbar·L/s | F4 | 融合各比值後的洩漏率估計 Q̂ |
| 002 | `1022700002` | Leak rate sigma | mbar·L/s | F4 | Q̂ 的 1σ 不確定度 |
| 003 | `1022700003` | Leak rate confidence | 0～1 | F4 | 各比值推得的洩漏率彼此一致的程度 |
| 004 | `1022700004` | Leak rate valid | — | U4 | 1 = 本幀有有效的洩漏率估計 |
| 005 | `1022700005` | Out of calibrated range | — | U4 | 1 = 超出校正涵蓋範圍（外插值） |
| 006 | `1022700006` | Calibration status | — | U4 | 0 未校正／1 有效／2 基準不符 |
| 007 | `1022700007` | Composite leak level | — | U4 | 0 Idle／1 Normal／2 Warning／3 Alarm |
| 008 | `1022700008` | Enabled ratio count | 個 | U4 | 未被停用的比值數 |
| 009 | `1022700009` | Warning ratio count | 個 | U4 | 處於 Warning 的比值數 |

可以分成三組來看：

- **001～005：洩漏率（定量）**——「漏了多少」。只有做過洩漏率校正才有意義。
- **006～007：兩個 Host 要據以動作的狀態**——校正能不能用、整台判定為何。
- **008～009：複合判定是怎麼得出來的**——只讀 007 的 Host 分不出「健康的 Normal」和「四個比值有三個因訊號不足停判的 Normal」。

## 2. Profile 設定例

### 2.1 檔案位置

| 檔案 | 位置 | 說明 |
|---|---|---|
| 站點 profile | `{ConfigDirectory}\profiles\*.json` | 第一次啟用 SECS 時由內建範本寫出，**`cc` 一律寫成 `00`**。站點可改編號或顯示名稱，**程式之後不會覆寫它** |
| 生效 profile | `{ConfigDirectory}\profiles\.effective\*.json` | 每次啟動時把 SECS 分頁設定的腔體號蓋進每個 `svid`，寫到這裡並由此載入 |

兩個路徑都會印在 SECS 分頁的 traffic log 開頭。

### 2.2 站點 profile（`cc = 00`，未蓋章）

```jsonc
"statusVariables": [
  { "svid": 1002700001, "name": "Leak rate",               "units": "mbar-L/s", "format": "F4", "bind": "oes.leakRate" },
  { "svid": 1002700002, "name": "Leak rate sigma",         "units": "mbar-L/s", "format": "F4", "bind": "oes.leakRateSigma" },
  { "svid": 1002700003, "name": "Leak rate confidence",    "units": "",         "format": "F4", "bind": "oes.leakRateConfidence" },
  { "svid": 1002700004, "name": "Leak rate valid",         "units": "",         "format": "U4", "bind": "oes.leakRateValid" },
  { "svid": 1002700005, "name": "Out of calibrated range", "units": "",         "format": "U4", "bind": "oes.outOfCalibratedRange" },
  { "svid": 1002700006, "name": "Calibration status",      "units": "",         "format": "U4", "bind": "oes.calibrationStatus" },
  { "svid": 1002700007, "name": "Composite leak level",    "units": "",         "format": "U4", "bind": "oes.compositeLevel" },
  { "svid": 1002700008, "name": "Enabled ratio count",     "units": "",         "format": "U4", "bind": "oes.enabledRatios" },
  { "svid": 1002700009, "name": "Warning ratio count",     "units": "",         "format": "U4", "bind": "oes.warningRatios" },
  ...
]
```

### 2.3 生效 profile（SECS 分頁腔體號設為 `02` 之後）

```jsonc
"statusVariables": [
  { "svid": 1022700001, "name": "Leak rate",               ..., "bind": "oes.leakRate" },
  { "svid": 1022700002, "name": "Leak rate sigma",         ..., "bind": "oes.leakRateSigma" },
  ...
  { "svid": 1022700009, "name": "Warning ratio count",     ..., "bind": "oes.warningRatios" },
]
```

### 2.4 站點可以改什麼、不能改什麼

| 欄位 | 可改 | 說明 |
|---|---|---|
| `name`、`units` | ✔ | S1F11／S1F12 回給 Host 的名稱與單位，可改成站點慣用語 |
| `svid` 的 `vvv`（後三碼） | ✔ | Host 若已有自己的編號可改，但要和 Host 的設定一致 |
| `svid` 的 `cc` | ✘ | 一律寫 `00`，由 SECS 分頁的腔體號蓋章；寫死別的值會被覆蓋 |
| `svid` 的 `ss`／`aa` | ✘ | 蓋章時會驗證 `ss = 27`、`aa = 00`，不符則**拒絕啟動** SECS，不會送出屬於別種感測器的編號 |
| `bind` | ✘ | 程式內的資料來源名稱。寫錯或寫了不存在的名稱，啟動時會以名稱報錯 |
| 刪掉一列 | ✔ | 該 VID 就不再提供；啟動時記一筆 `SecsProfileMissingBinds`（Information），不視為錯誤 |

> **升級注意：** 因為程式不覆寫既有 profile，新版程式增加的 VID 不會自動出現在已經啟用過 SECS 的機台上。traffic log 與系統記錄的 `SecsProfileMissingBinds` 會列出缺了哪些 bind；把缺的列貼進 profile，或刪掉 profile 讓程式重寫一份（站點的修改會一併失去）。

## 3. 各 VID 的來源與讀法

設備端不為 SECS 另外計算：每一幀 Leak Monitor 引擎產生一份快照（`LeakMonitorSnapshot`），Host 查詢時只讀這份快照的欄位、轉成規定型別。所以 SECS 讀到的值和設備畫面上 Leak Monitor 分頁顯示的是同一份數字。

| VID | bind | 設備端來源 | 讀法 |
|---|---|---|---|
| 001 | `oes.leakRate` | `Snapshot.LeakRate.LeakRate`；沒有估計時送 0 | **先看 004。** 004 = 0 時 001 的 0 代表「沒有估計」，不是「沒有漏氣」。估計值下限截在 0，不會是負數 |
| 002 | `oes.leakRateSigma` | `LeakRateEstimate.Sigma` = 1/√(Σwᵢ)，各比值依反變異數加權 | Q̂ ± σ 是 1σ 區間。σ 和 Q̂ 同量級時，「有漏」的結論還不穩 |
| 003 | `oes.leakRateConfidence` | `LeakRateEstimate.Confidence`，由各比值推得的 Qᵢ 彼此一致程度算出 | 接近 1 = 各比值說法一致；偏低表示比值之間互相矛盾，可能是單一比值受干擾而非真漏 |
| 004 | `oes.leakRateValid` | `LeakRate?.HasEstimate == true` → 1 | 001～003 的有效旗標。未校正、基準不符、電漿未開、沒有比值可用時都是 0 |
| 005 | `oes.outOfCalibratedRange` | `LeakRateEstimate.OutOfCalibratedRange` | 1 = Q̂ 超過校正點涵蓋的最大漏率（含 10 % 裕度），001 是外插值，**僅供參考** |
| 006 | `oes.calibrationStatus` | `Snapshot.CalibrationStatus`，列舉直接轉數值 | 0 未校正（只有定性判斷）／1 有效／2 基準不符（換了 Golden Run 但校正沒換，估計暫停） |
| 007 | `oes.compositeLevel` | `Snapshot.Overall`，列舉直接轉數值 | 0 Idle／1 Normal／2 Warning／3 Alarm。**3 是鎖存的**，要操作者在設備端 Acknowledge 才會解除（CEID 502） |
| 008 | `oes.enabledRatios` | 快照中狀態**不是 Disabled** 的比值數 | 見 [§7](#7-判讀注意事項)：NoPlasma、LowSignal、NoBaseline 的比值也算在內 |
| 009 | `oes.warningRatios` | 快照中狀態**等於 Warning** 的比值數 | 已升到 Alarm 的比值**不算**在 009，算在 010 |

## 4. 情境範例：六種機台狀態下的讀值

以下假設：出廠比值組四組，其中 `R_N2357Abs` 出廠停用，所以啟用的是 3 組；情境 B～E 已用標準漏孔做過校正，最大校正點為 1.0E-3 mbar·L/s。

| 情境 | 001 Q̂ | 002 σ | 003 conf | 004 | 005 | 006 | 007 | 008 | 009 |
|---|---|---|---|---|---|---|---|---|---|
| **A** 正常生產，未做洩漏率校正 | 0 | 0 | 0 | 0 | 0 | 0 | 1 | 3 | 0 |
| **B** 正常生產，校正有效 | 1.2E-5 | 3.0E-5 | 0.85 | 1 | 0 | 1 | 1 | 3 | 0 |
| **C** 輕微漏氣，兩組比值進入 Warning | 1.8E-4 | 3.0E-5 | 0.72 | 1 | 0 | 1 | 2 | 3 | 2 |
| **D** 嚴重漏氣，已確認並鎖存 | 2.4E-3 | 4.1E-4 | 0.64 | 1 | **1** | 1 | 3 | 3 | **0** |
| **E** 換了配方的 Golden Run，校正沒換 | 0 | 0 | 0 | 0 | 0 | **2** | 1 | 3 | 0 |
| **F** 電漿未開（機台待機中） | 0 | 0 | 0 | 0 | 0 | 1 | 0 | 3 | 0 |

逐一解讀：

- **A：** 006 = 0，所以 001～005 全無意義，只能看 007。007 = 1 是「定性判斷正常」，但說不出漏了多少。
- **B：** 004 = 1、006 = 1，估計有效。Q̂ = 1.2E-5 比它自己的 σ（3.0E-5）還小，應讀成「與零無法區分」，不是「漏了 1.2E-5」。
- **C：** Q̂ = 1.8E-4 ± 3.0E-5，約 6σ，有漏且量級可信；003 = 0.72 表示各比值大致一致。007 = 2、009 = 2：三組啟用的比值中有兩組持續高於警告門檻。這時 Host 也會收到 ALID `10227001`（Leak warning）的 S5F1。
- **D：** 007 = 3，已鎖存。注意 **009 = 0**——兩組比值都已升到 Alarm，改算在 VID 010（此例 010 = 2），Host 不能用 009 = 0 判斷「沒事」。005 = 1：2.4E-3 超過最大校正點 1.0E-3，數值是外插的，只能說「至少比校正範圍大」。從 Warning 升到 Alarm 時，設備會先送 ALID `10227001` 的 clear，再送 ALID `10227002`（Leak alarm）的 set——Host 的警報清單上同一時間只會掛著一個洩漏警報。漏點處理完、操作者在設備端按 Acknowledge 後，007 才會回落，並送出 CEID `10227502`。
- **E：** 006 = 2，估計暫停，004 = 0、001 = 0。這不是「漏率歸零」，是「目前沒有能用的換算」。007 仍照常判定（定性判斷不需要校正）。處理方式：在設備端選回相符的 Golden Run，或對新基準重新校正。
- **F：** 007 = 0（Idle）：電漿未開，無法判定。008 仍是 3——停用才不算，**沒電漿的比值仍算「啟用」**。004 = 0，因為沒有電漿就沒有估計；006 仍為 1，校正本身沒有失效。

## 5. S1F3 查詢範例

Host 一次查 VID 001～009，設備在情境 C：

```
S1F3 W                             # Selected Equipment Status Request
<L[9]
  <U4[1] 1022700001>               # Leak rate
  <U4[1] 1022700002>               # Leak rate sigma
  <U4[1] 1022700003>               # Leak rate confidence
  <U4[1] 1022700004>               # Leak rate valid
  <U4[1] 1022700005>               # Out of calibrated range
  <U4[1] 1022700006>               # Calibration status
  <U4[1] 1022700007>               # Composite leak level
  <U4[1] 1022700008>               # Enabled ratio count
  <U4[1] 1022700009>               # Warning ratio count
>
.
S1F4                               # 設備回覆，順序與查詢相同
<L[9]
  <F4[1] 0.00018>                  # Q̂ = 1.8E-4 mbar·L/s
  <F4[1] 3E-05>                    # σ = 3.0E-5 mbar·L/s
  <F4[1] 0.72>                     # 各比值一致度 0.72
  <U4[1] 1>                        # 估計有效
  <U4[1] 0>                        # 在校正範圍內
  <U4[1] 1>                        # 校正有效
  <U4[1] 2>                        # Warning
  <U4[1] 3>                        # 3 組啟用
  <U4[1] 2>                        # 2 組 Warning
>
.
```

## 6. S2F23 追蹤範例

連續趨勢請走 Trace，不要用 S1F3 高頻輪詢。下例每 10 秒取樣一次，每 6 筆（1 分鐘）回報一次，追蹤 001、004、007、009：

```
S2F23 W                            # Trace Initialize Send
<L[5]
  <U4[1] 1>                        # TRID
  <A[6] "000010">                  # DSPER：每 10 秒取樣
  <U4[1] 0>                        # TOTSMP：0 = 持續到 Host 停止
  <U4[1] 6>                        # REPGSZ：每 6 筆回報一次
  <L[4]
    <U4[1] 1022700001>             # Leak rate
    <U4[1] 1022700004>             # Leak rate valid
    <U4[1] 1022700007>             # Composite leak level
    <U4[1] 1022700009>             # Warning ratio count
  >
>
.
S2F24
<B[1] 0x00>                        # TIAACK：0 = 接受
.
```

之後設備以 S6F1 送出追蹤資料。追 001 時務必一起追 004，否則趨勢圖上的 0 分不出是「無漏」還是「無估計」。

## 7. 判讀注意事項

1. **001 必須搭配 004 讀。** 沒有估計時 001～003 送 0 而不是空值；004 = 0 時這些 0 不代表任何量測結果。
2. **001 只在 006 = 1 時才可能有效。** 006 = 2（基準不符）時估計暫停，應視為「無資料」而非「漏率 0」。
3. **005 = 1 的 001 是外插值**，可用來判斷「很大」，不可用來比較大小或做趨勢。
4. **007 = 3 是鎖存狀態**，漏氣停止後也不會自己回落，要操作者在設備端確認。本規格沒有讓 Host 遠端解除的命令。
5. **008 不是「正在判定的比值數」。** 它只排除操作者停用的比值；NoPlasma、LowSignal、NoBaseline，以及製程分類設定下的 NotApplicable、TrendOnly／Guard 角色的 Observing，全都算在 008 裡。要知道多少比值真的參與判定，需要搭配 010（Alarm）、011（LowSignal）一起看，或等比值槽位 VID 實作後讀各槽位的狀態。
6. **009 只算 Warning，不算 Alarm。** 008、009、010、011 是各自計數，不是互斥分類，相加也不等於 008。Host 判斷「有沒有比值異常」應看 `009 + 010`，或直接看 007。
7. **先看 VID 016。** 016 = 1 表示資料來自測試模式或錄檔回放，001～009 雖然照常更新，但不是實機量測，不可用於製程判定。
8. **VID 015 = 1 時，007 也要打折扣。** 採集參數與基準擷取時不同，所有比值的刻度都已改變，應視同警告並重新擷取 Golden Run。

## 附錄：列舉值對照

**Calibration status（VID 006）**

| 值 | 意義 | 001～005 |
|---|---|---|
| 0 | 未校正——未選用任何洩漏率校正，只有定性判斷 | 無意義 |
| 1 | 有效——校正與現用 Golden Run 相符 | 看 004 |
| 2 | 基準不符——校正是對另一組基準做的，估計暫停 | 無意義（004 = 0） |

**Composite leak level（VID 007）**

| 值 | 意義 | 伴隨的 SECS 訊息 |
|---|---|---|
| 0 | Idle——無法判定（電漿未開、尚無基準，或全部比值停用） | — |
| 1 | Normal——各啟用比值均在警告門檻以下 | 由 2 或 3 回落時，送出該警報的 clear |
| 2 | Warning——有比值持續高於警告門檻 | S5F1 ALID `1cc27001` set |
| 3 | Alarm——已確認洩漏，鎖存至操作者確認為止 | S5F1 ALID `1cc27001` clear + `1cc27002` set；確認時 S6F11 CEID `1cc27502` |

> 007 的值永遠如實回報；上表的 S5F1 則受 SECS 分頁的報告開關控制——**Report alarms (S5F1)** 未勾選時不送；測試模式／回放資料預設也不送，要勾選 **Report test / replay data too** 才送。
