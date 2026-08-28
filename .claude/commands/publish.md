---
description: Publish OES Leak Monitor as a standalone single-file self-contained .exe
---

Build the standalone executable for OES Leak Monitor using the
`SelfContained-win-x64` publish profile (self-contained, single-file — the
target PC needs no .NET install).

Steps:

1. Check that the versions pinned in `OES_Leak_Monitor.csproj` exist in
   `/mnt/c/Users/infor/source/repos/Ray1962/LocalPackages` (`ls` the folder) —
   at the time of writing `Aqst.OesApp.Core` **0.1.9**, `Aqst.OesApp.Wpf`
   **0.1.15**, `Aqst.OesSpectrometer` **0.4.6**, `Aqusen.Secs` **0.6.0**. Read
   the csproj rather than trusting these numbers; they move. If an
   `Aqst.OesApp.*` package is missing, STOP and tell the user to `dotnet pack`
   it from the sibling repo `Ray1962/DualOes_PlasmaMonitor` first — do not
   continue. A missing `Aqusen.Secs` is packed from `Ray1962/Test_SECS`.

2. Run the publish. This is a WPF/Windows build, so use the Windows .NET SDK
   (the WSL shell has no native `dotnet`):

   ```
   "/mnt/c/Program Files/dotnet/dotnet.exe" publish src/OES_Leak_Monitor/OES_Leak_Monitor.csproj -p:PublishProfile=SelfContained-win-x64
   ```

3. When it finishes, verify `src/OES_Leak_Monitor/bin/Publish/win-x64/` holds
   all **20** expected files. Report the full output folder path and the `.exe`
   size. **Anything missing means the folder is not shippable — say so rather
   than reporting success**, because the app launches happily without most of
   these and only fails on the production PC.

   | Group | Files |
   |---|---|
   | App | `OES_Leak_Monitor.exe` |
   | OES native | `UserApplication.dll`, `SiUSBXp.dll`, `libsodium.dll` |
   | WPF native | `wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `D3DCompiler_47_cor3.dll`, `PenImc_cor3.dll`, `vcruntime140_cor3.dll` |
   | VC++ runtime | `vcruntime140.dll`, `vcruntime140_1.dll`, `msvcp140.dll` |
   | Operator docs | `user-manual-zh-TW.html`, `daily-inspection-plan-zh-TW.html`, `secs-operation-sop-zh-TW.html`, `secs-acceptance-sheet-zh-TW.html`, `CONTEXT-zh-TW.html`, `golden-run-baseline-troubleshooting-zh-TW.html` |
   | Field diagnostic | `check-oes-connect.ps1`, `check-oes-connect.cmd` |

   The authority on this list is `publish.cmd`'s own check — if it and this
   table disagree, `publish.cmd` is right and this file is stale.

Notes:
- Never enable `IncludeNativeLibrariesForSelfExtract`. The OES native DLLs must
  stay loose next to the `.exe` — `DllResolver` only searches the app base dir.
- The VC++ runtime three are not optional: without them a PC that never had the
  VC++ 2015–2022 x64 redistributable falls back to test mode **silently**, and
  logs nothing about why (`docs/postmortem-test-mode-20260817.md`).
- Ship the whole output folder together, not just the `.exe`.
- If an operator `.html` is missing, regenerate it from its Markdown with
  `tools/md2html.py` (see CLAUDE.md > Documentation) and publish again — the
  HTML is what the fab actually reads, so a stale one is worse than none.
  The exception is `secs-acceptance-sheet-zh-TW.html`, which is a hand-authored
  form with no Markdown source: restore it from git, never regenerate it.
- If the publish fails on restore, a package from step 1 is missing.
- `publish.cmd` runs the same publish and then re-checks all 20 files itself, so
  prefer it when the user wants the folder opened in Explorer afterwards.
