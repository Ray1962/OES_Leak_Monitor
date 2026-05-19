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
    private readonly object _gate = new();
    private readonly LeakMonitorSettings _settings;
    private readonly SystemLogger? _log;
    private readonly List<RatioMonitor> _monitors = new();
    private readonly Dictionary<string, RatioDefinition> _defs = new();

    private GoldenRun? _activeRun;
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
        ApplyGoldenRun(_settings.FindGoldenRun(_settings.ActiveGoldenRun));
    }

    /// <summary>Raised for every processed frame. Fires on the acquisition thread.</summary>
    public event EventHandler<LeakMonitorSnapshot>? SampleProcessed;

    /// <summary>Raised when the composite alarm level changes.</summary>
    public event EventHandler<LeakAlarmEventArgs>? AlarmStateChanged;

    /// <summary>Raised when a Golden Run capture finishes and becomes the active baseline.</summary>
    public event EventHandler<GoldenRun>? GoldenRunCaptured;

    /// <summary>Raised when the ratio configuration changes (e.g. a reference line swap),
    /// so the host can persist <see cref="Settings"/>.</summary>
    public event EventHandler? ConfigurationChanged;

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

                double num = LineIntensityExtractor.Extract(wl, inten, def.Numerator);
                double den = LineIntensityExtractor.Extract(wl, inten, def.Denominator);

                bool plasma = !double.IsNaN(den) && den > 0 && den > floor;
                mon.Update(num, den, sample.Timestamp, plasma);

                if (_capturing)
                {
                    // Tally why each frame did or didn't feed the baseline, so a ratio
                    // that ends the capture with no samples can be explained in the log.
                    var diag = GetDiag(mon.Key);
                    diag.Frames++;
                    if (double.IsNaN(num)) diag.NumeratorMissing++;
                    if (double.IsNaN(den) || den <= 0) diag.ReferenceMissing++;
                    if (plasma && den != 0 && !double.IsNaN(num))
                    {
                        diag.Accepted++;
                        GetAccum(mon.Key).Add(num / den);
                        _captureDenom.Add(den);
                    }
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

    /// <summary>Switches the active baseline to a previously captured Golden Run.</summary>
    public void SelectGoldenRun(string? name)
    {
        lock (_gate)
        {
            _settings.ActiveGoldenRun = name;
            ApplyGoldenRun(_settings.FindGoldenRun(name));
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
            if (_captureAccum.TryGetValue(mon.Key, out var acc) && acc.Count > 0)
            {
                run.Baselines.Add(new GoldenRunRatioBaseline
                {
                    Key = mon.Key,
                    Mean = acc.Mean,
                    Sigma = acc.StdDev,
                    SampleCount = acc.Count,
                    ReferenceLabel = _defs[mon.Key].Denominator.Label,
                });
            }
            else
            {
                // The ratio produced no usable samples — record why so the operator
                // isn't left guessing at a permanent "No Baseline".
                LogDroppedRatio(mon.Key, run.Name);
            }
        }

        if (run.Baselines.Count == 0)
            _log?.LogSystemEvent(LogSeverity.Warning, "GoldenRunEmpty",
                $"Golden Run “{run.Name}” captured no usable ratio baselines — check the " +
                "spectrometer wavelength range, the plasma state, and which ratios are enabled.",
                related: $"GoldenRun={run.Name}");

        _settings.GoldenRuns.RemoveAll(g => g.Name == run.Name);
        _settings.GoldenRuns.Add(run);
        _settings.ActiveGoldenRun = run.Name;
        ApplyGoldenRun(run);
        return run;
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

        return new LeakMonitorSnapshot
        {
            Timestamp = ts,
            Overall = _overall,
            Ratios = _monitors.Select(m => m.Snapshot()).ToList(),
            TestMode = testMode,
            CaptureActive = _capturing,
            CaptureProgress01 = progress,
            ActiveGoldenRun = _settings.ActiveGoldenRun,
        };
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

    public void Dispose()
    {
        _disposed = true;
        SampleProcessed = null;
        AlarmStateChanged = null;
        GoldenRunCaptured = null;
        ConfigurationChanged = null;
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
