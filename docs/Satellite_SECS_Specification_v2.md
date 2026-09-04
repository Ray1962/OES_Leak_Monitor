# Satellite SECS 通訊協定規格書

> 本文件為 AQUSEN Satellite 感測器系統的 SECS/GEM 通訊協定規格整理。
> 內容涵蓋 SVID 編碼規則、SECS 連線參數、支援的 SECS 訊息功能，以及實際訊息範例與解讀。

> **v2 增補：** 新增 `ss=27` OES Leak Monitor（電漿發光光譜洩漏監測器）的 SVID 定義（§1.2、§1.4）、警報定義（§5）與事件／報告定義（§6）。

> **v2.1（實作狀態）：** `ss=27` 的設備端 SECS/HSMS 介面**已實作一部分**：
> **全域量測項目 VID 001 ~ 026**（§1.4(a)）、**警報 ALID 001／002／012 ~ 014**（§5.1）、
> **事件 CEID 502／508／509**（§6.1），加上 S1F3 狀態查詢與 S2F23／S6F1 追蹤。
> **比值槽位 VID 101 ~ 400（§1.4(b)）、其餘 ALID 與 CEID、遠端命令 S2F41 尚未實作。**
> §5.1 的 ALID 012 ~ 014（設備故障）為實作時新增。

---

## 0. 導讀（協定重點解讀）

SECS（SEMI Equipment Communications Standard）是半導體設備與主機（Host / MES）之間的標準通訊協定，本規格採用 **SECS-II 訊息格式** 搭配 **HSMS（High-Speed SECS Message Services，走 TCP/IP）** 傳輸。整份規格可拆成四個部分：

1. **SVID 編碼規則** — 定義每一筆量測資料（變數）的唯一 ID。Satellite 用一組 10 位數字 `1ccssaavvv` 把「哪個腔體、哪種感測器、對應哪個 slit valve、量測哪個物理量」全部編碼進去。這是整份文件最核心、也最需要理解的部分。
2. **SECS 連線參數** — HSMS 連線的 IP／Port 與各項逾時（T3、T5～T8）設定。
3. **SECS 功能（Stream/Function）** — 本設備支援哪些 SxFy 訊息（上線確認、狀態查詢、事件報告、警報等）。
4. **訊息範例** — 各 SxFy 的實際 SECS-II 資料結構範例。

其中 SVID 是「資料的地址」，SECS Function 是「溝通的動作」，兩者搭配即可讓 Host 訂閱、查詢並接收 Satellite 感測器的即時資料。

---

## 1. SVID 編碼格式

Satellite 以下列規則定義每個 SVID（Status Variable ID），總長 **10 位數字**：

```
1 c c s s a a v v v
│ └┬┘ └┬┘ └┬┘ └─┬─┘
│  │   │   │    └── vvv：VID（該感測器內的量測項目，3 碼）
│  │   │   └─────── aa ：Slit valve 名稱（用於調節/對應感測器訊號，2 碼）
│  │   └─────────── ss ：感測器類型（2 碼）
│  └─────────────── cc ：腔體（Chamber）名稱（2 碼）
└────────────────── 1  ：固定碼，代表 AQUSEN Sensor
```

| 欄位 | 位數 | 意義 |
|---|---|---|
| `1` | 1 | 固定值，AQUSEN Sensor 專用識別碼 |
| `cc` | 2 | 腔體（Chamber）代號 |
| `ss` | 2 | 感測器類型代號 |
| `aa` | 2 | Slit valve 代號（用於調節感測器訊號） |
| `vvv` | 3 | VID，該感測器類型下的量測項目 |

> **範例解讀：** `1110806001`
> = `1` + `cc=11` + `ss=08` + `aa=06` + `vvv=001`
> → AQUSEN 感測器，Buffer/Buffer 1 腔體，O2 sensor，對應 Ch_A/VIA_1 slit valve，量測項目為「O2 ppm」。

### 1.1 `cc` — 腔體（Chamber）代號

| 代號 | 腔體 | 代號 | 腔體 |
|---|---|---|---|
| 00 | 無 DVM 收集 slit valve 訊號 | 01 | Ch_1 |
| 02 | Ch_2 | 03 | Ch_3 |
| 04 | Ch_4 | 05 | Ch_5 |
| 06 | Ch_A / VIA_1 | 07 | Ch_E |
| 08 | Ch_F | 09 | Ch_B |
| 10 | X'fer / Buffer 2 | 11 | Buffer / Buffer 1 |
| 12 | Ch_C | 13 | Ch_D |
| 14 | LLA | 15 | LLB |
| 21 | X'fer Viewport 1 | 22 | X'fer Viewport 2 |
| 23 | X'fer Viewport 3 | 24 | X'fer Viewport 4 |
| 25 | X'fer Viewport 5 | 31 | Buffer Viewport 1 |
| 32 | Buffer Viewport 2 | 33 | Buffer Viewport 3 |
| 34 | Buffer Viewport 4 | | |

### 1.2 `ss` — 感測器類型代號

| 代號 | 感測器 | 代號 | 感測器 |
|---|---|---|---|
| 01 | RGA | 02 | OR4000 |
| 03 | Reserved | 04 | Reserved |
| 05 | IR300 | 06 | Temperature Sensor |
| 07 | Reserved | 08 | O2 sensor |
| 09 | H2 sensor | 10 | IR422C |
| 11 | Reserved | 12 | DC Arcing detector |
| 13 | RF Arc sensor | 14 | ESC monitor |
| 15 | Pulsed RF monitor | 16 | O2 & H2O sensor |
| 17 | IT470 | 18 | MS100 |
| 19 | AEDC_S | 20 | AEDC_E |
| 21 | MKSDC_S | 22 | ATM-T/M-01 |
| 23 | Marc1000PN | 24 | RFZN |
| 25 | RPS Monitor | 26 | Pumping efficient |
| 27 | OES Leak Monitor | | |

### 1.3 `aa` — Slit valve 代號

> 用於調節（condition）感測器訊號，編碼表與腔體 `cc` 相同。

| 代號 | Slit valve | 代號 | Slit valve |
|---|---|---|---|
| 00 | 無 DVM 收集 slit valve 訊號 | 01 | Ch_1 |
| 02 | Ch_2 | 03 | Ch_3 |
| 04 | Ch_4 | 05 | Ch_5 |
| 06 | Ch_A / VIA_1 | 07 | Ch_E |
| 08 | Ch_F | 09 | Ch_B |
| 10 | X'fer / Buffer 2 | 11 | Buffer / Buffer 1 |
| 12 | Ch_C | 13 | Ch_D |
| 14 | LLA | 15 | LLB |
| 21 | X'fer Viewport 1 | 22 | X'fer Viewport 2 |
| 23 | X'fer Viewport 3 | 24 | X'fer Viewport 4 |
| 25 | X'fer Viewport 5 | 31 | Buffer Viewport 1 |
| 32 | Buffer Viewport 2 | 33 | Buffer Viewport 3 |
| 34 | Buffer Viewport 4 | | |

### 1.4 `vvv` — VID（各感測器的量測項目）

以下依感測器類型列出對應的 VID 量測項目。

#### RGA（`ss=01`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 ~ 300 | Mass Amp（質譜各質量數振幅） | 301 ~ 600 | Mass pp（各質量數分壓） |
| 601 | H2O pp | 602 | N2 pp |
| 603 | O2 pp | 604 | He pp |
| 605 | CO2 pp | 606 | IPA pp |
| 607 | MP Oil pp | 608 | Fomblin pp |
| 609 | CxHy-S pp | 610 | Solvent pp |
| 611 | Polymer pp | 612 | DC704 pp |
| 613 | NH3 pp | 614 | SO2 pp |
| 615 | CxHy-L pp | 616 | CxHy-H pp |
| 617 | PM Index | 618 | N2/O2 ratio |
| 619 | N1/H2O ratio | 620 | O2/H2O ratio |
| 621 | N2/Ar ratio | 622 | O2/Ar ratio |
| 623 | H2O/Ar ratio | 624 | Ar pp |
| 625 | CxHy-L/Ar ratio | 626 | CO2/Ar ratio |
| 627 | O2 ppm | 628 | H2O ppm |
| 629 | N2 ppm | 630 | Ar ppm |
| 631 | CO2 ppm | 632 | IPA ppm |
| 633 | Solvent ppm | 634 | Polymer ppm |
| 635 | Fomblin ppm | 636 | MP Oil ppm |
| 637 | DC704 ppm | 638 | CxHy-L ppm |
| 639 | CxHy-H ppm | 640 | CxHy-S ppm |
| 641 | Cl2 ppm | 642 | HCl ppm |
| 643 | Cl ppm | 644 | Ar impurity ppm |
| 645 | H2 ppm | 646 | CO ppm |
| 647 | H2 pp | 648 | CO pp |
| 649 | O2/N2 ratio | 650 | H2O/N2 ratio |

#### OR4000（`ss=02`）

| VID | 項目 |
|---|---|
| 001 | Channel #1 Temperature |
| 002 | Channel #2 Temperature |
| 003 | Channel #3 Temperature |
| 004 | Channel #4 Temperature |

#### IR300（`ss=05`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | Concentration | 002 | Pressure |
| 003 | Temperature | 004 | Light |

#### Temperature Sensor（`ss=06`）

| VID | 項目 |
|---|---|
| 001 | Temperature |

#### O2 sensor（`ss=08`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | O2 ppm | 002 | Health index |
| 003 | O2 ppm – Base | 004 | Air leak index |
| 005 | Reserved | 006 | ROR |

#### H2 sensor（`ss=09`）

| VID | 項目 |
|---|---|
| 001 | H2 ppm |

#### IR422C（`ss=10`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | SiF4 pp | 002 | CF4 pp |
| 003 | Reserved | 004 | Pressure |
| 005 | Light #1 | 006 | Light #2 |
| 007 | Light #3 | 008 | Light #4 |
| 009 | SiF4 Slope | 010 | CF4 Slope |
| 011 | Reserved | 012 | SiF4 Max. |
| 013 | CF4 Max. | 014 | Reserved |
| 015 | SiF4 Flat | 016 | CF4 Flat |

#### DC Arc Detector（`ss=12`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | Arc count #1 | 002 | Arc Time #1 |
| 003 | Arc count #2 | 004 | Arc Time #2 |
| 005 | Arc count #3 | 006 | Arc Time #3 |
| 007 | Arc count #4 | 008 | Arc Time #4 |

#### RF Arc Sensor（`ss=13`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | RF Voltage | 002 | RF Current |
| 003 | E_11 | 004 | E_11 Sum |
| 005 | E_12 | 006 | E_12 Sum |
| 007 | E_13 | 008 | E_13 Sum |
| 009 | E_21 | 010 | E_21 Sum |
| 011 | E_22 | 012 | E_22 Sum |
| 013 | E_23 | 014 | E_23 Sum |
| 015 | E_31 | 016 | E_31 Sum |
| 017 | E_32 | 018 | E_32 Sum |
| 019 | E_33 | 020 | E_33 Sum |
| 021 | E_All | 022 | E_All Sum |

#### ESC Monitor（`ss=14`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | E1 I_Avg | 002 | E1 I_Max |
| 003 | E1 I_Min | 004 | E1 V_Avg |
| 005 | E1 V_Max | 006 | E1 V_Min |
| 007 | E2 I_Avg | 008 | E2 I_Max |
| 009 | E2 I_Min | 010 | E2 V_Avg |
| 011 | E2 V_Max | 012 | E2 V_Min |
| 013 | Arc_Front | 014 | Arc_Back |
| 015 | Aging Index | | |

#### Pulsed RF Monitor（`ss=15`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | RF Voltage | 002 | RF Current |
| 003 | E_11 | 004 | E_11 Sum |
| 005 | E_12 | 006 | E_12 Sum |
| 007 | E_13 | 008 | E_13 Sum |
| 009 | E_21 | 010 | E_21 Sum |
| 011 | E_22 | 012 | E_22 Sum |
| 013 | E_23 | 014 | E_23 Sum |
| 015 | E_31 | 016 | E_31 Sum |
| 017 | E_32 | 018 | E_32 Sum |
| 019 | E_33 | 020 | E_33 Sum |
| 021 | E_All | 022 | E_All Sum |
| 023 | Pulse AbCount | 024 | AbCount Sum |
| 025 | Pulse On time | 026 | Duty Cycle |
| 027 | Frequency | 028 | Pulse Voltage |
| 029 | Pulse Current | | |

#### O2 & H2O Sensor（`ss=16`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | O2 ppm | 002 | Health index_O2 |
| 003 | O2 ppm – Base | 004 | H2O ppm |
| 005 | H2O ppm – Base | 006 | Air leak index |
| 007 | Reserved | 008 | ROR |

#### IT470（`ss=17`）

| VID | 項目 |
|---|---|
| 001 | Temperature |

#### MS100（`ss=18`）

| VID | 項目 |
|---|---|
| 001 | H2O ppm |

#### AEDC_S（`ss=19`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | DC Power | 002 | DC Voltage |
| 003 | DC Current | 004 | Micro Arc Count |
| 005 | Hard Arc Count | | |

#### AEDC_E（`ss=20`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | DC Power | 002 | DC Voltage |
| 003 | DC Current | 004 | Micro Arc Count |
| 005 | Hard Arc Count | | |

#### MKSDC_S（`ss=21`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | DC Power | 002 | DC Voltage |
| 003 | DC Current | 004 | Arc Count |

#### ATM-T/M-01（`ss=22`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | Temperature | 002 | RH % |

#### MArc1000PN（`ss=23`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | E_11 | 002 | E_11 Sum |
| 003 | E_12 | 004 | E_12 Sum |
| 005 | E_13 | 006 | E_13 Sum |
| 007 | E_21 | 008 | E_21 Sum |
| 009 | E_22 | 010 | E_22 Sum |
| 011 | E_23 | 012 | E_23 Sum |
| 013 | E_31 | 014 | E_31 Sum |
| 015 | E_32 | 016 | E_32 Sum |
| 017 | E_33 | 018 | E_33 Sum |
| 019 | E_All | 020 | E_All Sum |
| 021 | Pulse AbCount | 022 | AbCount Sum |
| 023 | Pulse On time | 024 | Duty Cycle |
| 025 | Frequency | | |

#### RFZN（`ss=24`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | RF Frequency | 002 | RF Power |
| 003 | RF Voltage | 004 | RF Current |
| 005 | RF Phase | 006 | H1 Voltage |
| 007 | H1 Current | 008 | H2 Voltage |
| 009 | H2 Current | 010 | H3 Voltage |
| 011 | H3 Current | 012 | H4 Voltage |
| 013 | H4 Current | 014 | H5 Voltage |
| 015 | H5 Current | | |

#### RPS Monitor（`ss=25`）

| VID | 項目 |
|---|---|
| 001 | RPS Aging |

#### Pumping Efficient（`ss=26`）

| VID | 項目 | VID | 項目 |
|---|---|---|---|
| 001 | Pressure | 002 | Pumping Speed |

#### OES Leak Monitor（`ss=27`）

> **實作狀態（v2.1）：** 本節的 **VID 001 ~ 026 已由設備端實作**並可經 S1F3 查詢；
> **(b) 的比值槽位 VID 101 ~ 400 尚未實作**。以下 SVID（以及 §5 的 ALID、§6 的 CEID）
> 依現行設備軟體的演算法與資料結構訂定；設備端的實作細節見該軟體的 `docs/secs-integration.md`。
>
> 兩點與本文件措辭有出入，Host 端解讀時請注意：
> - **VID 023（Frame dropout count）是「本次執行累計」**，非「本次採集累計」——設備端的計數器
>   不隨採集起停歸零。請當單調遞增計數器、以差值判讀。
> - **ALID 在 S5F1／S5F6 以 ASCII 送出**（如 `<A[8] "10227002">`），與 §4、§5.3 的範例一致。

**編碼慣例：** OES 直接觀測腔體內的電漿發光，不透過 slit valve 調節訊號，故 `aa` 一律填 `00`，`cc` 填實際量測的腔體。

> **範例解讀：** `1022700001`
> = `1` + `cc=02`（Ch_2）+ `ss=27` + `aa=00` + `vvv=001`
> → Ch_2 的 OES 洩漏監測器，洩漏率 Q̂（mbar·L/s）。

**量測原理（供解讀數值用）：** 以 actinometry 為基礎——把漏氣特徵譜線（N₂、NO 等）除以電漿參考譜線（Ar 等）得到「比值（ratio）」，與無漏氣狀態下擷取的基準（Golden Run）比較；再以已知漏率的標準漏孔校正，把比值的上升量換算成洩漏率 Q̂（mbar·L/s）。設備最多可同時監測 **10 組比值**，以下稱「槽位（slot）」。槽位號即設備端比值設定畫面的第 N 列；某一槽位實際監測哪一條譜線由設備端設定決定，Host 可由該槽位的「身分」類 VID（位移 16–24）讀出，不需要取得設備的設定檔。

##### (a) 全域量測項目（VID 001–099）

| VID | 項目 | 單位 | 型別 | 說明 |
|---|---|---|---|---|
| 001 | Leak rate | mbar·L/s | F4 | 融合各比值後的洩漏率估計 Q̂；`004 = 0` 時本值無效 |
| 002 | Leak rate sigma | mbar·L/s | F4 | Q̂ 的 1σ 不確定度 |
| 003 | Leak rate confidence | 0–1 | F4 | 各比值推得的洩漏率彼此一致的程度 |
| 004 | Leak rate valid | — | U4 | 1 = 本幀有有效的洩漏率估計 |
| 005 | Out of calibrated range | — | U4 | 1 = 超出校正涵蓋的漏率範圍（外插值，僅供參考） |
| 006 | Calibration status | — | U4 | 0/1/2，見 (c)-2 |
| 007 | Composite leak level | — | U4 | 0/1/2/3，見 (c)-1 |
| 008 | Enabled ratio count | 個 | U4 | 目前啟用監測的槽位數 |
| 009 | Warning ratio count | 個 | U4 | 處於 Warning 的槽位數 |
| 010 | Alarm ratio count | 個 | U4 | 處於 Alarm（已鎖存）的槽位數 |
| 011 | Low-signal ratio count | 個 | U4 | 因訊噪比不足而停判的槽位數 |
| 012 | Baseline available | — | U4 | 1 = 已有可用的 Golden Run 基準 |
| 013 | Active Golden Run name | — | A[32] | 現用基準名稱（通常對應製程配方） |
| 014 | Active calibration name | — | A[32] | 現用洩漏率校正名稱；未選為空字串 |
| 015 | Acquisition mismatch | — | U4 | 1 = 目前採集參數（積分時間／平均／校正開關／波長軸）與基準擷取時不同 |
| 016 | Test / replay mode | — | U4 | 1 = 資料來自測試或錄檔回放，非實機量測 |
| 017 | Golden Run capture active | — | U4 | 1 = 正在擷取基準 |
| 018 | Golden Run capture progress | % | F4 | 0–100 |
| 019 | Calibration capture active | — | U4 | 1 = 正在擷取校正點 |
| 020 | Calibration capture progress | % | F4 | 0–100 |
| 021 | Plasma present | — | U4 | 1 = 判定電漿開啟（亮度閘門開啟，本幀納入評估） |
| 022 | Plasma gate available | — | U4 | 0 = 無法判定電漿有無；此時絕對強度類槽位不設閘門 |
| 023 | Frame dropout count | 次 | U4 | 本次採集累計的瞬時掉幀（單幀異常暗），供光譜儀健康度追蹤 |
| 024 | Integration time | ms | F4 | 現行積分時間 |
| 025 | Average count | 次 | U4 | 現行平均次數 |
| 026 | Frame rate | Hz | F4 | 實際取樣率 |
| 027 | Process class | — | A[16] | 現行電漿步驟的製程類別名稱；未分類時為空字串，理由見 028 |
| 028 | Process class state | — | U4 | 0 = 未設定分類器、1 = 無電漿步驟、2 = 步驟進行中尚未判定、3 = 已分類（名稱在 027）、4 = 判定為無法分類 |
| 029 | Process step index | 次 | U4 | 本次採集累計的電漿步驟數；不論是否設定分類器都會計數 |
| 030 ~ 099 | Reserved | | | 保留 |

##### (b) 比值槽位（VID 101–400）

槽位 N（N = 1 ~ 10）的 VID 由下式決定：

```
VID = 100 + (N − 1) × 30 + 位移
```

| 槽位 | VID 區段 | 槽位 | VID 區段 |
|---|---|---|---|
| Ratio 1 | 101 ~ 130 | Ratio 2 | 131 ~ 160 |
| Ratio 3 | 161 ~ 190 | Ratio 4 | 191 ~ 220 |
| Ratio 5 | 221 ~ 250 | Ratio 6 | 251 ~ 280 |
| Ratio 7 | 281 ~ 310 | Ratio 8 | 311 ~ 340 |
| Ratio 9 | 341 ~ 370 | Ratio 10 | 371 ~ 400 |

每個槽位的位移定義如下（前 15 項為每幀更新的即時量，16–24 為身分與門檻，僅在設定變更時改變）：

| 位移 | 項目 | 單位 | 型別 | 說明 |
|---|---|---|---|---|
| 01 | Monitored value | — | F4 | 本幀監測值。比值模式 = 訊號線／參考線的原始比值；絕對強度模式 = 訊號線強度本身 |
| 02 | Smoothed value | — | F4 | 監測值的指數平滑（EMA）結果，狀態機以此判定 |
| 03 | Percent of baseline / sigma score | % 或 σ | F4 | 位移 18 = 0 時為基準的百分比（100 = 基準）；= 1 時為 σ 分數（100 = 基準、120 = 高於基準一個警告 σ） |
| 04 | Slope | %/min 或 σ/min | F4 | 近兩分鐘的線性迴歸斜率，單位隨位移 18 |
| 05 | Ratio state | — | U4 | 0 ~ 6，見 (c)-3 |
| 06 | Signal SNR | — | F4 | 訊號譜線訊噪比 |
| 07 | Reference SNR | — | F4 | 參考譜線訊噪比；絕對強度模式不使用參考線，固定 0 |
| 08 | Signal intensity | counts 或 counts·nm | F4 | 訊號譜線強度，單位隨位移 19 |
| 09 | Reference intensity | counts 或 counts·nm | F4 | 參考譜線強度；絕對強度模式固定 0 |
| 10 | Baseline mean | — | F4 | Golden Run 基準平均值 |
| 11 | Baseline sigma | — | F4 | Golden Run 基準標準差 |
| 12 | Warn threshold | — | F4 | 現行警告門檻（已含依實測雜訊加寬的部分），與位移 02 同單位 |
| 13 | Alarm threshold | — | F4 | 現行警報門檻 |
| 14 | Ratio leak rate | mbar·L/s | F4 | 僅由本槽位推得的洩漏率 Qᵢ |
| 15 | Used in fusion | — | U4 | 1 = 本幀有納入 VID 001 的融合估計 |
| 16 | Slot enabled | — | U4 | 1 = 本槽位啟用監測 |
| 17 | Monitor mode | — | U4 | 0 = 比值、1 = 絕對強度，見 (c)-4 |
| 18 | Value semantics | — | U4 | 0 = 位移 03 為基準百分比、1 = σ 分數 |
| 19 | Extraction mode | — | U4 | 0 = 峰高、1 = 積分、2 = 原始平均，見 (c)-5 |
| 20 | Signal wavelength | nm | F4 | 訊號譜線波長（含波長校正偏移後的實際取值） |
| 21 | Reference wavelength | nm | F4 | 參考譜線波長；絕對強度模式固定 0 |
| 22 | Slot display name | — | A[32] | 槽位顯示名稱，例如 `R_N2Ar` |
| 23 | Signal species | — | A[16] | 訊號譜線物種，例如 `N2`、`NO`（`u` 開頭表示現場自訂譜線） |
| 24 | Reference species | — | A[16] | 參考譜線物種，例如 `Ar`；絕對強度模式為空字串 |
| 25 ~ 30 | Reserved | | | 保留 |

> **槽位範例：** Ch_2 的 Ratio 2「基準百分比」 = `100 + (2 − 1) × 30 + 03` = VID `133` → SVID `1022700133`。

##### (c) 列舉對照表

**(c)-1 Composite leak level（VID 007）**

| 值 | 意義 |
|---|---|
| 0 | Idle — 無法判定（電漿未開、尚無基準，或全部槽位停用） |
| 1 | Normal — 各啟用槽位均在警告門檻以下 |
| 2 | Warning — 有槽位持續高於警告門檻 |
| 3 | Alarm — 已確認洩漏，鎖存至操作者確認（Acknowledge）為止 |

**(c)-2 Calibration status（VID 006）**

| 值 | 意義 |
|---|---|
| 0 | 未校正 — 未選用任何洩漏率校正，僅提供定性判斷 |
| 1 | 有效 — 校正與現用基準相符，洩漏率估計進行中 |
| 2 | 基準不符 — 校正是對另一組基準做的，估計暫停（需選回相符基準或重新校正） |

**(c)-3 Ratio state（位移 05）**

| 值 | 意義 | 值 | 意義 |
|---|---|---|---|
| 0 | Normal | 1 | Warning |
| 2 | Alarm（鎖存） | 3 | NoPlasma（電漿未開／參考線過弱） |
| 4 | LowSignal（訊噪比不足，停判） | 5 | NoBaseline（本槽位無可用基準） |
| 6 | Disabled（操作者停用） | | |

**(c)-4 Monitor mode（位移 17）**

| 值 | 意義 |
|---|---|
| 0 | 比值（Ratio）— 訊號線 ÷ 參考線，可抵銷電漿條件漂移 |
| 1 | 絕對強度（Absolute intensity）— 只看訊號線本身，不使用參考線；適合接近雜訊的弱線，但對電漿條件變動較敏感 |

**(c)-5 Extraction mode（位移 19）**

| 值 | 意義 |
|---|---|
| 0 | 峰高（Peak height）— 扣除兩側連續光內插而得的背景，單位 counts |
| 1 | 積分（Integral）— 扣背景後的譜線面積，單位 counts·nm |
| 2 | 原始平均（Raw mean）— 不扣背景、不搜尋峰位，讀值含連續光基座 |

##### (d) 判讀注意事項

- **位移 03 的單位不固定，必須先讀位移 18。** 讀值含連續光基座時（原始平均 + 絕對強度模式），任何「值 ÷ 基準」都會被壓縮到不到 1 %，因此該槽位改以 σ 分數表示。兩者共用 100 / 120 / 150 的刻度（100 = 基準、120 = 警告、150 = 警報），可套用同一組參考線，但物理意義不同，不可混算。
- **狀態 3（NoPlasma）與 4（LowSignal）的槽位不納入複合警報。** 訊號接近雜訊時比值沒有意義，設備選擇停判而非誤報；Host 不應把這兩種狀態視為「正常」。
- **複合警報為鎖存式。** 進入 Alarm 後需操作者在設備端確認（見 §6 CEID `502`）才會解除；本規格未提供由 Host 遠端解除的命令。
- **洩漏率僅在 VID 006 = 1 時有效。** 換了基準而未換校正時會轉為 2，估計暫停（VID 004 = 0），此時 VID 001 應視為無效而非 0。
- **VID 015 = 1 時應視同警告。** 採集參數改變會整體改變絕對強度的刻度，與舊基準已無可比性，建議重新擷取 Golden Run。
- **VID 016 = 1 表示資料來自測試或回放**，不可用於製程判定。

---

## 2. SECS 連線參數

| 參數 | 值 | 說明 |
|---|---|---|
| Host IP | 客戶提供 | 主機端 IP 位址 |
| Host Port | 客戶提供 | 主機端連接埠 |
| Local IP | 客戶提供 | 設備（Satellite）端 IP 位址 |
| Local Port | 客戶提供（與 Host Port 相同） | 設備端連接埠 |
| Time out **T3** | 45（可設定） | Reply Timeout，等待回覆訊息的逾時（秒） |
| Time out **T5** | 10（可設定） | Connect Separation Timeout，重新連線的間隔（秒） |
| Time out **T6** | 10（可設定） | Control Transaction Timeout（秒） |
| Time out **T7** | 10（可設定） | Not Selected Timeout，連線後等待 Select 的逾時（秒） |
| Time out **T8** | 10（可設定） | Network Intercharacter Timeout（秒） |

---

## 3. 支援的 SECS 功能（Stream / Function）

| SxFy | 名稱 | 說明 |
|---|---|---|
| S1F1 / F2 | Are You Online? | 上線確認（Are you there / online data） |
| S1F3 / F4 | Equipment Status Request | 設備狀態查詢 |
| S1F13 / F14 | Establish Communication Request | 建立通訊請求 |
| S2F17 / F18 | Date and Time Request | 日期時間查詢 |
| S2F23 / F24 | Trace Initialize Send | 追蹤資料初始化設定 |
| S2F31 / F32 | Date and Time Set Request | 日期時間設定 |
| S2F33 / F34 | Define Report | 定義報告（Report） |
| S2F35 / F36 | Link Event Report | 連結事件與報告 |
| S2F37 / F38 | Enable / Disable Event Report | 啟用／停用事件報告 |
| S2F41 / F42 | Host Command Send | 主機命令下達 |
| S5F1 / F18 | Alarm Report Send | 警報回報 |
| S6F1 / F2 | Trace Data Send | 追蹤資料回傳 |
| S6F11 / F12 | Event Report Send | 事件報告回傳 |

> 命名慣例：奇數 Function（F1、F3…）多為主動（Primary）訊息；偶數 Function（F2、F4…）為對應回覆（Reply）。`W` 位元表示要求對方回覆。

---

## 4. SECS 訊息範例與解讀

以下為各 SxFy 的 SECS-II 資料結構範例。資料型別代碼：
`L`=List、`A`=ASCII、`B`/`Boolean`=Binary/布林、`U4`=4-byte 無號整數、`F4`/`F8`=4/8-byte 浮點數；中括號內數字為元素個數。

### S1F2 — 上線資料（回覆 Are You Online）
```
S1F2
<L[2]
  <A[14] "AQST_SATELLITE">   # 設備型號名稱
  <A[8]  "1.0.4.7i">         # 軟體版本
>
```

### S1F3 / S1F4 — 設備狀態查詢／回覆
```
S1F3 W
<L[1]
  <U4[1] 1110806001>         # 查詢的 SVID（Buffer1 / O2 sensor / Ch_A / O2 ppm）
>
.
S1F4
<L[1]
  <F8[1] 0.05064>            # 對應 SVID 的量測值
>
```

### S1F13 / S1F14 — 建立通訊
```
S1F13 W
<L[2]
  <A[9] "SATELLITE">         # 設備型號
  <A[6] "1.4.7L">            # 軟體版本
>
.
S1F14
<L[1]
  <Boolean[1] 0x00>          # 0x00 = 接受建立通訊
>
```

### S2F18 — 日期時間回覆
```
S2F18
<L[1]
  <A[12] "230530104725">     # YYMMDDhhmmss = 2023/05/30 10:47:25
>
```

### S2F23 / S2F24 — 追蹤初始化設定（Trace Initialize）
```
S2F23 W
<L[5]
  <U4[1] 12>                 # TRID：Trace 請求 ID
  <A[6] "001000">            # DSPER：取樣週期（時間）
  <U4[1] 99>                 # TOTSMP：總取樣次數
  <U4[1] 1>                  # REPGSZ：每次回報的取樣數
  <L[4]                      # 要追蹤的 SVID 清單
    <U4[1] 1020102018>
    <U4[1] 1020102028>
    <U4[1] 1020102032>
    <U4[1] 1020102040>
  >
>
.
S2F24
<B[1] 0x00>                  # 0x00 = 接受
```

### S2F31 / S2F32 — 日期時間設定
```
S2F31 W
<A[16] "2023092113521300">   # YYYYMMDDhhmmsscc = 2023/09/21 13:52:13.00
.
S2F32
<B[1] 0x00>                  # 0x00 = 接受
```

### S2F33 / S2F34 — 定義報告（Define Report）
```
S2F33 W
<L[2]
  <U4[1] 0>                  # DATAID
  <L[2]                      # 報告定義清單
    <L[2]
      <U4[1] 1>              # RPTID = 1
      <L[4]                  # 此報告包含的 SVID
        <U4[1] 1020102902>
        <U4[1] 1020102905>
        <U4[1] 1020102906>
        <U4[1] 1020102018>
      >
    >
    <L[2]
      <U4[1] 2>              # RPTID = 2
      <L[4]
        <U4[1] 1020102902>
        <U4[1] 1020102905>
        <U4[1] 1020102906>
        <U4[1] 1030103018>
      >
    >
  >
>
```
> 回覆 `S2F34 <B[1] 0x00>` 表示接受。

### S2F35 / S2F36 — 連結事件與報告（Link Event Report）
```
S2F35 W
<L[2]
  <U4[1] 0>                  # DATAID
  <L[2]                      # CEID 與 RPTID 的連結清單
    <L[2]
      <U4[1] 202001>         # CEID = 202001
      <L[1]
        <U4[1] 1>            # 連結 RPTID = 1
      >
    >
    <L[2]
      <U4[1] 203001>         # CEID = 203001
      <L[1]
        <U4[1] 2>            # 連結 RPTID = 2
      >
    >
  >
>
```
> 回覆 `S2F36 <B[1] 0x00>` 表示接受。

### S2F37 / S2F38 — 啟用／停用事件報告
```
S2F37 W
<L[2]
  <Boolean[1] 0xFF>          # 0xFF = 啟用（0x00 = 停用）
  <L[2]                      # 要啟用的 CEID 清單
    <U4[1] 202001>
    <U4[1] 203001>
  >
>
.
S2F38
<B[1] 0x00>                  # 0x00 = 接受
```

### S2F41 / S2F42 — 主機命令下達（Host Command Send）
```
S2F41 W
<L[2]
  <A[12] "ChamberStart">     # RCMD：遠端命令名稱
  <L[4]                      # 參數（CPNAME / CPVAL）清單
    <L[2] <A[11] "ChamberName">  <A[5]  "CELL1">          >
    <L[2] <A[12] "ProcessJobID"> <A[14] "xxx0nnnF-10-01"> >
    <L[2] <A[8]  "RecipeID">     <A[4]  "Test">           >
    <L[2] <A[6]  "SlotNo">       <A[2]  "10">             >
  >
>
.
S2F42
<L[2]
  <B[1] 0x00>                # HCACK = 0x00：命令已接受
  <L[0] >                    # 無錯誤參數
>
```

### S5F1 / S5F2 — 警報回報（Alarm Report）
```
S5F1 W
<L[3]
  <B[1] 0x80>                # ALCD：警報碼（bit7=1 表示警報發生/set）
  <A[16] "50201018 ">        # ALID：警報 ID
  <A[480] "CHB2 M18 too high ">  # ALTX：警報文字描述
>
.
S5F2
<L[1]
  <Boolean[1] 0x00>          # 0x00 = 接受
>
```

### S6F1 / S6F2 — 追蹤資料回傳（Trace Data Send）
```
S6F1 W
<L[4]
  <U4[1] 11>                 # TRID：Trace ID
  <U4[1] 1>                  # SMPLN：取樣序號
  <A[14] "20230811090732">   # STIME：時間戳 2023/08/11 09:07:32
  <L[6]                      # 各 SVID 的量測值
    <F4[1] 1.6e-009>
    <F4[1] 1.37e-010>
    <F4[1] 1.19e-010>
    <F4[1] 1.6e-009>
    <F4[1] 1.37e-010>
    <F4[1] 1.19e-010>
  >
>
.
S6F2
<L[1]
  <Boolean[1] 0x00>          # 0x00 = 接受
>
```

### S6F11 / S6F12 — 事件報告回傳（Event Report Send）
```
S6F11 W
<L[3]
  <U4[1] 0>                  # DATAID
  <U4[1] 202001>             # CEID：觸發的事件 ID
  <L[1]                      # 報告清單
    <L[2]
      <U4[1] 1>              # RPTID = 1
      <L[5]                  # 該報告內各項目的值
        <A[16] "2023092114024100">   # 時間戳
        <A[7]  "P1PL001">            # 例如：Recipe / Job 名稱
        <A[5]  "TEST1">
        <F4[1] 4.71e-012>
        <F4[1] 4.7e-012>
      >
    >
  >
>
.
S6F12
<B[1] 0x00>                  # 0x00 = 接受
```

---

## 5. OES Leak Monitor 警報定義（ALID）

> 本節與 §6 同為規格定義草案（見 §1.4 `ss=27` 開頭說明）。

編碼規則沿用 SVID 的前段：

```
ALID = 1 + cc + ss + nnn      （共 8 碼數字）
```

`cc` 為腔體代號、`ss` 固定為 `27`、`nnn` 為警報序號。於 S5F1 的 ALID 欄位以 ASCII 字串填入（與 §4 現有範例的型別一致）。

> **範例：** `10227002` = `1` + `cc=02`（Ch_2）+ `ss=27` + `nnn=002` → Ch_2 的 OES 洩漏警報。

### 5.1 全域警報（`nnn` = 001 ~ 099）

| nnn | 警報名稱 | 觸發條件 | 解除條件 |
|---|---|---|---|
| 001 | Leak warning | 複合狀態（VID 007）升至 2 | 降回 1 |
| 002 | Leak alarm | 複合狀態升至 3（已鎖存） | 操作者確認後降回 |
| 003 | Low signal | 任一啟用槽位進入 LowSignal（狀態 4） | 訊噪比回升 |
| 004 | No baseline | 無可用 Golden Run 基準（VID 012 = 0） | 擷取到可用基準 |
| 005 | Calibration baseline mismatch | VID 006 = 2，洩漏率估計暫停 | 選回相符基準或重新校正 |
| 006 | Not calibrated | VID 006 = 0，僅有定性判斷 | 建立／選定校正 |
| 007 | Acquisition parameter mismatch | VID 015 = 1 | 參數改回或重新擷取基準 |
| 008 | Plasma gate unavailable | VID 022 = 0，無法判定電漿有無 | 觸發訊號恢復可量測 |
| 009 | Frame dropout high | 掉幀率超過設備端門檻 | 恢復正常 |
| 010 | Golden Run capture rejected | 擷取視窗未取得任何可用基準（原基準維持不變） | 下一次擷取成功 |
| 011 | Recording disarmed | 記錄器未啟用，或與存檔設定不一致（資料未落檔） | 啟用記錄 |
| 012 | OES connection lost | 光譜儀連線在曾經連上之後中斷 | 重新連上 |
| 013 | Acquisition error | 設備回報擷取錯誤 | 離開錯誤狀態 |
| 014 | Data write failure | 資料檔寫入失敗 | 下一次成功開檔 |
| 015 ~ 099 | Reserved | | |

> 012 ~ 014 是設備本身的故障，與量測數據無關，因此**不受「測試／回放模式不上報」的抑制**：
> 光譜儀掉線或 CSV 寫不進去，是關於這台機器的事實。
> 012 是**轉態**而非狀態——設備啟動時本來就未連線，那不是故障。

### 5.2 槽位警報（`nnn` = 101 ~ 130）

| nnn | 警報名稱 | 對應槽位 |
|---|---|---|
| 101 ~ 110 | Ratio N warning | N = nnn − 100 |
| 111 ~ 120 | Ratio N alarm | N = nnn − 110 |
| 121 ~ 130 | Ratio N low signal | N = nnn − 120 |

### 5.3 ALCD 與 ALTX 慣例

- ALCD 的 bit7 = 1 表示警報發生（set）、= 0 表示解除（clear），與 §4 的範例一致。
- ALTX 建議帶入腔體、槽位、譜線與當下數值，讓 Host 端的警報紀錄本身即可讀：
  - `CH2 OES Ratio2 (NO 237.0 / Ar 750.4) ALARM 158% of baseline`
  - `CH2 OES leak rate 1.8e-004 mbar-L/s +/-3.0e-005 (conf 0.72)`
  - `CH2 OES Ratio4 LOW SIGNAL snr 3.1 < 5.0`

**範例（Ch_2，Ratio 2 進入警報）**
```
S5F1 W
<L[3]
  <B[1] 0x80>                # ALCD：bit7=1，警報發生
  <A[16] "10227112 ">        # ALID：Ch_2 / ss=27 / nnn=112 → Ratio 2 alarm
  <A[480] "CH2 OES Ratio2 (NO 237.0 / Ar 750.4) ALARM 158% of baseline ">
>
.
S5F2
<L[1]
  <Boolean[1] 0x00>          # 0x00 = 接受
>
```

---

## 6. OES Leak Monitor 事件與報告定義（CEID / RPTID）

編碼形式與 ALID 相同，`nnn` 自 **501** 起編，使兩者在人眼上一望即知：

```
CEID = 1 + cc + ss + nnn      （nnn 自 501 起）
```

> **範例：** `10227501` = Ch_2 的 OES Leak Monitor「洩漏狀態改變」事件。

### 6.1 事件清單

| nnn | 事件 | 觸發時機 |
|---|---|---|
| 501 | Leak state changed | 複合警報等級（VID 007）改變，含進出 Idle |
| 502 | Alarm acknowledged | 操作者確認並解除鎖存（報告帶出操作者帳號） |
| 503 | Golden Run capture started | 開始擷取基準 |
| 504 | Golden Run capture finished | 擷取結束，帶出是否採用、以及未取得基準的槽位數 |
| 505 | Active Golden Run changed | 切換現用基準（配對的校正隨之改變） |
| 506 | Calibration status changed | 校正啟用／暫停／清除（VID 006 改變） |
| 507 | Ratio configuration reloaded | 比值設定重新載入 — **槽位對應可能已改變，Host 應重讀身分類 VID（位移 16 ~ 24）** |
| 508 | Acquisition started | 開始採集光譜 |
| 509 | Acquisition stopped | 停止採集 |
| 510 | Recording session changed | CSV 記錄開啟／關閉 |
| 511 ~ 599 | Reserved | |

### 6.2 建議報告（RPTID）

以下 RPTID 僅為建議編號，實際由 Host 於 S2F33 定義；VID 欄位以 Ch_2（`cc=02`）為例。

| RPTID | 內容 | 包含的 VID |
|---|---|---|
| 27001 | 洩漏率摘要 | 001, 002, 003, 004, 005, 006 |
| 27002 | 複合狀態 | 007, 008, 009, 010, 011, 012, 015, 016, 021, 022 |
| 27011 ~ 27020 | 槽位 N 即時量（N = RPTID − 27010） | 位移 01, 03, 04, 05, 06, 14, 15 |
| 27021 ~ 27030 | 槽位 N 身分與門檻（N = RPTID − 27020） | 位移 16, 17, 18, 19, 20, 21, 22, 23, 24, 10, 11, 12, 13 |

> 建議把 27001 / 27002 連到 `501`（狀態改變）與 `506`；把 27021 ~ 27030 連到 `507`（設定重載）與 `505`，其餘時間不需重讀。持續趨勢請走 S2F23 / S6F1 的 Trace，不要用事件報告逐幀推送。

### 6.3 訊息範例（以 Ch_2、`cc=02` 為例）

**定義報告（S2F33）**
```
S2F33 W
<L[2]
  <U4[1] 0>                  # DATAID
  <L[2]
    <L[2]
      <U4[1] 27001>          # RPTID = 27001：洩漏率摘要
      <L[6]
        <U4[1] 1022700001>   # Leak rate
        <U4[1] 1022700002>   # Leak rate sigma
        <U4[1] 1022700003>   # Leak rate confidence
        <U4[1] 1022700004>   # Leak rate valid
        <U4[1] 1022700005>   # Out of calibrated range
        <U4[1] 1022700006>   # Calibration status
      >
    >
    <L[2]
      <U4[1] 27011>          # RPTID = 27011：槽位 1 即時量
      <L[7]
        <U4[1] 1022700101>   # Ratio1 monitored value      （位移 01）
        <U4[1] 1022700103>   # Ratio1 percent of baseline   （位移 03）
        <U4[1] 1022700104>   # Ratio1 slope                 （位移 04）
        <U4[1] 1022700105>   # Ratio1 state                 （位移 05）
        <U4[1] 1022700106>   # Ratio1 signal SNR            （位移 06）
        <U4[1] 1022700114>   # Ratio1 leak rate             （位移 14）
        <U4[1] 1022700115>   # Ratio1 used in fusion        （位移 15）
      >
    >
  >
>
.
S2F34
<B[1] 0x00>                  # 0x00 = 接受
```

**連結事件與報告（S2F35）**
```
S2F35 W
<L[2]
  <U4[1] 0>                  # DATAID
  <L[1]
    <L[2]
      <U4[1] 10227501>       # CEID：Ch_2 OES 洩漏狀態改變
      <L[2]
        <U4[1] 27001>
        <U4[1] 27002>
      >
    >
  >
>
.
S2F36
<B[1] 0x00>
```

**啟用事件報告（S2F37）**
```
S2F37 W
<L[2]
  <Boolean[1] 0xFF>          # 0xFF = 啟用
  <L[3]
    <U4[1] 10227501>         # 洩漏狀態改變
    <U4[1] 10227502>         # 操作者確認
    <U4[1] 10227507>         # 比值設定重載
  >
>
.
S2F38
<B[1] 0x00>
```

**事件報告回傳（S6F11，複合狀態由 Warning 升為 Alarm）**
```
S6F11 W
<L[3]
  <U4[1] 0>                  # DATAID
  <U4[1] 10227501>           # CEID：Ch_2 OES 洩漏狀態改變
  <L[1]
    <L[2]
      <U4[1] 27001>          # RPTID = 27001
      <L[6]
        <F4[1] 1.8e-004>     # Leak rate Q̂（mbar·L/s）
        <F4[1] 3.0e-005>     # Leak rate sigma
        <F4[1] 0.72>         # Confidence
        <U4[1] 1>            # Leak rate valid
        <U4[1] 0>            # Out of calibrated range
        <U4[1] 1>            # Calibration status = 有效
      >
    >
  >
>
.
S6F12
<B[1] 0x00>                  # 0x00 = 接受
```

**追蹤設定（S2F23，每 10 秒取樣一次洩漏率與槽位 1 的基準百分比）**
```
S2F23 W
<L[5]
  <U4[1] 27>                 # TRID
  <A[6] "001000">            # DSPER：取樣週期
  <U4[1] 0>                  # TOTSMP：0 = 持續
  <U4[1] 1>                  # REPGSZ
  <L[4]
    <U4[1] 1022700001>       # Leak rate
    <U4[1] 1022700007>       # Composite leak level
    <U4[1] 1022700103>       # Ratio1 percent of baseline
    <U4[1] 1022700133>       # Ratio2 percent of baseline
  >
>
.
S2F24
<B[1] 0x00>
```

---

## 附註

- 訊息範例中每則以 `.` 作為結束符號。
- `W` 位元（Wait bit）出現時，表示送出方要求對方回覆對應的偶數 Function。
- 時間戳格式：`YYMMDDhhmmss`（12 碼）、`YYYYMMDDhhmmss`（14 碼）、`YYYYMMDDhhmmsscc`（16 碼，末兩碼為百分之一秒）。
- 部分欄位解讀（如 S6F11 報告內各項目意義）依 S2F33 的報告定義而定，實際欄位順序以 Define Report 設定為準。
- ALID 與 CEID 雖同為 `1ccss` + 3 碼的形式，但屬不同命名空間；OES Leak Monitor 的 ALID 使用 001 ~ 130、CEID 自 501 起，兩者不會在人眼上混淆。
- §1.4 的 `ss=27` 全域量（VID 001 ~ 026）、§5.1 的 ALID 001／002／012 ~ 014、§6.1 的 CEID 502／508／509 已由設備端實作；比值槽位 VID 101 ~ 400、其餘 ALID／CEID 與遠端命令 S2F41 仍為規格定義草案。

*本文件由原始 Word 規格書「Satellite SECS Specification」整理轉換而成；v2 增補 OES Leak Monitor（`ss=27`）章節，v2.1 標註其實作狀態並增訂 ALID 012 ~ 014。*
