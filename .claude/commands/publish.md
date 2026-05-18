---
description: Publish OES Leak Monitor as a standalone single-file self-contained .exe
---

Build the standalone executable for OES Leak Monitor using the
`SelfContained-win-x64` publish profile (self-contained, single-file — the
target PC needs no .NET install).

Steps:

1. Check that `Aqst.OesApp.Wpf` version **0.1.2 or newer** exists in
   `/mnt/c/Users/infor/source/repos/Ray1962/LocalPackages` (`ls` the folder).
   The publish restore needs it. If only 0.1.1 or older is there, STOP and tell
   the user to `dotnet pack` the `Aqst.OesApp.Wpf` project from the sibling repo
   `Ray1962/DualOes_PlasmaMonitor` first — do not continue.

2. Run the publish. This is a WPF/Windows build, so use the Windows .NET SDK
   (the WSL shell has no native `dotnet`):

   ```
   "/mnt/c/Program Files/dotnet/dotnet.exe" publish src/OES_Leak_Monitor/OES_Leak_Monitor.csproj -p:PublishProfile=SelfContained-win-x64
   ```

3. When it finishes, verify `src/OES_Leak_Monitor/bin/Publish/win-x64/` contains
   `OES_Leak_Monitor.exe` plus the loose native DLLs (`UserApplication.dll`,
   `SiUSBXp.dll`, and the WPF `*_cor3.dll` set). Report the full output folder
   path and the `.exe` size.

Notes:
- Never enable `IncludeNativeLibrariesForSelfExtract`. The OES native DLLs must
  stay loose next to the `.exe` — `DllResolver` only searches the app base dir.
- Ship the whole output folder together, not just the `.exe`.
- If the publish fails on restore, it is almost always the missing 0.1.2
  package from step 1.
