# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Read [CONTEXT.md](CONTEXT.md) first. It is the definition of record for this project's own
vocabulary; where a term's meaning here differs from the definition there, CONTEXT.md wins.
This file explains how things are built and why they must stay that way — it is not where a
word is defined.

## Build & run

- WPF, .NET 8, Windows-only, x64-only (no AnyCPU). Configurations: `Debug|x64`, `Release|x64`.
- Build: `dotnet build src/OES_Leak_Monitor/OES_Leak_Monitor.csproj -c Debug` (or open `OES_Leak_Monitor.sln` in Visual Studio 2022).
- Run: `dotnet run --project src/OES_Leak_Monitor/OES_Leak_Monitor.csproj`.
- Tests: `dotnet test tests/OES_Leak_Monitor.Tests/OES_Leak_Monitor.Tests.csproj -c Debug` (xUnit, `net8.0-windows`/x64 to match the app it references). **Seven of them need a recording that is not in the repo** and report as *skipped* without it. What they cover, the recorded-run tests and how to verify the UI by hand (everything with a window on it is verified by running the app — the `run-app` skill drives it): `docs/dev/testing.md`.
- Do not add the test project with `dotnet sln add` — it writes `Any CPU` and `x86` configurations into the solution, which this repo deliberately does not have. Edit `OES_Leak_Monitor.sln` by hand.
- **From the WSL shell there is no native `dotnet`** — this is a Windows/WPF build. Invoke the Windows SDK explicitly, e.g. `"/mnt/c/Program Files/dotnet/dotnet.exe" build src/OES_Leak_Monitor/OES_Leak_Monitor.csproj -c Debug` (same for `publish`). The bare `dotnet …` commands above assume a Windows terminal / Visual Studio.
- Publish a delivery folder: `publish.cmd` (or the `/publish` command). Before changing it, the publish profile, or any csproj target that copies files into the output — `EmptyPublishDirBeforePublish`, `FlattenOesNativeDlls`, `CopyVcRuntime`, `CopyOperatorDocs`, none of which may be removed — read `docs/dev/build-and-publish.md`. Never `set` an MSBuild property name (`OUTDIR`, `CONFIGURATION`, `PLATFORM`, …) as an environment variable in `publish.cmd`, and never enable `IncludeNativeLibrariesForSelfExtract`.
- **A connect that lands in test mode when hardware should be present** usually means the native DLLs or the VC++ runtime did not reach the output root; `docs/dev/build-and-publish.md` and `tools/check-oes-connect.ps1` cover it.
- Restore needs the local feed `C:\Users\infor\source\repos\Ray1962\LocalPackages`. The four in-house packages, what each upgrade changed for this app, and how a new `Aqusen.Secs` arrives from the sibling `Test_SECS` checkout (default branch `main`; copy the nupkg into `LocalPackages`, clear a stale NuGet cache): `docs/dev/nuget-packages.md`.

## Architecture

This is a **single-OES** leak-monitoring fork of a previously dual-OES codebase. Most of the UI shell, device control, logging, access control, and plot/configuration panels live in the `Aqst.OesApp.*` NuGets — the project here is mostly composition + a custom Recordings tab.

Treat that NuGet surface as the framework. When something looks "missing," it is almost certainly defined in those packages, not here. Which types those are: `docs/dev/app-shell.md`.

### Component notes — read the one you are about to change

Each note says what the component does, why it is built that way, and the incidents that shaped it. They are the record of decisions that leave no trace in code; read the relevant one before changing behaviour, not after.

- `MainViewModel` (constructor ordering, trigger modes, recorder strip), `AppSettings`/`SettingsService` and their factory defaults, the framework surface, the Monitor tab's live trend → `docs/dev/app-shell.md`
- Recording CSVs and archives, Recordings / Ratio Review tabs, `ConfigSnapshot`/`SystemLogMirror`, the diagnostic bundle, data retention, `RatioCsvLogger` → `docs/dev/recordings-and-data.md`
- Ratio Setup (monitor mode, pedestal handling, staged edits, latch carry-over), line picker, user spectral lines, wavelength correction → `docs/dev/ratio-setup-and-lines.md`
- Leak engine: extraction modes, factory ratio set, Golden Run capture and acceptance, thresholds and alarms, `PlasmaGate`, `ProcessClassifier`, ratio roles, leak-rate calibration → `docs/dev/leak-engine.md`
- Baseline Builder → `docs/dev/baseline-builder.md`
- Batch layer and Batch Trend → `docs/dev/batch-layer.md`
- Replay → `docs/dev/replay.md`
- Tool events, process windows, `EapSecsLink` and the EAP tab → `docs/dev/tool-events-and-eap.md`
- SECS/GEM equipment interface (`SecsBridge`, profile stamping, Add missing rows, alarms/events, slot VIDs, traffic log) → `docs/dev/secs-interface.md`
- Tests, recorded-run tests, manual verification → `docs/dev/testing.md`
- Publish, native DLLs, VC++ runtime → `docs/dev/build-and-publish.md`
- NuGet feed and package upgrades → `docs/dev/nuget-packages.md`

### Conventions worth knowing

- The single device keeps the tag `"OES1"` (not `"OES"`) so the existing recording file-pairing infrastructure, filename scan, and `RecordingGroup` logic keep working unchanged. Don't rename it.
- The **SECS tab is deliberately not gated** — whether the tool is talking to the MES is a question an Operator gets asked, and the answer must not need a sign-in. Its settings carry the gate instead (`SecsViewModel.CanEdit`, bound to the settings `Border`'s `IsEnabled`). Same split as the Monitor tab's recorder strip: seeing the state is an Operator concern, changing it is not.
- The Configuration and Replay tabs are gated to Engineer+ (`MainWindow.IsEngineerGated`); window close is blocked for Guest. Both gates are enforced in `MainWindow.xaml.cs` (`MainTabControl_SelectionChanged`, `MainWindow_Closing`). The gate identifies its tabs by `x:Name` (`ConfigurationTab`, `ReplayTab`, `MonitorTab`) and compares by reference — **never reintroduce a hard-coded tab index here**. It was written that way twice and silently broke both times a tab was inserted ahead of Configuration (most recently `6734da1`, which left Configuration ungated for anyone including Guest while the gate guarded Leak Calibration instead).
- Sample flow: each `DeviceViewModel.SpectrumAvailable` event is forwarded to `DualIntensityLogger.ProcessSample(slot, sample)`. The logger's threshold state machine decides when to open/close CSV writers; lifecycle events (`StateChanged`, `ErrorOccurred`, `FilesChanged`) are bridged into `SystemLogger` for the audit log.
- `MainViewModel.ConnectBothAsync` calls `OesDiscovery.OpenAllDevices()` once, then hands one handle to each slot via `AttachAsync`; slots with `ForceTestMode=true` or that run out of handles fall back to `ConnectStandaloneAsync`. Unused handles are explicitly closed.
- Settings are saved atomically (temp file + `File.Move(..., overwrite:true)`). The `AccessControl` user list is re-read from disk on each persist so user-management edits don't clobber unsaved Configuration-tab edits, and vice versa.

## Documentation

`docs/` holds the human-facing docs; the design notes there are the authority on the maths. What each one is for, who reads it and whether it ships: `docs/dev/docs-index.md`. Rules for all of them:

- **The Markdown is the source; the HTML beside it is generated.** Regenerate after every edit with `python3 tools/check-docs.py --fix`, which keeps each document's own title (a hand-typed `tools/md2html.py` command does not). Without `--fix` it reports stale HTML, publish-list disagreements, and a package table in `docs/dev/nuget-packages.md` that no longer matches the csproj; CI runs it on every push and PR.
- Some HTML is written by hand and is its own source — `secs-acceptance-sheet-zh-TW.html`, `ratio-noise-pipeline.html`, `process-classification-20260820-21-zh-TW.html`, and `EAP ceid-events.html` (whose `.md` beside it is not its source). Edit those directly; never run them through `md2html.py`.
- A doc that ships with the publish folder needs one `_OperatorDoc` line in the csproj **and** its filename in `publish.cmd`'s completeness check; `check-docs.py` compares the two.
- Manual screenshots are `docs/images/manual-*.png`; `run-app` has the procedure for retaking one.
- `docs/dev/` is maintainer and agent reference in English: no HTML, not shipped.

## Related skills

One skill lives **in this repo** (`.claude/skills/`), because it is about this app rather than the stack:

- `run-app` — launching OES_Leak_Monitor from the WSL shell and **driving its window**: select a tab, press a button by its visible text, screenshot, read it back. `CLAUDE.md` says everything with a window on it is verified by running the app, which is only true if you can see it. Carries the facts that make it work cold — no native `dotnet` in WSL, launch the built `.exe`, the running app locks that `.exe` so a build or `dotnet test` fails with `MSB3027` until you close it, and UI Automation from Windows PowerShell 5.1 rather than coordinate clicking (the control enumeration alone answers most questions, and it survives a second monitor's negative window coordinates). The bundled `run` skill has no row for a WPF app driven from WSL.

Three more are tailored to this stack — invoke them when relevant rather than reinventing:

- `create-oes` — instantiating `OesParameters` / `OesSpectrometer`, wiring DLL paths, the `FlattenOesNativeDlls` build glue.
- `use-multi-oes` — `OesDiscovery.OpenMultiDevices` / `AttachAsync` patterns (relevant if this app ever returns to multi-device).
- `log-spectrometer-csv` — the threshold-triggered CSV logger state machine (Idle / WaitingToStart / Saving / WaitingToStop) that `DualIntensityLogger` implements.
