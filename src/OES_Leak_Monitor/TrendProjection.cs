using System;
using System.Collections.Generic;

namespace OES_Leak_Monitor;

/// <summary>Which quantity a trend line carries. Both are in "% vs reference" units: 0 means
/// "at the reference" (#102).</summary>
public enum TrendSeriesKind
{
    /// <summary>Rise against the rolling baseline, <c>StepRise × 100</c>.</summary>
    StepRise,

    /// <summary>Deviation from the Golden Run, <c>PercentOfBaseline − 100</c>.</summary>
    GoldenRunDeviation,
}

/// <summary>One line a ratio contributes to a trend chart.</summary>
public sealed record TrendSeriesSpec(string RatioKey, TrendSeriesKind Kind, string Title, bool Dashed);

/// <summary>A selected ratio's Warn or Alarm level for one judging path, in % vs reference.</summary>
public sealed record TrendThresholdLine(TrendSeriesKind Kind, bool IsAlarm, double Value, string Label);

/// <summary>The Golden Run half of a ratio's thresholds at one moment. The engine's Golden Run
/// levels move with the live noise (σ term), so they come from a snapshot, not the definition.</summary>
public readonly record struct GoldenRunLevels(bool HasPedestal, double BaselineMean,
                                              double WarnThreshold, double AlarmThreshold,
                                              double SigmaWarn, double SigmaAlarm);

/// <summary>
/// What a ratio trend chart draws — shared by the Leak Monitor and Ratio Review tabs so the
/// two cannot drift apart (#102). The rule is <b>the chart plots what is judged</b>: a
/// step-judged ratio plots its rise against the rolling baseline; a Golden-Run-judged one its
/// deviation from the Golden Run; both on one 0-centred axis. Pure: no OxyPlot, no WPF.
/// </summary>
public static class TrendProjection
{
    public const string AxisTitle = "% vs reference";
    public const string GoldenRunSuffix = " (GR)";

    /// <summary>The lines for a ratio judged as described. A ratio judged on neither path (a
    /// TrendOnly ratio with both off) still plots its Golden Run deviation, as it always did.</summary>
    public static IReadOnlyList<TrendSeriesSpec> SeriesFor(string key, string displayName,
                                                           bool goldenRun, bool step)
    {
        var list = new List<TrendSeriesSpec>(2);
        if (step) list.Add(new(key, TrendSeriesKind.StepRise, displayName, Dashed: false));
        if (goldenRun || !step)
            list.Add(step
                ? new(key, TrendSeriesKind.GoldenRunDeviation, displayName + GoldenRunSuffix, Dashed: true)
                : new(key, TrendSeriesKind.GoldenRunDeviation, displayName, Dashed: false));
        return list;
    }

    public static IReadOnlyList<TrendSeriesSpec> SeriesFor(RatioDefinition def) =>
        SeriesFor(def.Key, def.DisplayName, def.GoldenRunJudging, def.StepJudgingActive);

    /// <summary>The plotted value, NaN where there is none (no Golden Run; step warm-up).</summary>
    public static double Value(TrendSeriesKind kind, double percentOfBaseline, double stepRise) =>
        kind == TrendSeriesKind.StepRise ? stepRise * 100.0 : percentOfBaseline - 100.0;

    public static double Value(TrendSeriesKind kind, RatioSnapshot s) =>
        Value(kind, s.PercentOfBaseline, s.StepRise);

    /// <summary>
    /// The Warn / Alarm lines of a selected ratio, one pair per judging path it is judged on.
    /// Step lines are the rises themselves. Golden Run lines are the engine's own thresholds
    /// mapped through the same transform as the plotted deviation: a percentage of the baseline
    /// mean, or — for a value with a pedestal, whose plotted value is a σ-score at 20 points per
    /// SigmaWarn — 20 and 20·SigmaAlarm/SigmaWarn. A Golden Run path with no baseline loaded
    /// has no lines (pass <paramref name="goldenRun"/> as null or with NaN thresholds).
    /// </summary>
    public static IReadOnlyList<TrendThresholdLine> ThresholdsFor(RatioDefinition def, GoldenRunLevels? goldenRun)
    {
        var list = new List<TrendThresholdLine>(4);
        if (def.StepJudgingActive)
        {
            list.Add(new(TrendSeriesKind.StepRise, false, def.StepWarnRise * 100.0, $"warn +{def.StepWarnRise * 100.0:0.#} %"));
            list.Add(new(TrendSeriesKind.StepRise, true, def.StepAlarmRise * 100.0, $"alarm +{def.StepAlarmRise * 100.0:0.#} %"));
        }
        if (def.GoldenRunJudging && goldenRun is { } g)
        {
            double warn, alarm;
            if (g.HasPedestal)
            {
                double perSigma = g.SigmaWarn > 0 ? 20.0 / g.SigmaWarn : 20.0;
                warn = perSigma * g.SigmaWarn;
                alarm = perSigma * g.SigmaAlarm;
            }
            else
            {
                warn = g.WarnThreshold / g.BaselineMean * 100.0 - 100.0;
                alarm = g.AlarmThreshold / g.BaselineMean * 100.0 - 100.0;
            }
            string prefix = def.StepJudgingActive ? "GR " : "";
            if (double.IsFinite(warn)) list.Add(new(TrendSeriesKind.GoldenRunDeviation, false, warn, $"{prefix}warn +{warn:0.#} %"));
            if (double.IsFinite(alarm)) list.Add(new(TrendSeriesKind.GoldenRunDeviation, true, alarm, $"{prefix}alarm +{alarm:0.#} %"));
        }
        return list;
    }

    public static IReadOnlyList<TrendThresholdLine> ThresholdsFor(RatioDefinition def, RatioSnapshot s) =>
        ThresholdsFor(def, s.HasBaseline
            ? new GoldenRunLevels(s.HasPedestal, s.BaselineMean, s.WarnThreshold, s.AlarmThreshold, def.SigmaWarn, def.SigmaAlarm)
            : null);

    /// <summary>Headroom above the highest threshold, as a fraction of it, so the Alarm line
    /// and its label are not on the plot's top edge.</summary>
    public const double ThresholdMargin = 0.15;

    /// <summary>The Y range the axis must include for these lines — 0 and every line plus
    /// <see cref="ThresholdMargin"/> — or null when there are none (axis auto-scales).</summary>
    public static (double Min, double Max)? RequiredRange(IReadOnlyList<TrendThresholdLine> lines)
    {
        if (lines.Count == 0) return null;
        double min = 0, max = 0;
        foreach (var l in lines)
        {
            min = Math.Min(min, l.Value);
            max = Math.Max(max, l.Value);
        }
        return (min, max * (1 + ThresholdMargin));
    }
}

/// <summary>One projected line of a recorded run.</summary>
public sealed record RecordedTrendSeries(TrendSeriesSpec Spec, double[] Values);

/// <summary>A recorded ratio CSV projected the way the live chart draws it (#106).</summary>
public sealed record RecordedTrend(IReadOnlyList<RecordedTrendSeries> Series,
                                   IReadOnlyList<(int StartRow, int EndRow)> WarmupRows);

public static class RecordedTrendProjection
{
    /// <summary>
    /// Projects a recorded run. With the run's definitions (<paramref name="defs"/>, from its
    /// config snapshot) each ratio draws exactly what the live chart drew. Without them a ratio
    /// that recorded a <c>_stepRise</c> column was step-judged, and draws its rise plus its
    /// Golden Run deviation dashed — whether that path judged is unknown, so both are shown;
    /// any other ratio draws its Golden Run deviation. A σ-score series keeps its label.
    /// Warm-up is where a step-judged ratio's rise is empty.
    /// </summary>
    public static RecordedTrend Project(RatioTrendData d, IReadOnlyDictionary<string, RatioDefinition>? defs,
                                       Func<string, string> displayName)
    {
        var series = new List<RecordedTrendSeries>();
        var stepKeys = new List<string>();
        foreach (var key in d.RatioKeys)
        {
            bool hasStep = d.StepRise.ContainsKey(key);
            bool gr, step;
            if (defs is not null && defs.TryGetValue(key, out var def))
            {
                gr = def.GoldenRunJudging;
                step = def.StepJudgingActive && hasStep;
            }
            else { gr = true; step = hasStep; }
            if (step) stepKeys.Add(key);

            bool sigma = d.PctIsSigmaScore.TryGetValue(key, out var sg) && sg;
            foreach (var spec in TrendProjection.SeriesFor(key, displayName(key), gr, step))
            {
                var values = new double[d.RowCount];
                double[]? pct = d.Pct.TryGetValue(key, out var p) ? p : null;
                double[]? rise = d.StepRise.TryGetValue(key, out var r) ? r : null;
                for (int i = 0; i < d.RowCount; i++)
                    values[i] = TrendProjection.Value(spec.Kind, pct?[i] ?? double.NaN, rise?[i] ?? double.NaN);
                var s = sigma && spec.Kind == TrendSeriesKind.GoldenRunDeviation
                    ? spec with { Title = spec.Title + " (σ-score)" }
                    : spec;
                series.Add(new RecordedTrendSeries(s, values));
            }
        }

        var warm = new List<(int, int)>();
        if (stepKeys.Count > 0)
        {
            var xs = new double[d.RowCount];
            var warming = new bool[d.RowCount];
            for (int i = 0; i < d.RowCount; i++)
            {
                xs[i] = i;
                foreach (var k in stepKeys) if (double.IsNaN(d.StepRise[k][i])) { warming[i] = true; break; }
            }
            foreach (var (a, b) in WarmupBands.From(xs, warming)) warm.Add(((int)a, (int)b));
        }
        return new RecordedTrend(series, warm);
    }
}

/// <summary>
/// The step warm-up intervals on a trend chart's time axis (#105): one band per stretch in
/// which a step-judged ratio has no rolling baseline yet, so an empty stretch reads as expected
/// rather than as a fault. Fed one observation per frame, live or from a recorded file; a
/// capture restart opens a new band. Pure: X is whatever the caller's axis counts in.
/// </summary>
public sealed class WarmupBands
{
    private readonly List<(double Start, double End)> _bands = new();
    private bool _open;

    public IReadOnlyList<(double Start, double End)> Bands => _bands;

    public void Observe(double x, bool warming)
    {
        if (warming)
        {
            if (_open) _bands[^1] = (_bands[^1].Start, x);
            else { _bands.Add((x, x)); _open = true; }
        }
        else if (_open)
        {
            _bands[^1] = (_bands[^1].Start, x);   // close at the first judged frame: no gap
            _open = false;
        }
    }

    /// <summary>Forgets bands that ended before <paramref name="cutoff"/> (trend retention).</summary>
    public void DropBefore(double cutoff)
    {
        int keep = _open ? 1 : 0;   // the open band is still growing
        int n = 0;
        while (n < _bands.Count - keep && _bands[n].End < cutoff) n++;
        if (n > 0) _bands.RemoveRange(0, n);
    }

    public void Clear() { _bands.Clear(); _open = false; }

    /// <summary>Bands for a whole recorded series: <paramref name="warming"/>[i] at <paramref name="xs"/>[i].</summary>
    public static IReadOnlyList<(double Start, double End)> From(IReadOnlyList<double> xs, IReadOnlyList<bool> warming)
    {
        var w = new WarmupBands();
        for (int i = 0; i < xs.Count; i++) w.Observe(xs[i], warming[i]);
        return w.Bands;
    }
}
