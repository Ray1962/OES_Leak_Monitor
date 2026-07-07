using System;
using System.Text.Json.Serialization;

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

    /// <summary>Center wavelength of the line / band head, nm — from the emission-line catalog.</summary>
    public double CenterNm { get; set; }

    /// <summary>
    /// Manual wavelength-drift correction, nm. When &gt; 0 it overrides <see cref="CenterNm"/>
    /// at extraction time: the signal / baseline windows (and the peak search) center on this
    /// value instead of the catalog wavelength, so a drifted OES axis can be re-aligned without
    /// re-picking the catalog line. 0 keeps the catalog <see cref="CenterNm"/>. Persisted with
    /// the region; edited on the Ratio Setup tab and applied on the next acquisition restart.
    /// </summary>
    public double CalibrationNm { get; set; }

    /// <summary>The wavelength the windows actually center on: the <see cref="CalibrationNm"/>
    /// override when set (&gt; 0), otherwise the catalog <see cref="CenterNm"/>.</summary>
    [JsonIgnore]
    public double EffectiveCenterNm => CalibrationNm > 0 ? CalibrationNm : CenterNm;

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

        // Start from the effective center (the manual drift correction when set, else the
        // catalog wavelength), then re-center on the actual peak so a shifted line or a
        // mis-calibrated wavelength axis is still measured correctly (see
        // LineRegion.CalibrationNm / PeakSearchHalfWidthNm).
        double center = region.PeakSearchHalfWidthNm > 0
            ? FindPeakWavelength(wavelengths, intensities, n,
                                 region.EffectiveCenterNm, region.PeakSearchHalfWidthNm)
            : region.EffectiveCenterNm;

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

        int sigCount = s1 - s0 + 1;
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

        double sum = 0.0;
        for (int i = s0; i <= s1; i++)
            sum += intensities[i] - baseAt(wavelengths[i]);
        // An integral over N independent pixels accumulates noise as σ·√N.
        double integralNoise = double.IsNaN(pixelNoise) ? double.NaN : pixelNoise * Math.Sqrt(sigCount);
        return new LineMeasurement(sum, integralNoise);
    }

    /// <summary>Mean / scatter accumulator for one baseline window.</summary>
    private readonly record struct WindowStat(double Mean, double Variance, int Count)
    {
        public bool Has => Count > 0;
    }

    private static WindowStat WindowStats(float[] wl, float[] inten, int n, double lo, double hi)
    {
        int i0 = LowerBound(wl, n, lo);
        int i1 = UpperBound(wl, n, hi) - 1;
        if (i0 >= n || i1 < i0) return new WindowStat(0.0, 0.0, 0);
        double sum = 0.0, sumSq = 0.0;
        int count = i1 - i0 + 1;
        for (int i = i0; i <= i1; i++) { double v = inten[i]; sum += v; sumSq += v * v; }
        double mean = sum / count;
        double var = count > 1 ? Math.Max(0.0, sumSq / count - mean * mean) : 0.0;
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
