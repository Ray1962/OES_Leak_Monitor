using System;
using System.Collections.Generic;

namespace OES_Leak_Monitor;

/// <summary>Per-ratio monitoring state.</summary>
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
    MonitorMode Mode);

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

    private DateTime? _aboveWarnSince, _aboveAlarmSince;
    private bool _latchedAlarm;
    private RatioState _state = RatioState.NoBaseline;
    private bool _plasmaPresent;

    private readonly List<(double T, double V)> _trend = new();
    private double _slopePerMinute;

    public RatioMonitor(RatioDefinition def) => _def = def;

    public string Key => _def.Key;
    public RatioState State => _state;

    /// <summary>Mean of the active Golden Run baseline for this ratio (0 if none).</summary>
    public double BaselineMean => _baseMean;

    /// <summary>True when a usable Golden Run baseline is set for this ratio.</summary>
    public bool HasBaseline => _hasBaseline;

    public void SetBaseline(double mean, double sigma)
    {
        _baseMean = mean;
        _baseSigma = double.IsNaN(sigma) || sigma < 0 ? 0 : sigma;
        _hasBaseline = !double.IsNaN(mean) && mean > 0;
        if (!_hasBaseline) _state = RatioState.NoBaseline;
    }

    public void ClearBaseline()
    {
        _hasBaseline = false;
        _baseMean = _baseSigma = 0;
        _state = RatioState.NoBaseline;
    }

    /// <summary>Clears the latched alarm so a recovered process can return to Normal.</summary>
    public void Acknowledge() => _latchedAlarm = false;

    /// <summary>Marks the ratio excluded — resets the latch and smoothing so a later
    /// re-enable starts fresh.</summary>
    public void MarkDisabled()
    {
        _state = RatioState.Disabled;
        _hasEma = false;
        _aboveWarnSince = _aboveAlarmSince = null;
        _latchedAlarm = false;
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

    public double WarnThreshold => _hasBaseline
        ? Math.Max(_def.WarnFactor * _baseMean, _baseMean + _def.SigmaWarn * EffectiveSigma)
        : double.NaN;

    public double AlarmThreshold => _hasBaseline
        ? Math.Max(_def.AlarmFactor * _baseMean, _baseMean + _def.SigmaAlarm * EffectiveSigma)
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
            // Plasma off: hold the latch, but restart smoothing/trend so a stale
            // value doesn't bridge the gap when plasma returns.
            _state = RatioState.NoPlasma;
            _aboveWarnSince = _aboveAlarmSince = null;
            _hasEma = false;
            _trend.Clear();
            _slopePerMinute = 0;
            return;
        }

        // Signal-quality gate: even with plasma on, a line sitting in the noise makes the
        // ratio meaningless (σ_R/R blows up as either line → noise). An *unknown* SNR (NaN,
        // no baseline window) is not treated as low — only a measured SNR below the floor.
        double minSnr = _def.MinSnr;
        bool lowSignal = minSnr > 0 &&
            ((!double.IsNaN(_numSnr) && _numSnr < minSnr) ||
             (!double.IsNaN(_denSnr) && _denSnr < minSnr));
        if (lowSignal)
        {
            // Don't let near-noise excursions accumulate confirmation time, and don't smooth a
            // garbage value into the EMA; preserve a latched alarm so a real, already-confirmed
            // leak isn't cleared by the signal dipping.
            _aboveWarnSince = _aboveAlarmSince = null;
            _hasEma = false;
            _trend.Clear();
            _slopePerMinute = 0;
            _state = _latchedAlarm ? RatioState.Alarm : RatioState.LowSignal;
            return;
        }

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

        if (!_hasBaseline)
        {
            _state = RatioState.NoBaseline;
            return;
        }

        double warn = WarnThreshold, alarm = AlarmThreshold;
        if (_ema >= warn) _aboveWarnSince ??= ts; else _aboveWarnSince = null;
        if (_ema >= alarm) _aboveAlarmSince ??= ts; else _aboveAlarmSince = null;

        double confirm = Math.Max(0, _def.ConfirmSeconds);
        var confirmed = RatioState.Normal;
        if (_aboveAlarmSince is { } a && (ts - a).TotalSeconds >= confirm)
            confirmed = RatioState.Alarm;
        else if (_aboveWarnSince is { } w && (ts - w).TotalSeconds >= confirm)
            confirmed = RatioState.Warning;

        if (confirmed == RatioState.Alarm) _latchedAlarm = true;
        _state = _latchedAlarm ? RatioState.Alarm : confirmed;
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

    public RatioSnapshot Snapshot() => new(
        _def.Key,
        _def.DisplayName,
        _state,
        _rawRatio,
        _hasEma ? _ema : double.NaN,
        _hasBaseline,
        _baseMean,
        _baseSigma,
        _hasBaseline && _hasEma ? _ema / _baseMean * 100.0 : double.NaN,
        WarnThreshold,
        AlarmThreshold,
        _slopePerMinute,
        _plasmaPresent,
        _numerator,
        _denominator,
        _hasEma && _emaVar > 0 ? Math.Sqrt(_emaVar) : double.NaN,
        _numSnr,
        _denSnr,
        _def.MonitorMode);
}
