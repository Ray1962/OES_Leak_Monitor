using System;
using System.Collections.Generic;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Ratio Review in "% vs reference" (#106, #107): a recorded run is drawn the way the live chart
/// drew it, judged by the configuration that was in force when it was recorded — which the
/// <c>_config_HHmmss.json</c> snapshot beside it records, not today's settings.
/// </summary>
public class RecordedTrendTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 18, 11, 33);

    private static RatioTrendData Data(bool withStep, bool sigma = false)
    {
        var ts = Enumerable.Range(0, 6).Select(i => T0.AddSeconds(i)).ToArray();
        var step = new Dictionary<string, double[]>();
        if (withStep) step["R_NO"] = new[] { double.NaN, double.NaN, 0.01, 0.05, 0.30, 0.31 };
        return new RatioTrendData
        {
            RatioKeys = new[] { "R_NO", "R_A" },
            RatioLabels = new Dictionary<string, string> { ["R_NO"] = "NO / Ar", ["R_A"] = "A" },
            Timestamps = ts,
            Raw = new Dictionary<string, double[]> { ["R_NO"] = new double[6], ["R_A"] = new double[6] },
            Pct = new Dictionary<string, double[]>
            {
                ["R_NO"] = new double[] { 140, 140, 141, 145, 170, 171 },
                ["R_A"] = new double[] { 100, 101, 99, 100, 120, 121 },
            },
            PctIsSigmaScore = new Dictionary<string, bool> { ["R_A"] = sigma },
            StepRise = step,
            States = Enumerable.Repeat("Normal", 6).ToArray(),
        };
    }

    private static string Name(string key) => key == "R_NO" ? "NO / Ar" : "A";

    private static RatioDefinition Def(string key, bool gr, bool step) => new()
    {
        Key = key, DisplayName = Name(key), MonitorMode = MonitorMode.Ratio,
        GoldenRunJudging = gr, StepJudging = step, StepWarnRise = 0.10, StepAlarmRise = 0.15,
        Numerator = new LineRegion { Label = "N", CenterNm = 235.6, Mode = LineExtractMode.Integral },
        Denominator = new LineRegion { Label = "Ar", CenterNm = 750.4, Mode = LineExtractMode.RawMean },
    };

    // --- #106: the projection ----------------------------------------------------

    [Fact]
    public void An_old_file_without_step_columns_plots_its_Golden_Run_deviation_shifted_to_zero()
    {
        var t = RecordedTrendProjection.Project(Data(withStep: false), defs: null, Name);
        Assert.All(t.Series, s => Assert.Equal(TrendSeriesKind.GoldenRunDeviation, s.Spec.Kind));
        Assert.Equal(new[] { 40.0, 40, 41, 45, 70, 71 }, t.Series[0].Values);
        Assert.Empty(t.WarmupRows);
    }

    [Fact]
    public void Without_a_snapshot_a_step_column_draws_rise_solid_and_Golden_Run_dashed()
    {
        var t = RecordedTrendProjection.Project(Data(withStep: true), defs: null, Name);
        var no = t.Series.Where(s => s.Spec.RatioKey == "R_NO").ToList();
        Assert.Equal(new[] { "NO / Ar", "NO / Ar (GR)" }, no.Select(s => s.Spec.Title));
        Assert.Equal(31, no[0].Values[^1], 9);
        Assert.True(no[1].Spec.Dashed);
    }

    [Fact]
    public void With_the_runs_definitions_a_step_only_ratio_draws_only_its_rise()
    {
        var defs = new Dictionary<string, RatioDefinition>
        {
            ["R_NO"] = Def("R_NO", gr: false, step: true),
            ["R_A"] = Def("R_A", gr: true, step: false),
        };
        var t = RecordedTrendProjection.Project(Data(withStep: true), defs, Name);
        Assert.Equal(new[] { (TrendSeriesKind.StepRise, "NO / Ar"), (TrendSeriesKind.GoldenRunDeviation, "A") },
                     t.Series.Select(s => (s.Spec.Kind, s.Spec.Title)));
    }

    [Fact]
    public void A_sigma_score_series_keeps_its_label()
    {
        var t = RecordedTrendProjection.Project(Data(withStep: false, sigma: true), null, Name);
        Assert.Equal("A (σ-score)", t.Series.Single(s => s.Spec.RatioKey == "R_A").Spec.Title);
    }

    [Fact]
    public void Warm_up_is_where_the_step_rise_was_not_yet_recorded()
    {
        var t = RecordedTrendProjection.Project(Data(withStep: true), null, Name);
        Assert.Equal((0, 2), Assert.Single(t.WarmupRows));
    }

    // --- #107: which snapshot judged the run ----------------------------------------

    [Fact]
    public void The_newest_snapshot_not_after_the_run_ended_is_the_one_in_force()
    {
        // 11/19: P_Ratio_1119113835 ran until ~11:42; its snapshot was stamped 11:39:43, after
        // its first row — written when the intensity recording opened.
        var end = new DateTime(2026, 11, 19, 11, 42, 0);
        var names = new[] { @"C:\d\_config_105004.json", @"C:\d\_config_113943.json", @"C:\d\_config_114213.json", @"C:\d\P_Ratio_1119113835.csv" };
        Assert.Equal(@"C:\d\_config_113943.json", RecordedJudging.Choose(names, end));
    }

    [Fact]
    public void A_snapshot_written_after_the_run_ended_belongs_to_a_later_run()
    {
        Assert.Null(RecordedJudging.Choose(new[] { "_config_181500.json" }, T0));
        Assert.Null(RecordedJudging.Choose(Array.Empty<string>(), T0));
    }

    [Fact]
    public void A_snapshot_from_before_step_judging_reads_as_Golden_Run_only()
    {
        const string json = """
            {"leakMonitor":{"ratios":[{"key":"R_NO","displayName":"NO / Ar","warnFactor":1.1,"alarmFactor":1.2}]}}
            """;
        var defs = RecordedJudging.Parse(json);
        Assert.True(defs["R_NO"].GoldenRunJudging);
        Assert.False(defs["R_NO"].StepJudging);
    }

    [Fact]
    public void A_recorded_step_ratio_draws_the_thresholds_its_snapshot_recorded()
    {
        var defs = RecordedJudging.Parse("""
            {"leakMonitor":{"ratios":[{"key":"R_NO","displayName":"NO / Ar","goldenRunJudging":false,"stepJudging":true,
              "stepWarnRise":0.1,"stepAlarmRise":0.15,
              "numerator":{"label":"N","centerNm":235.6,"mode":"Integral"},
              "denominator":{"label":"Ar","centerNm":750.4,"mode":"RawMean"}}]}}
            """);
        var lines = TrendProjection.ThresholdsFor(defs["R_NO"], goldenRun: null);
        Assert.Equal(new[] { 10.0, 15.0 }, lines.Select(l => Math.Round(l.Value, 9)));
    }
}
