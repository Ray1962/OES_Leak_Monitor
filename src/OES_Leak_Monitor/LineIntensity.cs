using System;

namespace OES_Leak_Monitor;

/// <summary>How a line's intensity is reduced to a single number.</summary>
public enum LineExtractMode
{
    /// <summary>Baseline-subtracted maximum inside the window — for narrow atomic lines.</summary>
    PeakHeight,
    /// <summary>Baseline-subtracted sum over the window — for molecular band heads.</summary>
    Integral,
}

/// <summary>
/// Defines how to pull one line's intensity out of a spectrum: a signal window centered
/// on <see cref="CenterNm"/>, plus a baseline window on each side used to subtract the
/// local continuum (which also cancels most wavelength-dependent window-fogging drift).
/// </summary>
public sealed class LineRegion
{
    /// <summary>Free-text label for UI / CSV (e.g. "OH 308.9").</summary>
    public string Label { get; set; } = "";

    /// <summary>Center wavelength of the line / band head, nm.</summary>
    public double CenterNm { get; set; }

    /// <summary>Half-width of the signal window, nm. Signal window = Center ± this.</summary>
    public double HalfWidthNm { get; set; } = 0.5;

    /// <summary>Gap between the signal window edge and the baseline window, nm.</summary>
    public double BaselineGapNm { get; set; } = 1.0;

    /// <summary>Width of each side baseline window, nm.</summary>
    public double BaselineWidthNm { get; set; } = 1.0;

    public LineExtractMode Mode { get; set; } = LineExtractMode.PeakHeight;

    /// <summary>
    /// If &gt; 0, the extractor first locates the strongest point within
    /// <see cref="CenterNm"/> ± this value and re-centers the signal and baseline
    /// windows on it — absorbing wavelength-calibration drift and band-head offset so
    /// a slightly shifted peak is still measured correctly. 0 pins the window to
    /// <see cref="CenterNm"/>.
    /// </summary>
    public double PeakSearchHalfWidthNm { get; set; } = 1.0;

    public LineRegion Clone() => (LineRegion)MemberwiseClone();
}

/// <summary>
/// Extracts a baseline-subtracted line intensity from a spectrum frame. Assumes the
/// wavelength array is ascending (standard for the OES SDK).
/// </summary>
public static class LineIntensityExtractor
{
    /// <summary>
    /// Returns the baseline-subtracted intensity for <paramref name="region"/>, or
    /// <see cref="double.NaN"/> if the spectrum does not cover the signal window.
    /// </summary>
    public static double Extract(float[] wavelengths, float[] intensities, LineRegion region)
    {
        if (wavelengths is null || intensities is null || region is null) return double.NaN;
        int n = Math.Min(wavelengths.Length, intensities.Length);
        if (n < 2) return double.NaN;

        // Re-center on the actual peak so a shifted line or a mis-calibrated wavelength
        // axis is still measured correctly (see LineRegion.PeakSearchHalfWidthNm).
        double center = region.PeakSearchHalfWidthNm > 0
            ? FindPeakWavelength(wavelengths, intensities, n,
                                 region.CenterNm, region.PeakSearchHalfWidthNm)
            : region.CenterNm;

        double sigLo = center - region.HalfWidthNm;
        double sigHi = center + region.HalfWidthNm;
        int s0 = LowerBound(wavelengths, n, sigLo);
        int s1 = UpperBound(wavelengths, n, sigHi) - 1;
        if (s0 >= n || s1 < s0) return double.NaN;

        double leftHi  = sigLo - region.BaselineGapNm;
        double leftLo  = leftHi - region.BaselineWidthNm;
        double rightLo = sigHi + region.BaselineGapNm;
        double rightHi = rightLo + region.BaselineWidthNm;

        var (leftMean,  leftHas)  = WindowMean(wavelengths, intensities, n, leftLo,  leftHi);
        var (rightMean, rightHas) = WindowMean(wavelengths, intensities, n, rightLo, rightHi);

        // Continuum baseline: a line through the two side means; fall back to a flat
        // baseline if only one side is available, or zero if neither is.
        double baseAt(double wl)
        {
            if (leftHas && rightHas)
            {
                double xL = (leftLo + leftHi) * 0.5, xR = (rightLo + rightHi) * 0.5;
                double slope = (rightMean - leftMean) / (xR - xL);
                return leftMean + slope * (wl - xL);
            }
            if (leftHas)  return leftMean;
            if (rightHas) return rightMean;
            return 0.0;
        }

        if (region.Mode == LineExtractMode.PeakHeight)
        {
            double peak = double.NegativeInfinity;
            for (int i = s0; i <= s1; i++)
            {
                double v = intensities[i] - baseAt(wavelengths[i]);
                if (v > peak) peak = v;
            }
            return peak;
        }

        double sum = 0.0;
        for (int i = s0; i <= s1; i++)
            sum += intensities[i] - baseAt(wavelengths[i]);
        return sum;
    }

    private static (double mean, bool has) WindowMean(
        float[] wl, float[] inten, int n, double lo, double hi)
    {
        int i0 = LowerBound(wl, n, lo);
        int i1 = UpperBound(wl, n, hi) - 1;
        if (i0 >= n || i1 < i0) return (0.0, false);
        double sum = 0.0;
        for (int i = i0; i <= i1; i++) sum += inten[i];
        return (sum / (i1 - i0 + 1), true);
    }

    /// <summary>
    /// Wavelength of the strongest (3-point-smoothed) sample within <paramref name="center"/>
    /// ± <paramref name="half"/>. Returns <paramref name="center"/> if the window is empty.
    /// The 3-point smoothing keeps a single hot pixel from capturing the search.
    /// </summary>
    private static double FindPeakWavelength(float[] wl, float[] inten, int n,
        double center, double half)
    {
        int i0 = LowerBound(wl, n, center - half);
        int i1 = UpperBound(wl, n, center + half) - 1;
        if (i0 >= n || i1 < i0) return center;

        int best = i0;
        double bestVal = double.NegativeInfinity;
        for (int i = i0; i <= i1; i++)
        {
            double v = inten[i]
                     + (i > 0     ? inten[i - 1] : inten[i])
                     + (i < n - 1 ? inten[i + 1] : inten[i]);
            if (v > bestVal) { bestVal = v; best = i; }
        }
        return wl[best];
    }

    /// <summary>First index whose wavelength is &gt;= <paramref name="v"/>.</summary>
    private static int LowerBound(float[] a, int n, double v)
    {
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int m = (lo + hi) >> 1;
            if (a[m] < v) lo = m + 1; else hi = m;
        }
        return lo;
    }

    /// <summary>First index whose wavelength is &gt; <paramref name="v"/>.</summary>
    private static int UpperBound(float[] a, int n, double v)
    {
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int m = (lo + hi) >> 1;
            if (a[m] <= v) lo = m + 1; else hi = m;
        }
        return lo;
    }
}
