---
name: run-app
description: Launch OES_Leak_Monitor and drive its window from a WSL shell — select a tab, press a button, screenshot and read back the result. Use when asked to run, start, screenshot, or "see it working" in the real app, or to verify anything with a window on it (the automated tests cover only the SECS interface and the leak engine).
---

# Running OES_Leak_Monitor and looking at it

This is a **Windows WPF app driven from a WSL shell**. The built-in `run` skill has no
row for that shape — its desktop example is Electron under xvfb, which does not apply.
This is the path that actually works here, cold.

`CLAUDE.md` is explicit that everything with a window on it is verified by running the
app. That is only true if you can *see* it, so this skill ends at a screenshot you read.

## The four facts that make it work

1. **There is no native `dotnet` in WSL.** Use `"/mnt/c/Program Files/dotnet/dotnet.exe"`.
2. **Launch the built `.exe`, not `dotnet run`** — faster, and `dotnet run` holds a
   console that complicates killing it.
3. **The running app locks `bin\Debug\...\OES_Leak_Monitor.exe`.** A build or `dotnet test`
   while it is open fails with `MSB3027 … 檔案鎖定者: "OES_Leak_Monitor (<pid>)"`. Close
   the app first. This will bite you; it is not a broken build.
4. **Drive it with UI Automation from Windows PowerShell**, not by clicking coordinates.
   `SelectionItemPattern` selects a tab, `InvokePattern` presses a button, and both report
   the control's real size and enabled state — which is often the whole answer, before any
   screenshot. PowerShell 5.1 (`powershell.exe`) has `UIAutomationClient`; pwsh 7 may not.

## Do it

```bash
EXE='C:\Users\infor\source\repos\Ray1962\OES_Leak_Monitor\src\OES_Leak_Monitor\bin\Debug\net8.0-windows\OES_Leak_Monitor.exe'
"/mnt/c/Program Files/dotnet/dotnet.exe" build src/OES_Leak_Monitor/OES_Leak_Monitor.csproj -c Debug
cp .claude/skills/run-app/scripts/drive.ps1 /mnt/c/Users/infor/AppData/Local/Temp/
powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File 'C:\Users\infor\AppData\Local\Temp\drive.ps1' -Tab Logs -Press 'Create diagnostic bundle'
```

`scripts/drive.ps1` launches (or attaches to) the app, maximises it, lists every tab and
button with sizes and coordinates, optionally selects `-Tab` and presses `-Press`, saves a
PNG, and prints the path. Then copy the PNG somewhere readable and **read it**:

```bash
cp /mnt/c/Users/infor/AppData/Local/Temp/oeslm.png "$SCRATCH/shot.png"   # then Read that file
```

**Look at the screenshot.** A blank frame is a failure to launch, not a pass.

## Things that will trip you

- **A second monitor gives negative window coordinates** (`at -2386,269`). `CopyFromScreen`
  handles them; do not "fix" them.
- **Buttons are found by their visible text** (`NameProperty`). Renaming a button's content
  breaks the script's `-Press` argument, and the failure is `!! button not found`, not a
  crash.
- **Pressing a button does real work on real folders.** The diagnostic-bundle button writes
  into `%APPDATA%\OES_Leak_Monitor\Diagnostics\`; the app appends to
  `%APPDATA%\OES_Leak_Monitor\Logs\` on every launch. Clean up what you created, and say
  in your report that you launched the user's app — a window appears on their desktop.
- **Test mode.** With no spectrometer attached, Connect lands in test mode and streams
  synthetic spectra. Fine for exercising the UI; useless for timing (the simulator does not
  execute the exposure).
- **`-Press` on a background-threaded command returns immediately.** The script sleeps 6 s
  before the screenshot. Longer work needs a longer `-SettleSeconds`.

## Reading the result without a screenshot

Often the enumeration alone settles the question, and it is far cheaper than an image:

```
Button: 'Create diagnostic bundle'  225x36 at -2386,269  enabled=True
```

That one line proved the button existed, was the right size, sat above the framework panel,
and was **enabled while signed out as Guest** — which was the ungated-access requirement.
Reach for the screenshot when layout, colour, or truncation is the question.

## Cleaning up

```bash
powershell.exe -NoProfile -Command "Stop-Process -Name OES_Leak_Monitor -Force -ErrorAction SilentlyContinue"
```

Always close it before the next `build` or `dotnet test` (fact 3).
