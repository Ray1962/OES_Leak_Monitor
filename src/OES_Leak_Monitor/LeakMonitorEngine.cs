using System;
using System.Collections.Generic;
using System.Linq;
using Aqst.OesSpectrometer.Models;

namespace OES_Leak_Monitor;

/// <summary>Overall leak-monitor status, the worst of all monitored ratios.</summary>
public enum LeakAlarmLevel
{
    /// <summary>Nothing to evaluate — plasma off, no baseline, or monitor disabled.</summary>
    Idle,
    Normal,
    Warning,
    Alarm,
}

/// <summary>Validity of the selected leak-rate calibration for the current conditions.</summary>
public enum CalibrationStatus
{
    /// <summary>No calibration is selected — leak-rate estimation is off.</summary>
    NotCalibrated,
    /// <summary>The selected calibration applies and is producing estimates.</summary>
    Active,
    /// <summary>A calibration is selected but the active Golden Run baseline is not the one it
    /// was captured against — estimation is suspended (the rises are measured against a
    /// different baseline). Select the matching baseline, or re-calibrate.</summary>
    BaselineMismatch,
}

/// <summary>Immutable per-frame view of the whole monitor, handed to the UI.</summary>
public sealed class LeakMonitorSnapshot
{
    public DateTime Timestamp { get; init; }
    public LeakAlarmLevel Overall { get; init; }
    public IReadOnlyList<RatioSnapshot> Ratios { get; init; } = Array.Empty<RatioSnapshot>();
    public bool TestMode { get; init; }
    public bool CaptureActive { get; init; }
    public double CaptureProgress01 { get; init; }
    public string? ActiveGoldenRun { get; init; }

    /// <summary>A leak-rate calibration point is being averaged.</summary>
    public bool CalibrationCaptureActive { get; init; }
    public double CalibrationCaptureProgress01 { get; init; }
    /// <summary>Known leak rate of the calibration point currently being captured, mbar·L/s.</summary>
    public double CalibrationLeakRate { get; init; }

    /// <summary>Quantitative leak-rate estimate from the active calibration, or null when no
    /// calibration is active. <see cref="LeakRateEstimate.HasEstimate"/> is false when a
    /// calibration exists but no ratio currently yields a usable reading.</summary>
    public LeakRateEstimate? LeakRate { get; init; }

    /// <summary>Name of the selected leak-rate calibration, or null. Set even when the
    /// calibration is currently invalid — see <see cref="CalibrationStatus"/>.</summary>
    public string? ActiveCalibration { get; init; }

    /// <summary>Whether the selected calibration is valid for the current baseline.</summary>
    public CalibrationStatus CalibrationStatus { get; init; }
}

public sealed class LeakAlarmEventArgs : EventArgs
{
    public LeakAlarmLevel OldLevel { get; init; }
    public LeakAlarmLevel NewLevel { get; init; }
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Core actinometry leak-monitoring engine. Consumes spectrum frames off the acquisition
/// thread, extracts each ratio's line intensities, runs the per-ratio state machines, and
/// computes a composite alarm level. Also drives Golden Run baseline capture.
///
/// <para>Mirrors <c>DualIntensityLogger</c>: thread-safe ingest, events bridged to the UI
/// and the system log by the host view-model.</para>
/// </summary>
public sealed class LeakMonitorEngine : IDisposable
{
    // A Golden Run ratio baseline is rejected unless at least this fraction of its
    // SNR-evaluable frames cleared the per-ratio SNR floor — guards against a baseline built
    // from a biased sliver of upward noise excursions when the line hovers around noise.
    private const double MinBaselineAcceptFraction = 0.5;

    private readonly object _gate = new();
    private readonly LeakMonitorSettings _settings;
    private readonly SystemLogger? _log;
    private readonly List<RatioMonitor> _monitors = new();
    private readonly Dictionary<string, RatioDefinition> _defs = new();

    private GoldenRun? _activeRun;
    private LeakRateEstimator? _estimator;   // built from the active calibration, or null
    private CalibrationStatus _calStatus = CalibrationStatus.NotCalibrated;
    private LeakAlarmLevel _overall = LeakAlarmLevel.Idle;
    private bool _disposed;

    // Golden Run capture state.
    private bool _capturing;
    private string _captureName = "";
    private double _captureSeconds;
    private bool _captureHasStart;
    private DateTime _captureStart, _captureLast;
    private readonly Dictionary<string, Accum> _captureAccum = new();
    private readonly Dictionary<string, CaptureDiag> _captureDiag = new();
    private readonly Accum _captureDenom = new();

    // Leak-rate calibration-point capture state. Mirrors the Golden Run capture above but
    // averages each ratio's fractional rise (rawRatio / baselineMean − 1) at a known leak.
    private bool _calCapturing;
    private double _calLeakRate;
    private string _calLabel = "";
    private double _calSeconds;
    private bool _calHasStart;
    private DateTime _calStart, _calLast;
    private readonly Dictionary<string, Accum> _calAccum = new();

    public LeakMonitorEngine(LeakMonitorSettings settings, SystemLogger? systemLogger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = systemLogger;
        // A monitor exists for every defined ratio; the per-ratio Enabled flag decides
        // at runtime whether it is computed, so the operator can toggle it live.
        foreach (var def in _settings.Ratios)
        {
            _defs[def.Key] = def;
            _monitors.Add(new RatioMonitor(def));
        }
        ApplyGoldenRun(_settings.FindGoldenRun(_settings.ActiveGoldenRun)); // also builds the estimator
    }

    /// <summary>Raised for every processed frame. Fires on the acquisition thread.</summary>
    public event EventHandler<LeakMonitorSnapshot>? SampleProcessed;

    /// <summary>Raised when the composite alarm level changes.</summary>
    public event EventHandler<LeakAlarmEventArgs>? AlarmStateChanged;

    /// <summary>Raised when a Golden Run capture finishes and becomes the active baseline.</summary>
    public event EventHandler<GoldenRun>? GoldenRunCaptured;

    /// <summary>Raised when a leak-rate calibration point finishes averaging. The host collects
    /// these across leak elements and fits them into a <see cref="LeakCalibration"/>.</summary>
    public event EventHandler<LeakCalPoint>? CalibrationPointCaptured;

    /// <summary>Raised when the ratio configuration changes (e.g. a reference line swap),
    /// so the host can persist <see cref="Settings"/>.</summary>
    public event EventHandler? ConfigurationChanged;

    /// <summary>Raised after <see cref="ReloadRatios"/> rebuilds the monitored-ratio set.</summary>
    public event EventHandler? RatiosReloaded;

    /// <summary>The live settings object — mutated in place as Golden Runs are captured.</summary>
    public LeakMonitorSettings Settings => _settings;

    /// <summary>Definitions of the ratios actually being monitored (enabled only).</summary>
    public IReadOnlyList<RatioDefinition> MonitoredRatios =>
        _monitors.Select(m => _defs[m.Key]).ToList();

    /// <summary>Feeds one spectrum frame through the monitor. Safe to call off the UI thread.</summary>
    public void ProcessSample(SpectrumSample sample)
    {
        if (sample is null || _disposed) return;

        LeakMonitorSnapshot snap;
        LeakAlarmLevel oldOverall, newOverall;
        GoldenRun? capturedRun = null;
        LeakCalPoint? capturedPoint = null;

        lock (_gate)
        {
            if (!_settings.Enabled) return;

            var wl = sample.Wavelengths;
            var inten = sample.Intensities;

            // During a Golden Run capture the plasma gate ignores the inherited floor —
            // that floor came from a previous run and could otherwise block capturing a
            // fresh baseline (e.g. after a peak shift or a lower-power recipe). The new
            // floor is derived from the capture itself in FinalizeCapture().
            double floor = _capturing ? 0.0 : (_activeRun?.PlasmaPresentFloor ?? 0.0);

            foreach (var mon in _monitors)
            {
                var def = _defs[mon.Key];
                if (!def.Enabled)
                {
                    mon.MarkDisabled();
                    if (_capturing) GetDiag(mon.Key).Disabled = true;
                    continue;
                }

                var numM = LineIntensityExtractor.Extract(wl, inten, def.Numerator);
                var denM = LineIntensityExtractor.Extract(wl, inten, def.Denominator);
                double num = numM.Value, den = denM.Value;

                bool plasma = !double.IsNaN(den) && den > 0 && den > floor;
                mon.Update(numM, denM, sample.Timestamp, plasma);

                // The monitored quantity: the signal/reference ratio, or — in absolute mode —
                // the signal line's intensity (the reference only gates plasma-present above).
                bool absolute = def.MonitorMode == MonitorMode.AbsoluteIntensity;
                double value = absolute ? num : (den != 0 ? num / den : double.NaN);

                if (_capturing)
                {
                    // Tally why each frame did or didn't feed the baseline, so a ratio
                    // that ends the capture with no samples can be explained in the log.
                    var diag = GetDiag(mon.Key);
                    diag.Frames++;
                    if (double.IsNaN(num)) diag.NumeratorMissing++;
                    if (double.IsNaN(den) || den <= 0) diag.ReferenceMissing++;
                    if (plasma && den != 0 && !double.IsNaN(value))
                    {
                        // Mirror the runtime LowSignal gate: only frames whose lines clear the
                        // SNR floor feed the baseline, so a near-noise capture doesn't produce
                        // an unreliable mean/σ. MinSnr 0 disables the gate (legacy behaviour).
                        double minSnr = def.MinSnr;
                        bool lowSnr = minSnr > 0 &&
                            ((!double.IsNaN(numM.Snr) && numM.Snr < minSnr) ||
                             (!double.IsNaN(denM.Snr) && denM.Snr < minSnr));
                        if (lowSnr)
                        {
                            diag.LowSnr++;
                        }
                        else
                        {
                            diag.Accepted++;
                            GetAccum(mon.Key).Add(value);
                            _captureDenom.Add(den);
                        }
                    }
                }

                // Calibration point: average the fractional rise relative to the active
                // baseline. Needs a baseline (x is defined against it) and live plasma.
                if (_calCapturing && mon.HasBaseline && plasma &&
                    mon.BaselineMean > 0 && den != 0 && !double.IsNaN(value))
                {
                    double x = value / mon.BaselineMean - 1.0;
                    GetCalAccum(mon.Key).Add(x);
                }
            }

            if (_capturing)
            {
                if (!_captureHasStart)
                {
                    _captureHasStart = true;
                    _captureStart = sample.Timestamp;
                }
                _captureLast = sample.Timestamp;
                if ((_captureLast - _captureStart).TotalSeconds >= _captureSeconds)
                    capturedRun = FinalizeCapture();
            }

            if (_calCapturing)
            {
                if (!_calHasStart)
                {
                    _calHasStart = true;
                    _calStart = sample.Timestamp;
                }
                _calLast = sample.Timestamp;
                if ((_calLast - _calStart).TotalSeconds >= _calSeconds)
                    capturedPoint = FinalizeCalibrationPoint();
            }

            oldOverall = _overall;
            _overall = ComputeOverall();
            newOverall = _overall;
            snap = BuildSnapshot(sample.Timestamp, sample.IsTestMode);
        }

        SampleProcessed?.Invoke(this, snap);

        if (newOverall != oldOverall &&
            !(snap.TestMode && _settings.SuppressAlarmsInTestMode))
        {
            AlarmStateChanged?.Invoke(this, new LeakAlarmEventArgs
            {
                OldLevel = oldOverall,
                NewLevel = newOverall,
                Timestamp = snap.Timestamp,
            });
        }

        if (capturedRun is not null)
            GoldenRunCaptured?.Invoke(this, capturedRun);

        if (capturedPoint is not null)
            CalibrationPointCaptured?.Invoke(this, capturedPoint);
    }

    /// <summary>Starts averaging the ratios into a new Golden Run baseline.</summary>
    public void BeginGoldenRunCapture(string name, double seconds)
    {
        lock (_gate)
        {
            _capturing = true;
            _captureName = string.IsNullOrWhiteSpace(name) ? "Default" : name.Trim();
            _captureSeconds = Math.Max(1.0, seconds);
            _captureHasStart = false;
            _captureAccum.Clear();
            _captureDiag.Clear();
            _captureDenom.Reset();
        }
    }

    public void CancelGoldenRunCapture()
    {
        lock (_gate) _capturing = false;
    }

    /// <summary>
    /// Starts averaging each ratio's fractional rise at a known leak rate into one calibration
    /// point. Requires an active Golden Run baseline (the rise is measured against it) and live
    /// plasma; ratios with no usable frames are simply omitted from the resulting point.
    /// </summary>
    public void BeginCalibrationPointCapture(double leakRate, string label, double seconds)
    {
        lock (_gate)
        {
            _calCapturing = true;
            _calLeakRate = leakRate;
            _calLabel = label?.Trim() ?? "";
            _calSeconds = Math.Max(1.0, seconds);
            _calHasStart = false;
            _calAccum.Clear();
        }
    }

    public void CancelCalibrationCapture()
    {
        lock (_gate) _calCapturing = false;
    }

    /// <summary>Current reference (denominator) line label per monitored ratio — used to stamp
    /// a fitted calibration so a later reference swap invalidates it.</summary>
    public IReadOnlyDictionary<string, string> CurrentReferenceLabels()
    {
        lock (_gate)
            return _defs.ToDictionary(kv => kv.Key, kv => kv.Value.Denominator.Label);
    }

    /// <summary>Clears every latched alarm.</summary>
    public void Acknowledge()
    {
        LeakAlarmLevel oldOverall, newOverall;
        lock (_gate)
        {
            foreach (var mon in _monitors) mon.Acknowledge();
            oldOverall = _overall;
            _overall = ComputeOverall();
            newOverall = _overall;
        }
        if (newOverall != oldOverall)
            AlarmStateChanged?.Invoke(this, new LeakAlarmEventArgs
            {
                OldLevel = oldOverall, NewLevel = newOverall, Timestamp = DateTime.Now,
            });
    }

    /// <summary>
    /// Switches the active baseline to a previously captured Golden Run, and auto-selects the
    /// leak-rate calibration bound to it (a calibration follows its recipe baseline, so the live
    /// estimate doesn't fall into a stale <see cref="CalibrationStatus.BaselineMismatch"/> on a
    /// recipe change). Persists the new baseline + paired calibration via
    /// <see cref="ConfigurationChanged"/>.
    /// </summary>
    public void SelectGoldenRun(string? name)
    {
        lock (_gate)
        {
            _settings.ActiveGoldenRun = name;
            AutoPairCalibration(name);
            ApplyGoldenRun(_settings.FindGoldenRun(name)); // rebuilds the estimator + logs status
        }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Points <see cref="LeakMonitorSettings.ActiveCalibration"/> at the calibration bound to
    /// <paramref name="goldenRunName"/> — the most recently captured one when several share the
    /// baseline — or clears it when none exists, so the active calibration always tracks the
    /// active recipe. Caller holds <see cref="_gate"/>; the subsequent
    /// <see cref="ApplyGoldenRun"/> → <see cref="BuildEstimator"/> logs the status transition.
    /// </summary>
    private void AutoPairCalibration(string? goldenRunName)
    {
        string? match = null;
        if (goldenRunName is not null)
        {
            LeakCalibration? best = null;
            foreach (var c in _settings.Calibrations)
                if (string.Equals(c.GoldenRunName, goldenRunName, StringComparison.Ordinal) &&
                    (best is null || c.CapturedUtc > best.CapturedUtc))
                    best = c;
            match = best?.Name;
        }
        _settings.ActiveCalibration = match;
    }

    /// <summary>Rebuilds the runtime leak-rate estimator from <see cref="Settings"/>'s active
    /// calibration. Call after a calibration is saved or the active one is switched.</summary>
    public void ReloadCalibration()
    {
        lock (_gate) BuildEstimator();
    }

    /// <summary>
    /// (Re)builds the runtime estimator and re-evaluates calibration validity. A calibration
    /// only applies while the active Golden Run baseline matches the one it was captured against
    /// — the per-ratio rise is defined relative to that baseline, so a different baseline would
    /// silently corrupt the estimate. Logs each status transition.
    /// </summary>
    private void BuildEstimator()
    {
        var prev = _calStatus;
        string? prevCal = _activeCalForLog;
        var cal = _settings.FindCalibration(_settings.ActiveCalibration);
        if (cal is null)
        {
            _estimator = null;
            _calStatus = CalibrationStatus.NotCalibrated;
        }
        else if (_settings.ActiveGoldenRun is null ||
                 !string.Equals(cal.GoldenRunName, _settings.ActiveGoldenRun, StringComparison.Ordinal))
        {
            _estimator = null;
            _calStatus = CalibrationStatus.BaselineMismatch;
        }
        else
        {
            _estimator = new LeakRateEstimator(cal);
            _calStatus = CalibrationStatus.Active;
        }
        _activeCalForLog = cal?.Name;

        if (_calStatus != prev || !string.Equals(_activeCalForLog, prevCal, StringComparison.Ordinal))
            LogCalibrationStatus(cal);
    }

    private string? _activeCalForLog;

    private void LogCalibrationStatus(LeakCalibration? cal)
    {
        switch (_calStatus)
        {
            case CalibrationStatus.Active:
                _log?.LogSystemEvent(LogSeverity.Information, "LeakCalibrationActive",
                    $"Leak-rate calibration “{cal!.Name}” active against baseline " +
                    $"“{_settings.ActiveGoldenRun}”.",
                    related: $"Calibration={cal.Name},Baseline={_settings.ActiveGoldenRun}");
                break;
            case CalibrationStatus.BaselineMismatch:
                _log?.LogSystemEvent(LogSeverity.Warning, "LeakCalibrationSuspended",
                    $"Leak-rate calibration “{cal!.Name}” suspended — it was captured against " +
                    $"baseline “{cal.GoldenRunName}”, but the active baseline is " +
                    $"“{_settings.ActiveGoldenRun ?? "(none)"}”. Select that baseline or re-calibrate.",
                    related: $"Calibration={cal.Name},NeedBaseline={cal.GoldenRunName}," +
                             $"ActiveBaseline={_settings.ActiveGoldenRun ?? "(none)"}");
                break;
            case CalibrationStatus.NotCalibrated:
                // Only meaningful as a transition away from a previously selected calibration.
                _log?.LogSystemEvent(LogSeverity.Information, "LeakCalibrationCleared",
                    "Leak-rate estimation off — no calibration selected.");
                break;
        }
    }

    /// <summary>
    /// Includes or excludes a ratio from monitoring. A disabled ratio is not computed
    /// and never contributes to the composite alarm; it can be toggled back on live.
    /// </summary>
    public void SetRatioEnabled(string ratioKey, bool enabled)
    {
        lock (_gate)
        {
            if (!_defs.TryGetValue(ratioKey, out var def) || def.Enabled == enabled) return;
            def.Enabled = enabled;
        }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Swaps a ratio's reference (denominator) line. Any Golden Run baseline captured
    /// against the previous reference stops applying — that ratio reads "No Baseline"
    /// until a new Golden Run is captured.
    /// </summary>
    public void SetRatioReference(string ratioKey, LineRegion reference)
    {
        if (reference is null) return;
        lock (_gate)
        {
            if (!_defs.TryGetValue(ratioKey, out var def)) return;
            def.Denominator = reference.Clone();
            ApplyGoldenRun(_activeRun);
        }
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rebuilds the monitored-ratio set from <see cref="Settings"/> — applies a configuration
    /// edited in the Ratio Setup tab. Meant to be called when OES acquisition (re)starts, so
    /// a mid-run edit never disturbs a live evaluation. Resets per-ratio smoothing/state and
    /// re-applies the active Golden Run.
    /// </summary>
    public void ReloadRatios()
    {
        lock (_gate)
        {
            _monitors.Clear();
            _defs.Clear();
            foreach (var def in _settings.Ratios)
            {
                _defs[def.Key] = def;
                _monitors.Add(new RatioMonitor(def));
            }
            ApplyGoldenRun(_settings.FindGoldenRun(_settings.ActiveGoldenRun)); // rebuilds the estimator
            _overall = LeakAlarmLevel.Idle;
        }
        RatiosReloaded?.Invoke(this, EventArgs.Empty);
    }

    // --- internals -----------------------------------------------------------

    private GoldenRun FinalizeCapture()
    {
        _capturing = false;

        double denomMean = _captureDenom.Count > 0 ? _captureDenom.Mean : 0.0;
        var run = new GoldenRun
        {
            Name = _captureName,
            CapturedUtc = DateTime.UtcNow,
            DurationSeconds = (_captureLast - _captureStart).TotalSeconds,
            PlasmaPresentFloor = 0.2 * denomMean,
        };
        foreach (var mon in _monitors)
        {
            _captureAccum.TryGetValue(mon.Key, out var acc);
            int accepted = acc?.Count ?? 0;
            if (accepted == 0)
            {
                // The ratio produced no usable samples — record why so the operator
                // isn't left guessing at a permanent "No Baseline".
                LogDroppedRatio(mon.Key, run.Name);
                continue;
            }

            // Even with some samples, if too few of the SNR-evaluable frames cleared the floor
            // the line hovered around noise and the survivors are a biased upward sliver — reject
            // rather than set a misleading baseline.
            _captureDiag.TryGetValue(mon.Key, out var d);
            int lowSnr = d?.LowSnr ?? 0;
            int evaluable = accepted + lowSnr;
            if (evaluable > 0 && accepted < MinBaselineAcceptFraction * evaluable)
            {
                var dropDef = _defs[mon.Key];
                _log?.LogSystemEvent(LogSeverity.Warning, "GoldenRunRatioLowSnr",
                    $"Ratio {dropDef.DisplayName} baseline rejected — only {accepted} of {evaluable} " +
                    $"frames cleared the SNR floor ({dropDef.MinSnr:0.#}); the line sat near the noise " +
                    "floor. Raise plasma intensity / exposure, lower Min SNR, or use a stronger line.",
                    related: $"GoldenRun={run.Name},Ratio={mon.Key}");
                continue;
            }

            run.Baselines.Add(new GoldenRunRatioBaseline
            {
                Key = mon.Key,
                Mean = acc!.Mean,
                Sigma = acc.StdDev,
                SampleCount = acc.Count,
                ReferenceLabel = _defs[mon.Key].Denominator.Label,
            });
        }

        if (run.Baselines.Count == 0)
            _log?.LogSystemEvent(LogSeverity.Warning, "GoldenRunEmpty",
                $"Golden Run “{run.Name}” captured no usable ratio baselines — check the " +
                "spectrometer wavelength range, the plasma state, and which ratios are enabled.",
                related: $"GoldenRun={run.Name}");

        _settings.GoldenRuns.RemoveAll(g => g.Name == run.Name);
        _settings.GoldenRuns.Add(run);
        _settings.ActiveGoldenRun = run.Name;
        // Pair the calibration to the new baseline: re-capturing a recipe re-selects its
        // calibration; a brand-new baseline has none, so estimation turns off rather than
        // mismatching against a stale one.
        AutoPairCalibration(run.Name);
        ApplyGoldenRun(run);
        return run;
    }

    private LeakCalPoint FinalizeCalibrationPoint()
    {
        _calCapturing = false;
        var pt = new LeakCalPoint
        {
            LeakRate = _calLeakRate,
            Label = _calLabel,
            CapturedUtc = DateTime.UtcNow,
        };
        foreach (var mon in _monitors)
        {
            if (_calAccum.TryGetValue(mon.Key, out var acc) && acc.Count > 0)
                pt.Measurements.Add(new RatioCalMeasurement
                {
                    Key = mon.Key,
                    X = acc.Mean,
                    Sigma = acc.StdDev,
                    SampleCount = acc.Count,
                });
        }
        return pt;
    }

    private void ApplyGoldenRun(GoldenRun? run)
    {
        _activeRun = run;
        foreach (var mon in _monitors)
        {
            var b = run?.Find(mon.Key);
            var def = _defs[mon.Key];
            // A baseline applies only if it was captured against the ratio's current
            // reference line — switching the reference invalidates the old baseline.
            bool labelMatches = b is not null && b.ReferenceLabel == def.Denominator.Label;
            bool usable = b is not null && b.Mean > 0 && labelMatches;
            if (usable)
            {
                mon.SetBaseline(b!.Mean, b.Sigma);
            }
            else
            {
                mon.ClearBaseline();
                // A baseline exists but is being rejected purely on a reference-line
                // mismatch — surface it so it doesn't look like the capture failed.
                if (b is not null && b.Mean > 0 && !labelMatches)
                    _log?.LogSystemEvent(LogSeverity.Information, "LeakMonitorBaselineMismatch",
                        $"Golden Run “{run!.Name}” has a baseline for {def.DisplayName}, " +
                        $"but it was captured against reference {b.ReferenceLabel}, not the current " +
                        $"{def.Denominator.Label} — capture a new Golden Run for this reference.",
                        related: $"GoldenRun={run.Name},Ratio={mon.Key}");
            }
        }
        // The active baseline just changed — re-evaluate calibration validity against it.
        BuildEstimator();
    }

    /// <summary>Logs why a ratio ended a Golden Run capture with no usable baseline.</summary>
    private void LogDroppedRatio(string key, string runName)
    {
        var def = _defs[key];
        _captureDiag.TryGetValue(key, out var d);
        string reason;
        if (d is { Disabled: false, Frames: > 0 })
        {
            if (d.NumeratorMissing == d.Frames)
                reason = $"the numerator line {def.Numerator.Label} ({def.Numerator.CenterNm:0.#} nm) " +
                         "fell outside the spectrometer wavelength range in every frame";
            else if (d.ReferenceMissing == d.Frames)
                reason = $"the reference line {def.Denominator.Label} ({def.Denominator.CenterNm:0.#} nm) " +
                         "never registered — the plasma was off, or the line is outside the spectrum";
            else if (d.LowSnr > 0)
                reason = $"the line(s) stayed below the SNR floor ({def.MinSnr:0.#}) — near the noise " +
                         $"floor — in every usable frame ({d.LowSnr}/{d.Frames}). Raise plasma " +
                         "intensity / exposure, lower Min SNR, or use a stronger line";
            else
                reason = $"no frame had both lines valid ({d.NumeratorMissing}/{d.Frames} frames " +
                         $"missing the numerator, {d.ReferenceMissing}/{d.Frames} missing the reference)";
        }
        else if (d is { Disabled: true } || !def.Enabled)
        {
            reason = "the ratio was disabled for the whole capture";
        }
        else
        {
            reason = "no spectrum frames were processed during the capture window (plasma off?)";
        }

        _log?.LogSystemEvent(LogSeverity.Warning, "GoldenRunRatioDropped",
            $"Ratio {def.DisplayName} got no baseline from Golden Run “{runName}”: {reason}.",
            related: $"GoldenRun={runName},Ratio={key}");
    }

    private LeakAlarmLevel ComputeOverall()
    {
        int alarm = 0, warn = 0, active = 0;
        foreach (var mon in _monitors)
        {
            switch (mon.State)
            {
                case RatioState.Alarm:   alarm++; active++; break;
                case RatioState.Warning: warn++;  active++; break;
                case RatioState.Normal:           active++; break;
            }
        }
        if (active == 0) return LeakAlarmLevel.Idle;

        int need = _settings.RequireTwoForAlarm ? 2 : 1;
        if (alarm >= need) return LeakAlarmLevel.Alarm;
        if (alarm > 0 || warn > 0) return LeakAlarmLevel.Warning;
        return LeakAlarmLevel.Normal;
    }

    private LeakMonitorSnapshot BuildSnapshot(DateTime ts, bool testMode)
    {
        double progress = 0.0;
        if (_capturing && _captureHasStart && _captureSeconds > 0)
            progress = Math.Clamp(
                (_captureLast - _captureStart).TotalSeconds / _captureSeconds, 0.0, 1.0);

        double calProgress = 0.0;
        if (_calCapturing && _calHasStart && _calSeconds > 0)
            calProgress = Math.Clamp(
                (_calLast - _calStart).TotalSeconds / _calSeconds, 0.0, 1.0);

        var ratios = _monitors.Select(m => m.Snapshot()).ToList();
        LeakRateEstimate? estimate = ComputeLeakRate(ratios);

        return new LeakMonitorSnapshot
        {
            Timestamp = ts,
            Overall = _overall,
            Ratios = ratios,
            TestMode = testMode,
            CaptureActive = _capturing,
            CaptureProgress01 = progress,
            ActiveGoldenRun = _settings.ActiveGoldenRun,
            CalibrationCaptureActive = _calCapturing,
            CalibrationCaptureProgress01 = calProgress,
            CalibrationLeakRate = _calLeakRate,
            LeakRate = estimate,
            ActiveCalibration = _settings.ActiveCalibration,
            CalibrationStatus = _calStatus,
        };
    }

    /// <summary>
    /// Inverts the current per-ratio rises into a fused leak-rate estimate via the active
    /// calibration. Returns null when no calibration is active. The fractional rise feeds
    /// from each ratio's % -of-baseline; its noise from the ratio's EWMA scatter, scaled to x.
    /// </summary>
    private LeakRateEstimate? ComputeLeakRate(IReadOnlyList<RatioSnapshot> ratios)
    {
        if (_estimator is null) return null;

        var readings = new List<LeakRateEstimator.RatioReading>(ratios.Count);
        foreach (var r in ratios)
        {
            double x = r.HasBaseline && !double.IsNaN(r.PercentOfBaseline)
                ? r.PercentOfBaseline / 100.0 - 1.0
                : double.NaN;
            double sigX = r.BaselineMean > 0 && !double.IsNaN(r.RatioNoiseSigma)
                ? r.RatioNoiseSigma / r.BaselineMean
                : 0.0;
            readings.Add(new LeakRateEstimator.RatioReading(r.Key, x, sigX));
        }

        var refLabels = _defs.ToDictionary(kv => kv.Key, kv => kv.Value.Denominator.Label);
        return _estimator.Estimate(readings, refLabels);
    }

    private Accum GetAccum(string key)
    {
        if (!_captureAccum.TryGetValue(key, out var acc))
            _captureAccum[key] = acc = new Accum();
        return acc;
    }

    private CaptureDiag GetDiag(string key)
    {
        if (!_captureDiag.TryGetValue(key, out var d))
            _captureDiag[key] = d = new CaptureDiag();
        return d;
    }

    private Accum GetCalAccum(string key)
    {
        if (!_calAccum.TryGetValue(key, out var acc))
            _calAccum[key] = acc = new Accum();
        return acc;
    }

    public void Dispose()
    {
        _disposed = true;
        SampleProcessed = null;
        AlarmStateChanged = null;
        GoldenRunCaptured = null;
        CalibrationPointCaptured = null;
        ConfigurationChanged = null;
        RatiosReloaded = null;
    }

    /// <summary>Per-ratio tally of why frames were or weren't usable during a Golden Run capture.</summary>
    private sealed class CaptureDiag
    {
        /// <summary>Frames seen while this ratio was enabled.</summary>
        public int Frames;
        /// <summary>Frames whose numerator line was outside the spectrum (NaN).</summary>
        public int NumeratorMissing;
        /// <summary>Frames whose reference line was NaN or ≤ 0 (no plasma at that line).</summary>
        public int ReferenceMissing;
        /// <summary>Frames with plasma + both lines present but below the SNR floor (near noise).</summary>
        public int LowSnr;
        /// <summary>Frames that contributed a sample to the baseline.</summary>
        public int Accepted;
        /// <summary>The ratio was excluded by the operator for the whole capture.</summary>
        public bool Disabled;
    }

    /// <summary>Running mean / standard deviation accumulator.</summary>
    private sealed class Accum
    {
        private double _sum, _sumSq;
        public int Count { get; private set; }

        public void Add(double v) { _sum += v; _sumSq += v * v; Count++; }
        public void Reset() { _sum = _sumSq = 0; Count = 0; }

        public double Mean => Count > 0 ? _sum / Count : 0.0;
        public double StdDev
        {
            get
            {
                if (Count < 2) return 0.0;
                double m = Mean;
                double var = _sumSq / Count - m * m;
                return var > 0 ? Math.Sqrt(var) : 0.0;
            }
        }
    }
}
