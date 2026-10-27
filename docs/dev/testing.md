# Testing

What the automated tests cover, the recorded-run tests, and how to verify by hand what they do not reach.

## What the tests cover

Tests: `dotnet test tests/OES_Leak_Monitor.Tests/OES_Leak_Monitor.Tests.csproj -c Debug` (xUnit, `net8.0-windows`/x64 to match the app it references). They cover the SECS interface — chamber-code stamping, the profile/binding contract, the wire behaviour against a real `GemHost` over loopback, and the SECS tab's traffic list (`SecsPanelTests`, which shows an off-screen window) — plus the leak engine replayed against a recorded plasma run, and the tool-event process windows at the engine seam (`ToolEventWindowTests`), on the wire (`ToolEventWireTests`, S2F41 over loopback), in the outputs (`ToolEventOutputTests`), the SECS-tab VM (`ToolEventUiTests`) and the batch layer (`BatchToolClassTests`), and the ratio slot table the engine publishes for the slot SVIDs (`RatioSlotTableTests`) and the slot VIDs 101–400 behind it (`SecsRatioSlotTests`, plus `SecsRatioSlotWireTests` over loopback), the SECS tab's Add missing rows (`SecsProfileRowsTests`, `SecsProfileRowsButtonTests`), Ratio Setup's row moves (`RatioSetupReorderTests`), and the EAP link — the OES as SECS host against an `EquipmentSimulator` playing the EAP over loopback (`EapSecsLinkTests`), the context reports it feeds the engine (`ToolContextReportTests`) and the EAP tab's VM (`ToolEventUiTests`). **Three of them need a recording that is not in the repo** and report as *skipped* without it; see `RecordedRun` and the note below.

## Manual verification (no hardware required)

The automated tests cover the SECS interface and the leak engine; **everything with a window on it is still verified by running the app.** With no spectrometer attached, Connect falls back to **test mode** (`ConnectStandaloneAsync`), which streams synthetic spectra — enough to exercise the full UI without hardware. A connect that silently lands in test mode when hardware *should* be present usually means the native DLLs didn't reach the output root (see the native-DLL note in `build-and-publish.md`). To smoke-test a change:

- Launch, then **Connect** → **Start** in the Monitor tab's `DevicePanel`; confirm spectra stream and the Leak Monitor / Wavelength-Trend charts update.
- The Leak Monitor tab shows a **"test mode"** note while synthetic; alarms are suppressed there by default (`LeakMonitorSettings.SuppressAlarmsInTestMode`), so to exercise alarm transitions either tick **Raise leak alarms during replay** on the Replay tab or feed real/abnormal data. The note names what the frames actually are and whether alarms are live (`LeakMonitorViewModel.SetTestModeContext`).
- To validate against a **recorded spectrum** instead of the synthetic sine wave, use the **Replay tab** (Engineer+): Choose CSV… → Connect → Start (test mode) → Play. The `DevicePanel` live plot, Wavelength-Trend chart, leak monitor, and the written CSVs all show that recording, on its own wavelength axis and at its own frame intervals. See `replay.md`.
- The intensity logger only writes CSVs while the selected wavelength's intensity is **above the trigger threshold** — lower the threshold in the `LoggerPanel` if you need test-mode spectra to cross it and open a save session. The **Ratio CSV is independent**: it writes for as long as the OES is acquiring, whatever the threshold logger is doing, so it appears even in a test-mode run that never crosses the threshold.
- Recordings / Ratio Review tabs read back the CSVs written above; use them to confirm a logging change round-trips to disk and re-parses.

## The recorded-run tests

`RecordedRunTests` replays a real plasma recording through the leak engine and out to a SECS host. The default is `C:\DualOES\202608\14\P_OES1_0814220358.csv` — ten minutes of a chamber on 2026-08-14 during which the tool went Normal → Warning → Alarm; its ratio CSV (`P_Ratio_0814220355.csv`) is the contemporaneous record of that. Point `OES_TEST_RECORDING` at another full-spectrum CSV to use a different run.

**The recording is deliberately not committed.** It is 5.5 MB (2.5 MB zipped) against a repository whose whole history is ~5 MB and whose largest file is 128 KB. Synthetic spectra cannot replace it: what the engine does is decided by the continuum under each line, the noise on it, and the axis the spectrometer actually produced.

The tests replay it through the **factory** ratio set with a baseline captured from the recording's own first minute — not the configuration that produced the live transitions, which has since been retuned and cannot be recovered. It lands within a few seconds of them anyway (warning +2 s, alarm +5 s), which is what makes the ±90 s tolerance meaningful rather than generous: **if it starts drifting, either detection changed or the factory ratio set was retuned.** Widening the tolerance is not the fix.
