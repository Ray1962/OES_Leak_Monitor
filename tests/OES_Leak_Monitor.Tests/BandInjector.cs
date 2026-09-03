using System;
using OES_Leak_Monitor;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Adds a fractional rise to one emission band in a recorded frame, keeping the band's own shape.
///
/// <para><b>What this is for.</b> The leak monitor's sensitivity has never been measured: no
/// calibration has ever been stored, and the one leak test on record (2026-08-19) used valve
/// units rather than a leak rate, with a response its own analysis refuses to attribute to the
/// valve. So the question "does a leak of Q move the indicator" cannot be answered offline. What
/// <em>can</em> be answered offline is everything downstream of that: given a rise of x % in the
/// band, does the extraction report x %, does it reach the alarm, and how long does it take.
/// That is what this exists to drive.</para>
///
/// <para><b>What it is not.</b> A leak this size is not a leak — it is a rise this size. Nothing
/// here converts to mbar·L/s, and an injected rise is an <em>upper bound</em> on what a real leak
/// of the same magnitude would show, for two reasons: it moves one band and leaves the reference
/// line untouched, whereas real air ingress changes the discharge loading and moves both; and it
/// adds no noise of its own, whereas a leak arrives on a fluctuating flow.</para>
///
/// <para><b>How the rise is shaped.</b> Not as a Gaussian — the N₂ second-positive band has a
/// head and a tail degraded to the red, and a symmetric bump would put counts where the band has
/// none. Instead each frame's own excess over its local continuum is scaled: the continuum is the
/// same linear interpolation between two side windows that <see cref="LineIntensityExtractor"/>
/// draws, and every bin in the span gets <c>fraction × max(0, intensity − continuum)</c> added.
/// The band therefore keeps exactly the profile that frame measured, and a frame where the band
/// is absent gains nothing — which is correct: there is nothing there to get brighter.</para>
///
/// <para><b>The bounds are explicit, and getting them wrong fakes the answer.</b> They must be
/// wide enough to cover the band the extractor measures <em>and</em> its two continuum windows —
/// otherwise the rise lands only in the peak and the extractor's own continuum subtraction is
/// never exercised. They must also stop short of the <em>denominator's</em> continuum windows: a
/// first run of this harness used ±5 nm about 337.4, which reached down to 332.4 and so brightened
/// the window CO 329.6 draws its continuum from. That lowered the denominator's peak height and
/// made one process report a larger rise than was injected — an artifact of the harness reading
/// as extra sensitivity. Bounds that touch anything the denominator reads will do it again.</para>
/// </summary>
internal static class BandInjector
{
    /// <summary>
    /// Returns a copy of <paramref name="intensities"/> with everything between
    /// <paramref name="loNm"/> and <paramref name="hiNm"/> scaled by <c>1 + fraction</c> above
    /// its local continuum.
    /// </summary>
    /// <param name="wavelengths">The frame's axis.</param>
    /// <param name="intensities">The frame's counts; not modified.</param>
    /// <param name="loNm">Low edge of the region the rise is applied over.</param>
    /// <param name="hiNm">High edge of the region the rise is applied over.</param>
    /// <param name="continuumLeftNm">Centre of the left continuum window.</param>
    /// <param name="continuumRightNm">Centre of the right continuum window.</param>
    /// <param name="continuumWidthNm">Half-width of each continuum window.</param>
    /// <param name="fraction">Rise as a fraction of the band's height above continuum; 0.10 is
    /// a 10 % brighter band. Negative is allowed (it is how the harness checks the response is
    /// signed correctly), 0 returns an unchanged copy.</param>
    public static float[] Inject(
        float[] wavelengths, float[] intensities,
        double loNm, double hiNm,
        double continuumLeftNm, double continuumRightNm, double continuumWidthNm,
        double fraction)
    {
        if (wavelengths is null) throw new ArgumentNullException(nameof(wavelengths));
        if (intensities is null) throw new ArgumentNullException(nameof(intensities));

        int n = Math.Min(wavelengths.Length, intensities.Length);
        var outp = (float[])intensities.Clone();
        if (fraction == 0 || n < 2) return outp;

        double lv = WindowMean(wavelengths, intensities, n, continuumLeftNm, continuumWidthNm);
        double rv = WindowMean(wavelengths, intensities, n, continuumRightNm, continuumWidthNm);
        if (double.IsNaN(lv) || double.IsNaN(rv)) return outp;   // cannot model the continuum

        double dx = continuumRightNm - continuumLeftNm;
        if (Math.Abs(dx) < 1e-9) return outp;
        double slope = (rv - lv) / dx;

        for (int i = 0; i < n; i++)
        {
            double w = wavelengths[i];
            if (w < loNm || w > hiNm) continue;
            double continuum = lv + slope * (w - continuumLeftNm);
            double excess = intensities[i] - continuum;
            if (excess <= 0) continue;      // nothing there to make brighter
            outp[i] = (float)(intensities[i] + fraction * excess);
        }
        return outp;
    }

    /// <summary>Mean counts in a window, or NaN when the window falls off the axis.</summary>
    private static double WindowMean(float[] wl, float[] inten, int n, double centreNm, double halfWidthNm)
    {
        double sum = 0; int count = 0;
        for (int i = 0; i < n; i++)
        {
            if (Math.Abs(wl[i] - centreNm) > halfWidthNm) continue;
            sum += inten[i]; count++;
        }
        return count == 0 ? double.NaN : sum / count;
    }
}
