using System;
using System.Collections.Generic;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Pixel-based line windows (#125). The spectrometer axis steps 0.6 nm, so the old ±0.5 nm
/// default covered one or two pixels; windows are now sized in pixels, old ones warn, and
/// Apply suggested widths raises only what is too narrow.
/// </summary>
public class PixelWindowTests
{
    private static readonly IReadOnlyList<SpectralLineOption> Lines =
        SpectralLineCatalog.All.Select(l => new SpectralLineOption(l)).ToList();

    private static float[] Axis(double pitch, double lo = 200, double hi = 900)
    {
        int n = (int)Math.Round((hi - lo) / pitch) + 1;
        return Enumerable.Range(0, n).Select(i => (float)(lo + i * pitch)).ToArray();
    }

    // --- LineWindowAdvice ------------------------------------------------------

    [Fact]
    public void Defaults_at_06_nm_match_the_agreed_table()
    {
        var line = new SpectralLine("Ar", 750.4);
        var peak = LineWindowAdvice.Defaults(line, LineExtractMode.PeakHeight, 0.6);
        Assert.Equal(1.5, peak.HalfWidthNm, 6);
        Assert.Equal(1.2, peak.BaselineGapNm, 6);
        Assert.Equal(1.8, peak.BaselineWidthNm, 6);
        Assert.Equal(1.2, peak.PeakSearchHalfWidthNm, 6);
        Assert.Equal(1.0, LineWindowAdvice.Defaults(line, LineExtractMode.Integral, 0.6).HalfWidthNm, 6);
        var raw = LineWindowAdvice.Defaults(line, LineExtractMode.RawMean, 0.6);
        Assert.Equal(1.5, raw.HalfWidthNm, 6);
        Assert.Equal(0.0, raw.PeakSearchHalfWidthNm);
    }

    [Fact]
    public void Defaults_follow_another_pitch_and_fall_back_to_06()
    {
        var line = new SpectralLine("Ar", 750.4);
        Assert.Equal(0.75, LineWindowAdvice.Defaults(line, LineExtractMode.PeakHeight, 0.3).HalfWidthNm, 6);
        Assert.Equal(1.5, LineWindowAdvice.Defaults(line, LineExtractMode.PeakHeight, null).HalfWidthNm, 6);
    }

    [Fact]
    public void Pitch_is_the_median_axis_spacing()
    {
        Assert.Equal(0.6, LineWindowAdvice.PitchOf(Axis(0.6))!.Value, 4);
        Assert.Null(LineWindowAdvice.PitchOf(new float[] { 300f }));
        Assert.Null(LineWindowAdvice.PitchOf(null));
    }

    [Fact]
    public void Suggested_windows_meet_their_minimum_and_old_defaults_do_not()
    {
        var line = new SpectralLine("Ar", 750.4);
        foreach (var mode in new[] { LineExtractMode.PeakHeight, LineExtractMode.Integral, LineExtractMode.RawMean })
            Assert.False(LineWindowAdvice.IsBelowMinimum(LineWindowAdvice.Defaults(line, mode, 0.6), 0.6));

        var old = new LineRegion { CenterNm = 750.4, HalfWidthNm = 0.5, BaselineGapNm = 1, BaselineWidthNm = 1,
                                   PeakSearchHalfWidthNm = 1, Mode = LineExtractMode.PeakHeight };
        var cov = LineWindowAdvice.Check(old, 0.6).ToDictionary(c => c.Name);
        Assert.Equal(1, cov["Signal"].Pixels);
        Assert.Equal(1, cov["Baseline"].Pixels);
        Assert.Contains("Signal 1 px (needs ≥5)", LineWindowAdvice.WarningText(old, 0.6));
    }

    [Fact]
    public void Apply_raises_only_narrow_windows_and_keeps_a_separate_right_side()
    {
        var r = new LineRegion
        {
            CenterNm = 308.9, HalfWidthNm = 0.5, BaselineGapNm = 1.0, BaselineWidthNm = 2.4,
            RightBaselineGapNm = 6.7, RightBaselineWidthNm = 1.0,
            PeakSearchHalfWidthNm = 1.0, Mode = LineExtractMode.PeakHeight,
        };
        var a = LineWindowAdvice.ApplySuggested(r, 0.6);
        Assert.Equal(1.5, a.HalfWidthNm, 6);
        Assert.Equal(1.2, a.BaselineGapNm, 6);
        Assert.Equal(2.4, a.BaselineWidthNm, 6);        // already wide enough
        Assert.True(a.HasSeparateRightSide);
        Assert.Equal(6.7, a.EffectiveRightGapNm, 6);    // already wide enough
        Assert.Equal(1.8, a.EffectiveRightWidthNm, 6);
        Assert.Equal(1.2, a.PeakSearchHalfWidthNm, 6);
        Assert.Equal(308.9, a.CenterNm);
        Assert.False(LineWindowAdvice.IsBelowMinimum(a, 0.6));
    }

    [Fact]
    public void An_integral_signal_window_is_shown_but_never_widened()
    {
        var r = new LineRegion { CenterNm = 235.6, HalfWidthNm = 1.2, BaselineGapNm = 4, BaselineWidthNm = 2,
                                 PeakSearchHalfWidthNm = 0, Mode = LineExtractMode.Integral };
        Assert.Contains("Signal 4 px", LineWindowAdvice.CoverageText(r, 0.6));
        Assert.DoesNotContain("Signal", LineWindowAdvice.WarningText(r, 0.6));
        Assert.Equal(1.2, LineWindowAdvice.ApplySuggested(r, 0.6).HalfWidthNm);
    }

    [Fact]
    public void A_raw_reading_checks_only_its_signal_window()
    {
        var r = new LineRegion { CenterNm = 237, HalfWidthNm = 1.5, BaselineGapNm = 0.1, BaselineWidthNm = 0.1,
                                 PeakSearchHalfWidthNm = 0, Mode = LineExtractMode.RawMean };
        Assert.Single(LineWindowAdvice.Check(r, 0.6));
        Assert.False(LineWindowAdvice.IsBelowMinimum(r, 0.6));
    }

    // --- Ratio Setup ------------------------------------------------------------

    private static RatioDefinition OldRatio() => new()
    {
        Key = "R_old", MonitorMode = MonitorMode.Ratio,
        Numerator = new LineRegion { Label = "N₂ 357.7", CenterNm = 357.7, HalfWidthNm = 0.5, BaselineGapNm = 1,
                                     BaselineWidthNm = 1, PeakSearchHalfWidthNm = 1, Mode = LineExtractMode.PeakHeight },
        Denominator = new LineRegion { Label = "Ar 750.4", CenterNm = 750.4, HalfWidthNm = 0.5, BaselineGapNm = 1,
                                       BaselineWidthNm = 1, PeakSearchHalfWidthNm = 1, Mode = LineExtractMode.PeakHeight },
    };

    [Fact]
    public void Picking_a_line_fills_widths_for_the_current_pitch()
    {
        double? pitch = 0.3;
        var row = new RatioEditViewModel(OldRatio(), Lines, () => pitch);
        row.SignalLine = Lines.First(l => l.Species == "O" && Math.Abs(l.WavelengthNm - 777.2) < 0.05);
        Assert.Equal(0.75, row.SignalHalfWidthNm, 6);
        Assert.Equal(0.6, row.SignalBaselineGapNm, 6);
        Assert.Equal(0.9, row.SignalBaselineWidthNm, 6);
    }

    [Fact]
    public void An_old_row_is_left_alone_but_warns()
    {
        var row = new RatioEditViewModel(OldRatio(), Lines, () => 0.6);
        Assert.Equal(0.5, row.ToDefinition().Numerator.HalfWidthNm);
        Assert.True(row.HasNarrowWindows);
        Assert.Contains("Signal 1 px", row.SignalCoverageText);
        Assert.Contains("needs ≥5", row.SignalCoverageWarning);
        Assert.Contains("needs ≥5", row.ReferenceCoverageWarning);
    }

    [Fact]
    public void Apply_widens_both_lines_and_changes_the_window_keys()
    {
        var def = OldRatio();
        var row = new RatioEditViewModel(def.Clone(), Lines, () => 0.6);
        Assert.True(row.ApplySuggestedWidthsCommand.CanExecute(null));
        row.ApplySuggestedWidths();
        var after = row.ToDefinition();
        Assert.False(row.HasNarrowWindows);
        Assert.Equal("", row.SignalCoverageWarning);
        Assert.Equal(1.5, after.Numerator.HalfWidthNm, 6);
        Assert.Equal(1.5, after.Denominator.HalfWidthNm, 6);
        // A changed WindowKey is what makes the Golden Run's baseline inapplicable.
        Assert.NotEqual(def.Numerator.WindowKey, after.Numerator.WindowKey);
        Assert.NotEqual(def.Denominator.WindowKey, after.Denominator.WindowKey);
        Assert.False(row.ApplySuggestedWidthsCommand.CanExecute(null));
    }

    [Fact]
    public void Editing_a_window_updates_the_coverage()
    {
        var row = new RatioEditViewModel(OldRatio(), Lines, () => 0.6);
        var seen = new List<string?>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        row.SignalHalfWidthNm = 1.5;
        Assert.Contains(nameof(RatioEditViewModel.SignalCoverageText), seen);
        Assert.Contains("Signal 5 px", row.SignalCoverageText);
    }

    // --- factory windows ----------------------------------------------------------

    private static IEnumerable<(string Name, LineRegion Region)> FactoryRegions()
    {
        foreach (var r in LeakMonitorSettings.CreateDefault().Ratios)
        {
            yield return ($"{r.Key} numerator", r.Numerator);
            if (r.MonitorMode == MonitorMode.Ratio) yield return ($"{r.Key} denominator", r.Denominator);
        }
        foreach (var p in ReferenceLineCatalog.All) yield return ($"preset {p.Name}", p.CreateRegion());
    }

    [Fact]
    public void Every_factory_and_preset_window_meets_its_minimum_at_06_nm()
    {
        var bad = FactoryRegions()
            .Where(x => LineWindowAdvice.IsBelowMinimum(x.Region, 0.6))
            .Select(x => $"{x.Name}: {LineWindowAdvice.WarningText(x.Region, 0.6)}").ToList();
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    // Overlaps no placement at the minimum widths can avoid (searched 1.2–8 nm either side).
    // Ar 750.4: Cl 754.7 on the right — chlorine is not in the Ar/air plasma; the left side is
    // clean. N₂ 357.7 sits in dense band structure (N₂ 353.7 band head, Cr, Zr, He) with no
    // clean window within 8 nm. #130 checks both on the 11/19 recordings instead.
    private static readonly (string Label, string Line)[] AcceptedOverlaps =
    {
        ("Ar 750.4", "Cl 754.7"),
        ("N₂ 357.7", "N2 353.7"), ("N₂ 357.7", "Zr 354.8"), ("N₂ 357.7", "Cr 360.5"), ("N₂ 357.7", "He 361.4"),
        ("N₂ 357.7", "Cr 359.3"), ("N₂ 357.7", "Zr 360.1"), ("N₂ 357.7", "CN 359.0"),
    };

    private static bool Accepted(LineRegion r, string warning) =>
        AcceptedOverlaps.Any(a => a.Label == r.Label && warning.Contains($"overlaps {a.Line} nm"));

    [Fact]
    public void No_factory_or_preset_baseline_window_overlaps_a_catalogue_neighbour()
    {
        // A peak at each region's centre on the 0.6 nm axis keeps the search on the nominal centre.
        var wl = Axis(0.6);
        var bad = new List<string>();
        foreach (var (name, region) in FactoryRegions())
        {
            var y = wl.Select(x => (float)(100 + 1000 * Math.Exp(-Math.Pow((x - region.CenterNm) / 0.6, 2)))).ToArray();
            var p = LinePreview.Compute(wl, y, region, SpectralLineCatalog.All);
            bad.AddRange(p.Warnings.Where(w => w.Text.Contains("overlaps") && !Accepted(region, w.Text))
                                   .Select(w => $"{name}: {w.Text}"));
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }
}
