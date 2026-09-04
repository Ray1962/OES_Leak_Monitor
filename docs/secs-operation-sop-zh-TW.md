# SECS 連線操作 SOP（OES Leak Monitor）

> **用途**：讓現場人員把 OES Leak Monitor 接上 fab 的 Host／MES，並在日常運轉中確認它「真的還在講話」。
>
> **這份文件寫給誰**
> - **Operator**：只看第 [1](#1-角色與權限)、[5](#5-日常確認operator每班一次)、[8](#8-故障排除) 節——你只需要會看狀態，不需要、也不應該改設定。
> - **Engineer／設備工程師**：全部。首次設定、對測、變更、驗收都在這裡。
>
> **相關文件**
> - 實作規格（欄位從哪來、為什麼這樣設計）：[`secs-integration.md`](secs-integration.md)
> - 通訊協定的上位規格（欄位語意以它為準）：[`Satellite_SECS_Specification_v2.md`](Satellite_SECS_Specification_v2.md)
> - App 本身的操作：[`user-manual-zh-TW.md`](user-manual-zh-TW.md)

---

## 目錄

- [1. 角色與權限](#1-角色與權限)
- [2. 開工前：要先跟 Host 對齊的五件事](#2-開工前要先跟-host-對齊的五件事)
- [3. 首次設定步驟](#3-首次設定步驟)
- [4. 確認連線成功](#4-確認連線成功)
- [5. 日常確認（Operator，每班一次）](#5-日常確認operator每班一次)
- [6. 上報開關怎麼用](#6-上報開關怎麼用)
- [7. 變更設定的正確做法](#7-變更設定的正確做法)
- [8. 故障排除](#8-故障排除)
- [9. 對測／驗收程序](#9-對測驗收程序)
- [10. 交給 Host 端的對照表](#10-交給-host-端的對照表)
- [11. 檢核表（可列印）](#11-檢核表可列印)
- [附錄 A：檔案位置](#附錄-a檔案位置)
- [附錄 B：settings.json 的 secs 區塊](#附錄-bsettingsjson-的-secs-區塊)

---

## 1. 角色與權限

**SECS 分頁人人都能進**，不需要登入。這是刻意的：「這台機器現在有沒有在跟 MES 講話」是 Operator 會被問到的問題，答案不該需要一組帳號。

| 你要做的事 | 需要的角色 |
|---|---|
| 看連線狀態、控制狀態、腔體號、profile 路徑、收送日誌 | 任何人（含 Guest） |
| 改任何一項設定、按 **Save & apply** 或 **Restart interface** | **Engineer 以上** |

未登入或角色不足時，設定區整塊是灰的——**這不是故障**。要改設定請先用右上角登入 Engineer 帳號。

> ⚠️ 設定區的每一個欄位都會改變 Host 看到的東西。Operator 在沒有工程師在場時，**只看不改**。

---

## 2. 開工前：要先跟 Host 對齊的五件事

這五項只要有一項不一致，結果都是「看起來連上了，可是讀不到東西」——比完全連不上更難查。**在動 App 之前先把這張表填好、雙方簽字。**

| # | 項目 | 我方（設備端）填 | 必須與 Host 一致 | 備註 |
|---|---|---|---|---|
| 1 | **腔體代號 `cc`** | | ✅ | 這台 OES 監看的是哪個腔體。**所有 SVID／ALID／CEID 都由它算出來**，錯了就整組編號都對不上 |
| 2 | **IP 位址／Port** | | ✅ | 設備端是 **passive**（監聽），由 Host 主動連進來。預設 port 5000 |
| 3 | **Device ID** | | ✅ | HSMS 交握**不會**檢查它，所以打錯不會在連線階段報錯，而是之後資料訊息被回 S9F1 |
| 4 | **MDLN／SOFTREV** | | 建議一致 | 回在 S1F2／S1F14，Host 端的機台清單會顯示 |
| 5 | **ALID 型別 = ASCII** | 固定 ASCII | ✅ **請 Host 確認** | 本設備依 Satellite 規格 §5.3 送 ASCII（`<A[8] "10227002">`）。若 Host 只吃 U4，上線前就要談，不要等到警報發不出去才發現 |

另外請 IT／廠務確認：

- 設備 PC 的防火牆對該 port **允許 inbound（由外部連入）TCP 連線**。
- 設備 PC 與 Host 之間的網路可通（`ping` 得到、port 不被中間設備擋掉）。
- **同一台 PC 若跑兩套監測不同腔體，port 必須分開。** 一個 App 執行個體只對應一個 `cc`。

---

## 3. 首次設定步驟

> 全程約 5 分鐘。做完之後 App 每次啟動都會自動帶起 SECS，不需要重做。

1. **登入 Engineer 以上帳號**（主畫面右上角）。
2. 切到 **SECS 分頁**。
3. **勾選 `Enable SECS`**（總開關。沒勾時完全不開 port，狀態顯示 `DISABLED`）。
4. **`Chamber`** 下拉選這台 OES 監看的腔體，例如 `02 — Ch_2`。
   選完後**立刻檢查狀態區最下面那行編號範例**，例如：
   ```
   Leak rate SVID 1022700001, leak alarm ALID 10227002, acquisition-started CEID 10227508
   ```
   **把這三個數字唸給 Host 端的人核對。** 這是最快、也最常抓到錯誤的一步。
5. **`Address`** 填 `0.0.0.0`（監聽所有網卡，一般用這個）。同機對測時填 `127.0.0.1`。
   **`Port`** 與 **`Device ID`** 依第 2 節的表填。
6. **`Connect out (active)` 不要勾。** 設備端正常是被動等待 Host 連進來；只有 Host 明確要求設備主動撥出時才勾。
7. **`Model (MDLN)` / `Revision`** 依表填（預設 `OESLM` / `1.0.0`）。
8. **`T3`–`T8`**（秒）除非 Host 指定，否則**保持預設** `45/10/10/10/10`。T3 是等回覆的時間。
9. **`Keep traffic logs (days)`** 保持 `30`。驗收時客戶通常會要這份紀錄，不要設成太小的值。
10. **上報開關**：首次上線建議先**只連線、不上報**——
    - `Report alarms (S5F1)`：**先取消**
    - `Report events (S6F11)`：**先取消**
    - `Report test / replay data too`：**保持取消**

    等到第 4 節確認連線正常、第 9 節對測通過之後，再回來把前兩項勾起來。這樣測試期間不會有假警報進到 fab 的 MES。
11. 按 **`Save & apply`**。

> **`Save & apply` 才會生效。** 欄位打字的當下什麼都不會改變——半個 port 號不是一個位址。畫面上出現橘色的 **`Unsaved changes`** 就表示你改了東西但還沒套用。

---

## 4. 確認連線成功

### 4.1 看狀態燈

分頁最上方的色塊，由淺到深就是連線的四個階段：

| 顯示 | 顏色 | 意思 | 該怎麼辦 |
|---|---|---|---|
| `DISABLED` | 灰 | 總開關沒開，沒有開 port | 要用就去勾 `Enable SECS` |
| `LISTENING` | 藍 | **設備端正常**，port 已開，正在等 Host 連進來 | 這是 Host 還沒連時的正常狀態 |
| `SELECTED` | 橘 | TCP 連上、HSMS 已 select，但還沒建立通訊 | 通常一兩秒就會跳成綠色；卡住看 8.3 |
| `COMMUNICATING` | 綠 | **完全正常**，S1F13/S1F14 已完成，可以收送訊息 | 這是上線後該長期停留的狀態 |
| `FAILED` | 紅 | 起不來 | 旁邊那行字就是原因，看 8.1 |

色塊右邊那行字補充細節，例如 `listening on 0.0.0.0:5000, device id 0`；`FAILED` 時顯示的是失敗原因。

下面一行 **`Control state`** 是 SEMI E30 的控制狀態（`OnlineRemote` 等）。本設備**不接受任何遠端命令**，所以這個狀態只影響 Host 送 S1F17 時的回應，不影響資料查詢。

### 4.2 看 Traffic（收送日誌）

分頁下半部逐行列出每一則進出的訊息，最新的在最下面。啟動時應該看到這三行（或類似）：

```
[CFG] profile template C:\Users\<user>\AppData\Roaming\OES_Leak_Monitor\profiles\oes-leak-monitor.json
[CFG] chamber 02 (Ch_2) -> ...\profiles\.effective\oes-leak-monitor.json
[EQP] listening on 0.0.0.0:5000
```

Host 連進來之後，會看到 S1F13/S1F14、以及 Host 每次查詢的 S1F3/S1F4。

**`[CFG] not saved — …`** 這種行表示你按了 Save 但設定不合法（腔體代號不在規格表內、port 超出範圍、T 值超出 1–3600 等），**設定沒有被套用**。把該行講的問題改掉再存一次。

### 4.3 三個要點

- **兩邊各送一次 S1F13 是正常的。** 設備與 Host 都會主動建立通訊，日誌裡看到兩次不是重複或錯誤。
- **`COMMUNICATING` 不代表有在上報。** 狀態區的 `ReportingSummary` 那行才是答案，例如
  `alarms OFF, events on, test/replay data is not reported. Status queries always answer.`
  **看到 `OFF` 就表示那類訊息不會主動送出去**，即使一切都連得好好的。
- **不論開關怎麼設，Host 用 S1F3 查狀態永遠查得到真值。** 開關只擋設備主動送出的訊息。

---

## 5. 日常確認（Operator，每班一次）

**約 20 秒。不需要登入，也不要改任何設定。**

1. 切到 **SECS 分頁**。
2. 確認狀態燈是 **綠色 `COMMUNICATING`**。
3. 確認 `ReportingSummary` 那行是 **`alarms on, events on`**（正式生產時應為此）。
4. 確認 Traffic 最下面幾行的時間戳是**最近的**（Host 通常會定期查詢；長時間完全沒有新行值得問一下）。

**任何一項不符 → 記錄下來並通知設備工程師，不要自己改設定。** 特別是狀態燈變成藍色 `LISTENING`：那代表 Host 掉線了，App 本身沒問題，但這段期間的洩漏警報**送不出去也不會補送**（本版不做 spooling），只會留在本機的日誌與稽核紀錄裡。

---

## 6. 上報開關怎麼用

四個勾選框，語意各自獨立：

| 開關 | 取消勾選時的效果 | 什麼時候取消 |
|---|---|---|
| **`Enable SECS`** | 完全不啟動、不開 port，狀態 `DISABLED` | 這台機器不接 MES 時 |
| **`Report alarms (S5F1)`** | 不主動送洩漏警報與設備故障警報。狀態照樣進本機 UI、稽核日誌，Host 查 S1F3 也照樣讀得到 | **調機、對測、保養期間**，避免測試動作在 fab 的 MES 留下假警報 |
| **`Report events (S6F11)`** | 不送「開始擷取／停止擷取／警報已確認」三個事件 | 同上 |
| **`Report test / replay data too`** | **測試模式與 Replay 期間**不送任何警報與事件（預設就是取消） | **保持取消**，除非你正在做下一段那件事 |

### 6.1 測試模式閘門為什麼預設關

沒有接光譜儀時 App 會落到 **test mode**（合成頻譜），Replay 分頁還會播放錄檔。這兩種都不是量測值——CSV 檔名會被標成 `SIM` 前綴，但**送到 Host 的警報上沒有任何記號說「這是假資料」**。所以預設不送。

不論這個開關怎麼設，**VID 016（Test / replay mode）永遠回報真相**，Host 端可以自己判斷這批數據不能用於製程判定。

### 6.2 什麼時候該把它勾起來

只有一種情況：**要用一段真實錄檔驗證整條警報路徑**。做法見第 9.3 節。驗證完**當場取消勾選**，不要留著過夜。

### 6.3 設備故障警報不受這個閘門影響

ALID 012（連線中斷）／013（擷取錯誤）／014（寫檔失敗）**在測試模式下照樣會送**。光譜儀掉線、CSV 寫不進去，是關於這台機器的事實，跟畫面上跑的是不是合成頻譜無關。

---

## 7. 變更設定的正確做法

| 你改了什麼 | 該按哪個鍵 | 說明 |
|---|---|---|
| 分頁上的任何欄位（腔體、位址、開關、T 值…） | **`Save & apply`** | 寫進 `settings.json`，然後整個介面重建 |
| 用文字編輯器手改了 profile JSON | **`Restart interface`** | 重新讀 profile 檔、重新戳腔體號、重新啟動 |

兩顆按鍵都會**先存檔再重建**——正在跑的一定等於磁碟上的那份，所以下次重開 App 不會悄悄變成別的行為。

### 7.1 改腔體代號 `cc`

**這會改變全部的 SVID／ALID／CEID。** 改之前務必通知 Host 端，兩邊同時改。改完按 `Save & apply`，然後照第 4 節重新確認一次，並把新的編號範例唸給 Host 核對。

### 7.2 改 profile（SVID 名稱、單位、警報文字）

**要改的是「Profile (edit this)」那一份**，不是下面「In use (generated)」那份——後者每次啟動都會被重寫，改它沒有任何效果。

```
Profile (edit this):   ...\profiles\oes-leak-monitor.json          ← 改這個
In use (generated):    ...\profiles\.effective\oes-leak-monitor.json  ← 不要改
```

範本檔裡的編號一律以 **`cc=00`** 書寫，App 啟動時才把第 2–3 位換成你設定的腔體號。**手改時不要把 `00` 換成真正的腔體號**，那會被當成錯誤而拒絕啟動。

改完按 **`Restart interface`**。如果 JSON 打錯（位數不對、`ss` 不是 27、`aa` 不是 00），介面會直接變 `FAILED` 並在旁邊說明原因——這是刻意的，寧可起不來，也不要送出屬於別種感測器的編號。

> 檔案不小心刪掉沒關係：下次啟動會自動寫回一份預設範本（Traffic 會出現 `[CFG] wrote the default profile to …`）。

> ### ⚠️ 升級之後：新版新增的 SVID 不會自己出現在你的 profile 裡
>
> 既有的 profile **永遠不會被覆寫**（它可能帶著你改過的編號與文字），所以新版新增的狀態變數
> 不會自動加進去，Host 查那幾個 VID 會什麼都讀不到。
>
> 每次啟動時 App 會自己比對並在 Traffic 裡說出來：
>
> ```
> [CFG] 3 status variable(s) this build can serve are not in the profile: oes.processClass, ...
> ```
>
> 系統記錄裡對應的是 `SecsProfileMissingBinds`。**升級後請看一次這行**。要補的話兩種做法：
> 照 [`secs-integration.md`](secs-integration.md) §4 把缺的幾列貼進 profile，
> 或把 profile 檔刪掉讓它重新產生（**你改過的內容會一起不見**，所以先看清楚 log 列出的名字）。

---

## 8. 故障排除

### 8.1 狀態是紅色 `FAILED`

色塊右邊那行字就是原因。常見三種：

| 訊息大意 | 原因 | 處置 |
|---|---|---|
| `chamber code 00 is not one the specification defines` | 沒選腔體（或選了規格沒定義的代號） | 選一個腔體，`Save & apply` |
| port 被佔用／位址無法繫結 | 同一台 PC 已有另一個程式（或另一套本 App）佔住該 port | 換 port，或關掉另一個執行個體。**兩套監測不同腔體時 port 必須分開** |
| profile 相關的錯誤 | 手改 JSON 打錯位數／`ss`／`aa` | 照 7.2 修正，或刪掉範本檔讓它重新產生，再 `Restart interface` |

### 8.2 一直停在藍色 `LISTENING`，Host 連不進來

設備端沒問題，問題在網路或 Host。依序檢查：

1. Host 端設定的 IP／port 是否就是本機這組？（`Address` 若是 `0.0.0.0`，Host 要連的是這台 PC 的實際 IP）
2. 設備 PC 的**防火牆**是否放行該 port 的 inbound TCP？
3. Host 是否被設成 passive？**兩邊都在等對方連，就永遠連不起來。** 設備端 `Connect out (active)` 應為取消勾選、Host 端為 active。
4. 網路是否通（ping、tracert）。

### 8.3 卡在橘色 `SELECTED` 不變綠

TCP 通了，但通訊建立（S1F13/S1F14）沒完成。多半是 **Device ID 不一致**或 Host 端尚未送出建立通訊的請求。核對第 2 節表格的第 3 項，並看 Traffic 有沒有出現 S1F13。

### 8.4 綠燈，但 Host 讀不到值／收到 S9F1

**九成是 Device ID 或腔體代號不一致。** HSMS 交握不檢查 Device ID，所以不一致要到資料訊息階段才會被拒。

1. 對 Device ID。
2. 把狀態區的編號範例（`Leak rate SVID …`）唸給 Host 端核對——Host 若照別的腔體號來查，查到的 SVID 不存在，自然什麼都讀不到。

### 8.5 綠燈，但沒收到任何警報

依序確認：

1. `ReportingSummary` 是不是寫著 `alarms OFF`？→ 勾回 `Report alarms`，`Save & apply`。
2. 現在是不是 **test mode／Replay**？（Leak Monitor 分頁會標示，SECS 分頁的 `ReportingSummary` 也會說 `test/replay data is not reported`）→ 這是預設行為，不是故障。
3. App 這邊到底有沒有發生警報？→ 看 Leak Monitor 分頁與稽核日誌。SECS 只是轉送，不會自己產生警報。
4. Host 端是不是用 ASCII 解析 ALID？→ 見第 2 節第 5 項。

### 8.6 Host 端說某個警報「沒有結束」

洩漏警報（ALID 002）是**鎖存**的：必須由操作者在 Leak Monitor 分頁按 **Acknowledge** 才會解除，並同時送出 CEID 502 事件。沒人按，它就會一直是 set 狀態——這是設計如此。

### 8.7 斷線期間發生的警報會補送嗎

**不會。** 本版不做 spooling，斷線期間送出的訊息會失敗，只記在 SECS 日誌檔裡（`[EQP] S5F1 ALID=… failed: …`）。本機的稽核紀錄與比值 CSV 仍然完整。客戶若要求「MES 重開後資料不能掉」，需要由 Host 送 S2F43 開啟 spooling，並另行評估。

### 8.8 找不到日誌檔

分頁中段「Traffic log:」那行就是完整路徑（`…\Logs\secs_YYYYMMDD.log`，每日一檔）。畫面上的清單只留最近 500 行，**要查久一點的事情看檔案**。按 `Clear` 只清畫面，不動檔案。

---

## 9. 對測／驗收程序

不必等 fab 的 MES，用 `Test_SECS.exe` 當對測 Host 就能走完整套（該工具的 `使用手冊.md` 有雙視窗流程）。

### 9.1 基本連線（必做）

1. App 設 `Address = 127.0.0.1`、`Port = 5000`、`Device ID = 0`、腔體 = 實際要用的代號，`Save & apply`。
2. 啟動 `Test_SECS` 的 Host 端，連到 `127.0.0.1:5000`。
3. 確認兩邊都到 **`COMMUNICATING`**。
4. Host 送 **S1F3**（查狀態）→ 應回 **S1F4，29 個 SV**，SVID 為 `1{cc}2700001`…`1{cc}2700029`。抽驗 VID 024/025 是否等於裝置目前的積分時間與平均次數。
5. Host 送 **S1F17** → 回 `ONLACK`（已在 online 時為 2）。
6. Host 送 **S5F5**（查警報清單）→ 應回 **5 條**，ALID 為 **ASCII**（`<A[8] "1{cc}27002">`），category 依序 6/4/5/5/8。
7. Host 做一次 **S2F23 Trace** → 確認 S6F1 依週期回傳。

### 9.2 設備故障警報（必做）

拔掉光譜儀 USB → Host 應收到 ALID `1{cc}27012` **set**；插回並重新連線 → 收到 **clear**。

> 注意 012 是**轉態**而非狀態：App 啟動時本來就沒連線，這不會送警報。要先連上、再拔掉才會觸發。

### 9.3 洩漏警報全路徑（用錄檔驗證）

這是唯一需要把測試模式閘門打開的場合：

1. SECS 分頁勾 **`Report test / replay data too`**，確認 `Report alarms` 與 `Report events` 也勾著，`Save & apply`。
2. Replay 分頁勾 **`Raise leak alarms during replay`**，選一段**含洩漏事件**的錄檔。
3. Connect（test mode）→ Start → Play。
4. Host 端應依序收到：
   - CEID `1{cc}27508`（開始擷取）
   - ALID `1{cc}27001` **set**（Warning）
   - ALID `1{cc}27001` **clear** + ALID `1{cc}27002` **set**（升到 Alarm）
5. 在 Leak Monitor 分頁按 **Acknowledge** → Host 收到 ALID `1{cc}27002` **clear** 與 CEID `1{cc}27502`。
6. 停止擷取 → CEID `1{cc}27509`。
7. **反向驗證**：取消 `Report test / replay data too`，重播同一段 → **不應再收到任何 S5F1／S6F11**，但 Host 送 S1F3 仍讀得到值，且 **VID 016 = 1**。
8. **測完把 `Report test / replay data too` 取消勾選並 `Save & apply`。**

### 9.4 收尾

- 比對 `secs_YYYYMMDD.log` 與 Host 端的收送紀錄，逐則對上。
- 把上報開關切回正式生產該有的狀態（alarms on、events on、test/replay off）。
- 存一份日誌檔作為驗收附件。

---

## 10. 交給 Host 端的對照表

`cc` 請代入實際腔體代號（例：`cc=02` → SVID `1022700001`、ALID `10227002`）。

### 10.1 編碼規則

```
SVID = 1 + cc + 27 + 00 + vvv    （10 碼）
ALID = 1 + cc + 27 + nnn         （ 8 碼，ASCII 傳送）
CEID = 1 + cc + 27 + nnn         （ 8 碼）
```

### 10.2 警報（S5F1）

| ALID `1{cc}27nnn` | 名稱 | category | 何時 set | 何時 clear |
|---|---|---|---|---|
| 001 | OES LEAK WARNING | 6 | 綜合狀態升到 Warning | 降回 Normal／Idle |
| 002 | OES LEAK ALARM | 4 | 綜合狀態升到 Alarm（**鎖存**） | 操作者 Acknowledge 之後 |
| 012 | OES CONNECTION LOST | 5 | 光譜儀連線由通變斷 | 重新連上 |
| 013 | OES ACQUISITION ERROR | 5 | 擷取進入錯誤狀態 | 離開錯誤狀態 |
| 014 | OES DATA WRITE FAILURE | 8 | CSV 寫檔失敗 | 下次寫檔成功 |

012–014 **不受測試模式閘門影響**。

### 10.3 事件（S6F11）

| CEID `1{cc}27nnn` | 事件 |
|---|---|
| 502 | 操作者確認（Acknowledge）了洩漏警報 |
| 508 | 開始擷取 |
| 509 | 停止擷取 |

### 10.4 狀態變數重點（完整 29 項見 [`secs-integration.md`](secs-integration.md) §5）

| VID | 意義 | 備註 |
|---|---|---|
| 001–005 | 洩漏率、σ、信心度、是否有效、是否超出校正範圍 | 未做洩漏率校正時 004 = 0 |
| 006 | 校正狀態 | 0=未校正 / 1=生效 / 2=與基準不符 |
| **007** | **綜合洩漏等級** | **0=Idle / 1=Normal / 2=Warning / 3=Alarm。Host 端最該訂閱的一項** |
| 008–011 | 啟用／警告／警報／低訊號的比值數量 | |
| 012–014 | 是否有基準、目前 Golden Run 名稱、目前校正名稱 | |
| 015 | 擷取參數與基準不符 | 1 = 目前的曝光設定與擷取基準時不同，讀值不可信 |
| **016** | **測試／Replay 模式** | **1 = 這批數據不是量測值，不可用於製程判定** |
| 021–023 | 電漿是否存在、閘門是否可用、累計掉幀數 | 023 為**本次執行**累計，請以差值判讀 |
| 024–026 | 積分時間、平均次數、實際幀率 | |
| **027–029** | **製程類別、類別狀態、步驟序號** | **028 才是能讀的那一個**：027 在「沒設分類器」「沒有電漿步驟」「還沒判定」三種情況下都是空字串。028 = 0/1/2/3/4 分別對應這三種、已分類、判定為無法分類 |

---

## 11. 檢核表（可列印）

### 11.1 上線前（Engineer）

- [ ] 第 2 節的五項參數已與 Host 端逐項對過並簽字
- [ ] 防火牆已放行該 port 的 inbound TCP
- [ ] 腔體代號正確，狀態區的編號範例已唸給 Host 核對
- [ ] `Connect out (active)` 未勾選（設備端為 passive）
- [ ] 第 9.1 節的基本連線七步全部通過
- [ ] 第 9.2 節的設備故障警報已實測
- [ ] 第 9.3 節的洩漏警報全路徑已用錄檔驗證，**且測試模式閘門已關回**
- [ ] 上報開關為正式狀態：alarms **on**、events **on**、test/replay **off**
- [ ] 日誌保留天數 ≥ 30，驗收日誌已存檔

### 11.2 每班（Operator）

- [ ] SECS 分頁狀態燈為綠色 `COMMUNICATING`
- [ ] `ReportingSummary` 顯示 `alarms on, events on`
- [ ] Traffic 最新幾行的時間戳是最近的
- [ ] 有異常 → 記錄並通知工程師，**不自行更改設定**

### 11.3 交接／異常回報時要附上

- [ ] SECS 分頁的截圖（狀態燈 + `ReportingSummary` + 編號範例那三行）
- [ ] 當天的 `secs_YYYYMMDD.log`
- [ ] 當天的系統稽核 CSV（`Logs\` 底下）

---

## 附錄 A：檔案位置

`{Config}` = `C:\Users\<登入帳號>\AppData\Roaming\OES_Leak_Monitor`

| 檔案 | 路徑 | 說明 |
|---|---|---|
| 設定檔 | `{Config}\settings.json` | 含 `secs` 區塊（附錄 B） |
| **Profile 範本（要改的是這份）** | `{Config}\profiles\oes-leak-monitor.json` | 編號一律以 `cc=00` 書寫 |
| 有效 profile（產生物，不要改） | `{Config}\profiles\.effective\oes-leak-monitor.json` | 每次啟動依腔體號重寫 |
| SECS 收送日誌 | `{Config}\Logs\secs_YYYYMMDD.log` | 每日一檔，保留 `logRetentionDays` 天 |
| 系統稽核日誌 | `{Config}\Logs\{YYMMDDHH}.csv` | `SecsStarted`／`SecsStopped`／`SecsSettingsSaved` 等記在這裡 |

分頁中段會直接顯示前三個路徑的實際值——**現場找檔案看那裡就好，不用背。**

---

## 附錄 B：settings.json 的 `secs` 區塊

一般情況請用 SECS 分頁修改；直接改檔案只在遠端支援、或要批次佈署多台時才做（改完須重開 App）。

```jsonc
"secs": {
  "enabled": false,              // 總開關
  "reportAlarms": true,
  "reportEvents": true,
  "reportInTestMode": false,     // 測試模式閘門，預設關
  "chamberCode": 2,              // cc：01-15 / 21-25 / 31-34
  "isActive": false,             // 設備端 passive，等 Host 連進來
  "ipAddress": "0.0.0.0",        // 監聽所有網卡；本機對測用 127.0.0.1
  "port": 5000,
  "deviceId": 0,                 // 必須與 Host 一致
  "modelName": "OESLM",          // MDLN
  "softwareRevision": "1.0.0",   // SOFTREV
  "t3": 45, "t5": 10, "t6": 10, "t7": 10, "t8": 10,   // 逾時（秒）
  "profileFileName": "oes-leak-monitor.json",
  "logRetentionDays": 30
}
```

舊版 `settings.json` 沒有這個區塊時取上述預設值，**亦即升級之後 SECS 預設不啟動、不開任何 port**，要用才開。

### 合法的腔體代號

| 代號 | 名稱 | 代號 | 名稱 |
|---|---|---|---|
| 01–05 | Ch_1 … Ch_5 | 11 | Buffer / Buffer 1 |
| 06 | Ch_A / VIA_1 | 12–13 | Ch_C, Ch_D |
| 07–08 | Ch_E, Ch_F | 14–15 | LLA, LLB |
| 09 | Ch_B | 21–25 | X'fer Viewport 1–5 |
| 10 | X'fer / Buffer 2 | 31–34 | Buffer Viewport 1–4 |

代號不在此表內時，介面會拒絕啟動並顯示原因。
