# OES Leak Monitor 操作手冊

> 版本對應：`Aqst.OesSpectrometer 0.4.6` / `Aqst.OesApp.Core 0.1.3` / `Aqst.OesApp.Wpf 0.1.7`
> 適用對象：現場操作員（Operator）、製程／設備工程師（Engineer）、系統管理者（Admin）

---

## 目錄

1. [這個軟體在做什麼](#1-這個軟體在做什麼)
2. [安裝與啟動](#2-安裝與啟動)
3. [帳號與權限](#3-帳號與權限)
4. [畫面總覽](#4-畫面總覽)
5. [快速上手：從零到監控](#5-快速上手從零到監控)
6. [分頁詳解](#6-分頁詳解)
7. [檔案與資料夾](#7-檔案與資料夾)
8. [日常操作 SOP](#8-日常操作-sop)
9. [疑難排解](#9-疑難排解)
10. [附錄：參數預設值](#10-附錄參數預設值)

---

## 1. 這個軟體在做什麼

OES Leak Monitor 用**光學發射光譜（OES）**即時偵測製程腔體的**空氣／氧氣洩漏**。

核心方法是 **actinometry（比值法）**：

- 空氣漏進電漿後，會生成 O、OH、NO 等自由基，它們的特徵譜線強度上升。
- 但譜線的絕對強度也會隨電漿功率、壓力、氣流、窗口髒污而漂移 —— 直接看絕對強度會誤判。
- 所以把「訊號譜線」除以一條**參考譜線**（預設 N₂ 337.1 nm）。電漿條件漂移會同時放大／縮小兩者，相除後被抵消，剩下的變化就主要來自洩漏。

軟體會：

1. 在無洩漏的良品製程中錄下**基準線（Golden Run）** —— 每個比值的平均值與標準差 σ。
2. 即時計算比值，做 EMA 平滑，與基準線比較。
3. 超過警告／警報門檻並**持續一段時間**後才升級狀態（避免瞬間雜訊誤報）。
4. 若做過**洩漏率校正（Leak Calibration）**，還會把比值上升量換算成**定量洩漏率 Q̂（mbar·L/s）**。

同時它也是一台資料記錄器：當指定波長的強度超過門檻時，自動開檔記錄光譜 CSV 與比值 CSV，事後可在 Recordings / Ratio Review 分頁回放。

---

## 2. 安裝與啟動

### 2.1 標準安裝（免安裝 .NET）

發布產物是一個資料夾，內含單一 `.exe` 與數個原生 DLL：

```
OES_Leak_Monitor.exe          ← 主程式（已內含 .NET 8 執行環境）
UserApplication.dll           ← OES 硬體 SDK
SiUSBXp.dll                   ← USB 驅動介面
libsodium.dll                 ← SDK 相依
wpfgfx_cor3.dll               ← 以下為 WPF 原生元件
PresentationNative_cor3.dll
D3DCompiler_47_cor3.dll
PenImc_cor3.dll
vcruntime140_cor3.dll
```

> ⚠️ **整個資料夾要一起複製，不能只拷貝 `.exe`。**
> 那幾個 DLL 是刻意放在 `.exe` 旁邊的 —— SDK 的 `DllResolver` 只會在程式所在目錄找它們。少了 `UserApplication.dll` / `SiUSBXp.dll`，程式仍會啟動，但**連線會無聲地掉進測試模式**（畫面看得到假光譜，卻連不到硬體）。

需求：64 位元 Windows。目標電腦**不需要**安裝 .NET。

### 2.2 啟動

雙擊 `OES_Leak_Monitor.exe`。首次啟動會在 `%APPDATA%\OES_Leak_Monitor\` 建立設定與資料夾。

### 2.3 測試模式（無硬體）

沒接到光譜儀時，按 **Connect** 會自動退回**測試模式**，產生合成光譜（正弦波＋雜訊，約 200–800 nm、1000 點）。整個 UI 都能操作，適合教育訓練與功能驗證。

在測試模式下：

- Leak Monitor 分頁會顯示 **"test mode"** 註記。
- **警報預設被抑制**（`SuppressAlarmsInTestMode`），比值照算照畫，但不會真的跳 ALARM。
- 可以在 Configuration 分頁用 **Choose CSV…** 選一個已錄製的全光譜 CSV 來取代合成波形（見 §6.2.4）。

---

## 3. 帳號與權限

### 3.1 四種角色

| 角色 | 等級 | 可以做什麼 |
|---|---|---|
| **Guest** | 0 | 只能看。**不能關閉程式**（必須先登入） |
| **Operator** | 1 | 連線／啟動擷取、Start/Stop Save、Reset Run、**Acknowledge Alarm** |
| **Engineer** | 2 | 以上全部，加：Configuration 分頁、Capture Golden Run、Ratio Setup 存檔、Wavelength Calibration 存檔、Leak Calibration 存檔 |
| **Admin** | 3 | 以上全部，加：**Users** 使用者管理（新增／刪除帳號、改密碼、改角色） |

### 3.2 登入

右上角工具列：

- **Sign In** — 未登入（Guest）時顯示。
- **Sign Out** — 已登入時顯示。
- **Users** — 只有 Admin 看得到。

**出廠預設帳號：`admin` / 密碼 `admin`。**

> 🔒 **請在第一次部署後立刻改掉這組密碼**（Sign In 成 admin → Users → 改密碼）。

### 3.3 自動鎖定

閒置超過 **5 分鐘**（`AutoLockTimeoutMinutes`）會自動登出回 Guest。滑鼠移動或按鍵都會重置計時。

若在 Configuration 分頁時角色掉到 Engineer 以下，畫面會自動跳回 Monitor 分頁。

---

## 4. 畫面總覽

視窗上方是工具列（標題、狀態訊息、使用者／角色），下方是九個分頁：

| # | 分頁 | 用途 | 權限 |
|---|---|---|---|
| 1 | **Monitor** | 即時全光譜圖 + 選定波長強度趨勢；裝置連線／擷取控制 | Operator+ 操作 |
| 2 | **Leak Monitor** | 洩漏偵測主畫面：狀態燈號、各比值、趨勢圖、Golden Run | 檢視全開放 |
| 3 | **Ratio Setup** | 設定要監控哪些譜線比值、門檻 | Engineer+ 存檔 |
| 4 | **Wavelength Calibration** | 波長漂移補償（校正整個 catalog 的譜線位置） | Engineer+ 存檔 |
| 5 | **Leak Calibration** | 用已知漏率的標準漏孔，把比值上升換算成 mbar·L/s | Engineer+ 存檔 |
| 6 | **Configuration** | 光譜儀硬體參數 + 資料記錄器設定 | Engineer+ |
| 7 | **Recordings** | 回放已存的光譜 CSV（線圖／熱圖／單幀光譜） | 全開放 |
| 8 | **Ratio Review** | 回放已存的比值 CSV（% 基準／原始比值／洩漏率） | 全開放 |
| 9 | **Logs** | 系統稽核日誌 | 全開放 |

> Configuration 分頁在切入時會檢查角色；權限不足會跳出登入視窗，取消就退回原本的分頁。

---

## 5. 快速上手：從零到監控

第一次在一台新機台上導入，依序做完這六步：

### 步驟 1 — 連線並確認有光譜

1. 到 **Monitor** 分頁。
2. 按 **Connect**。下方狀態列的 **Test Mode** 應顯示 `False`，**Serial** 應顯示實際序號。
   - 若顯示 `True` 但硬體確實接著 → 見 §9.1。
3. 按 **Start**。全光譜圖應開始更新，**Last** 時間持續跳動。

### 步驟 2 — 調整硬體參數

1. 到 **Configuration** 分頁（需 Engineer）。
2. 設定 **Integration Time**（積分時間）：讓最強的譜線約落在滿刻度的 50–80%，不要飽和。
3. 需要降雜訊時調 **Average Count**；若發現峰位被硬體平均扭曲，把 **Average Mode** 改成 `Software`（見 §6.2.1）。
4. 按 **Apply** 推送到硬體，再按 **Save** 寫入 `settings.json`。

### 步驟 3 — 設定要監控的比值

1. 到 **Ratio Setup** 分頁。
2. 出廠預設已有四組比值：`O 777 / N₂ 337`、`OH 309 / N₂ 337`、`NO 237 / N₂ 337`、`Ar 750 / N₂ 337`。
3. 檢查你的光譜儀波段是否涵蓋這些波長 —— 涵蓋不到的請**取消勾選**或改選別的譜線。
4. 若某條訊號線太弱（接近雜訊底），把該比值的 **Monitoring method** 改成 `AbsoluteIntensity`（見 §6.3.2）。
5. 按 **Save**。
6. **⚠️ 回 Monitor 分頁按 Stop → 再按 Start**。Ratio Setup 的修改是「暫存」的，只有在擷取重啟後才生效。

### 步驟 4 — 錄基準線（Golden Run）

1. 讓機台跑一段**確定沒有洩漏**的良品製程，電漿條件穩定。
2. 到 **Leak Monitor** 分頁 → 按 **Capture Golden Run**。
3. 輸入配方／基準線名稱（例如 `Recipe-A-20260804`）。
4. 勾選「I confirm the process is currently running leak-free.」→ 按 **Start Capture**。
5. 預設平均 **60 秒**。進度條跑完後，基準線立刻寫入 `settings.json`。
6. 上方 **Baseline** 下拉會自動選到這條新基準線。

此時洩漏偵測就已經在運作了 —— 狀態列會顯示 `OK — within baseline`。

### 步驟 5（選用）— 洩漏率定量校正

只有需要把「比值上升」讀成 **mbar·L/s** 時才要做。見 §6.5。

### 步驟 6 — 開啟資料記錄

1. **Configuration** → 下半部 **Data Logger**。
2. 設定 **Trigger Wavelength**（判斷「電漿開了」的波長，例如 337.1）與 **Save Threshold Intensity**。
3. 按 **Apply** → **Save**。
4. 按 **Start Save** 武裝觸發器。當強度持續超過門檻達 **Start Confirm Time** 後，自動開檔。

---

## 6. 分頁詳解

### 6.1 Monitor（即時監看）

#### 6.1.1 頂部：Reset Run

**Reset Run**（Operator+）用在「調完參數，想從乾淨的狀態重新開始這一爐」：

- 關閉目前的 Intensity CSV 與 Ratio CSV（下一次超過門檻時會開新檔）。
- 清空 Monitor 強度趨勢圖與 Leak Monitor 的 % 趨勢圖。
- 重置各比值的即時平滑。

**保留不動**：Golden Run 基準線、洩漏率校正、已鎖存的警報（必須由人按 Acknowledge 才清除）、擷取本身持續進行。

> 若你剛改過**積分時間**或其他會影響絕對強度的設定，Reset Run 不夠 —— 請**重新錄一次 Golden Run**，因為舊基準線是在不同曝光條件下錄的。

#### 6.1.2 裝置控制列（DevicePanel）

| 按鈕 | 作用 |
|---|---|
| **Connect** | 連線光譜儀。找不到硬體時退回測試模式 |
| **Disconnect** | 中斷連線 |
| **Start** | 開始連續擷取 |
| **Stop** | 停止擷取（Ratio Setup / Wavelength Calibration 的暫存修改就是靠 Stop→Start 生效） |
| **Save CSV** | 把目前這張光譜存成 CSV，**同時凍結一條疊圖**在圖上當參考 |
| **Clear Spectrum** | 清掉所有由 Save CSV 疊上去的參考光譜 |

下方狀態列：**Serial**（序號）、**Frame**（每幀點數）、**Last**（最後一幀時間）、**Test Mode**（True/False）。

圖表操作：滾輪縮放、右鍵選單有 Zoom In / Zoom Out / **Zoom All**（快捷鍵 `A`）。

#### 6.1.3 強度趨勢圖（Wavelength Trend）

畫面下半部，顯示**時間 vs 強度**，最多 6 條線：

- **Trigger 波長**（Configuration 裡設的那個「Selected wavelength」）。
- **Monitored Wavelengths** 清單的前 **5** 個波長。

每條線取的是該波長 **±0.5 nm 範圍內的峰值** —— 這是「峰值定位」，用來吸收波長漂移，**不是**雜訊濾波。預設畫的是每幀原始計數，刻意不濾波，讓它像示波器一樣誠實。

| 控制項 | 說明 |
|---|---|
| **Smooth** | 套用 τ ≈ 3 s 的 EMA 平滑。切換時**連已經畫在螢幕上的歷史也會一起重算** |
| **Normalize** | 以「勾選當下」的值為 1.0 做正規化，看各線的相對發散。正規化期間門檻參考線會隱藏 |
| **Zoom All** | 縮放回全部資料。放大狀態下時間軸仍會持續跟隨最新資料 |

虛線是 Save 門檻強度的參考線。資料保留 **30 分鐘**。

---

### 6.2 Configuration（設定）

**需 Engineer 權限。** 分上下兩塊，中間可拖曳分隔。底部是共用的動作列。

底部三顆按鈕的差別**很重要**：

| 按鈕 | 作用 |
|---|---|
| **Apply** | 把目前參數**推送到硬體**與記錄器狀態機（立即生效，但**沒有存檔**） |
| **Save** | 把參數**寫入 `settings.json`**（下次開機沿用） |
| **Load Defaults** | 回到出廠值（**不會**自動 Apply，也**不會**自動 Save） |

> 改完記得 **Apply → Save** 兩個都按。只按 Apply 的話，下次開機會回到舊值。

#### 6.2.1 Hardware Parameters（硬體參數）

| 參數 | 預設 | 說明 |
|---|---|---|
| **Integration Time (ms)** | 50 | 積分時間，0.001–10000 ms。**接受小數，離開欄位才送出** |
| **Acquire Mode** | `HardwareAverage` | 底層擷取方式，見下表 |
| **Average Count** | 1 | 平均次數，1–1000 |
| **Average Mode** | `Hardware` | 平均在硬體或軟體做，見下表 |
| **Boxcar Width** | 1 | Boxcar 平滑視窗（1 = 關閉） |
| **Polling Interval (ms)** | 200 | 讀取間隔，10–10000 ms |
| **Max Consecutive Errors** | 5 | 連續錯誤幾次後自動斷線 |
| **Force Test Mode** | 勾選 | 強制用模擬器，不碰硬體 |

**Acquire Mode（擷取模式）**

| 值 | 何時用 |
|---|---|
| `HardwareAverage` | **預設。** 傳統 USB 機種的標準做法 |
| `Oneshot` | 當畫面出現**斷裂／撕裂的光譜幀**時改用這個 —— 常見於 Z5／乙太網路機種在長積分時間下 |
| `Standard` | 一般擷取，前兩者都不合用時再試 |

**Average Mode（平均模式）**

| 值 | 取捨 |
|---|---|
| `Hardware` | **預設。** 由韌體內部平均，速度快 |
| `Software` | 抓 N 張單幀在軟體逐點平均。**避免某些機種硬體平均造成的峰位偏移／峰形變寬**，代價是耗時約 N 倍 |

> 這兩項按 **Apply** 即可熱套用，**不需要**重新連線。

#### 6.2.2 Corrections（修正）

| 選項 | 預設 | 說明 |
|---|---|---|
| **Background Remove** | 開 | 硬體暗電流扣除。**注意**：不是每台機器的 ROM 都有背景校正資料，沒有的機種開了會被 SDK 略過 |
| **Straylight Correction** | 關 | 雜散光補償 |
| **Linearity Correction** | 開 | 偵測器非線性修正 |

#### 6.2.3 Data Logger（資料記錄器）

門檻觸發式記錄器：**只有在指定波長的強度高於門檻時才開檔寫入**。

**Trigger Conditions（觸發條件）**

| 參數 | 說明 |
|---|---|
| **Trigger Wavelength (nm)** | 用來判斷門檻的波長（取最接近的譜線 bin）。接受小數 |
| **Wavelength Tolerance (nm)** | 請求波長與實際 bin 的最大容許差距 |
| **Save Threshold Intensity** | 高於這個強度才可能開始存檔 |
| **Start Confirm Time (s)** | 需**持續**高於門檻幾秒才真的開檔 |
| **Stop Confirm Time (s)** | 需**持續**低於門檻幾秒才收檔 |
| **Min Save Time (s)** | 單一 session 的最短長度，避免產生一堆極短的碎檔 |

**Output Files（輸出檔案）**

| 參數 | 說明 |
|---|---|
| **Base Directory** | 留空 = `%APPDATA%\OES_Leak_Monitor\Data`（欄位下方的 *Effective:* 會顯示實際路徑） |
| **File Prefix** | 檔名前綴，例如 `P` |
| **Monitored Wavelengths** | 逗號分隔的波長清單，例如 `387, 482.5`。關閉全光譜時就是 CSV 的欄位；開啟時用於摘要檔 |
| **Include Full Spectrum** | 存下每一個波長欄位（另外產生一個摘要伴隨檔） |
| **Memory Budget (MB)** | 單檔列數上限依此估算，確保單一檔案可以整個載入這麼多 RAM |

**Start Save / Stop Save**

- **Start Save** — *武裝*觸發器。並不是立刻開檔，而是等強度條件成立。
- **Stop Save** — 解除武裝，並**立即**關閉所有開啟中的檔案。

**Logger Status** 區塊顯示狀態機目前狀態（`Idle` / `WaitingToStart` / `Saving` / `WaitingToStop`）與目前檔名。本專案是單一 OES，所以只會看到一列 **"OES #1 file"**。

#### 6.2.4 Test-mode sim（測試模式光譜播放）

底部動作列右側：

- **Choose CSV…** — 選一個已錄製的**全光譜 CSV**（就是記錄器寫出的那種寬格式），在測試模式下當成模擬電漿播放，播完自動循環。路徑會被記住，下次開機自動載入。
- **Clear** — 回到內建的合成光譜。

用途：不用接硬體就能重現真實製程資料，驗證比值設定、門檻、記錄流程是否正確。

> ⚠️ 播放受限於測試模式的波長軸（約 200–800 nm、1000 點）。**軸外的波長會被丟棄**（例如 Ar 811.5 nm），細節也會被內插掉。

---

### 6.3 Leak Monitor（洩漏監控主畫面）

這是日常監看時盯著的畫面。由上而下四層：

#### 6.3.1 狀態橫幅（最上方大色塊）

| 顯示文字 | 顏色 | 意義 |
|---|---|---|
| `Idle — waiting for plasma / baseline` | 灰 | 沒電漿、沒基準線，或監控被關閉 |
| `OK — within baseline` | 綠 | 在基準線範圍內 |
| `WARNING — oxygen ratio rising` | 橘 | 有比值持續超過警告門檻 |
| `ALARM — suspected O₂ / air leak` | 紅 | 達警報條件（**鎖存**，必須人工 Acknowledge） |

橫幅下方可能出現兩行小字：
- **測試模式註記** —— 提醒現在是模擬資料。
- **LOW SIGNAL 警示** —— 有啟用中的比值低於它的 SNR 下限（見 §6.3.4）。

**複合判定規則**：預設 `RequireTwoForAlarm = true`，也就是**要有兩個以上比值同時進入 Alarm**，整體狀態才會變 ALARM。這是為了避免單一譜線受干擾就跳警報。

#### 6.3.2 洩漏率讀值（橫幅正下方）

| 顯示 | 意義 |
|---|---|
| `Leak rate: not calibrated` | 還沒做洩漏率校正 |
| `Leak rate: — (waiting for plasma / baseline)` | 有校正，但目前沒有有效讀值 |
| `Leak rate ≈ 2.3E-4 ± 4E-5 mbar·L/s · 87% confidence` | 正常估計值 |
| `… · extrapolated` | 超出校正過的漏率範圍，數字僅供參考 |
| `Leak rate: calibration "X" needs its baseline — select that Golden Run…` | 目前的基準線不是這份校正當初綁定的那條，**估算已暫停**（避免給出錯誤數字） |

#### 6.3.3 工具列

| 控制項 | 權限 | 說明 |
|---|---|---|
| **Baseline** 下拉 | — | 切換使用中的 Golden Run。切換時會**自動配對**綁定該基準線的洩漏率校正 |
| **Capture Golden Run** | Engineer+ | 錄製新基準線（見 §6.3.5） |
| **Cancel** | — | 取消進行中的擷取（只在擷取中出現） |
| **Acknowledge Alarm** | Operator+ | 洩漏排除後，清除鎖存的警報 |
| **Zoom All** | — | 趨勢圖縮放回全部資料 |

#### 6.3.4 各比值列

每一列由左到右：

| 欄位 | 說明 |
|---|---|
| 圓點 | 狀態顏色 |
| **名稱** | 例如 `O 777 / N₂ 337` |
| **smoothed ratio** | EMA 平滑後的比值 |
| **百分比** | 相對基準線的 %（100% = 基準線） |
| **baseline (mean ± σ)** | 這條比值的基準線平均值與標準差 |
| **斜率 / SNR** | 上升速率，以及目前訊噪比 |
| **狀態** | `Normal` / `WARNING` / `ALARM` / `No Plasma` / `Low Signal` |

滑鼠停在該列上會顯示詳細數值（含目前的 warn / alarm 門檻）。

**五種狀態**

| 狀態 | 顏色 | 意義 |
|---|---|---|
| `Normal` | 綠 | 低於警告門檻 |
| `WARNING` | 橘 | 持續超過警告門檻 |
| `ALARM` | 紅 | 持續超過警報門檻（鎖存） |
| `No Plasma` | — | 參考譜線太弱，判定電漿沒開 |
| `Low Signal` | 藍紫 | 電漿有開，但訊號或參考譜線太靠近雜訊底 —— **這條比值被排除在警報判定之外** |

> 💡 `Low Signal` 是刻意設計的保護。兩條都接近雜訊的譜線相除，結果再怎麼平滑都沒有意義，硬要判讀只會製造假警報。

**門檻怎麼算**：警告門檻取 `max(WarnFactor × 基準均值, 基準均值 + SigmaWarn × σ)` —— 兩者取大。而且這裡的 σ 會取「Golden Run 的 σ」與「即時 EWMA 的 σ」**較大者**，所以當現場比基準時期更吵時，門檻會自動放寬，不會被自己的抖動觸發。

#### 6.3.5 Golden Run 擷取流程

1. 確保製程正在跑，而且**確定沒有洩漏**。
2. 按 **Capture Golden Run**。
3. 填入配方／基準線名稱。
4. 勾選確認框（不勾就不能按 Start Capture）。
5. 按 **Start Capture**，預設平均 **60 秒**。
6. 完成後**立即**寫入 `settings.json`，並自動設為使用中的基準線。

擷取期間的品質把關：

- 只有**通過 SNR 下限**的幀才會被納入該比值的基準線（跟即時判定用同一道關卡）。
- 若某條比值可評估的幀當中，**通過率不到 50%**，這條比值的基準線會被**直接拒絕**，並記進系統日誌。
  → 避免弱訊號譜線用「剛好往上跳的那幾筆雜訊」湊出一條假基準線。
- 同時會記錄 **PlasmaPresentFloor**（＝參考譜線基準均值的 **20%**），之後用來判斷「電漿有沒有開」。

> ⚠️ **什麼時候必須重錄 Golden Run**：改了積分時間或其他影響絕對強度的設定、換了參考譜線、清理過觀察窗、換過光纖、製程配方改變。

#### 6.3.6 趨勢圖

畫每個比值的 **% of baseline** 隨時間變化，保留 **30 分鐘**。放大時時間軸仍會跟隨最新資料。

---

### 6.4 Ratio Setup（比值設定）

**Engineer+ 才能存檔。**

> ⚠️ **這裡的修改是「暫存」的。** 按 Save 只是寫進 `settings.json`；要真正生效，必須回 Monitor 分頁 **Stop → Start** 重啟擷取。畫面上方的黃色提示條就是在講這件事。

#### 6.4.1 左側：比值清單

- 勾選框 = 是否納入洩漏監控。
- **Add** / **Remove** 增刪比值，**最多 10 組**（下方會顯示 `N of 10 ratios`）。

#### 6.4.2 右側：選定比值的細節

**基本**
- **Display name** — 顯示名稱。
- **Enabled** — 是否納入監控。

**Monitoring method（監控方式）** — 這是關鍵選擇：

| 模式 | 監控的量 | 適用情境 |
|---|---|---|
| `Ratio` | 訊號 ÷ 參考 | **預設。** 標準 actinometry，能抵消電漿條件漂移 |
| `AbsoluteIntensity` | 訊號線的絕對強度（扣背景後）<br>參考線**只用來判斷電漿有沒有開**，不做除法 | 訊號線很弱、接近雜訊底時。兩個小數字相除會劇烈擺盪，拿掉除法就少一個雜訊來源 |

`AbsoluteIntensity` 的代價：**不再抵消電漿條件漂移**（功率／壓力／流量變化會直接反映在讀值上）。因此使用它時必須維持穩定的操作點，並更頻繁地重錄基準線。

> 絕對強度模式下，% 趨勢的數字是用 σ 正規化過的分數（基準線 = 100，高一個 warn-σ = 120），這樣同一組 100/120/150 的參考線在兩種模式下都讀得通；斜率則以 σ/min 為單位。

**Signal line（訊號譜線）／ Reference line（參考譜線）**

- **Emission line** — 從依元素分組的譜線 catalog 中挑選。若該譜線套用了波長修正，這裡會顯示例如 `+0.20 nm → 777.4 nm`。
- **Extraction（擷取方式）**：
  - `PeakHeight` — 窄的原子譜線用這個。
  - `Integral` — 分子帶頭（如 OH、NO、N₂）用這個。

> ⚠️ **換了參考譜線，原本的 Golden Run 基準線就失效**（基準線綁定當初的參考譜線）。換完必須重錄。

**Alarm thresholds（警報門檻）**

| 參數 | 預設 | 說明 |
|---|---|---|
| **Warn factor (×mean)** | 1.2 | 基準均值的幾倍觸發警告（1.2 = +20%） |
| **Alarm factor (×mean)** | 1.5 | 基準均值的幾倍觸發警報（1.5 = +50%） |
| **Warn σ (×sigma)** | 3.0 | 基準均值 + 幾個 σ 觸發警告（與上面**取較高者**） |
| **Alarm σ (×sigma)** | 6.0 | 基準均值 + 幾個 σ 觸發警報 |
| **EMA τ (s)** | 5.0 | 指數移動平均時間常數 |
| **Confirm (s)** | 15.0 | 必須持續超過門檻幾秒才升級狀態 |
| **Min SNR** | 5.0 | 兩條譜線都要超過這個訊噪比，比值才被採信。低於此值 → `Low Signal` 並排除在警報外。**填 0 = 關閉這道保護** |

底部 **Save**（Engineer+）／ **Revert**（丟棄未存檔的修改）。

---

### 6.5 Wavelength Calibration（波長校正）

**Engineer+ 才能存檔，同樣是暫存 → Stop/Start 才生效。**

用途：補償光譜儀的**波長軸漂移**。每一列把 catalog 中的一條 `(元素, 波長)` 譜線加上一個位移量（nm）。

**關鍵特性**：因為 key 是 catalog 的 `(元素, 波長)`，所以修正一次就會**同時套用到所有用到這條譜線的比值**（不論它當訊號線還是參考線），不必逐一去改。

| 欄位 | 說明 |
|---|---|
| **Element** | 元素／物種 |
| **Wavelength (nm)** | catalog 中的原始波長 |
| **Correction (nm)** | 加上去的位移量。有效中心 = catalog 波長 + 此值。**上限 ±5 nm** |

沒有任何列時＝所有譜線都用 catalog 原始波長。

**Add** / **Remove** 增刪，底部 **Save** / **Revert**。存檔時會檢查位移範圍，並拒絕重複的 `(元素, 波長)` 組合。

> 💡 **跟 Peak Search 的差別**：每條譜線本來就會在 ±1 nm 內自動重新對準區域峰值。這個修正是給**更大的軸漂移**，或是「±1 nm 搜尋範圍裡剛好卡了另一根錯的峰」的情況用的。
>
> 這裡的修正**只影響量測**，不會寫回 `settings.json` 裡的原始 catalog 波長。

---

### 6.6 Leak Calibration（洩漏率校正）

**Engineer+ 才能存檔。** 目的：把「比值上升了多少」換算成「漏了多少 mbar·L/s」。

> ⚠️⚠️ **標準漏孔必須是空氣**（或就是你要偵測的那種洩漏）。用惰性氣體（如 He）的標準漏孔**完全無效** —— 它不會抬升 O / OH / NO 的訊號。

#### 6.6.1 原理

對每個比值擬合一條**過原點**的加權最小平方直線：

```
x ≈ s · Q
```

- `x` = 相對基準線的上升量（Ratio 模式是**相對**上升 `比值/基準均值 − 1`；AbsoluteIntensity 模式是**絕對**上升 `Δ = 值 − 基準均值`）
- `Q` = 已知漏率（mbar·L/s）
- `s` = 該比值的靈敏度

即時運算時反過來求 `Qᵢ = xᵢ / sᵢ`，再把所有比值用**逆變異數加權融合**成一個 `Q̂ ± σ_Q`，並給出跨比值一致性的信心度。

「無洩漏」狀態就是原點（Q = 0, x = 0），不需要另外量一個點。

#### 6.6.2 操作步驟

1. **選 Baseline (Golden Run)** — 這份校正會綁定到這條基準線。
2. **填 Calibration name** — 例如 `Recipe-A-cal-20260804`。
3. 掛上第一顆已知漏率的標準漏孔，**打開它**，等電漿穩定。
4. 按 **Capture Point…**
   - 填 **Known leak rate (mbar·L/s)**，支援科學記號（`1.2e-4`）。
   - 填 **Leak element / label**（選填，建議填標準漏孔編號）。
   - 勾選確認框 → **Start Capture**。預設平均 **30 秒**。
5. 換下一顆不同漏率的標準漏孔，重複步驟 3–4。**至少兩點**，建議三點以上並涵蓋你關心的漏率範圍。
6. 檢查中間的資料表（漏率、標籤、比值數、各比值的 `x ± σ`）。
   - 要刪除某一點：選取後按 **Remove Selected**。
   - 全部重來：**Clear Points**。
7. 按 **Save Calibration**。**Fitted sensitivities** 區塊會顯示各比值擬合出的靈敏度。

存檔後**立即生效**（不像 Ratio Setup 需要重啟擷取），Leak Monitor 分頁的讀值馬上會出現。

#### 6.6.3 有效性把關

校正**只在對應的 Golden Run 是使用中基準線時**才會套用：

| 狀態 | 行為 |
|---|---|
| `NotCalibrated` | 沒有使用中的校正，只做定性警報 |
| `Active` | 正常估算 |
| `BaselineMismatch` | 基準線換成別條了 → **暫停估算**（而不是給出被錯誤縮放的數字） |

切換或重新擷取 Golden Run 時，系統會**自動配對**綁定該基準線的最新校正，所以換配方不會留下過期的 `BaselineMismatch`。狀態轉換都會寫進系統日誌。

若某個比值的監控模式（Ratio ↔ AbsoluteIntensity）在校正之後被改過，該比值的擬合會被拒用 —— 因為兩種模式的 `x` 單位不同。

> 完整的數學模型與設計文件見 `docs/leak-rate-calibration.md`。

---

### 6.7 Recordings（光譜回放）

回放記錄器寫出的光譜 CSV。

**工具列**

| 按鈕 | 說明 |
|---|---|
| **Refresh** | 重新掃描資料夾 |
| **Open Base** | 用檔案總管開啟記錄器的根目錄 |
| **Open Folder** | 開啟選定 session 的資料夾 |
| **Open File** | 用預設程式開啟該 CSV |
| **Line** / **Heatmap** | 切換檢視模式（目前模式有綠色外框） |
| **Save PNG** / **Copy Image** | 匯出目前圖表 |
| **Clear Compare** | 移除比較用的第二個 session |

**篩選**：Search（日期／時間／檔名關鍵字）、From / To 日期、**Wavelength (nm)**（投影到線圖的波長；按 Enter 或點到別處才套用）。

**版面**
- 左側：session 清單（Date / Time / Rot / Size）。**可以多選兩個 session 做疊圖比較。**
- 右上：主圖（線圖或熱圖）。
- 右下左：單幀光譜 —— 在主圖上點一下就會顯示該時間點的完整光譜。
- 右下右：**Notes** —— 打字後按 **Save Notes**，會存成 CSV 旁邊的 `.notes.txt` 伴隨檔。

---

### 6.8 Ratio Review（比值回放）

回放記錄器寫出的比值 CSV（`{prefix}_Ratio_*.csv`）。

**三種檢視模式**（同樣以綠框標示目前模式）：

| 模式 | 內容 |
|---|---|
| **% of baseline** | 各比值相對基準線的百分比，附 100 / 120 / 150 % 參考線 |
| **Raw ratio** | 未正規化的原始比值 |
| **Leak rate** | 校正後的 `Q̂`（mbar·L/s），附半透明 ±1σ 帶 |

圖表背景會用半透明色帶標出當時的 Warning / Alarm 狀態。

> 📝 **Leak rate** 模式只有在該 CSV 含有 `LeakRate` / `LeakRateSigma` 欄位時才有東西可畫。校正功能上線前錄的舊檔沒有這兩欄，仍然可以正常開啟與檢視前兩種模式。

其餘按鈕（Refresh / Open Base / Open Folder / Open File / Zoom All / Save PNG / Copy Image / Search）用法同 Recordings。

---

### 6.9 Logs（系統日誌）

顯示系統稽核日誌，記錄：

- 裝置連線／中斷、擷取啟停
- 記錄器開檔／收檔／錯誤
- 登入／登出／自動鎖定／使用者管理
- **警報狀態轉換**
- **Golden Run 擷取**（含被拒絕的比值與原因）
- **洩漏率校正的啟用／暫停／清除**
- Ratio CSV 被略過的原因

日誌檔位置：`%APPDATA%\OES_Leak_Monitor\Logs\yyMMddHH.csv`（每小時一個檔）。

> 追查「為什麼那時候跳警報」或「為什麼這條比值沒有基準線」，這裡是第一個要看的地方。

---

## 7. 檔案與資料夾

### 7.1 位置

```
%APPDATA%\OES_Leak_Monitor\
├── settings.json          ← 所有設定（裝置參數、記錄器、比值、Golden Run、校正、波長修正）
├── Data\                  ← 記錄的 CSV（可在 Configuration 改成別的路徑）
│   └── 202608\
│       └── 04\
│           ├── P_OES1_0804153012.csv        ← 強度／全光譜
│           ├── P_OES1_0804153012_1.csv      ← 超過記憶體上限時的續檔
│           ├── P_Ratio_0804153012.csv       ← 對應的比值檔
│           └── P_0804153012.notes.txt       ← Recordings 分頁存的筆記（檔名不含 tag）
└── Logs\
    └── 26080415.csv       ← 系統稽核日誌（每小時一檔）
```

`%APPDATA%` 通常是 `C:\Users\<你的帳號>\AppData\Roaming`。

### 7.2 檔名規則

```
{prefix}_{tag}_{MMddHHmmss}[_N].csv
```

- `prefix` — Configuration 裡設的 File Prefix
- `tag` — `OES1`（強度檔）或 `Ratio`（比值檔）
- `MMddHHmmss` — session 起始時間（**年份由上層資料夾還原**）
- `_N` — 超過記憶體預算後的續檔序號

### 7.3 比值 CSV 格式

```
Timestamp,R_O,R_O_pctBaseline,R_OH,R_OH_pctBaseline,…,OverallState,LeakRate,LeakRateSigma
```

每個比值兩欄（原始值 + % 基準），最後三欄是整體狀態與洩漏率估計。欄位是**依標題名稱**尋址的，所以舊版沒有 `LeakRate` 欄位的檔案仍可正常解析。

比值檔與強度檔**同步開關** —— 只有在電漿強度高於觸發門檻時才有資料。

### 7.4 備份建議

要保存一台機台的完整組態，備份 **`settings.json`** 一個檔就夠了 —— 裡面包含所有裝置參數、記錄器設定、比值定義、Golden Run 基準線、洩漏率校正與波長修正。

---

## 8. 日常操作 SOP

### 8.1 每天開機

1. 啟動程式 → **Sign In**。
2. **Monitor** → **Connect** → 確認 `Test Mode = False`。
3. 按 **Start**。
4. 切到 **Leak Monitor**，確認：
   - **Baseline** 下拉選的是今天配方對應的基準線。
   - 狀態橫幅在電漿點著後轉為綠色 `OK — within baseline`。
   - 沒有 `LOW SIGNAL` 警示。
5. **Configuration** → **Start Save** 武裝記錄器（若需要留資料）。

### 8.2 換配方

1. **Leak Monitor** → **Baseline** 下拉切到新配方的基準線。
   - 洩漏率校正會**自動跟著切換**。
   - 若新配方還沒有基準線 → 跑一段良品製程後 **Capture Golden Run**。
2. 按 **Reset Run** 開新的記錄檔並清空趨勢。

### 8.3 警報發生時

1. 記下狀態橫幅的時間與 **Leak rate** 讀值。
2. 看**哪幾條比值**進了 ALARM：
   - O / OH / NO 一起上升 → 典型的**空氣洩漏**。
   - 只有單一條上升 → 可能是該譜線受干擾，或有其他製程變化。
3. 到 **Ratio Review** 回放這段時間，確認是**持續上升**還是短暫尖峰。
4. 實體檢查（O-ring、法蘭、視窗、閥件）。
5. 排除後 → **Acknowledge Alarm** 清除鎖存。

### 8.4 定期維護

| 週期 | 動作 |
|---|---|
| 每次清潔觀察窗／換光纖後 | **重錄 Golden Run** |
| 每次更動積分時間等曝光參數後 | **重錄 Golden Run** |
| 定期（依機台穩定度） | 檢查波長是否漂移，必要時用 Wavelength Calibration 修正 |
| 定期 | 用標準漏孔重驗洩漏率校正是否仍準確 |
| 定期 | 清理舊的 `Data\` 資料夾（程式不會自動刪檔） |

---

## 9. 疑難排解

### 9.1 按 Connect 後掉進測試模式（明明接了硬體）

最常見原因：**原生 DLL 不在 `.exe` 旁邊**。

檢查程式資料夾裡有沒有 `UserApplication.dll`、`SiUSBXp.dll`、`libsodium.dll`。SDK 的 DLL 解析器**只會在程式所在目錄找**，放到子資料夾沒有用。

其他要檢查的：
- Configuration 分頁的 **Force Test Mode** 是不是被勾起來了。
- USB 線／驅動、裝置管理員裡有沒有看到裝置。
- 乙太網路機種：0.4.6 已修正預設 device type（改為 Z5，並會自動改試另一種），若仍連不上請確認 IP 設定。

### 9.2 光譜幀出現斷裂／撕裂

Configuration → **Acquire Mode** 改成 `Oneshot` → **Apply**。常見於 Z5／乙太網路機種在長積分時間下。

### 9.3 峰形變寬、峰位偏移

Configuration → **Average Mode** 改成 `Software` → **Apply**。某些機種的硬體平均器在多次讀取時會錯位。代價是速度變慢約 N 倍（N = Average Count）。

### 9.4 一直顯示 Low Signal

代表訊號或參考譜線太靠近雜訊底。依序嘗試：

1. **拉長積分時間**（Configuration → Integration Time → Apply）。
2. **提高 Average Count**（配合 `Software` 平均模式）。
3. 檢查**光纖對位**與**觀察窗是否髒污**。
4. 該譜線在你的機台上本來就很弱 → 到 Ratio Setup 把它的 **Monitoring method** 改成 `AbsoluteIntensity`。
5. 最後手段：調低該比值的 **Min SNR**。**不建議**設為 0 —— 那等於關掉假警報保護。

### 9.5 Golden Run 錄完後某條比值沒有基準線

系統日誌會有 `GoldenRunRatioLowSnr` 記錄。原因是該比值可評估的幀當中，通過 SNR 下限的**不到一半**，基準線被主動拒絕了。

處理方式同 §9.4 —— 先把訊號救起來，再重錄。

### 9.6 進不了 Configuration 分頁

正常行為 —— 該分頁需要 **Engineer** 以上。點進去會跳出登入視窗，取消就退回原本的分頁。請用 Engineer 或 Admin 帳號登入。

若登入後角色又掉回 Engineer 以下（例如閒置自動鎖定），畫面會自動跳回 Monitor 分頁。

> 📌 **歷史問題（已修正）**：舊版用硬編的分頁索引做這道檢查，在插入 Wavelength Calibration 分頁後指到了錯誤的分頁，導致 Configuration 一度**完全沒有權限保護**。現已改為以分頁名稱（`x:Name`）比對，插入新分頁不會再讓權限檢查失準。若你手上的版本早於此修正，請升級。

### 9.7 改了 Ratio Setup 但沒反應

Ratio Setup 與 Wavelength Calibration 的修改是**暫存**的。按完 **Save** 之後，必須到 Monitor 分頁 **Stop → Start** 重啟擷取才會生效。

（Leak Calibration 例外 —— 它存檔後立即生效。）

### 9.8 沒有產生 Ratio CSV

依序確認：

1. 記錄器有沒有真的在存檔（Logger Status 應為 `Saving`）—— 強度必須**高於觸發門檻**才會開檔。
2. `settings.json` 裡 `RatioCsvEnabled` 是否為 `true`（預設是）。
3. 看 **Logs** 分頁有沒有 `RatioCsvSkipped` 記錄，裡面會寫原因。

### 9.9 測試模式下警報不會跳

這是預期行為（`SuppressAlarmsInTestMode` 預設為 `true`）。若要在測試模式下演練警報，需要改這個設定，或改用真實／異常資料。

### 9.10 Guest 關不掉程式

這也是刻意設計 —— 必須先 **Sign In**（任何非 Guest 角色都可以）才能關閉視窗。X 鈕、Alt+F4、系統選單都會被擋。

---

## 10. 附錄：參數預設值

### 10.1 硬體參數

| 參數 | 預設值 |
|---|---|
| Integration Time | 50 ms |
| Acquire Mode | `HardwareAverage` |
| Average Count | 1 |
| Average Mode | `Hardware` |
| Boxcar Width | 1（關閉） |
| Polling Interval | 200 ms |
| Max Consecutive Errors | 5 |
| Force Test Mode | 開 |
| Background Remove | 開 |
| Straylight Correction | 關 |
| Linearity Correction | 開 |

### 10.2 比值門檻（每組比值各自可調）

| 參數 | 預設值 |
|---|---|
| Warn factor | 1.2（基準線 +20%） |
| Alarm factor | 1.5（基準線 +50%） |
| Warn σ | 3.0 |
| Alarm σ | 6.0 |
| EMA τ | 5.0 s |
| Confirm time | 15.0 s |
| Min SNR | 5.0 |
| Monitoring method | `Ratio` |

### 10.3 洩漏監控全域設定

| 參數 | 預設值 |
|---|---|
| Golden Run 擷取時間 | 60 s |
| 校正點擷取時間 | 30 s |
| 需要兩條比值才報 Alarm | 是 |
| 測試模式抑制警報 | 是 |
| 寫出 Ratio CSV | 是 |
| 基準線接受門檻 | 可評估幀中至少 50% 通過 SNR |
| 電漿存在判定 | 參考譜線基準均值的 20% |
| 比值數量上限 | 10 |
| 波長修正上限 | ±5 nm |

### 10.4 出廠預設比值

| Key | 名稱 | 訊號線 | 擷取方式 | 參考線 |
|---|---|---|---|---|
| `R_O` | O 777 / N₂ 337 | O 777.2 nm | PeakHeight | N₂ 337.1 nm |
| `R_OH` | OH 309 / N₂ 337 | OH 308.9 nm | Integral | N₂ 337.1 nm |
| `R_NO` | NO 237 / N₂ 337 | NO 237.0 nm | Integral | N₂ 337.1 nm |
| `R_Ar` | Ar 750 / N₂ 337 | Ar 750.4 nm | PeakHeight | N₂ 337.1 nm |

### 10.5 可選參考譜線

`N₂ 337.1`、`N₂ 662.4`、`Hα 656.3`、`Ar 750.4`、`Ar 811.5`

### 10.6 趨勢圖

| 項目 | 值 |
|---|---|
| Monitor 強度趨勢保留 | 30 分鐘 |
| Leak Monitor % 趨勢保留 | 30 分鐘 |
| 強度趨勢峰值搜尋範圍 | ±0.5 nm |
| Smooth 的 EMA τ | 3 s |
| 強度趨勢最多線數 | 1 條 trigger + 5 條 monitored |

### 10.7 帳號

| 項目 | 值 |
|---|---|
| 預設帳號 | `admin` / `admin` ← **請立即更改** |
| 閒置自動鎖定 | 5 分鐘 |
| 角色等級 | Guest(0) < Operator(1) < Engineer(2) < Admin(3) |

---

## 名詞對照

| 中文 | 英文 | 說明 |
|---|---|---|
| 光學發射光譜 | OES (Optical Emission Spectroscopy) | 分析電漿自身發光的技術 |
| 比值法 | Actinometry | 用參考譜線相除以消除電漿條件漂移 |
| 基準線 | Golden Run / Baseline | 無洩漏狀態下錄的參考值 |
| 訊噪比 | SNR | 譜線強度相對本地雜訊的比值 |
| 積分時間 | Integration Time | 偵測器單次曝光時間 |
| 鎖存 | Latched | 警報一旦觸發就保持，需人工清除 |
| 標準漏孔 | Calibrated Leak | 已知漏率的校正件 |
| 漏率 | Leak Rate (mbar·L/s) | 單位時間洩漏的氣體量 |
