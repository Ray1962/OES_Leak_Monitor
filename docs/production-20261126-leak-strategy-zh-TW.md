# 新生產機台測漏策略(11/26 DV 測試之後)與 Replay 驗證

這份是根據 11/26 洩漏閥加量測試(`production-20261126-dv-test-zh-TW.md`)修訂的測漏策略,
取代 11/25 文件(`production-20261125-precoat-recipe-leak-strategy-zh-TW.md`)§5.3 的比值組。
分類器、取樣與 batch 設定沿用 11/25 §5.1–§5.2、§5.5。
策略已經在開發機用 App(master `0232ba7`)Replay 11/26 與 11/25 的錄檔驗證過(§4)。

設定片段:`tools/analysis/20261126/strategy-settings-fragment.json`(只替換裡面列的鍵,其他保留)。
Replay 用的工具:`make_replay_seq.py`(把多筆錄檔串成一個 Replay 檔)、`replay_summary.py`(逐 step 摘要)。

> **結論**
>
> 1. **主判改成 N₂ 357 / Ar(Integral),第二判是 N₂ 337 / Ar(RawMean,σ 判定)**,`RequireTwoForAlarm`。
>    CN、O 777、OH 全部退為 TrendOnly。
> 2. **11/26 的錄檔 Replay:DV 0–400 沒有 Alarm,DV 500 第一個 step 開段後 25 s 進入 Alarm**,之後一直鎖存。
>    precoat 後位置 1–2 只有 Warning。
> 3. **Golden Run 必須當天、在 recipe 平台(precoat 後第 7 步以後)用 Baseline Builder 重建。**
>    用 11/26 的 GR 判 11/25(沒漏氣)的錄檔,precoat 後位置 1–2 兩個指標的中位數都超過 Alarm 門檻,
>    只是 step 短、確認時間沒走完才停在 Warning。跨日的 GR 不能用。
> 4. 偵測能力不變:**只有 DV 500 級(約 0.1–0.3 Torr/min 以上)抓得到**。這套策略能做到的是「大漏會報、無漏不誤報」,
>    不是 mTorr/min 級的規格。

---

## 1. 為什麼這樣選

11/26 DV 文件 §3 的離線抽法說 N₂ 337 peak-height 最乾淨,但上了引擎之後兩個限制改變了選擇:

1. **Golden Run 要求 mean/σ ≥ 10**(`MinBaselineMeanToSigma`,逐幀算)。
   N₂ 337 的 PeakHeight 在沒漏氣時只有約 80 counts、一個像素的雜訊就占 20 %,mean/σ 只有 4.6,
   Baseline Builder 直接拒絕(`not clear of zero`),也就沒有 GR 可判。
2. **SNR 門檻 5**:CN 388 用現場的取線(±1.5 nm)SNR 中位數只有 2.9,每一幀都是 `LowSignal`,一樣沒有基準。

所以用離線模擬引擎的取線(Ar 用 Integral、線性背景)比較了七種取線,選 mean/σ 夠、DV 500 反應大的:

| 取線 | GR mean/σ | SNR 中位數 | precoat 後位置 1 / 2 / 3 | DV 0–400(位置 4–12) | DV 500 |
|---|---:|---:|---:|---:|---:|
| **N₂ 357 Integral**(356.4 ±1.4) | 27 | 29 | +21 / +16 / +11 % | −4 … +4 % | **+78–84 %** |
| N₂ 357 PeakHeight | 41 | 15 | +26 / +17 / +11 % | −4 … +4 % | +62–67 % |
| N₂ 337 PeakHeight(寬背景) | 4.6 | 3.1 | — | — | — |
| N₂ 337 Integral | 3.2–3.4 | 5–6 | — | — | — |
| CN 388 PeakHeight(現場 ±1.5) | 22 | **2.9** | — | — | +6 % |
| CN 388 PeakHeight(11/25 ±0.6) | 28 | 8 | +28 / +17 / +13 % | −2 … +6 % | +4 % |
| CN 帶積分(384.5–389.5) | 6.5 | 5 | — | — | — |

N₂ 337 RawMean(現場原本那條)不在表裡,因為它是 pedestal 值、只能用 σ 判:
GR 0.01319 ± 0.000336(mean/σ 39),DV 500 是 +14 到 +19 σ,位置 1–3 是 +7、+7、+5 σ。

兩條都是 N₂,不是兩種物種 —— 這台 recipe 看不到 O(11/26 DV 文件 §2.1),CN 對 DV 500 只有 +4–6 %,
沒有第二種物種可用。`RequireTwoForAlarm` 在這裡擋的是**取線與雜訊**造成的單邊誤報,
以及 precoat 後前幾步兩條各自不同的上升(N₂ 357 是 +21 %、N₂ 337 是 +7 σ,同時都過門檻的機會很小)。

## 2. 設定

### 2.1 比值組(全部 `ProcessClass = Recipe`)

| # | key | 名稱 | 分子 | 分母 | Role | 門檻 |
|---|---|---|---|---|---|---|
| 1 | `R_N2357_Ar` | N2 357 / Ar 750 | N₂ 356.4 `Integral` ±1.4,背景左 gap 2.0 寬 1.5、右 gap 1.5 寬 1.5,不搜峰 | Ar 750.4 `Integral` ±1(波長修正 +0.4) | **Alarm** | ×1.3 / ×1.4、3 σ / 6 σ |
| 2 | `R_82ecc466` | N2 337 / Ar 750 | N₂ 337.1 `RawMean` ±1.5 | 同上 | **Alarm** | 5 σ / **10 σ**(pedestal,只用 σ) |
| 3 | `R_d1cfada5` | CN 388 / Ar 750 | CN 388.3 `PeakHeight` ±0.6,搜峰 ±1 | 同上 | TrendOnly | — |
| 4 | `R_58006cc7` | O 777 / Ar 750 | O 777.2 `RawMean` ±0.5 | 同上 | TrendOnly | — |
| 5 | `R_0c991e70` | OH 309 / Ar 750 | OH 308.9 `PeakHeight` ±1.5 | 同上 | TrendOnly | (SNR 不足,沒有基準) |
| 6 | `R_5e6412df` | CN 388 / H 656 | CN 388.3 `PeakHeight` ±0.6 | H 656.5 `PeakHeight` | TrendOnly | — |

其他:`emaTauSeconds` 5、`confirmSeconds` 15、`minSnr` 5;`requireTwoForAlarm = true`。
N₂ 337 的 10 σ 是刻意放寬的:位置 1–2 是 +7 σ,DV 500 最小 +14 σ,10 σ 剛好在中間。

### 2.2 分類器、觸發與 batch(同 11/25 §5.2,幀數依 1.1 s/幀)

- 規則:`N2 336.7 / Ar 750.8`(兩邊 RawMean ±0.5)`> 5` → Precoat、`< 0.5` → Recipe,fallback 空白(Unknown 不判)。
- `decideAfterFrames` **10**(約 11 s,跳過頭兩幀 N₂ / Ar 是平台 500 倍的轉換段)。
- 類別門檻:Precoat 0、Recipe 3000;觸發 `SpectrumPercentile` p99 > 1000。
- batch:`batchStartClass = Precoat`、視窗 10–24 s、`minWindowFrames` 5。

### 2.3 Golden Run:每天重建

1. 當天 precoat 之後跑到**第 7 步以後**(CN、N₂ 357 的 precoat 記憶效應在第 6 步左右退完)。
2. Baseline Builder → 日期選當天 → 勾連續 **6 個** recipe step → **Scan ticked**,State 都要是 `Recipe`。
3. 每筆 **Baseline window** 設 `15`–`40` s(15 s 之前 N₂ 的頭幀尾巴還沒退完)。
4. **Build**,確認 N₂ 357 與 N₂ 337 都有基準、mean/σ 分別在 25 與 35 以上,然後 **Save as active baseline**。

11/26 用位置 7–12(17:13:53–17:20:16)建的 `recipe-20261126`:

| 比值 | 基準 |
|---|---|
| N2 357 / Ar 750 | 0.01927 ± 0.00069(mean/σ 27.9,138 幀) |
| N2 337 / Ar 750 | 0.01319 ± 0.000336(mean/σ 39.2) |
| CN 388 / Ar 750 | 0.00427 ± 0.000149(mean/σ 28.7) |
| O 777 / Ar 750 | 0.00212 ± 0.000113(mean/σ 18.7) |
| CN 388 / H 656 | 0.603 ± 0.0236(mean/σ 25.6) |
| OH 309 / Ar 750 | 沒有基準(SNR 不足) |

> 這 6 步其實開著 DV 300 / 400(12 sccm、約 42 sccm)。11/26 DV 文件已經證明它們跟 DV 0–200 分不出來,
> 那天又沒有其他平台區的無漏 step,所以拿來當基準。上機時要用**真的沒漏**的 step。

## 3. 開發機的本機設定(2026-11-27)

`%AppData%\OES_Leak_Monitor\settings.json` 已經照 §2 改好,GR `recipe-20261126` 是作用中的基準。
改之前的檔:`settings.json.bak-before-20261126-strategy-20261127093857`(SECS 驗收用的比值組在裡面)。
取樣(`devices[0]`)沒有改 —— 開發機沒有光譜儀,Replay 時會出現 `Acquisition differs from Golden Run` 的提示,
那只是說測試模式的取樣參數跟錄檔不同,比值的判定不受影響。

## 4. Replay 驗證

做法:Replay 一次只播一個檔,開始時會重設引擎,所以用 `make_replay_seq.py` 把 precoat 與連續的 recipe step 串成一個檔,
step 之間的空檔縮成 12 s,用 20× 播一次。`settings.json` 的 `simulationCsvPath` 指到那個檔,
Connect → Start(測試模式)→ Replay → Play。結果用 `replay_summary.py` 讀 `SIMP_Ratio_*.csv`。

### 4.1 11/26:precoat + DV 0 → 500 共 16 步(同日 GR)

```
python3 -I tools/analysis/20261126/make_replay_seq.py <Temp>\P_OES1_1126162108_seq.csv \
    P_OES1_1126162108.csv P_OES1_1126170057.csv … P_OES1_1126173137.csv
```

| 位置 | DV | 類別 | N₂ 357 vs GR | N₂ 337 vs GR | 判定 |
|---:|---|---|---:|---:|---|
| — | precoat | Precoat | — | — | Idle(不判定) |
| 1 | 0 | Recipe | +21 % | +7 σ | Normal → **Warning** |
| 2 | 0 | Recipe | +17 % | +7 σ | Normal → **Warning** |
| 3 | 0 | Recipe | +11 % | +5 σ | Normal |
| 4–6 | 200 | Recipe | +5 … −5 % | +3 … 0 σ | Normal |
| 7–12 | 300 / 400 | Recipe | −4 … +2 % | −1 … 0 σ | Normal |
| **13** | **500** | Recipe | **+82 %** | **+18.5 σ** | Normal → **ALARM**(開段後 25 s) |
| 14–16 | 500 | Recipe | +77 … +81 % | +14 … +16 σ | ALARM(鎖存) |

每個 step 前 14 幀是 `Idle`:分類器在第 10 幀才判定類別,之前不判。

![圖 1:11/26 Replay 結束時的 Leak Monitor。兩條 Alarm 比值都是 ALARM;趨勢圖每個 step 開頭的尖峰是頭幀(還沒判定類別、不參與判定),11:23 以後(DV 500)平台整段抬高。](images/production-20261126-replay-1126.png)

### 4.2 11/25:precoat + 弱 step + 19 步(沒漏氣、別天)判 11/26 的 GR

11/25 是 `averageCount` 20(4 s/幀),這一輪暫時把 `decideAfterFrames` 改成 3、`minWindowFrames` 改成 3(11/25 Replay SOP §0),
做完已改回 10 / 5。

| 位置 | N₂ 357 vs GR | N₂ 337 vs GR | 判定 |
|---:|---:|---:|---|
| 弱 step | — | — | Idle(低於 Recipe 門檻 3000) |
| 1 | +57 % | +11 σ | Normal → Warning |
| 2 | +55 % | +11 σ | Warning |
| 3 | +51 % | +9 σ | Warning |
| 4–6 | +42 … +33 % | +7 … +4 σ | Warning |
| 7–19 | +30 … +20 % | +3 … −2 σ | Warning(N₂ 357 單獨鎖在 ALARM) |

**沒有 composite Alarm,但這是運氣**:位置 1–2 兩條的中位數都超過 Alarm 門檻(×1.4、10 σ),
只是 4 s 一幀、step 36 s,判定開始(第 3 幀,12 s)後到結束只剩約 24 s,EMA 加上 15 s 確認沒來得及同時成立。
平台區 N₂ 357 還高 20–30 %,N₂ 357 自己一整天鎖在 ALARM。

![圖 2:11/25 Replay 判 11/26 的 GR。整體 WARNING、N₂ 357 單獨 ALARM。](images/production-20261126-replay-1125.png)

結論:**N₂ 357 跨日漂移 +20–30 %**(視窗沉積、腔體狀態),跟 DV 500 的 +80 % 只差三倍。GR 一定要當天重建(§2.3);
換腔體零件、清腔、或停機超過一天之後也要重建。

### 4.3 Replay 時看到的 App 問題

第二輪 Replay 的比值 CSV 寫成 `P_Ratio_1127111221.csv`,不是 `SIMP_Ratio_*`:
日誌裡 `RatioCsvOpened`(11:12:21.603)比 `ReplayStarted`(.604)早 1 ms,
應該是合成光譜的一幀在「關閉錄檔 → 換成 SIM 前綴」之間重新開了比值 CSV。
**重播資料被寫成正式量測的檔名**,這正是 `docs/dev/replay.md` 說要避免的情況。第一輪沒有發生。原因是 `BeginReplayOutput` 先關錄檔、後換前綴,追蹤在 issue #142。

## 5. 上機步驟

1. 先備份現場 `settings.json`。用記事本把 `strategy-settings-fragment.json` 的 `logger` 四個鍵與 `leakMonitor` 的五個鍵替換進去。
   分類器在畫面上沒有設定欄位,只能改檔。
2. 確認 `devices[0]` 是積分 200 ms、`averageCount` 5。
3. 開 App → Ratio Setup 確認 6 條比值、Process class 都是 Recipe。
4. 生產跑過 precoat 與至少 12 個 recipe step 之後,照 §2.3 用當天第 7–12 步建 GR,Save as active baseline。
5. 之後每天(或每次清腔後)照 §2.3 重建一次。
6. **不要**在沒有電漿或只有 precoat 時按 Capture Golden Run(11/26 的 16 次擷取都是這樣丟掉的)。

## 6. 還沒解決的事

- **偵測下限**:只到 DV 500 級。要更好要靠製程端(在 recipe 以外找一個沒有 C₂H₂ 的含 Ar 步驟),不是調門檻。
- **precoat 後位置 1–3** 兩個指標都偏高;同日 GR 是 Warning,跨日 GR 接近 Alarm。依位置建 GR 的需求是 issue #135。
- **DV 500 的 ROR**、DV 450 的補測(11/26 DV 文件 §7)。
