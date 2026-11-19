using System;
using System.Collections.Generic;

namespace OES_Leak_Monitor;

/// <summary>
/// Per-ratio monitoring state.
/// <para>
/// ⚠️ <b>The declaration order is a wire format.</b> These members are reported to a SECS host
/// as their integer values (specification §1.4(c)-3, slot offset 05), and the counts in VID
/// 009–011 are derived from them. Inserting a member in the middle silently changes what a
/// host reads. Append only — see <c>docs/secs-integration.md</c> §5.1.
/// </para>
/// </summary>
public enum RatioState
{
    /// <summary>Plasma on, baseline set, ratio below the warning threshold.</summary>
    Normal,
    /// <summary>Ratio has sustained above the warning threshold.</summary>
    Warning,
    /// <summary>Ratio has sustained above the alarm threshold (latched).</summary>
    Alarm,
    /// <summary>Reference (N2) line too weak — plasma is off or absent.</summary>
    NoPlasma,
    /// <summary>Plasma is on but the signal or reference line sits too close to the noise floor
    /// for the ratio to be trusted — held out of the alarm rather than allowed to swing.</summary>
    LowSignal,
    /// <summary>No Golden Run baseline captured for this ratio yet.</summary>
    NoBaseline,
    /// <summary>Excluded by the operator — not computed and never alarmed.</summary>
    Disabled,
    /// <summary>
    /// Plasma on and the value is being recorded, but this entry is not judged — it is
    /// <see cref="RatioRole.TrendOnly"/> or <see cref="RatioRole.Guard"/>. Distinct from
    /// <see cref="Normal"/>, which means "judged, and fine": showing Normal here would be a
    /// reassurance nobody measured. Distinct from <see cref="NoBaseline"/> too, because for
    /// these entries not having one is the expected condition, not a fault to fix.
    /// </summary>
    Observing,
    /// <summary>The running process step is not the one this entry measures
    /// (<see cref="RatioDefinition.ProcessClass"/>). Held out of the composite alarm, but a
    /// latched alarm from its own process is kept — distinct from <see cref="Disabled"/> (an
    /// operator decision) and from <see cref="NoPlasma"/> (a fault or an idle tool), because
    /// on screen those three call for three different reactions.</summary>
    NotApplicable,
}

/// <summary>Immutable per-frame view of one ratio, handed to the UI.</summary>
public readonly record struct RatioSnapshot(
    string Key,
    string DisplayName,
    RatioState State,
    double RawRatio,
    double SmoothedRatio,
    bool HasBaseline,
    double BaselineMean,
    double BaselineSigma,
    double PercentOfBaseline,
    double WarnThreshold,
    double AlarmThreshold,
    double SlopePerMinute,
    bool PlasmaPresent,
    double NumeratorIntensity,
    double DenominatorIntensity,
    double RatioNoiseSigma,
    double NumeratorSnr,
    double DenominatorSnr,
    MonitorMode Mode,
    /// <summary>What this entry is for — whether the state above is a judgement at all.</summary>
    RatioRole Role,
    /// <summary>The reading carries a continuum pedestal, so <see cref="PercentOfBaseline"/> is
    /// a σ-score rather than a percentage and the slope reads in σ/min — see
    /// <see cref="RatioDefinition.ValueHasPedestal"/>.</summary>
    bool HasPedestal)
{
    /// <summary>Which judging paths are on for this entry (Golden Run is the only one unless the
    /// entry turns step judging on).</summary>
    public JudgingPaths EnabledPaths { get; init; } = JudgingPaths.GoldenRun;

    /// <summary>The path(s) behind the current Warning or Alarm; <see cref="JudgingPaths.None"/>
    /// in every other state.</summary>
    public JudgingPaths RaisedBy { get; init; }

    /// <summary>Smoothed value over the rolling baseline, minus one (a fraction). NaN while the
    /// step path is off or warming up.</summary>
    public double StepRise { get; init; } = double.NaN;

    /// <summary>The rolling baseline the rise is measured against (raw-ratio units). NaN while
    /// the step path is off or warming up.</summary>
    public double StepReference { get; init; } = double.NaN;

    /// <summary>Seconds until the step path has enough history to judge; 0 once it judges, NaN
    /// when the path is off.</summary>
    public double StepWarmupSecondsRemaining { get; init; } = double.NaN;

    /// <summary>True while the rolling baseline is held at its pre-event value because the rise
    /// is at or above the Warn rise.</summary>
    public bool StepReferenceFrozen { get; init; }
}

/// <summary>The ways a ratio can be judged. A flags set, because a ratio runs either or both and
/// an alarm can be raised by either or both.</summary>
[Flags]
public enum JudgingPaths
{
    None = 0,
    /// <summary>Against the Golden Run baseline (factor / σ thresholds).</summary>
    GoldenRun = 1,
    /// <summary>Against the rolling baseline (rise thresholds).</summary>
    Step = 2,
}

/// <summary>Short labels for <see cref="JudgingPaths"/>, as the panel and the log write them.</summary>
public static class JudgingPathsText
{
    /// <summary>"GR", "Step", "GR+Step", or "" for none.</summary>
    public static string Label(JudgingPaths p) => p switch
    {
        JudgingPaths.GoldenRun => "GR",
        JudgingPaths.Step => "Step",
        JudgingPaths.GoldenRun | JudgingPaths.Step => "GR+Step",
        _ => "",
    };
}

/// <summary>The rolling-baseline windows, shared by every ratio's step path
/// (<see cref="LeakMonitorSettings.StepLookbackSeconds"/> and friends).</summary>
public readonly record struct StepWindows(double LookbackSeconds, double GapSeconds, double MinHistorySeconds)
{
    public static StepWindows Default => new(LeakMonitorSettings.DefaultStepLookbackSeconds,
        LeakMonitorSettings.DefaultStepGapSeconds, LeakMonitorSettings.DefaultStepMinHistorySeconds);

    public static StepWindows From(LeakMonitorSettings s) =>
        new(s.StepLookbackSeconds, s.StepGapSeconds, s.StepMinHistorySeconds);
}

/// <summary>
/// Runtime monitor for one actinometric ratio: EMA smoothing, two-level threshold
/// state machine with a sustained-confirmation timer, alarm latching, and a sliding
/// linear-regression slope for trend display. Updated on the acquisition thread.
/// </summary>
public sealed class RatioMonitor
{
    private const double SlopeWindowSeconds = 120.0;

    private readonly RatioDefinition _def;

    private double _ema;
    private bool _hasEma;
    // EWMA variance of the raw ratio — feeds the leak-rate estimator's per-ratio σ_x.
    private double _emaVar;
    private DateTime _lastTs;
    private double _rawRatio = double.NaN;
    private double _numerator = double.NaN, _denominator = double.NaN;
    private double _numSnr = double.NaN, _denSnr = double.NaN;

    private double _baseMean, _baseSigma;
    private bool _hasBaseline;

    // The Golden Run judgement: thresholds relative to the baseline mean, its confirmation
    // timers and its latch. The ratio's state is formed from its paths (see JudgingPath).
    private readonly JudgingPath _gr = new();

    // The step judgement: the smoothed value against the median of this ratio's own recent raw
    // readings (the rolling baseline). History holds raw readings within the lookback window; it
    // stops taking frames while frozen, and is cleared wherever the EMA restarts.
    private readonly JudgingPath _step = new();
    private readonly StepWindows _windows;
    private readonly List<(DateTime T, double V)> _stepHistory = new();
    private DateTime? _stepHistoryStart;
    private double _stepRef = double.NaN, _stepRise = double.NaN, _stepWarmup = double.NaN;
    private bool _stepFrozen;
    private RatioState _state = RatioState.NoBaseline;
    private bool _plasmaPresent;

    // Grace window before an active display (Normal/Warning/Alarm) is allowed to drop back to
    // NoPlasma/LowSignal. Mirrors the Warn/Alarm confirmation timers, applied to the Idle<->active
    // boundary so a single frame jittering at the plasma floor or SNR floor doesn't flicker the
    // banner between "OK" (green) and "waiting for plasma / baseline" (grey) at the frame rate.
    private const double IdleGraceSeconds = 1.5;
    private DateTime? _plasmaLostSince, _lowSignalSince;

    private static bool StateIsActive(RatioState s) =>
        s is RatioState.Normal or RatioState.Warning or RatioState.Alarm;

    private readonly List<(double T, double V)> _trend = new();
    private double _slopePerMinute;

    public RatioMonitor(RatioDefinition def, StepWindows? windows = null)
    {
        _def = def;
        _windows = windows ?? StepWindows.Default;
        _stepWarmup = StepOn ? _windows.GapSeconds + _windows.MinHistorySeconds : double.NaN;
    }

    private bool GrOn => _def.GoldenRunJudging;
    private bool StepOn => _def.StepJudgingActive;

    public string Key => _def.Key;
    public RatioState State => _state;

    /// <summary>Mean of the active Golden Run baseline for this ratio (0 if none).</summary>
    public double BaselineMean => _baseMean;

    /// <summary>σ of the active Golden Run baseline for this ratio (0 if none).</summary>
    public double BaselineSigma => _baseSigma;

    /// <summary>
    /// While set, a confirmed alarm is still shown but not <em>latched</em>. Set during a Golden
    /// Run capture: the thresholds it is being judged against belong to the previous baseline,
    /// which is usually the very thing being replaced — a recipe change makes a deviation
    /// certain, and latching it would leave the operator with an alarm to acknowledge that was
    /// never about a leak. Does not clear an existing latch: one raised before the capture began
    /// is a real, already-confirmed leak.
    /// </summary>
    public bool SuppressLatch { get; set; }

    /// <summary>True when a usable Golden Run baseline is set for this ratio.</summary>
    public bool HasBaseline => _hasBaseline;

    public void SetBaseline(double mean, double sigma)
    {
        _baseMean = mean;
        _baseSigma = double.IsNaN(sigma) || sigma < 0 ? 0 : sigma;
        _hasBaseline = !double.IsNaN(mean) && mean > 0;
        if (!_hasBaseline && !StepOn) _state = RatioState.NoBaseline;
    }

    public void ClearBaseline()
    {
        _hasBaseline = false;
        _baseMean = _baseSigma = 0;
        _state = RatioState.NoBaseline;
    }

    /// <summary>Clears the latched alarm so a recovered process can return to Normal.</summary>
    public void Acknowledge()
    {
        _gr.Acknowledge();
        _step.Acknowledge();
    }

    /// <summary>True while an alarm is latched and unacknowledged.</summary>
    public bool HasLatchedAlarm => _gr.Latched || _step.Latched;

    /// <summary>Which paths hold a latch right now.</summary>
    public JudgingPaths LatchedPaths =>
        (_gr.Latched ? JudgingPaths.GoldenRun : JudgingPaths.None) |
        (_step.Latched ? JudgingPaths.Step : JudgingPaths.None);

    /// <summary>
    /// Re-arms a latch carried over from the monitor instance this one replaced (a ratio-set
    /// reload rebuilds every monitor). Only the operator's Acknowledge clears a latch — a
    /// confirmed leak must not disappear because acquisition was restarted.
    /// </summary>
    public void RestoreLatchedAlarm(JudgingPaths paths = JudgingPaths.GoldenRun)
    {
        if (paths.HasFlag(JudgingPaths.Step)) _step.RestoreLatch();
        if (paths.HasFlag(JudgingPaths.GoldenRun) || paths == JudgingPaths.None) _gr.RestoreLatch();
        _state = RatioState.Alarm;
    }

    /// <summary>
    /// Drops the live smoothing / trend / confirmation state so a new experiment run starts
    /// clean — pre-change frames no longer bridge into the post-change EMA. Keeps the Golden
    /// Run baseline; keeps the latched alarm unless <paramref name="clearLatch"/> is set
    /// (a Reset deliberately leaves a real, already-confirmed leak latched).
    /// </summary>
    public void ResetRuntime(bool clearLatch)
    {
        _hasEma = false;
        _emaVar = 0;
        _rawRatio = _numerator = _denominator = double.NaN;
        _numSnr = _denSnr = double.NaN;
        _gr.ResetTimers();
        ClearStepHistory();
        _plasmaLostSince = _lowSignalSince = null;
        _trend.Clear();
        _slopePerMinute = 0;
        _plasmaPresent = false;
        if (clearLatch) Acknowledge();
        // "No Baseline" names a fault to fix. For an entry that is never judged, not having one
        // is the expected condition, so idle reads as plasma-off instead.
        _state = HasLatchedAlarm
            ? RatioState.Alarm
            : (_hasBaseline || !_def.Alarms || StepOn ? RatioState.NoPlasma : RatioState.NoBaseline);
    }

    /// <summary>
    /// Marks the ratio out of scope for the running process step: restarts the smoothing and
    /// the confirmation timers so the next step of its own class does not inherit a value
    /// measured during a different plasma, and <em>keeps the latch</em> — a leak confirmed in
    /// one process is still a leak while the tool runs the next one, and clearing it here would
    /// end a confirmed alarm without an Acknowledge and without a log entry.
    /// </summary>
    public void MarkNotApplicable()
    {
        _hasEma = false;
        _gr.ResetTimers();
        ClearStepHistory();
        _plasmaLostSince = _lowSignalSince = null;
        _trend.Clear();
        _slopePerMinute = 0;
        _rawRatio = _numerator = _denominator = double.NaN;
        _numSnr = _denSnr = double.NaN;
        _plasmaPresent = false;
        _state = HasLatchedAlarm ? RatioState.Alarm : RatioState.NotApplicable;
    }

    /// <summary>Marks the ratio excluded — resets the latch and smoothing so a later
    /// re-enable starts fresh.</summary>
    public void MarkDisabled()
    {
        _state = RatioState.Disabled;
        _hasEma = false;
        _gr.ResetTimers();
        ClearStepHistory();
        _plasmaLostSince = _lowSignalSince = null;
        Acknowledge();
        _trend.Clear();
        _slopePerMinute = 0;
        _rawRatio = _numerator = _denominator = double.NaN;
        _numSnr = _denSnr = double.NaN;
        _plasmaPresent = false;
    }

    /// <summary>Live scatter of the smoothed ratio (√ of its EWMA variance), 0 until enough
    /// frames have accumulated. Used to widen the alarm bands when the current signal is noisier
    /// than the Golden Run was — a near-noise ratio shouldn't trip on its own jitter.</summary>
    private double LiveSigma => _hasEma && _emaVar > 0 ? Math.Sqrt(_emaVar) : 0.0;

    /// <summary>The larger of the baseline-capture σ and the live σ — so the threshold tracks
    /// whichever noise estimate is currently worse.</summary>
    private double EffectiveSigma => Math.Max(_baseSigma, LiveSigma);

    /// <summary>
    /// Warn level. Normally the higher of the multiplicative and the σ form, so neither a
    /// proportionally small rise nor one inside the noise can trip it. When the value carries a
    /// continuum pedestal (<see cref="RatioDefinition.ValueHasPedestal"/>) the multiplicative
    /// form is dropped: 1.2 × a pedestal of a few thousand counts is tens of σ away, and
    /// <c>Math.Max</c> would pick exactly that branch and bury the σ term — an alarm that can
    /// never fire, which is worse than no alarm because it looks armed.
    /// </summary>
    public double WarnThreshold => _hasBaseline
        ? (_def.ValueHasPedestal
            ? _baseMean + _def.SigmaWarn * EffectiveSigma
            : Math.Max(_def.WarnFactor * _baseMean, _baseMean + _def.SigmaWarn * EffectiveSigma))
        : double.NaN;

    /// <summary>Alarm level — same rule as <see cref="WarnThreshold"/>.</summary>
    public double AlarmThreshold => _hasBaseline
        ? (_def.ValueHasPedestal
            ? _baseMean + _def.SigmaAlarm * EffectiveSigma
            : Math.Max(_def.AlarmFactor * _baseMean, _baseMean + _def.SigmaAlarm * EffectiveSigma))
        : double.NaN;

    /// <summary>Feeds one frame's extracted line measurements into the state machine.</summary>
    public void Update(LineMeasurement num, LineMeasurement den, DateTime ts, bool plasmaPresent)
    {
        double numerator = num.Value, denominator = den.Value;
        _plasmaPresent = plasmaPresent;
        _numerator = numerator;
        _denominator = denominator;
        _numSnr = num.Snr;
        _denSnr = den.Snr;
        // Absolute-intensity mode tracks the signal line itself; the reference (denominator)
        // only gates plasma-present (already folded into plasmaPresent), it is not divided in.
        bool absolute = _def.MonitorMode == MonitorMode.AbsoluteIntensity;
        double rawRatio;
        if (!plasmaPresent)
            rawRatio = double.NaN;
        else if (absolute)
            rawRatio = double.IsNaN(numerator) ? double.NaN : numerator;
        else
            rawRatio = denominator != 0 && !double.IsNaN(denominator)
                ? numerator / denominator
                : double.NaN;
        _rawRatio = rawRatio;

        if (!plasmaPresent || double.IsNaN(rawRatio) || double.IsInfinity(rawRatio))
        {
            // Grace: if we were showing an active state, hold it (and the EMA/trend) for a
            // short window so a single frame dipping below the plasma floor doesn't flicker
            // the banner. Only commit to NoPlasma once the dropout is sustained.
            if (StateIsActive(_state))
            {
                _plasmaLostSince ??= ts;
                if ((ts - _plasmaLostSince.Value).TotalSeconds < IdleGraceSeconds)
                    return;
            }
            _plasmaLostSince = null;
            // Plasma off: hold the latch, but restart smoothing/trend so a stale
            // value doesn't bridge the gap when plasma returns.
            _state = RatioState.NoPlasma;
            _gr.ResetTimers();
            ClearStepHistory();
            _hasEma = false;
            _trend.Clear();
            _slopePerMinute = 0;
            return;
        }
        _plasmaLostSince = null;

        // Smooth the raw ratio into the EMA and trend regardless of signal quality, so the
        // % -of-baseline trend chart keeps drawing even when a line dips toward the noise
        // floor. The SNR gate below only governs the Warn/Alarm decision and the leak-rate
        // fit — not the plotted curve (the operator asked to always see the trend).
        if (!_hasEma)
        {
            _ema = rawRatio;
            _emaVar = 0;
            _hasEma = true;
        }
        else
        {
            double dt = (ts - _lastTs).TotalSeconds;
            if (dt <= 0) dt = 0.001;
            double tau = Math.Max(0.1, _def.EmaTauSeconds);
            double alpha = 1.0 - Math.Exp(-dt / tau);
            double delta = rawRatio - _ema;
            _ema += alpha * delta;
            // EWMA variance (West, 1979): tracks the raw ratio's scatter about its mean.
            _emaVar = (1.0 - alpha) * (_emaVar + alpha * delta * delta);
        }
        _lastTs = ts;
        UpdateTrend(ts, _ema);
        if (StepOn) UpdateStepReference(ts, rawRatio);

        // A non-alarming entry stops here: the value, the EMA and the trend are all computed
        // above and reach the CSV, the batch record and the panel, but nothing below this line
        // — thresholds, the confirmation timer, the latch — applies to it. Placed after the
        // smoothing rather than before it so a role change does not also change what is
        // recorded, only what is judged.
        if (!_def.Alarms)
        {
            _gr.ResetTimers();
            _step.ResetTimers();
            _state = RatioState.Observing;
            return;
        }

        // Signal-quality gate: even with plasma on, a line sitting in the noise makes the
        // ratio meaningless (σ_R/R blows up as either line → noise). An *unknown* SNR (NaN,
        // no baseline window) is not treated as low — only a measured SNR below the floor.
        // The trend is still plotted (above); we only hold the ratio out of the Warn/Alarm
        // decision so near-noise jitter can't trip an alarm — and the engine drops LowSignal
        // ratios from the leak-rate fit.
        //
        // In absolute mode only the monitored line is judged. The reference is not divided in
        // and no longer gates plasma either, so letting its SNR force LowSignal would defeat the
        // whole point of the mode: a weak monitored line paired with a reference that barely
        // registers would read "low signal" for ever, however strong the monitored line got.
        double minSnr = _def.MinSnr;
        bool lowSignal = minSnr > 0 &&
            ((!double.IsNaN(_numSnr) && _numSnr < minSnr) ||
             (!absolute && !double.IsNaN(_denSnr) && _denSnr < minSnr));
        if (lowSignal)
        {
            // Grace: hold an active display briefly so a single frame at the SNR floor doesn't
            // flicker the banner to LowSignal. A latched alarm is never grace-gated (it already
            // shows Alarm); only the Normal/Warning -> LowSignal flip is held.
            if (!HasLatchedAlarm && StateIsActive(_state))
            {
                _lowSignalSince ??= ts;
                if ((ts - _lowSignalSince.Value).TotalSeconds < IdleGraceSeconds)
                    return;
            }
            _lowSignalSince = null;
            // Don't let near-noise excursions accumulate confirmation time; preserve a latched
            // alarm so a real, already-confirmed leak isn't cleared by the signal dipping.
            _gr.ResetTimers();
            _step.ResetTimers();
            _state = HasLatchedAlarm ? RatioState.Alarm : RatioState.LowSignal;
            return;
        }
        _lowSignalSince = null;

        // Golden Run alone, with no baseline: unjudgeable, exactly as before there was a second path.
        if (!StepOn && !_hasBaseline)
        {
            _state = RatioState.NoBaseline;
            return;
        }

        bool grJudges = GrOn && _hasBaseline;
        bool stepJudges = StepOn && !double.IsNaN(_stepRise);
        if (grJudges)
            _gr.Judge(_ema, WarnThreshold, AlarmThreshold, ts, _def.ConfirmSeconds, SuppressLatch);
        else
            _gr.ResetTimers();
        // The step path does not depend on the baseline being replaced, so a Golden Run capture
        // has no reason to suppress it: a step during a capture is a leak being recorded as the
        // leak-free level, which is exactly when it must be said.
        if (stepJudges)
            _step.Judge(_stepRise, _def.StepWarnRise, _def.StepAlarmRise, ts, _def.ConfirmSeconds, suppressLatch: false);
        else
            _step.ResetTimers();

        if (HasLatchedAlarm) { _state = RatioState.Alarm; return; }
        // Nothing judges yet (step warming up, Golden Run off or without a baseline): the
        // not-yet-judged state, which takes no part in the composite. The snapshot's warm-up
        // seconds say why, so the panel can call it "warming up" rather than a missing baseline.
        if (!grJudges && !stepJudges) { _state = RatioState.NoBaseline; return; }
        _state = Worse(grJudges ? _gr.State : RatioState.Normal, stepJudges ? _step.State : RatioState.Normal);
    }

    private static RatioState Worse(RatioState a, RatioState b) =>
        a == RatioState.Alarm || b == RatioState.Alarm ? RatioState.Alarm
        : a == RatioState.Warning || b == RatioState.Warning ? RatioState.Warning
        : RatioState.Normal;

    /// <summary>The path(s) behind the current state: for Alarm, every latched or Alarm-confirming
    /// path; for Warning, every path confirming at least a Warning.</summary>
    private JudgingPaths RaisedBy()
    {
        JudgingPaths p = JudgingPaths.None;
        if (_state == RatioState.Alarm)
        {
            if (_gr.Latched || _gr.Confirmed == RatioState.Alarm) p |= JudgingPaths.GoldenRun;
            if (_step.Latched || _step.Confirmed == RatioState.Alarm) p |= JudgingPaths.Step;
        }
        else if (_state == RatioState.Warning)
        {
            if (_gr.Confirmed is RatioState.Warning or RatioState.Alarm) p |= JudgingPaths.GoldenRun;
            if (_step.Confirmed is RatioState.Warning or RatioState.Alarm) p |= JudgingPaths.Step;
        }
        return p;
    }

    private void ClearStepHistory()
    {
        _stepHistory.Clear();
        _stepHistoryStart = null;
        _stepRef = _stepRise = double.NaN;
        _stepWarmup = StepOn ? _windows.GapSeconds + _windows.MinHistorySeconds : double.NaN;
        _stepFrozen = false;
        _step.ResetTimers();
    }

    /// <summary>
    /// Feeds one raw reading into the rolling baseline and recomputes the rise. The reference is
    /// the median of the readings in <c>[t − lookback, t − gap]</c> (a median, so one bad frame
    /// cannot move it), available once the history spans gap + minimum history. While the rise is
    /// at or above the Warn rise the reference is frozen: it takes no new readings and keeps its
    /// value, so a leak lasting longer than the lookback is not absorbed into its own reference.
    /// </summary>
    private void UpdateStepReference(DateTime ts, double raw)
    {
        _stepHistoryStart ??= ts;
        if (!_stepFrozen)
        {
            _stepHistory.Add((ts, raw));
            var cutoff = ts.AddSeconds(-_windows.LookbackSeconds);
            int drop = 0;
            while (drop < _stepHistory.Count && _stepHistory[drop].T < cutoff) drop++;
            if (drop > 0) _stepHistory.RemoveRange(0, drop);
        }

        double remaining = (_windows.GapSeconds + _windows.MinHistorySeconds)
                           - (ts - _stepHistoryStart.Value).TotalSeconds;
        if (remaining > 0)
        {
            _stepWarmup = remaining;
            _stepRef = _stepRise = double.NaN;
            return;
        }
        _stepWarmup = 0;

        if (!_stepFrozen)
        {
            var upTo = ts.AddSeconds(-_windows.GapSeconds);
            var vals = new List<double>(_stepHistory.Count);
            foreach (var (t, v) in _stepHistory)
                if (t <= upTo && !double.IsNaN(v)) vals.Add(v);
            // Keep the last reference when the window is momentarily empty — after a frozen event
            // longer than the lookback, every pre-event reading has aged out and the next `gap`
            // seconds would otherwise go unjudged.
            double median = Median(vals);
            if (!double.IsNaN(median)) _stepRef = median;
        }
        _stepRise = _stepRef > 0 ? _ema / _stepRef - 1.0 : double.NaN;
        _stepFrozen = !double.IsNaN(_stepRise) && _stepRise >= _def.StepWarnRise;
    }

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        v.Sort();
        int n = v.Count;
        return n % 2 == 1 ? v[n / 2] : 0.5 * (v[n / 2 - 1] + v[n / 2]);
    }

    private void UpdateTrend(DateTime ts, double value)
    {
        double t = ts.Ticks / (double)TimeSpan.TicksPerSecond;
        _trend.Add((t, value));
        double cutoff = t - SlopeWindowSeconds;
        int drop = 0;
        while (drop < _trend.Count && _trend[drop].T < cutoff) drop++;
        if (drop > 0) _trend.RemoveRange(0, drop);
        _slopePerMinute = LinearSlope(_trend) * 60.0;
    }

    private static double LinearSlope(List<(double T, double V)> pts)
    {
        int n = pts.Count;
        if (n < 3) return 0;
        double t0 = pts[0].T;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (t, v) in pts)
        {
            double x = t - t0;
            sx += x; sy += v; sxx += x * x; sxy += x * v;
        }
        double denom = n * sxx - sx * sx;
        return Math.Abs(denom) < 1e-9 ? 0 : (n * sxy - sx * sy) / denom;
    }

    /// <summary>
    /// The value plotted on the % -of-baseline trend and shown in the row's percent column.
    /// <para>Normally the honest <c>ema / baseMean · 100</c> — for a ratio, and for an absolute
    /// reading of a baseline-subtracted line, the baseline mean <em>is</em> the signal, so a
    /// percentage of it says what it appears to say.</para>
    /// <para>When the value carries a continuum pedestal
    /// (<see cref="RatioDefinition.ValueHasPedestal"/>) that percentage is compressed to
    /// uselessness — a real excursion moves a 3757 ± 19 reading by a fraction of a percent, and
    /// the 100/120/150 guide lines would sit tens of σ away. There we normalize the excess above
    /// baseline by the <em>noise σ</em> (a z-score) and rescale onto the same axis — baseline
    /// stays at 100, one warning-σ above lands at 120 — so the guide lines keep meaning what
    /// they say. The state machine and the leak-rate fit use their own stable quantities
    /// (additive σ thresholds and the absolute rise Δ), not this display value.</para>
    /// </summary>
    private double PercentOfBaselineDisplay()
    {
        if (!_hasBaseline || !_hasEma) return double.NaN;
        if (!_def.ValueHasPedestal)
            return _ema / _baseMean * 100.0;

        double sigma = EffectiveSigma;
        // No usable noise estimate yet (no baseline scatter, no live scatter) — fall back to the
        // raw form for the first few frames rather than leaving a gap in the trend.
        if (!(sigma > 0)) return _ema / _baseMean * 100.0;
        double ptsPerSigma = _def.SigmaWarn > 0 ? 20.0 / _def.SigmaWarn : 20.0;
        return 100.0 + ptsPerSigma * (_ema - _baseMean) / sigma;
    }

    public RatioSnapshot Snapshot() => new(
        _def.Key,
        _def.DisplayName,
        _state,
        _rawRatio,
        _hasEma ? _ema : double.NaN,
        _hasBaseline,
        _baseMean,
        _baseSigma,
        PercentOfBaselineDisplay(),
        WarnThreshold,
        AlarmThreshold,
        _slopePerMinute,
        _plasmaPresent,
        _numerator,
        _denominator,
        _hasEma && _emaVar > 0 ? Math.Sqrt(_emaVar) : double.NaN,
        _numSnr,
        _denSnr,
        _def.MonitorMode,
        _def.Role,
        _def.ValueHasPedestal)
    {
        EnabledPaths = (GrOn ? JudgingPaths.GoldenRun : JudgingPaths.None) |
                       (StepOn ? JudgingPaths.Step : JudgingPaths.None),
        RaisedBy = RaisedBy(),
        StepRise = StepOn ? _stepRise : double.NaN,
        StepReference = StepOn ? _stepRef : double.NaN,
        StepWarmupSecondsRemaining = StepOn ? _stepWarmup : double.NaN,
        StepReferenceFrozen = StepOn && _stepFrozen,
    };
}
