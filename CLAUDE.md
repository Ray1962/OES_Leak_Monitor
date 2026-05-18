# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & run

- WPF, .NET 8, Windows-only, x64-only (no AnyCPU). Configurations: `Debug|x64`, `Release|x64`.
- Build: `dotnet build src/OES_Leak_Monitor/OES_Leak_Monitor.csproj -c Debug` (or open `OES_Leak_Monitor.sln` in Visual Studio 2022).
- Run: `dotnet run --project src/OES_Leak_Monitor/OES_Leak_Monitor.csproj`.
- No test project exists yet.

### Standalone publish (no .NET install required on the target PC)

`Properties/PublishProfiles/SelfContained-win-x64.pubxml` produces a single self-contained `.exe` (the .NET 8 runtime is bundled in). Three ways to run it: double-click `publish.cmd` in the repo root; `dotnet publish src/OES_Leak_Monitor/OES_Leak_Monitor.csproj -p:PublishProfile=SelfContained-win-x64`; or in Visual Studio right-click the project → Publish → that profile. The `/publish` Claude slash command does the same. Output: `src/OES_Leak_Monitor/bin/Publish/win-x64/`.

The `.exe` bundles all managed code plus the .NET 8 runtime, but native DLLs are not embedded — the output folder also holds the WPF native DLLs (`wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`, `vcruntime140_cor3.dll`) and the OES native DLLs (`UserApplication.dll`, `SiUSBXp.dll`). Ship the whole folder together — those DLLs are loose next to the `.exe` on purpose (the `DllResolver` only searches the app base directory; see the native-DLL note below). Do not enable `IncludeNativeLibrariesForSelfExtract` — it would bundle the OES DLLs into the exe and break hardware connect.

### NuGet feed prerequisite

`nuget.config` pins a local feed at `C:\Users\infor\source\repos\Ray1962\LocalPackages` and clears default sources before adding nuget.org. Restore will fail if the three `Aqst.*` nupkgs (`Aqst.OesApp.Core`, `Aqst.OesApp.Wpf`, `Aqst.OesSpectrometer`) are not present there. Those packages are produced by the sibling repo `Ray1962/DualOes_PlasmaMonitor`; rebuild and re-pack there if you bump versions.

### Native DLL flattening (do not remove)

`Aqst.OesSpectrometer` ships `UserApplication.dll` and `SiUSBXp.dll` under `runtimes\win-x64\native\`, but the package's `DllResolver` only searches the app base directory. The `FlattenOesNativeDlls` MSBuild target in `OES_Leak_Monitor.csproj` copies those two DLLs into `$(OutDir)` (and `$(PublishDir)`) after Build/Publish. If hardware connect silently falls back to test mode, check that those files reached the output root.

## Architecture

This is a **single-OES** leak-monitoring fork of a previously dual-OES codebase. Most of the UI shell, device control, logging, access control, and plot/configuration panels live in the `Aqst.OesApp.*` NuGets — the project here is mostly composition + a custom Recordings tab.

### What comes from `Aqst.OesApp.Core` / `Aqst.OesApp.Wpf` (referenced, not defined here)

- `DeviceViewModel`, `LoggerViewModel`, `LogViewerViewModel`, `SystemLogger`, `DualIntensityLogger`, `IntensityCsvWriter`
- `AccessControlService`, `UserRole` (Guest < Operator < Engineer < Admin), `LoginDialog`, `UserManagementDialog`, `AccessControlConfig`
- `OesAppPaths` — resolves per-user AppData subfolders (`Config`, `Log`, `Data`) under the app folder name
- `RelayCommand`, `DeviceSettings`, `LoggerSettings`, `LogSeverity`
- WPF user controls bound in `MainWindow.xaml`: `DevicePanel`, `ConfigurationPanel`, `LoggerPanel`, `LogViewerPanel`
- Global `using Aqst.OesApp.Core;` and `using Aqst.OesApp.Wpf;` are injected via the csproj, so these types appear unqualified throughout.

Treat that NuGet surface as the framework. When something looks "missing," it is almost certainly defined in those packages, not here.

### What is defined in this project

- `MainViewModel` — wires a single `DeviceViewModel` (the `DeviceProfiles` array has one entry tagged `"OES1"`), the `DualIntensityLogger`, the `RecordingsViewModel`, and the toolbar role badge. Per-device Connect/Disconnect/Start/Stop live in the embedded `DevicePanel` header (Monitor tab); the LoggerPanel exposes Start Save / Stop Save. The top toolbar holds only the title, status message, and the user/role controls — there is no longer a "Both" command that bundles device + logger.
- `AppSettings` + `SettingsService` — `settings.json` in `OesAppPaths.ConfigDirectory`. `AppSettings.OnDeserialized` migrates the legacy `device1`/`device2` keys into the `devices` list, and pads the list to at least 2 entries (legacy schema lock — do not shrink to 1).
- `Recording` / `RecordingGroup` — parse CSVs produced by `IntensityCsvWriter` under `{baseDir}\YYYYMM\DD\{prefix}_{tag}_{MMddHHmmss}[_N].csv`. The group key is `{prefix}|{sessionStart}|{rotation}` (dual-OES file layout preserved) but only the `"OES1"`-tagged file populates the group; any historical `"OES2"` siblings on disk are ignored at scan time. Year is recovered from the parent folder.
- `RecordingsViewModel` + `RecordingsPanel` — Recordings tab: scans the data folder, builds OxyPlot line / heatmap / frame-spectrum views, supports two-row compare mode (two sessions overlaid — orthogonal to device count), PNG export, clipboard copy, notes, search.
- **Leak Monitor** — an actinometry-based air-leak detector on its own tab. `LineIntensityExtractor` + `LineRegion` pull baseline-subtracted line intensities out of each `SpectrumSample`; `LeakMonitorEngine` divides signal lines by the N₂ 337.1 nm reference into ratios (`R_O`, `R_OH`, `R_NO`, `R_Ar`), runs a per-ratio `RatioMonitor` (EMA + two-level threshold state machine + sustained-confirmation + latched alarm), and computes a composite `LeakAlarmLevel`. A "Golden Run" captures the leak-free baseline (mean/σ per ratio) and is persisted to `settings.json` the moment it completes. `LeakMonitorViewModel` / `LeakMonitorPanel` show the per-ratio rows, the % -of-baseline trend chart, and the capture / acknowledge commands. `SpectralLineCatalog` is the static reference line table. Engine alarm transitions and Golden Run captures bridge into `SystemLogger`. All of this lives in this project, not the NuGets.

### Conventions worth knowing

- The single device keeps the tag `"OES1"` (not `"OES"`) so the existing recording file-pairing infrastructure, filename scan, and `RecordingGroup` logic keep working unchanged. Don't rename it.
- The Configuration tab is gated to Engineer+; window close is blocked for Guest. Both gates are enforced in `MainWindow.xaml.cs` (`MainTabControl_SelectionChanged`, `MainWindow_Closing`).
- Sample flow: each `DeviceViewModel.SpectrumAvailable` event is forwarded to `DualIntensityLogger.ProcessSample(slot, sample)`. The logger's threshold state machine decides when to open/close CSV writers; lifecycle events (`StateChanged`, `ErrorOccurred`, `FilesChanged`) are bridged into `SystemLogger` for the audit log.
- `MainViewModel.ConnectBothAsync` calls `OesDiscovery.OpenAllDevices()` once, then hands one handle to each slot via `AttachAsync`; slots with `ForceTestMode=true` or that run out of handles fall back to `ConnectStandaloneAsync`. Unused handles are explicitly closed.
- Settings are saved atomically (temp file + `File.Move(..., overwrite:true)`). The `AccessControl` user list is re-read from disk on each persist so user-management edits don't clobber unsaved Configuration-tab edits, and vice versa.

## Related skills

Three Skill-tool skills are tailored to this stack — invoke them when relevant rather than reinventing:

- `create-oes` — instantiating `OesParameters` / `OesSpectrometer`, wiring DLL paths, the `FlattenOesNativeDlls` build glue.
- `use-multi-oes` — `OesDiscovery.OpenMultiDevices` / `AttachAsync` patterns (relevant if this app ever returns to multi-device).
- `log-spectrometer-csv` — the threshold-triggered CSV logger state machine (Idle / WaitingToStart / Saving / WaitingToStop) that `DualIntensityLogger` implements.
