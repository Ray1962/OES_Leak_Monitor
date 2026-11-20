using System;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The Ratio Setup line preview's model (#112, #121): where a region's windows fall on a
/// frame, what the engine reads there, and what is worth warning about. The preview is only
/// useful if its number is the engine's number.
/// </summary>
public class LinePreviewTests
{
    private static (float[] wl, float[] y) Spectrum(double step = 0.1, double peakAt = 308.8)
    {
        int count = (int)Math.Round(40 / step) + 1;
        var wl = Enumerable.Range(0, count).Select(i => (float)(290 + i * step)).ToArray();
        var y = wl.Select(w => (float)(100 + 0.5 * (w - 290) + 1000 * Math.Exp(-Math.Pow((w - peakAt) / 0.2, 2)))).ToArray();
        return (wl, y);
    }

    private static LineRegion Oh(LineExtractMode mode = LineExtractMode.Integral, double search = 0) => new()
    {
        Label = "OH 308.8", CenterNm = 308.8, HalfWidthNm = 0.5, BaselineGapNm = 1, BaselineWidthNm = 1,
        Mode = mode, PeakSearchHalfWidthNm = search,
    };

    private static readonly SpectralLine[] NoLines = Array.Empty<SpectralLine>();

    [Theory]
    [InlineData(LineExtractMode.PeakHeight, false)]
    [InlineData(LineExtractMode.Integral, false)]
    [InlineData(LineExtractMode.RawMean, false)]
    [InlineData(LineExtractMode.Integral, true)]
    public void The_reading_is_the_engines(LineExtractMode mode, bool asymmetric)
    {
        var (wl, y) = Spectrum();
        var r = Oh(mode, search: mode == LineExtractMode.RawMean ? 0 : 0.6);
        if (asymmetric) { r.RightBaselineGapNm = 3; r.RightBaselineWidthNm = 2; }
        var p = LinePreview.Compute(wl, y, r, NoLines);
        Assert.Equal(LineIntensityExtractor.Extract(wl, y, r).Value, p.Reading.Value);
    }

    [Fact]
    public void A_re_centred_window_is_drawn_where_it_was_measured()
    {
        var (wl, y) = Spectrum(peakAt: 309.2);
        var p = LinePreview.Compute(wl, y, Oh(search: 1.0), NoLines);
        Assert.Equal(0.4, p.ShiftNm, 3);
        Assert.Equal(309.2 - 0.5, p.Windows.SignalLo, 3);
        Assert.Equal(309.2 + 0.5 + 1, p.Windows.RightLo, 3);
        Assert.Equal((307.8, 309.8), (Math.Round(p.SearchLo, 6), Math.Round(p.SearchHi, 6)));
    }

    [Fact]
    public void The_view_covers_every_window_the_search_could_reach_plus_a_margin()
    {
        var (wl, y) = Spectrum();
        var r = Oh(search: 1.0); r.RightBaselineGapNm = 4; r.RightBaselineWidthNm = 2;
        var p = LinePreview.Compute(wl, y, r, NoLines);
        Assert.Equal(308.8 - 1 - 0.5 - 1 - 1 - LinePreview.ViewMarginNm, p.ViewLo, 6);
        Assert.Equal(308.8 + 1 + 0.5 + 4 + 2 + LinePreview.ViewMarginNm, p.ViewHi, 6);
    }

    [Fact]
    public void A_window_narrower_than_two_pixels_is_warned_about()
    {
        var (wl, y) = Spectrum(step: 0.5);
        var r = Oh(); r.BaselineWidthNm = 0.3;
        var p = LinePreview.Compute(wl, y, r, NoLines);
        Assert.Contains(p.Warnings, w => w.Text.StartsWith("Left baseline window covers"));
        Assert.DoesNotContain(LinePreview.Compute(wl, y, Oh(), NoLines).Warnings, w => w.Text.Contains("pixel"));
    }

    [Fact]
    public void A_window_outside_the_spectrum_is_warned_about()
    {
        var (wl, y) = Spectrum();
        var r = Oh(); r.RightBaselineGapNm = 19; r.RightBaselineWidthNm = 5;
        var p = LinePreview.Compute(wl, y, r, NoLines);
        Assert.Contains(p.Warnings, w => w.Text.StartsWith("Right baseline window") && w.Text.Contains("outside the spectrum"));
    }

    [Fact]
    public void A_baseline_window_over_another_catalogue_line_is_warned_about_but_not_the_line_itself()
    {
        var (wl, y) = Spectrum();
        var lines = new[] { new SpectralLine("OH", 308.9), new SpectralLine("OH", 306.8), new SpectralLine("uX", 312.0) };
        var p = LinePreview.Compute(wl, y, Oh(), lines);
        Assert.Equal(new[] { "Left baseline window overlaps OH 306.8 nm." }, p.Warnings.Select(w => w.Text));

        var r = Oh(); r.RightBaselineGapNm = 2.5;      // right window now 311.8–312.8
        Assert.Contains(LinePreview.Compute(wl, y, r, lines).Warnings, w => w.Text == "Right baseline window overlaps uX 312.0 nm.");
    }

    [Fact]
    public void A_raw_reading_has_no_baseline_windows_to_warn_about()
    {
        var (wl, y) = Spectrum();
        var p = LinePreview.Compute(wl, y, Oh(LineExtractMode.RawMean), new[] { new SpectralLine("OH", 306.8) });
        Assert.False(p.HasBaselineWindows);
        Assert.Empty(p.Warnings);
    }
}
