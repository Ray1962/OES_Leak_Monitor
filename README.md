# OES_Leak_Monitor

A Windows desktop application for OES-based leak monitoring, ratio configuration, recording review, and spectral data analysis.  
一個用於 **OES（Optical Emission Spectroscopy，光學發射光譜）** 漏氣監測、比值設定、紀錄檢視與光譜資料分析的 Windows 桌面應用程式。

---

## Overview | 專案概述

`OES_Leak_Monitor` is a WPF desktop application designed for **single-device OES monitoring** workflows. It integrates real-time monitoring, leak detection, baseline capture, ratio configuration, data review, and historical recording inspection into one Windows application.  
`OES_Leak_Monitor` 是一個為**單一 OES 裝置監測流程**設計的 WPF 桌面應用程式，整合了即時監控、漏氣偵測、基準建立、比值設定、資料檢視與歷史紀錄分析等功能。

This project is especially suitable for scenarios where operators or engineers need to track spectral behavior, compare ratio trends, and identify leak-related anomalies using a consistent workflow.  
此專案特別適合用於操作人員或工程師需要追蹤光譜變化、比較比值趨勢，並透過一致化流程判斷漏氣異常的應用場景。

---

## Use Cases | 適用情境

### English
This application is suitable for:

- OES-based process monitoring
- Leak detection workflows using spectral ratio analysis
- Golden Run baseline setup for known-good conditions
- Review of recorded spectral sessions
- Ratio trend inspection and historical comparison
- Engineering workflows that require controlled configuration and operator access separation

### 中文
本應用程式適合用於：

- 以 OES 為基礎的製程監控
- 使用光譜比值分析進行漏氣偵測
- 建立無漏氣狀態下的 Golden Run 基準
- 檢視歷史錄製光譜資料
- 觀察 ratio 趨勢與進行歷史比較
- 需要區分工程設定與操作權限的工作流程

---

## Main Features | 主要功能

### 1. Real-time Monitor | 即時監控
Provides the main monitoring view for the connected OES device.  
提供已連接 OES 裝置的主要即時監控畫面。

### 2. Leak Monitor | 漏氣監測
Supports leak detection workflows based on spectral line intensity extraction and ratio evaluation.  
支援根據光譜線強度擷取與比值分析進行漏氣監測。

Key capabilities | 主要能力：

- Live overall status display  
  即時整體狀態顯示
- Ratio-by-ratio status tracking  
  逐項比值狀態追蹤
- Golden Run baseline selection  
  Golden Run 基準選擇
- Golden Run capture from a leak-free process  
  由無漏氣製程建立 Golden Run 基準
- Alarm acknowledgment workflow  
  警報確認與解除流程
- Trend visualization and zoom controls  
  趨勢圖顯示與縮放控制

### 3. Ratio Setup | 比值設定
Allows staged editing of species-ratio configuration used by the monitoring logic.  
提供用於監測邏輯的光譜比值設定介面，支援分階段編輯與儲存。

### 4. Ratio Review | 比值檢視
Provides tools for reviewing ratio CSV outputs generated during monitoring.  
提供檢視監測期間產生之 ratio CSV 資料的工具。

### 5. Recordings Review | 錄製資料檢視
Supports browsing and analyzing previously recorded spectral sessions.  
支援瀏覽與分析先前錄製的光譜 session。

### 6. Configuration & Access Control | 設定與權限控制
Includes role-based access control and separates operational functions from engineering-level settings.  
包含角色式權限控制，區分操作層級功能與工程設定功能。

---

## Screenshots | 畫面截圖

> You can replace the placeholder image paths below after uploading screenshots into the repository, for example under `docs/images/`.  
> 你可以在之後將實際截圖放入倉庫中，例如 `docs/images/` 目錄，再把以下圖片路徑替換掉。

### Main Window | 主畫面
![Main Window](docs/images/main-window.png)

### Leak Monitor | 漏氣監測畫面
![Leak Monitor](docs/images/leak-monitor.png)

### Ratio Setup | 比值設定畫面
![Ratio Setup](docs/images/ratio-setup.png)

### Ratio Review | 比值檢視畫面
![Ratio Review](docs/images/ratio-review.png)

### Recordings Review | 錄製資料檢視畫面
![Recordings Review](docs/images/recordings-review.png)

---

## Quick Start | 快速開始

### English

1. Install **.NET 8 SDK**
2. Ensure required local `Aqst.*` NuGet packages are available
3. Open the solution in **Visual Studio 2022** or use the .NET CLI
4. Build the project in `x64`
5. Run the application and connect the OES device
6. Configure ratios and establish a Golden Run baseline before production monitoring

### 中文

1. 安裝 **.NET 8 SDK**
2. 確認本機可取得所需的 `Aqst.*` NuGet 套件
3. 使用 **Visual Studio 2022** 開啟 solution，或使用 .NET CLI
4. 以 `x64` 模式建置專案
5. 執行應用程式並連接 OES 裝置
6. 在正式監控前先完成 ratio 設定與 Golden Run 基準建立

---

## Technology Stack | 技術架構

- **.NET 8**
- **WPF**
- **C#**
- **OxyPlot.Wpf** for charting / 用於圖表顯示
- `Aqst.OesApp.Core`
- `Aqst.OesApp.Wpf`
- `Aqst.OesSpectrometer`

The project targets `net8.0-windows`, uses WPF, and is configured as **x64-only**.  
本專案以 `net8.0-windows` 為目標框架，使用 WPF，並設定為 **x64-only**。 citeturn0fetch5

---

## Project Structure | 專案結構

```text
OES_Leak_Monitor/
├── OES_Leak_Monitor.sln
├── publish.cmd
├── nuget.config
└── src/
    └── OES_Leak_Monitor/
        ├── App.xaml
        ├── MainWindow.xaml
        ├── MainViewModel.cs
        ├── LeakMonitorEngine.cs
        ├── LeakMonitorViewModel.cs
        ├── LeakMonitorPanel.xaml
        ├── RatioSetupViewModel.cs
        ├── RatioSetupPanel.xaml
        ├── RatioReviewViewModel.cs
        ├── RatioReviewPanel.xaml
        ├── RecordingsViewModel.cs
        ├── RecordingsPanel.xaml
        ├── AppSettings.cs
        ├── SettingsService.cs
        └── OES_Leak_Monitor.csproj
```

The current repository structure centers on a single WPF application project under `src/OES_Leak_Monitor`.  
目前倉庫的結構核心是一個位於 `src/OES_Leak_Monitor` 下的單一 WPF 應用程式專案。 citeturn0fetch3turn0fetch5

---

## Build Requirements | 建置需求

- **Windows**
- **.NET 8 SDK**
- **x64 environment**
- **Visual Studio 2022** or `dotnet` CLI

This project is **Windows-only** and **x64-only**.  
本專案僅支援 **Windows**，且只支援 **x64** 環境。 citeturn0fetch3turn0fetch5

---

## Build | 建置方式

### Using Visual Studio | 使用 Visual Studio

Open:  
開啟：

```text
OES_Leak_Monitor.sln
```

Build with:  
建置組態：

- `Debug | x64`
- or `Release | x64`

### Using .NET CLI | 使用 .NET CLI

```bash
dotnet build src/OES_Leak_Monitor/OES_Leak_Monitor.csproj -c Debug
```

This build workflow is documented in the repository guidance.  
此建置流程已在倉庫說明中明確記錄。 citeturn0fetch3

---

## Run | 執行方式

```bash
dotnet run --project src/OES_Leak_Monitor/OES_Leak_Monitor.csproj
```

The application starts from `MainWindow.xaml`.  
應用程式會從 `MainWindow.xaml` 啟動。 citeturn0fetch3turn0fetch5

---

## Publish | 發佈方式

The repository includes a publish script:  
倉庫中內建發佈腳本：

```text
publish.cmd
```

It produces a **self-contained Windows x64 executable**, so the target PC does **not** need a separate .NET runtime installation.  
它會產生 **self-contained 的 Windows x64 執行版本**，因此目標電腦**不需要另外安裝 .NET runtime**。 citeturn0fetch4

### Publish command | 發佈指令

```bash
publish.cmd
```

### Output folder | 輸出資料夾

```text
src\OES_Leak_Monitor\bin\Publish\win-x64
```

When deploying, ship the **entire `win-x64` folder**, not only the `.exe`, because native DLLs are required next to the executable.  
部署時請提供整個 `win-x64` 資料夾，而不是只複製 `.exe`，因為執行時仍需要一併提供原生 DLL。 citeturn0fetch4

---

## NuGet / Dependency Notes | 套件與相依性說明

This project uses a local NuGet feed configured in `nuget.config` in addition to `nuget.org`.  
本專案除了 `nuget.org` 外，也透過 `nuget.config` 使用本機 NuGet feed。

Important dependencies include:  
主要依賴包括：

- `Aqst.OesApp.Core`
- `Aqst.OesApp.Wpf`
- `Aqst.OesSpectrometer`

If the required local packages are missing, restore/build/publish may fail.  
如果缺少所需本機套件，則 restore / build / publish 可能失敗。 citeturn0fetch3turn0fetch4turn0fetch5

---

## Native DLL Notes | 原生 DLL 說明

`Aqst.OesSpectrometer` relies on native DLLs such as:  
`Aqst.OesSpectrometer` 依賴以下原生 DLL：

- `UserApplication.dll`
- `SiUSBXp.dll`

The project file includes custom logic to flatten these native DLLs into the output directory after build/publish so runtime resolution works correctly.  
專案檔中包含自訂邏輯，會在 build / publish 後將這些原生 DLL 複製到輸出目錄，以確保執行時可以正確解析。 citeturn0fetch5

---

## Configuration | 設定檔

Application settings are persisted through settings services into `settings.json`.  
應用程式設定會透過設定服務保存到 `settings.json`。

Some changes are saved immediately but only take effect after stopping and restarting OES acquisition.  
部分變更會立即寫入，但必須在停止並重新啟動 OES 擷取後才會生效。 citeturn0fetch3

---

## Current Scope | 目前範圍

This project currently focuses on:

- Single-device OES monitoring  
  單一 OES 裝置監控
- Leak monitoring workflows  
  漏氣監測流程
- Ratio configuration and review  
  比值設定與檢視
- Recording inspection and review  
  錄製資料檢視
- Windows desktop operation  
  Windows 桌面應用

There is currently **no separate test project** in the repository.  
目前此倉庫中**尚未包含獨立的測試專案**。 citeturn0fetch3

---

## Future Improvements | 後續改進方向

Possible next steps:

- Add real screenshots into `docs/images/`
- Add sample data for demo/testing
- Add automated tests for core logic
- Add a more detailed operator guide
- Add architecture diagrams
- Add troubleshooting documentation

可能的下一步包括：

- 將實際截圖加入 `docs/images/`
- 加入示範 / 測試資料
- 為核心邏輯增加自動化測試
- 補充更完整的操作手冊
- 增加架構圖
- 增加疑難排解文件

---

## Repository Status | 專案狀態

**Status: In Progress**  
**狀態：持續開發中**

This repository is under active development and ongoing refinement.  
此倉庫目前仍持續開發與整理中。

---

## Notes | 備註

This README is intended to provide a clearer technical and presentation-oriented overview for maintenance, onboarding, and project communication.  
本 README 的目的是提供更清楚、同時兼顧技術說明與展示用途的專案概覽，方便維護、交接、導入與溝通。
