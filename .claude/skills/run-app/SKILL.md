---
name: run-app
description: Launch OES_Leak_Monitor and drive its window from a WSL shell — select a tab, press a button, run a multi-step sequence, screenshot and read back the result. Use when asked to run, start, screenshot, or "see it working" in the real app, to retake a user-manual screenshot, or to verify anything with a window on it (the automated tests cover only the SECS interface and the leak engine).
---

# Running OES_Leak_Monitor and looking at it

This is a **Windows WPF app driven from a WSL shell**. The built-in `run` skill has no
row for that shape — its desktop example is Electron under xvfb, which does not apply.
This is the path that actually works here, cold.

`CLAUDE.md` is explicit that everything with a window on it is verified by running the
app. That is only true if you can *see* it, so this skill ends at a screenshot you read.

## The six facts that make it work

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
5. **Check for a running copy first** with the `ps` step of `scripts/steps.ps1` (below): it
   prints each running copy with its path, or `none running`. One already open is
   usually the user's published build (`bin\Publish\win-x64`), sharing `settings.json`, the
   log folder and SECS port 5000 with the one you would launch. Ask the user to close it
   and wait for them to say it is closed; the auto-mode classifier refuses `Stop-Process`
   on it, and a bare "continue" is not that consent.
6. **`Test_SECS` playing the EAP listens on 5047.** Its default, 5000, is this app's own
   SECS port towards the FDC, so the EAP link ends up talking to itself. Set it with
   `edit|TS|1|5047` before `press|TS|Connect`, and the same port on the EAP tab.

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

## Leave the machine as you found it

Every run, not only a screenshot retake, writes into the user's real folders:

1. **Before launching**: `sha1sum` and copy `%APPDATA%\OES_Leak_Monitor\settings.json` (it
   holds the site's SECS acceptance configuration — it must come back byte-identical),
   copy `C:\DualOES\_batches.csv`, and list today's day folder `C:\DualOES\YYYYMM\DD\`.
2. **After closing**: copy `settings.json` back and confirm the sha matches, restore
   `_batches.csv`, and delete what the run added to today's folder (`P_Ratio_*` from a
   test-mode capture; `SIMP_*`, `_config_*`, `_log_*` from a replay). Keep files whose
   first row predates your launch — those are the user's.

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

## Several steps in a row: `scripts/steps.ps1`

`drive.ps1` does one tab and one button. Anything longer — sign in, connect, start a replay,
fill a dialog, tick a row, shoot several tabs — goes through `steps.ps1`, which takes the
whole sequence as one `;`-separated string (verbs are listed at the top of the script):

```bash
S='C:\Users\infor\source\repos\Ray1962\OES_Leak_Monitor\.claude\skills\run-app\scripts\steps.ps1'
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \
  "\$env:OESLM_USER='<user>'; \$env:OESLM_PASSWORD='<password>'; & '$S' -Steps 'launch|OES;login;press|OES|Connect;sleep|6;press|OES|Start;tab|OES|Logs;shot|logs'"
```

Shots land in `%TEMP%\oeslm-shots\`, already scaled to 1200 px. The sign-in comes from
`OESLM_USER` / `OESLM_PASSWORD` on the command line, never from a file in the repo. A line
starting `!!` means that step did nothing. A sequence longer than ~110 s exceeds the Bash
timeout: run it with `run_in_background` and wait for the notification.

What the script works around, so you do not rediscover it:

- **A dialog is a child of the main window** in the UIA tree, not a second top-level window.
- **A UIA toggle does not raise `Click`.** `GoldenRunCaptureDialog` enables **Start Capture**
  from the checkbox's `Click`, so after `dlg-toggle` it stays disabled; `dlg-edit` the name
  box afterwards and `TextChanged` re-evaluates it.
- **List and grid rows are named after their view-model type**, so `rows`/`row`/`tick` match
  on the cells' text instead.
- **Text boxes have no name.** `edits` lists the on-screen ones with the index `edit` takes.
- **`buttons|TS` lists what `press` can find** — Test_SECS's profile actions (PM3 Chamber
  Process Start, …) included. Cheaper than reading the profile's source for their labels.

## Retaking a user-manual screenshot

The manual's figures (`docs/images/manual-*.png`) were taken while **replaying the
2026-08-14 leak recording**, so the Leak Monitor, Ratio Review and Logs shots show a real
Normal → Warning → Alarm. Retake one the same way when its tab's layout changes:

1. **Snapshot what you will disturb** ("Leave the machine as you found it", step 1).
2. **Extract the recording** — it is archived: `P_OES1_0814220358.csv` from
   `C:\DualOES\202608\14.zip` into a temp folder — and point `simulationCsvPath` in
   `settings.json` at it.
3. **Play it**: `launch|OES;login;press|OES|Connect;sleep|6;press|OES|Start;tab|OES|Replay;press|OES|Play`.
   At 5× the 9:37 recording takes about two minutes; the leak shows from ~6:30 in.
   The tabs that need the leak on screen are shot before the replay ends.
4. **Shoot, read every image, and check each caption against what the image and the log
   actually show** — a caption written from memory is how Fig. 4 came to blame the wrong
   cause.
5. **Restore**: kill the app, then step 2 of "Leave the machine as you found it".
6. Copy the PNG over `docs/images/manual-<tab>.png` and regenerate the manual.

Tool events are never applied during a replay, so the EAP tab's "in window A" needs the
replay stopped, **Apply tool events in test mode** ticked, and `Test_SECS` running its
"EAP (PM3 event forwarder)" profile on the EAP port, then **PM3 Chamber Process Start**
pressed in it.

## Cleaning up

```bash
S='C:\Users\infor\source\repos\Ray1962\OES_Leak_Monitor\.claude\skills\run-app\scripts\steps.ps1'
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '$S' -Steps 'kill|OES;kill|TS;ps'"
```

Go through `steps.ps1` for these checks rather than a bare `Get-Process` / `Stop-Process`:
with nothing running, those exit 1 even with `-ErrorAction SilentlyContinue`, and the command
reads as failed when it did exactly what it should. `ps` and `kill` exit 0 either way.

Always close it before the next `build` or `dotnet test` (fact 3).
