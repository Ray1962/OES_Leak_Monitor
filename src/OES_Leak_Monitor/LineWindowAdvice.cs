using System;
using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>Pixel coverage of one window of a line region against its minimum (#125).</summary>
public sealed record WindowCoverage(string Name, int Pixels, int Minimum)
{
    public bool BelowMinimum => Pixels < Minimum;
}

/// <summary>
/// How wide a line region's windows should be, in pixels of the spectrometer axis (#125).
/// Regions are stored in nm (so <see cref="LineRegion.WindowKey"/> does not change); this
/// converts between the two with the pitch of the spectrum at hand. Pure: no UI.
/// <para>The minimums: a peak needs ≈5 pixels for its top to land inside the window and for
/// RawMean to average any noise; a baseline
/// window under 3 pixels gives an unstable floor and noise estimate; a gap under 2 lets the
/// line's wings into the baseline; a search under 2 cannot follow a one-pixel drift.</para>
/// <para>An Integral signal window has no minimum: how much of a molecular band to integrate
/// depends on the band's shape, not the pixel count. On the 11/19 recordings widening the
/// tuned NO 235.6 and OH 308.8 windows to 7 pixels cut their DV=300 step SNR by 25–35 % —
/// the extra pixels do not respond to the leak — while plasma flicker, not pixel noise,
/// dominated the noise either way. A new Integral line starts at the old ±1.0 nm.</para>
/// </summary>
public static class LineWindowAdvice
{
    /// <summary>Pitch of the lab and site spectrometers (200.0, 200.6, …): used when no
    /// spectrum is available to measure it from.</summary>
    public const double FallbackPitchNm = 0.6;

    public const int SignalPixels = 5;
    /// <summary>Starting half-width of a new Integral line, nm; not pixel-based (see above).</summary>
    public const double IntegralHalfWidthNm = 1.0;
    public const int BaselinePixels = 3;
    public const int GapPixels = 2;
    public const int SearchPixels = 2;

    private const double Eps = 1e-6;

    /// <summary>Median spacing of the wavelength axis, or null when it has fewer than two
    /// increasing points.</summary>
    public static double? PitchOf(IReadOnlyList<float>? wavelengths)
    {
        if (wavelengths is null || wavelengths.Count < 2) return null;
        var d = new List<double>(wavelengths.Count - 1);
        for (int i = 1; i < wavelengths.Count; i++)
        {
            double s = wavelengths[i] - wavelengths[i - 1];
            if (s > 0 && !double.IsNaN(s)) d.Add(s);
        }
        if (d.Count == 0) return null;
        d.Sort();
        double m = d.Count % 2 == 1 ? d[d.Count / 2] : (d[d.Count / 2 - 1] + d[d.Count / 2]) / 2;
        return Math.Round(m, 4);
    }

    private static double P(double? pitchNm) =>
        pitchNm is double p && p > 0 && !double.IsNaN(p) ? p : FallbackPitchNm;

    /// <summary>Suggested signal half-width, nm: PeakHeight/RawMean span the minimum pixel
    /// count; Integral starts at <see cref="IntegralHalfWidthNm"/>.</summary>
    public static double HalfWidthNm(LineExtractMode mode, double? pitchNm) =>
        mode == LineExtractMode.Integral ? IntegralHalfWidthNm : Math.Round(SignalPixels * P(pitchNm) / 2, 4);

    public static double GapNm(double? pitchNm) => Math.Round(GapPixels * P(pitchNm), 4);
    public static double BaselineWidthNm(double? pitchNm) => Math.Round(BaselinePixels * P(pitchNm), 4);

    /// <summary>Suggested peak search half-width; 0 for RawMean, which never searches.</summary>
    public static double PeakSearchNm(LineExtractMode mode, double? pitchNm) =>
        mode == LineExtractMode.RawMean ? 0.0 : Math.Round(SearchPixels * P(pitchNm), 4);

    /// <summary>A fresh region for a catalogue line with the suggested windows.</summary>
    public static LineRegion Defaults(SpectralLine line, LineExtractMode mode, double? pitchNm) => new()
    {
        Label = $"{line.Species} {line.WavelengthNm:0.###}",
        CenterNm = line.WavelengthNm,
        Mode = mode,
        HalfWidthNm = HalfWidthNm(mode, pitchNm),
        BaselineGapNm = GapNm(pitchNm),
        BaselineWidthNm = BaselineWidthNm(pitchNm),
        PeakSearchHalfWidthNm = PeakSearchNm(mode, pitchNm),
    };

    /// <summary>Pixels a window of <paramref name="widthNm"/> is sure to cover wherever it
    /// falls on the axis.</summary>
    public static int Pixels(double widthNm, double? pitchNm) =>
        widthNm <= 0 || double.IsNaN(widthNm) ? 0 : (int)Math.Floor(widthNm / P(pitchNm) + Eps);

    /// <summary>Coverage of each window the region's mode uses. RawMean uses only the signal
    /// window; a zero-width RawMean reads one interpolated point and is not checked.</summary>
    public static IReadOnlyList<WindowCoverage> Check(LineRegion r, double? pitchNm)
    {
        var list = new List<WindowCoverage>();
        if (r.Mode == LineExtractMode.RawMean)
        {
            if (r.HalfWidthNm > 0)
                list.Add(new("Signal", Pixels(2 * r.HalfWidthNm, pitchNm), SignalPixels));
            return list;
        }
        // Integral: shown, never below a minimum.
        list.Add(new("Signal", Pixels(2 * r.HalfWidthNm, pitchNm),
                     r.Mode == LineExtractMode.Integral ? 0 : SignalPixels));
        // A search of 0 pins the windows on purpose (the lab's NO/OH bands read a fixed
        // position); only a search that exists but cannot follow a pixel is too narrow.
        list.Add(new("Peak search", Pixels(r.PeakSearchHalfWidthNm, pitchNm),
                     r.PeakSearchHalfWidthNm <= 0 ? 0 : SearchPixels));
        if (r.HasSeparateRightSide)
        {
            list.Add(new("Left gap", Pixels(r.BaselineGapNm, pitchNm), GapPixels));
            list.Add(new("Left baseline", Pixels(r.BaselineWidthNm, pitchNm), BaselinePixels));
            list.Add(new("Right gap", Pixels(r.EffectiveRightGapNm, pitchNm), GapPixels));
            list.Add(new("Right baseline", Pixels(r.EffectiveRightWidthNm, pitchNm), BaselinePixels));
        }
        else
        {
            list.Add(new("Gap", Pixels(r.BaselineGapNm, pitchNm), GapPixels));
            list.Add(new("Baseline", Pixels(r.BaselineWidthNm, pitchNm), BaselinePixels));
        }
        return list;
    }

    public static bool IsBelowMinimum(LineRegion r, double? pitchNm) =>
        Check(r, pitchNm).Any(c => c.BelowMinimum);

    /// <summary>A copy with every too-narrow window raised to its suggestion; windows already
    /// wide enough, the centre, an Integral signal window and a separate right side are kept
    /// as they are.</summary>
    public static LineRegion ApplySuggested(LineRegion r, double? pitchNm)
    {
        var c = r.Clone();
        static double Up(double v, double s) => v + Eps < s ? s : v;
        if (c.Mode == LineExtractMode.RawMean)
        {
            if (c.HalfWidthNm > 0) c.HalfWidthNm = Up(c.HalfWidthNm, HalfWidthNm(c.Mode, pitchNm));
            return c;
        }
        if (c.Mode != LineExtractMode.Integral) c.HalfWidthNm = Up(c.HalfWidthNm, HalfWidthNm(c.Mode, pitchNm));
        if (c.PeakSearchHalfWidthNm > 0) c.PeakSearchHalfWidthNm = Up(c.PeakSearchHalfWidthNm, PeakSearchNm(c.Mode, pitchNm));
        c.BaselineGapNm = Up(c.BaselineGapNm, GapNm(pitchNm));
        c.BaselineWidthNm = Up(c.BaselineWidthNm, BaselineWidthNm(pitchNm));
        if (c.HasSeparateRightSide)
        {
            c.RightBaselineGapNm = Up(c.EffectiveRightGapNm, GapNm(pitchNm));
            c.RightBaselineWidthNm = Up(c.EffectiveRightWidthNm, BaselineWidthNm(pitchNm));
        }
        return c;
    }

    /// <summary>One line for the editor: "Signal 2 px · Gap 1 px · …".</summary>
    public static string CoverageText(LineRegion r, double? pitchNm) =>
        string.Join(" · ", Check(r, pitchNm).Select(c => $"{c.Name} {c.Pixels} px")) +
        $"  (at {P(pitchNm):0.###} nm/px)";

    /// <summary>Warning for the windows below their minimum, or empty.</summary>
    public static string WarningText(LineRegion r, double? pitchNm)
    {
        var low = Check(r, pitchNm).Where(c => c.BelowMinimum).ToList();
        return low.Count == 0 ? "" :
            "Too narrow: " + string.Join(", ", low.Select(c => $"{c.Name} {c.Pixels} px (needs ≥{c.Minimum})"));
    }
}
