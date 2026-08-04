---
description: Publish OES Leak Monitor as a standalone single-file self-contained .exe
---

Build the standalone executable for OES Leak Monitor using the
`SelfContained-win-x64` publish profile (self-contained, single-file — the
target PC needs no .NET install).

Steps:

1. Check that the versions pinned in `OES_Leak_Monitor.csproj` exist in
   `/mnt/c/Users/infor/source/repos/Ray1962/LocalPackages` (`ls` the folder) —
   currently `Aqst.OesApp.Core` **0.1.3**, `Aqst.OesApp.Wpf` **0.1.7**,
   `Aqst.OesSpectrometer` **0.4.6**. Read the csproj rather than trusting these
   numbers; they move. If an `Aqst.OesApp.*` package is missing, STOP and tell
   the user to `dotnet pack` it from the sibling repo
   `Ray1962/DualOes_PlasmaMonitor` first — do not continue.

2. Run the publish. This is a WPF/Windows build, so use the Windows .NET SDK
   (the WSL shell has no native `dotnet`):

   ```
   "/mnt/c/Program Files/dotnet/dotnet.exe" publish src/OES_Leak_Monitor/OES_Leak_Monitor.csproj -p:PublishProfile=SelfContained-win-x64
   ```

3. When it finishes, verify `src/OES_Leak_Monitor/bin/Publish/win-x64/` holds
   **10** files: `OES_Leak_Monitor.exe`, the OES native DLLs
   (`UserApplication.dll`, `SiUSBXp.dll`, `libsodium.dll`), the WPF
   `*_cor3.dll` set (5 files), and `user-manual-zh-TW.html`. Report the full
   output folder path and the `.exe` size. A count below 10 means the folder is
   not shippable — say so rather than reporting success.

Notes:
- Never enable `IncludeNativeLibrariesForSelfExtract`. The OES native DLLs must
  stay loose next to the `.exe` — `DllResolver` only searches the app base dir.
- Ship the whole output folder together, not just the `.exe`.
- If the publish fails on restore, an `Aqst.*` package from step 1 is missing.
- `publish.cmd` runs the same publish and then re-checks the 10 files itself, so
  prefer it when the user wants the folder opened in Explorer afterwards.
