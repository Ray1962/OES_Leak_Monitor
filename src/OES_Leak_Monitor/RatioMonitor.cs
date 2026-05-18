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
    /// <summary>No Golden Run baseline captured for this ratio yet.</summary>
    NoBaseline,
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
    bool PlasmaPresent);

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
    private DateTime _lastTs;
    private double _rawRatio = double.NaN;

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

    public double WarnThreshold => _hasBaseline
        ? Math.Max(_def.WarnFactor * _baseMean, _baseMean + _def.SigmaWarn * _baseSigma)
        : double.NaN;

    public double AlarmThreshold => _hasBaseline
        ? Math.Max(_def.AlarmFactor * _baseMean, _baseMean + _def.SigmaAlarm * _baseSigma)
        : double.NaN;

    /// <summary>Feeds one frame's raw ratio into the state machine.</summary>
    public void Update(double rawRatio, DateTime ts, bool plasmaPresent)
    {
        _plasmaPresent = plasmaPresent;
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

        if (!_hasEma)
        {
            _ema = rawRatio;
            _hasEma = true;
        }
        else
        {
            double dt = (ts - _lastTs).TotalSeconds;
            if (dt <= 0) dt = 0.001;
            double tau = Math.Max(0.1, _def.EmaTauSeconds);
            double alpha = 1.0 - Math.Exp(-dt / tau);
            _ema += alpha * (rawRatio - _ema);
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
        _plasmaPresent);
}
