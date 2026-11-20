using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// What a ratio trend chart draws (#103): the chart plots what is judged, on one 0-centred
/// "% vs reference" axis. A step-judged ratio draws its rise against the rolling baseline, a
/// Golden-Run-judged one its deviation from the Golden Run, and a ratio judged on both draws
/// both, the Golden Run line dashed.
/// </summary>
public class TrendProjectionTests
{
    private static RatioDefinition Def(bool gr, bool step, LineExtractMode signal = LineExtractMode.Integral,
                                       RatioRole role = RatioRole.Alarm) => new()
    {
        Key = "R", DisplayName = "NO / Ar", MonitorMode = MonitorMode.Ratio, Role = role,
        GoldenRunJudging = gr, StepJudging = step,
        Numerator = new LineRegion { Label = "NO", CenterNm = 235.6, Mode = signal },
        Denominator = new LineRegion { Label = "Ar", CenterNm = 750.4, Mode = LineExtractMode.RawMean },
    };

    [Fact]
    public void A_step_only_ratio_draws_its_rise_with_no_Golden_Run_line()
    {
        var s = Assert.Single(TrendProjection.SeriesFor(Def(gr: false, step: true)));
        Assert.Equal(TrendSeriesKind.StepRise, s.Kind);
        Assert.Equal("NO / Ar", s.Title);
        Assert.False(s.Dashed);

        // No Golden Run loaded: the rise still plots — the #101 empty chart.
        Assert.Equal(31.1, TrendProjection.Value(s.Kind, percentOfBaseline: double.NaN, stepRise: 0.311), 9);
    }

    [Fact]
    public void A_Golden_Run_only_ratio_draws_the_old_curve_shifted_to_zero()
    {
        var s = Assert.Single(TrendProjection.SeriesFor(Def(gr: true, step: false)));
        Assert.Equal(TrendSeriesKind.GoldenRunDeviation, s.Kind);
        Assert.Equal("NO / Ar", s.Title);
        Assert.False(s.Dashed);
        Assert.Equal(25, TrendProjection.Value(s.Kind, 125, double.NaN), 9);
        Assert.Equal(-9, TrendProjection.Value(s.Kind, 91, double.NaN), 9);   // drift below GR stays visible
    }

    [Fact]
    public void A_ratio_judged_on_both_paths_draws_the_rise_solid_and_the_Golden_Run_dashed()
    {
        var lines = TrendProjection.SeriesFor(Def(gr: true, step: true));
        Assert.Equal(new[] { TrendSeriesKind.StepRise, TrendSeriesKind.GoldenRunDeviation }, lines.Select(l => l.Kind));
        Assert.Equal(new[] { "NO / Ar", "NO / Ar (GR)" }, lines.Select(l => l.Title));
        Assert.Equal(new[] { false, true }, lines.Select(l => l.Dashed));
    }

    [Fact]
    public void A_TrendOnly_ratio_judged_on_neither_path_still_draws_its_Golden_Run_deviation()
    {
        var s = Assert.Single(TrendProjection.SeriesFor(Def(gr: false, step: false, role: RatioRole.TrendOnly)));
        Assert.Equal(TrendSeriesKind.GoldenRunDeviation, s.Kind);
        Assert.False(s.Dashed);
    }

    [Fact]
    public void A_raw_reading_cannot_be_step_judged_so_it_draws_its_sigma_score_deviation()
    {
        // A pedestal-carrying value ignores StepJudging (StepJudgingActive is false); its
        // PercentOfBaseline is the σ-score form, 100 at the baseline, so it is shifted the same way.
        var s = Assert.Single(TrendProjection.SeriesFor(Def(gr: true, step: true, signal: LineExtractMode.RawMean)));
        Assert.Equal(TrendSeriesKind.GoldenRunDeviation, s.Kind);
        Assert.Equal("NO / Ar", s.Title);
        Assert.Equal(20, TrendProjection.Value(s.Kind, 120, double.NaN), 9);
    }

    [Fact]
    public void Warm_up_and_a_missing_Golden_Run_leave_gaps()
    {
        Assert.True(double.IsNaN(TrendProjection.Value(TrendSeriesKind.StepRise, 100, double.NaN)));
        Assert.True(double.IsNaN(TrendProjection.Value(TrendSeriesKind.GoldenRunDeviation, double.NaN, 0.1)));
    }

    // --- #104: a selected ratio's thresholds -----------------------------------

    private static RatioDefinition Lab(bool gr, double warn, double alarm) =>
        new() {
            Key = "R", DisplayName = "OH / Ar", MonitorMode = MonitorMode.Ratio,
            GoldenRunJudging = gr, StepJudging = true, StepWarnRise = warn, StepAlarmRise = alarm,
            WarnFactor = 1.1, AlarmFactor = 1.2, SigmaWarn = 3, SigmaAlarm = 6,
            Numerator = new LineRegion { Label = "OH", CenterNm = 308.8, Mode = LineExtractMode.Integral },
            Denominator = new LineRegion { Label = "Ar", CenterNm = 750.4, Mode = LineExtractMode.RawMean },
        };

    [Fact]
    public void A_step_only_ratio_draws_its_step_rises_as_lines()
    {
        var lines = TrendProjection.ThresholdsFor(Lab(gr: false, 0.02, 0.03), goldenRun: null);
        Assert.Equal(new[] { 2.0, 3.0 }, lines.Select(l => System.Math.Round(l.Value, 9)));
        Assert.Equal(new[] { false, true }, lines.Select(l => l.IsAlarm));
        Assert.All(lines, l => Assert.Equal(TrendSeriesKind.StepRise, l.Kind));
        Assert.Equal("alarm +3 %", lines[1].Label);
    }

    [Fact]
    public void Golden_Run_lines_are_the_engines_thresholds_over_the_baseline_mean()
    {
        // Warn 0.33 / Alarm 0.36 on a 0.30 baseline: the engine already took the larger of the
        // factor and σ forms; the chart only maps them like the plotted deviation.
        var g = new GoldenRunLevels(false, 0.30, 0.33, 0.36, 3, 6);
        var lines = TrendProjection.ThresholdsFor(Lab(gr: true, 0.02, 0.03), g);
        var gr = lines.Where(l => l.Kind == TrendSeriesKind.GoldenRunDeviation).ToList();
        Assert.Equal(10, gr[0].Value, 6);
        Assert.Equal(20, gr[1].Value, 6);
        Assert.StartsWith("GR ", gr[0].Label);   // two paths on screen: say which
        Assert.Equal(4, lines.Count);
    }

    [Fact]
    public void A_pedestal_ratio_draws_its_sigma_score_lines()
    {
        var def = Def(gr: true, step: false, signal: LineExtractMode.RawMean);
        def.SigmaWarn = 3; def.SigmaAlarm = 6;
        var lines = TrendProjection.ThresholdsFor(def, new GoldenRunLevels(true, 5000, 5300, 5600, 3, 6));
        Assert.Equal(new[] { 20.0, 40.0 }, lines.Select(l => System.Math.Round(l.Value, 9)));
        Assert.Equal("warn +20 %", lines[0].Label);
    }

    [Fact]
    public void No_Golden_Run_loaded_means_no_Golden_Run_lines()
    {
        var lines = TrendProjection.ThresholdsFor(Lab(gr: true, 0.10, 0.15), goldenRun: null);
        Assert.All(lines, l => Assert.Equal(TrendSeriesKind.StepRise, l.Kind));
    }

    [Fact]
    public void The_axis_must_reach_the_selected_alarm_line_with_margin_and_include_zero()
    {
        var lines = TrendProjection.ThresholdsFor(Lab(gr: false, 0.02, 0.03), null);
        var (min, max) = TrendProjection.RequiredRange(lines)!.Value;
        Assert.Equal(0, min);
        Assert.True(max > 3.0, "OH's +3 % Alarm line would sit on the top edge");
        Assert.Null(TrendProjection.RequiredRange(System.Array.Empty<TrendThresholdLine>()));
    }

    // --- #105: warm-up bands ----------------------------------------------------

    [Fact]
    public void Warm_up_is_one_band_that_closes_at_the_first_judged_frame()
    {
        var bands = WarmupBands.From(new double[] { 0, 1, 2, 3, 4 }, new[] { true, true, true, false, false });
        var b = Assert.Single(bands);
        Assert.Equal((0.0, 3.0), b);
    }

    [Fact]
    public void A_capture_restart_opens_a_new_band()
    {
        var bands = WarmupBands.From(new double[] { 0, 1, 2, 10, 11, 12 }, new[] { true, false, false, true, true, false });
        Assert.Equal(new[] { (0.0, 1.0), (10.0, 12.0) }, bands);
    }

    [Fact]
    public void Retention_drops_old_bands_but_never_the_one_still_open()
    {
        var w = new WarmupBands();
        w.Observe(0, true); w.Observe(1, false);
        w.Observe(5, true); w.Observe(6, true);
        w.DropBefore(100);
        Assert.Equal((5.0, 6.0), Assert.Single(w.Bands));
    }
}
