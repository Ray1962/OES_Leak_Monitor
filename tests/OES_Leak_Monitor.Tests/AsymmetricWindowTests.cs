using System;
using System.Linq;
using System.Text.Json;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Asymmetric baseline windows (#112, #120). OH 308.8 sits on the blue wing of N2 C–B (1,0):
/// clean continuum lies further away on one side than the other, which one shared gap cannot
/// express. A right side that is not set must change nothing — readings, keys, or Golden Runs.
/// </summary>
public class AsymmetricWindowTests
{
    /// <summary>Continuum 100, a 1000-count line at 308.8, and a 500-count band at 305–307.5
    /// that a symmetric 1 nm gap puts in the left baseline window.</summary>
    private static (float[] wl, float[] y) Spectrum()
    {
        var wl = Enumerable.Range(0, 401).Select(i => (float)(290 + i * 0.1)).ToArray();
        var y = wl.Select(w => (float)(100
            + 1000 * Math.Exp(-Math.Pow((w - 308.8) / 0.15, 2))
            + (w >= 305 && w <= 307.5 ? 500 : 0))).ToArray();
        return (wl, y);
    }

    private static LineRegion Oh(double? rightGap = null, double? rightWidth = null, double leftGap = 1) => new()
    {
        Label = "OH 308.8", CenterNm = 308.8, HalfWidthNm = 0.5, BaselineGapNm = leftGap, BaselineWidthNm = 1,
        RightBaselineGapNm = rightGap, RightBaselineWidthNm = rightWidth,
        Mode = LineExtractMode.PeakHeight, PeakSearchHalfWidthNm = 0,
    };

    [Fact]
    public void A_wider_left_gap_alone_moves_the_left_window_off_the_neighbouring_band()
    {
        var (wl, y) = Spectrum();
        double symmetric = LineIntensityExtractor.Extract(wl, y, Oh()).Value;
        double asym = LineIntensityExtractor.Extract(wl, y, Oh(rightGap: 1, rightWidth: 1, leftGap: 4)).Value;
        Assert.True(symmetric < 800, $"the band lifts the symmetric baseline: {symmetric}");
        Assert.Equal(1000, asym, 0);

        var l = LineIntensityExtractor.Layout(wl, y, Oh(rightGap: 1, rightWidth: 2, leftGap: 4));
        Assert.Equal((303.3, 304.3), (Math.Round(l.LeftLo, 6), Math.Round(l.LeftHi, 6)));
        Assert.Equal((310.3, 312.3), (Math.Round(l.RightLo, 6), Math.Round(l.RightHi, 6)));
    }

    [Fact]
    public void An_unset_right_side_mirrors_the_left_and_keeps_todays_keys()
    {
        var r = Oh();
        Assert.False(r.HasSeparateRightSide);
        Assert.Equal("OH 308.8|PeakHeight|0.5|1|1|0", r.WindowKey);
        Assert.Equal("OH 308.8|PeakHeight|308.8|0.5|1|1|0", r.MeasurementKey);

        var (wl, y) = Spectrum();
        Assert.Equal(LineIntensityExtractor.Extract(wl, y, r).Value,
                     LineIntensityExtractor.Extract(wl, y, Oh(rightGap: 1, rightWidth: 1)).Value);
    }

    [Fact]
    public void A_set_right_side_is_part_of_what_the_region_measures()
    {
        Assert.NotEqual(Oh().WindowKey, Oh(rightGap: 1, rightWidth: 1).WindowKey);   // even when equal in value
        Assert.NotEqual(Oh(rightGap: 2).WindowKey, Oh(rightGap: 3).WindowKey);
        Assert.False(Oh().MeasuresSameAs(Oh(rightGap: 2)));
    }

    [Fact]
    public void A_settings_file_from_before_asymmetric_windows_loads_symmetric()
    {
        var r = JsonSerializer.Deserialize<LineRegion>(
            """{"label":"OH 308.8","centerNm":308.8,"halfWidthNm":1.5,"baselineGapNm":6.7,"baselineWidthNm":2,"mode":1}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Null(r.RightBaselineGapNm);
        Assert.Null(r.RightBaselineWidthNm);
        Assert.Equal(6.7, r.EffectiveRightGapNm);
    }

    // --- Ratio Setup ------------------------------------------------------------

    private static (LeakMonitorSettings s, RatioSetupViewModel vm, int[] saves) Tab(GoldenRun? run = null)
    {
        var s = new LeakMonitorSettings { Enabled = true };
        var oh = Oh(); oh.Mode = LineExtractMode.Integral; oh.BaselineGapNm = 6.7; oh.BaselineWidthNm = 2;
        s.Ratios.Add(new RatioDefinition
        {
            Key = "R_OH", DisplayName = "OH 308.8 / Ar 750.4", MonitorMode = MonitorMode.Ratio, Numerator = oh,
            Denominator = new LineRegion { Label = "Ar 750.4", CenterNm = 750.4, Mode = LineExtractMode.RawMean, PeakSearchHalfWidthNm = 0 },
        });
        if (run is not null) { s.GoldenRuns.Add(run); s.ActiveGoldenRun = run.Name; }
        var saves = new int[1];
        var vm = new RatioSetupViewModel(new LeakMonitorEngine(s), () => saves[0]++);
        vm.SetRole(engineerOrHigher: true);
        return (s, vm, saves);
    }

    [Fact]
    public void Ticking_seeds_the_right_side_from_the_left_and_unticking_clears_it()
    {
        var (s, vm, _) = Tab();
        var row = vm.Ratios.Single();
        Assert.False(row.SignalSeparateRight);
        row.SignalSeparateRight = true;
        Assert.Equal(6.7, row.SignalRightGapNm);
        Assert.Equal(2, row.SignalRightWidthNm);
        row.SignalRightGapNm = 1.0;
        Assert.Equal(1.0, row.ToDefinition().Numerator.RightBaselineGapNm);

        row.SignalSeparateRight = false;
        Assert.Null(row.ToDefinition().Numerator.RightBaselineGapNm);
        Assert.Null(row.ToDefinition().Numerator.RightBaselineWidthNm);
        Assert.Equal(6.7, row.SignalRightGapNm);   // shown as the mirrored left value
    }

    [Fact]
    public void A_new_line_resets_to_symmetric_and_a_mode_change_keeps_the_right_side()
    {
        var (_, vm, _) = Tab();
        var row = vm.Ratios.Single();
        row.SignalSeparateRight = true;
        row.SignalRightGapNm = 1.5;
        row.SignalMode = LineExtractMode.PeakHeight;
        Assert.True(row.SignalSeparateRight);
        Assert.Equal(1.5, row.SignalRightGapNm);

        row.SignalLine = vm.LineCatalog.Cast<SpectralLineOption>().First(o => o.Species == "Ar" && Math.Abs(o.WavelengthNm - 811.5) < 0.1);
        Assert.False(row.SignalSeparateRight);
    }

    [Theory]
    [InlineData("SignalRightGapNm", 25.0, "signal right gap")]
    [InlineData("SignalRightWidthNm", 0.0, "signal right width")]
    public void An_out_of_range_right_side_is_refused_naming_the_field(string field, double value, string named)
    {
        var (s, vm, saves) = Tab();
        var row = vm.Ratios.Single();
        row.SignalSeparateRight = true;
        typeof(RatioEditViewModel).GetProperty(field)!.SetValue(row, value);
        vm.SaveCommand.Execute(null);
        Assert.Equal(0, saves[0]);
        Assert.Contains(named, vm.StatusMessage);
        Assert.Null(s.Ratios[0].Numerator.RightBaselineGapNm);
    }

    [Fact]
    public void Saving_an_untouched_symmetric_row_keeps_its_Golden_Run_and_an_asymmetric_one_does_not()
    {
        var run = new GoldenRun { Name = "Lab" };
        var (s, vm, saves) = Tab(run);
        run.Baselines.Add(new GoldenRunRatioBaseline
        {
            Key = "R_OH", Mean = 0.3, Sigma = 0.002,
            NumeratorWindows = s.Ratios[0].Numerator.WindowKey, DenominatorWindows = s.Ratios[0].Denominator.WindowKey,
        });

        vm.SaveCommand.Execute(null);
        Assert.Equal(1, saves[0]);
        Assert.Equal(run.Baselines[0].NumeratorWindows, s.Ratios[0].Numerator.WindowKey);
        Assert.DoesNotContain("capture a new Golden Run", vm.StatusMessage);

        var row = vm.Ratios.Single();
        row.SignalSeparateRight = true;
        row.SignalRightGapNm = 1.0;
        vm.SaveCommand.Execute(null);
        Assert.Equal(2, saves[0]);
        Assert.NotEqual(run.Baselines[0].NumeratorWindows, s.Ratios[0].Numerator.WindowKey);
        Assert.Contains("capture a new Golden Run", vm.StatusMessage);
    }
}
