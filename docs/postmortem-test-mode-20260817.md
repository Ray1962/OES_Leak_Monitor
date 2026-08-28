# 事件複盤：靜默的測試模式（2026-08-17）

一台交付機台整段時間都在跑合成光譜，畫面卻顯示「已連線」，日誌也一切正常。根因是一支從沒有人想到的相依 DLL；真正的問題是——程式明明知道原因，卻沒有說出來。

| | |
|---|---|
| **影響** | 合成資料寫進正式資料夾，檔名與真實量測完全相同 |
| **根因** | 目標電腦從未安裝 VC++ 2015–2022 x64 Redistributable，DLL 載入失敗（Win32 126） |
| **查出所需** | 另寫一支診斷腳本；程式日誌完全沒有線索 |
| **狀態** | 已修正並重新交付（4 個 commit，含框架 `Aqst.OesApp.Wpf` 0.1.13） |

---

## 1. 事發經過

時間取自那台機台自己的系統日誌與診斷輸出。

| 時間 | 事件 |
|---|---|
| 11:56:56 | 程式啟動，載入 `C:\Users\OIS\AppData\Roaming\OES_Leak_Monitor\settings.json`。參數是出廠值（積分 50 ms、平均 1 次），帳號只有預設 admin——這是一份全新的設定檔。 |
| **11:57:10** | **按下 Connect。日誌記 `Device_Connect / OES Connect ok`，等級 Information，`TestMode=False`（沒有人勾 Force Test Mode），但 `Serial=TEST_MODE_SIMULATOR`。合成光譜開始串流。** |
| 11:57:11 | 開始擷取，記錄器開檔 `D:\OES_LEAK_Data\202608\17\P_OES1_0817115713.csv` 與對應的 Ratio CSV。前綴是正式的 `P_`，只有 Replay 產生的檔案才標 `SIM`——這些假資料在檔案層面與真實量測無法區分。 |
| 11:57:58 | 斷線重連一次，結果相同。操作者沒有理由懷疑：畫面在動、檔案在長、日誌沒有紅字。 |
| 12:00 | 現場把日誌整理成 `LOG.ods` 送出。事後發現那是從 **Logs 分頁**複製的（7 欄、時分秒格式），不是磁碟上的 CSV（8 欄）——分頁只顯示本次執行的記憶體內容，關掉就沒了，現場因此誤以為「日誌存不下來」。 |
| 12:37 | 日誌能證明的只有「不是 Force Test Mode、不是 DLL 找不到」，剩下三條路徑程式一個字都沒留。於是寫 `tools/check-oes-connect.ps1`，把連線流程原樣重跑並印出被吞掉的錯誤。 |
| **12:50** | **診斷輸出：`vcruntime140.dll NOT FOUND`；`libsodium.dll` 與 `UserApplication.dll` 皆 `FAILED (Win32 126)`；USB 列舉根本沒有執行到。** |
| 13:45 | VC++ 執行階段納入發佈流程，重新打包交付。後續再補上出廠值修正、程式自我回報、診斷腳本隨附與手冊改寫。 |

## 2. 根因鏈：四個環節，每一個單看都「正常」

1. **目標電腦從未安裝 VC++ 2015–2022 x64 Redistributable。**
   `UserApplication.dll` 靜態相依 `libsodium.dll`，而 libsodium 相依 `VCRUNTIME140.dll`。這條鏈沒有任何文件寫過，交付清單上也看不出來。
2. **`LoadLibrary` 失敗，錯誤碼 126（`ERROR_MOD_NOT_FOUND`）。**
   失敗的不是我們檢查的那支 DLL，而是它的相依項——所以「檔案都在」的檢查完全通過。
3. **SDK 把例外吞掉，改為設定測試模式。**
   `OesDevice.CheckHardwareDllAvailability()` 捕捉例外後呼叫 `SetupTestMode()`。設計原意是「沒有硬體也能操作 UI」，代價是硬體故障與硬體不存在走同一條路。
4. **`SetupTestMode()` 回傳 `true`——被當成連線成功。**
   於是稽核 CSV 記的是 Information 等級的 `Connect ok`。唯一破綻是 `Serial=TEST_MODE_SIMULATOR`，而那需要事先知道要看它。

完整的回退路徑（六條）與各自的判別方式，見手冊 §9.1 與 `CLAUDE.md` 的〈The Visual C++ runtime ships app-local〉。

## 3. 為什麼查了兩個小時

三條誤導線索：

- **原廠軟體連得到。** SpectraSmart 在同一台連得上，「硬體與驅動正常」因此是確定的，反而把注意力推向設定與線路。它是 32 位元（或自帶 runtime），要的是另一套執行階段。
- **資料夾裡就有 `vcruntime140_cor3.dll`。** 名字幾乎一樣，讓交付資料夾看起來已經帶了執行階段。它是 WPF 的私有改名副本，滿足不了 `VCRUNTIME140.dll` 這個匯入名稱。
- **交付檢查全部通過。** 11 個檔案齊全、位元正確、路徑正確。既有檢查針對「檔案有沒有帶到」，而這次的問題是「帶到的檔案能不能載入」。

三個診斷缺口：

- **成功路徑不記錄原因。** 回退被當成成功，`LastConnectionAttemptResult`（「找不到裝置」「DLL 載入失敗」）哪裡都沒寫；`DeviceViewModel` 沒有公開它，SDK 內部的警告也因為沒有掛 `ILogger` 而落空。
- **Logs 分頁只有記憶體。** 重開程式就空白，現場合理地以為日誌沒存檔。實際檔案在 `%APPDATA%\OES_Leak_Monitor\Logs\yyMMddHH.csv`，分頁上就有「Open log folder」按鈕。
- **出廠值來自框架，不是本產品。** `DeviceSettings.ForceTestMode` 框架預設 true，本專案沒有覆寫——全新設定檔第一次啟動就是測試模式，按一次「Load Defaults」也會把它重新打開。這次不是主因，但它是同一個 bug 的另一半，遲早會單獨造成一次。

## 4. 已經修掉什麼

| 修正 | 位置 | 作用 | Commit |
|---|---|---|---|
| VC++ 執行階段隨程式發佈 | `CopyVcRuntime`（csproj）、`publish.cmd` 檢查 16 檔 | 目標電腦不再需要安裝 Redistributable；缺檔則拒絕出貨 | `e8b737a` |
| 連線回退會自己說話 | `Aqst.OesApp.Wpf` 0.1.13 | 記一筆警告 `Device_TestModeFallback` 帶原因；Test Mode 轉紅並附 tooltip | `227bd7f`（DualOes_PlasmaMonitor） |
| 出廠值改為連硬體 | `AppSettings.DefaultForceTestMode`、`LoadDefaultsAll` | 新機台第一次啟動就連硬體；已存檔的決定仍生效；`AppSettingsDefaultsTests` 鎖住契約 | `751a4ac` |
| 診斷腳本隨程式交付 | `tools/check-oes-connect.*`、手冊 §2.1 / §2.3 / §9.1 | 現場點兩下就能分辨「載不起來」與「找不到裝置」 | `cafd9b8` |

## 5. 以後怎麼不再犯

### 交付一台新機台之前

- [ ] 用 `publish.cmd` 產生交付資料夾，確認印出「all 20 files present」。
- [ ] 整包複製，不要只拷貝 `.exe`，也不要從壓縮檔裡直接執行。
- [ ] 到現場先跑 `check-oes-connect.cmd`，再開程式。
- [ ] 第一次 Connect 後確認 `Serial` 是真實機號，不是 `TEST_MODE_SIMULATOR`。
- [ ] 確認 Test Mode 顯示為黑色 `False`；紅色代表資料是假的。

### 懷疑掉進測試模式時

- [ ] 先看 Monitor 分頁的 `Serial`，一眼就能確認。
- [ ] 到 **Logs 分頁按「Create diagnostic bundle」**，把產生的 `diag_*.zip` 回傳。
      這一顆按鈕就涵蓋了下面「程式開不起來時」的整張清單：當日與前六天的日誌、
      遮蔽過的組態與其備份、當日的洩漏紀錄、以及 `oes-diagnostic.txt`——原生 DLL
      到底載不載得起來，正是這份報告裡程式平常吞掉的那一行。
      不需要簽入，Operator 就能按；它只讀不寫，不會送出任何東西到外部。
- [ ] 清掉這段期間寫出的 CSV——那是合成資料，前綴與真實量測相同。

> [!NOTE]
> zip 落在 `%APPDATA%\OES_Leak_Monitor\Diagnostics\`，按完會自動用檔案總管選起來，
> 完整路徑同時寫進系統日誌（`DiagnosticBundleCreated`）——檔案總管的視窗會被關掉，
> 但日誌不會。先讀 zip 裡的 `README.txt`，**尤其是「NOT in this bundle」那一段**：
> 過大而未收錄的錄製檔、以及正在寫入中被複製而遭截斷的檔案，都只會在那裡說。

### 程式開不起來時

按鈕在程式裡，程式起不來就按不到。這時仍然是原本的手動路徑：

- [ ] 翻 `%APPDATA%\OES_Leak_Monitor\Logs\` 的當日 CSV，不是 Logs 分頁。
- [ ] 找 `Device_TestModeFallback` 警告，Description 就是原因。
- [ ] 跑 `check-oes-connect.cmd`，回傳 `oes-diagnostic.txt`。這支腳本**不會**因為
      程式內建了同樣的探測而被移除——它是兩者中唯一在程式掛掉時還能跑的那個。

### 寫程式時（這幾條不限於這個 bug）

- [ ] 任何「降級但繼續跑」的路徑都必須記錄原因，等級高於 Information。
- [ ] 回傳 `true` 的失敗處理是最貴的一種錯誤——成功路徑同樣要留下診斷資訊。
- [ ] 出廠預設要屬於這個產品，不能繼承框架的通用值，並用測試鎖住。
- [ ] 交付包要自帶相依，包含**相依的相依**；檢查「能不能載入」而不只是「檔案在不在」。
- [ ] 只存在於記憶體的畫面，要在 UI 上指出真正的檔案在哪裡。

## 6. 一分鐘判別卡

接了硬體，但螢幕上出現以下任何一項 → 資料是假的，立刻停止採信：

1. `Serial` 顯示 `TEST_MODE_SIMULATOR`
2. **Test Mode** 顯示紅色 `True`（滑鼠停留可看原因）
3. 波長軸剛好是 200–800 nm、1000 點的等距軸
4. 系統日誌出現警告 `Device_TestModeFallback`

處理：跑同資料夾的 `check-oes-connect.cmd`，依 `oes-diagnostic.txt` 第 4 節的 Win32 錯誤碼對照手冊 §9.1。

---

**這次真正的教訓**：缺一支 DLL 是小事，補上就好。真正花掉一整個上午的，是一個「失敗了還回報成功」的設計——它讓現場、日誌與交付檢查三方同時顯示正常。程式知道原因，只是沒有人要求它說出來。
