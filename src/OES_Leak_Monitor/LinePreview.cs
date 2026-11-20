using System;
using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>One thing the line preview warns about. Never blocks Save (#112 Q5).</summary>
public sealed record LinePreviewWarning(string Text);

/// <summary>A line region placed on one spectrum frame, ready to draw (#112, #121).</summary>
public sealed record LinePreviewResult(
    LineWindowLayout Windows,
    double ShiftNm,
    LineMeasurement Reading,
    double ViewLo, double ViewHi,
    double SearchLo, double SearchHi,
    IReadOnlyList<LinePreviewWarning> Warnings)
{
    public bool HasBaselineWindows { get; init; } = true;
}

/// <summary>
/// What the Ratio Setup line preview shows for a region on a frame. Pure: no OxyPlot, no WPF.
/// The windows and the reading come from <see cref="LineIntensityExtractor"/> itself, so the
/// preview draws exactly what the engine measures — a number that differs from the live
/// reading would make the preview worse than none.
/// </summary>
public static class LinePreview
{
    /// <summary>Margin either side of the outermost window, nm.</summary>
    public const double ViewMarginNm = 2.0;

    public static LinePreviewResult Compute(float[] wavelengths, float[] intensities, LineRegion region,
                                            IEnumerable<SpectralLine> catalog)
    {
        var layout = LineIntensityExtractor.Layout(wavelengths, intensities, region);
        var reading = LineIntensityExtractor.Extract(wavelengths, intensities, region);
        bool raw = region.Mode == LineExtractMode.RawMean;
        double search = raw ? 0 : region.PeakSearchHalfWidthNm;

        double lo = Math.Min(region.CenterNm - search - region.HalfWidthNm, raw ? layout.SignalLo : layout.LeftLo);
        double hi = Math.Max(region.CenterNm + search + region.HalfWidthNm, raw ? layout.SignalHi : layout.RightHi);
        if (!raw)
        {
            // The windows travel with the peak search: cover where they could land.
            lo = Math.Min(lo, region.CenterNm - search - region.HalfWidthNm - region.BaselineGapNm - region.BaselineWidthNm);
            hi = Math.Max(hi, region.CenterNm + search + region.HalfWidthNm + region.EffectiveRightGapNm + region.EffectiveRightWidthNm);
        }

        var warnings = new List<LinePreviewWarning>();
        int n = Math.Min(wavelengths.Length, intensities.Length);
        double specLo = n > 0 ? wavelengths[0] : double.NaN, specHi = n > 0 ? wavelengths[n - 1] : double.NaN;

        void Check(string name, double a, double b)
        {
            if (n == 0 || a < specLo || b > specHi)
            {
                warnings.Add(new($"{name} window {a:0.0}–{b:0.0} nm is outside the spectrum ({specLo:0.0}–{specHi:0.0} nm)."));
                return;
            }
            int px = 0;
            for (int i = 0; i < n; i++) if (wavelengths[i] >= a && wavelengths[i] <= b) px++;
            // A zero-width raw window reads the interpolated value at the centre: not a pixel count.
            if (px < 2 && !(raw && region.HalfWidthNm <= 0))
                warnings.Add(new($"{name} window covers {px} pixel{(px == 1 ? "" : "s")} — the reading will be unstable."));
        }

        Check("Signal", layout.SignalLo, layout.SignalHi);
        if (!raw)
        {
            Check("Left baseline", layout.LeftLo, layout.LeftHi);
            Check("Right baseline", layout.RightLo, layout.RightHi);

            // "Not the line itself": anything inside the signal window or the search range is
            // the line being measured (or its own multiplet), not a neighbour.
            double ownLo = Math.Min(layout.SignalLo, region.CenterNm - search);
            double ownHi = Math.Max(layout.SignalHi, region.CenterNm + search);
            foreach (var l in catalog)
            {
                if (l.WavelengthNm >= ownLo && l.WavelengthNm <= ownHi) continue;
                string? side = l.WavelengthNm >= layout.LeftLo && l.WavelengthNm <= layout.LeftHi ? "Left"
                             : l.WavelengthNm >= layout.RightLo && l.WavelengthNm <= layout.RightHi ? "Right" : null;
                if (side is not null)
                    warnings.Add(new($"{side} baseline window overlaps {l.Species} {l.WavelengthNm:0.0} nm."));
            }
        }

        // Below the minimum pixel counts of #125 — advisory, like every preview warning.
        var pitch = LineWindowAdvice.PitchOf(wavelengths);
        string narrow = LineWindowAdvice.WarningText(region, pitch);
        if (narrow.Length > 0) warnings.Add(new(narrow));

        return new LinePreviewResult(layout, layout.Center - region.CenterNm, reading,
                                     lo - ViewMarginNm, hi + ViewMarginNm,
                                     region.CenterNm - search, region.CenterNm + search,
                                     warnings) { HasBaselineWindows = !raw };
    }
}
