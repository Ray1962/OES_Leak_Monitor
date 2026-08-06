using System;

namespace OES_Leak_Monitor;

/// <summary>How a line's intensity is reduced to a single number.</summary>
public enum LineExtractMode
{
    /// <summary>Baseline-subtracted maximum inside the window — for narrow atomic lines.</summary>
    PeakHeight,
    /// <summary>Baseline-subtracted integral over the window, in counts·nm — for molecular
    /// band heads.</summary>
    Integral,
    /// <summary>
    /// The raw intensity at the wavelength — no continuum subtraction, no peak search. For a
    /// position where there is <em>no peak to find</em>: the other two modes both estimate the
    /// local continuum as a straight line between two side windows and subtract it, which on a
    /// peak-less stretch is not a small correction but the entire reading — a convex continuum
    /// (the usual shape) puts that line above the middle, so the "intensity" comes out
    /// systematically negative, and the peak search walks the window to the top of whatever
    /// slope it sits on. Taking the raw value instead is unbiased, and measurably quieter (it
    /// doesn't pay the baseline estimate's own noise).
    /// <para>What it gives up is drift rejection: the reading carries the continuum pedestal, so
    /// it responds to window fogging, stray light and exposure changes as well as to the line.
    /// The Golden Run mean then plays the role the side windows used to — a baseline measured
    /// once, at a trusted moment, instead of guessed every frame.</para>
    /// <para>Consequences elsewhere, all deliberate: no noise estimate is produced (SNR is
    /// <em>unknown</em>, so the SNR gate stands down rather than passing a pedestal off as a
    /// healthy signal), and the multiplicative Warn/Alarm factors are ignored in favour of the
    /// σ terms — +20 % of a pedestal is tens of σ and would never trip.</para>
    /// </summary>
    RawMean,
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

    /// <summary>Half-width of the signal window, nm. Signal window = Center ± this. In
    /// <see cref="LineExtractMode.RawMean"/> it is the averaging width; 0 there means "the
    /// linearly interpolated value at exactly <see cref="CenterNm"/>".</summary>
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
    /// <para>Ignored in <see cref="LineExtractMode.RawMean"/>: searching for a peak where there
    /// is none doesn't find drift, it finds the top of the local slope — on a monotonic stretch
    /// the window pins to the edge of the search range every single frame.</para>
    /// </summary>
    public double PeakSearchHalfWidthNm { get; set; } = 1.0;

    public LineRegion Clone() => (LineRegion)MemberwiseClone();
}

/// <summary>
/// One extracted line reading: the baseline-subtracted intensity plus an estimate of the
/// local measurement noise (the scatter of the line-free continuum in the baseline windows,
/// propagated to the value). <see cref="Snr"/> is the value-to-noise ratio used to decide
/// whether the line is strong enough to trust — a ratio of two near-noise lines is meaningless
/// however well it is smoothed.
/// </summary>
public readonly record struct LineMeasurement(double Value, double Noise)
{
    /// <summary>A reading whose signal window fell outside the spectrum.</summary>
    public static readonly LineMeasurement Invalid = new(double.NaN, double.NaN);

    /// <summary>True when the signal window was covered and a value was produced.</summary>
    public bool HasValue => !double.IsNaN(Value);

    /// <summary>
    /// Signal-to-noise ratio (value / noise). <see cref="double.NaN"/> when the value is
    /// missing or the noise could not be estimated (no usable baseline window) — callers must
    /// treat an unknown SNR as "cannot assess", not as "low signal".
    /// </summary>
    public double Snr => HasValue && Noise > 0 ? Value / Noise : double.NaN;
}

/// <summary>
/// Extracts a baseline-subtracted line intensity from a spectrum frame. Assumes the
/// wavelength array is ascending (standard for the OES SDK).
/// </summary>
public static class LineIntensityExtractor
{
    /// <summary>
    /// Returns the baseline-subtracted intensity for <paramref name="region"/> together with a
    /// local noise estimate, or <see cref="LineMeasurement.Invalid"/> if the spectrum does not
    /// cover the signal window.
    /// </summary>
    public static LineMeasurement Extract(float[] wavelengths, float[] intensities, LineRegion region)
    {
        if (wavelengths is null || intensities is null || region is null) return LineMeasurement.Invalid;
        int n = Math.Min(wavelengths.Length, intensities.Length);
        if (n < 2) return LineMeasurement.Invalid;

        // Raw mode short-circuits the whole continuum machinery — that is the point of it.
        if (region.Mode == LineExtractMode.RawMean)
            return ExtractRaw(wavelengths, intensities, n, region);

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
        if (s0 >= n || s1 < s0) return LineMeasurement.Invalid;

        double leftHi  = sigLo - region.BaselineGapNm;
        double leftLo  = leftHi - region.BaselineWidthNm;
        double rightLo = sigHi + region.BaselineGapNm;
        double rightHi = rightLo + region.BaselineWidthNm;

        var left  = WindowStats(wavelengths, intensities, n, leftLo,  leftHi);
        var right = WindowStats(wavelengths, intensities, n, rightLo, rightHi);
        double leftMean = left.Mean, rightMean = right.Mean;
        bool leftHas = left.Has, rightHas = right.Has;

        // Per-pixel continuum noise: pool the baseline windows' scatter about *their own*
        // means so a sloped continuum doesn't masquerade as noise. NaN when no baseline
        // window is available — the caller then treats SNR as unknown rather than low.
        double pixelNoise = PooledNoise(left, right);

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
            // A peak height is one pixel above the continuum: its noise is the per-pixel σ.
            return new LineMeasurement(peak, pixelNoise);
        }

        // Integral in counts·nm: each pixel contributes the width of the overlap between its
        // own bin and the signal window, so (a) the value doesn't jump by a whole pixel when
        // the window edge crosses one — the peak search moves the window by whole pixels, which
        // used to change the pixel count from 5 to 6 and the reading with it — and (b) the
        // result is independent of how finely the spectrometer samples, so a baseline captured
        // on one axis still means something on another. Var = σ²·Σwᵢ², hence σ·√(Σwᵢ²).
        double sum = 0.0, sumW2 = 0.0;
        int first = Math.Max(0, s0 - 1), last = Math.Min(n - 1, s1 + 1);
        for (int i = first; i <= last; i++)
        {
            double w = CellOverlap(wavelengths, n, i, sigLo, sigHi);
            if (w <= 0) continue;
            sum += w * (intensities[i] - baseAt(wavelengths[i]));
            sumW2 += w * w;
        }
        double integralNoise = double.IsNaN(pixelNoise) ? double.NaN : pixelNoise * Math.Sqrt(sumW2);
        return new LineMeasurement(sum, integralNoise);
    }

    /// <summary>
    /// The raw reading: no peak search, no continuum subtraction, and deliberately no noise
    /// estimate — the baseline windows' scatter would divide into a pedestal and report an SNR
    /// of a few hundred for a wavelength with no line on it at all. NaN noise means "SNR
    /// unknown", which the monitors already treat as "cannot assess" rather than "low signal".
    /// </summary>
    private static LineMeasurement ExtractRaw(float[] wl, float[] inten, int n, LineRegion region)
    {
        double c = region.CenterNm;
        if (c < wl[0] || c > wl[n - 1]) return LineMeasurement.Invalid;

        if (region.HalfWidthNm <= 0)
        {
            // Exactly at CenterNm: linear interpolation between the neighbouring pixels, so a
            // line that falls between two bins isn't quantized onto whichever is nearer.
            int hi = LowerBound(wl, n, c);
            if (hi <= 0) return new LineMeasurement(inten[0], double.NaN);
            if (hi >= n) return new LineMeasurement(inten[n - 1], double.NaN);
            double span = wl[hi] - wl[hi - 1];
            if (span <= 0) return new LineMeasurement(inten[hi], double.NaN);
            double t = (c - wl[hi - 1]) / span;
            return new LineMeasurement(inten[hi - 1] + t * (inten[hi] - inten[hi - 1]), double.NaN);
        }

        // Averaged over the window, with the same fractional-edge weighting the integral uses
        // so widening the window by less than a pixel doesn't step the value.
        double lo = c - region.HalfWidthNm, hi2 = c + region.HalfWidthNm;
        int i0 = Math.Max(0, LowerBound(wl, n, lo) - 1);
        int i1 = Math.Min(n - 1, UpperBound(wl, n, hi2));
        double sumW = 0.0, sumWV = 0.0;
        for (int i = i0; i <= i1; i++)
        {
            double w = CellOverlap(wl, n, i, lo, hi2);
            if (w <= 0) continue;
            sumW += w;
            sumWV += w * inten[i];
        }
        return sumW > 0
            ? new LineMeasurement(sumWV / sumW, double.NaN)
            : LineMeasurement.Invalid;
    }

    /// <summary>
    /// Width (nm) of the overlap between pixel <paramref name="i"/>'s bin — half way to each
    /// neighbour — and the window [<paramref name="lo"/>, <paramref name="hi"/>]. 0 when the
    /// pixel is outside. This is what makes a window edge move continuously instead of in
    /// whole-pixel steps.
    /// </summary>
    private static double CellOverlap(float[] wl, int n, int i, double lo, double hi)
    {
        double cellLo = i == 0 ? wl[0] - (wl[1] - wl[0]) * 0.5 : (wl[i - 1] + wl[i]) * 0.5;
        double cellHi = i == n - 1 ? wl[n - 1] + (wl[n - 1] - wl[n - 2]) * 0.5 : (wl[i] + wl[i + 1]) * 0.5;
        return Math.Max(0.0, Math.Min(cellHi, hi) - Math.Max(cellLo, lo));
    }

    /// <summary>Mean / scatter accumulator for one baseline window.</summary>
    private readonly record struct WindowStat(double Mean, double Variance, int Count)
    {
        public bool Has => Count > 0;
    }

    /// <summary>
    /// Weighted mean / variance of one baseline window, using the same fractional-pixel edges as
    /// the integral — otherwise the continuum estimate itself steps whenever a window edge
    /// crosses a pixel, which is exactly the jitter the weighting exists to remove.
    /// <see cref="WindowStat.Count"/> stays a pixel count, since it only feeds the "≥ 2 samples"
    /// test for whether a scatter can be estimated at all.
    /// </summary>
    private static WindowStat WindowStats(float[] wl, float[] inten, int n, double lo, double hi)
    {
        int i0 = Math.Max(0, LowerBound(wl, n, lo) - 1);
        int i1 = Math.Min(n - 1, UpperBound(wl, n, hi));
        double sumW = 0.0, sumWV = 0.0, sumWV2 = 0.0;
        int count = 0;
        for (int i = i0; i <= i1; i++)
        {
            double w = CellOverlap(wl, n, i, lo, hi);
            if (w <= 0) continue;
            double v = inten[i];
            sumW += w; sumWV += w * v; sumWV2 += w * v * v;
            count++;
        }
        if (sumW <= 0) return new WindowStat(0.0, 0.0, 0);
        double mean = sumWV / sumW;
        double var = count > 1 ? Math.Max(0.0, sumWV2 / sumW - mean * mean) : 0.0;
        return new WindowStat(mean, var, count);
    }

    /// <summary>
    /// Pooled per-pixel continuum σ from the two side windows. Each side's variance is taken
    /// about its own mean (so the continuum slope doesn't inflate it) and pooled by sample
    /// count. Returns <see cref="double.NaN"/> when neither window has ≥ 2 samples — noise
    /// cannot be estimated and SNR is therefore unknown.
    /// </summary>
    private static double PooledNoise(WindowStat left, WindowStat right)
    {
        int nL = left.Count > 1 ? left.Count : 0;
        int nR = right.Count > 1 ? right.Count : 0;
        int tot = nL + nR;
        if (tot == 0) return double.NaN;
        double pooledVar = (nL * left.Variance + nR * right.Variance) / tot;
        return Math.Sqrt(pooledVar);
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
