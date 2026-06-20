using System;
using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>One ratio's measured response at one known leak rate, fed into the sensitivity fit.</summary>
public readonly record struct CalSample(double LeakRate, double X, double Sigma);

/// <summary>One ratio's contribution to a fused leak-rate estimate.</summary>
public readonly record struct RatioLeakEstimate(
    string Key,
    bool Used,
    double LeakRate,     // Qᵢ = xᵢ / sᵢ
    double Weight,       // wᵢ = 1 / Var(Qᵢ)
    bool OutOfRange,     // Qᵢ beyond this ratio's calibrated span
    string? SkipReason); // why the ratio was excluded (Used == false)

/// <summary>Fused leak-rate estimate for one frame, plus the per-ratio breakdown.</summary>
public sealed class LeakRateEstimate
{
    public bool HasEstimate { get; init; }
    public double LeakRate { get; init; }            // Q̂, mbar·L/s (clamped ≥ 0)
    public double Sigma { get; init; }               // σ_Q = 1/√(Σ wᵢ)
    public double Confidence { get; init; }          // 0..1, from cross-ratio consistency
    public bool OutOfCalibratedRange { get; init; }
    public IReadOnlyList<RatioLeakEstimate> PerRatio { get; init; } = Array.Empty<RatioLeakEstimate>();

    public static readonly LeakRateEstimate None = new();
}

/// <summary>
/// Pure math core for the leak-rate calibration (no engine / UI dependencies, so it can be
/// exercised with synthetic data). Two halves:
/// <list type="bullet">
/// <item><see cref="FitRatio"/> / <see cref="FitAll"/> — turn captured calibration points into
/// a through-origin sensitivity <c>x ≈ s·Q</c> per ratio (weighted least squares).</item>
/// <item><see cref="Estimate"/> — invert each ratio's sensitivity to a leak rate and fuse them
/// by inverse variance into a single <see cref="LeakRateEstimate"/>.</item>
/// </list>
/// See <c>docs/leak-rate-calibration.md</c> §2 for the formulae.
/// </summary>
public sealed class LeakRateEstimator
{
    /// <summary>Default assumed relative slope error when a single-point fit has no σ to lean on.</summary>
    public const double SinglePointRelError = 0.30;

    /// <summary>Sensitivities at or below this (fractional rise per mbar·L/s) are treated as
    /// "ratio doesn't respond to this leak" and excluded from estimation.</summary>
    public const double MinUsableSlope = 1e-9;

    /// <summary>A reading is flagged out-of-range once it exceeds the calibrated span by this factor.</summary>
    public const double ExtrapolationMargin = 0.10;

    private readonly LeakCalibration _cal;

    public LeakRateEstimator(LeakCalibration calibration) =>
        _cal = calibration ?? throw new ArgumentNullException(nameof(calibration));

    // ---- fitting -----------------------------------------------------------

    /// <summary>
    /// Fits the through-origin sensitivity <c>x ≈ s·Q</c> for one ratio from its calibration
    /// samples, weighting each by <c>1/σ²</c> when a σ is available. The leak-free origin
    /// (Q = 0, x = 0) is implicit and need not be passed.
    /// </summary>
    public static RatioSensitivity FitRatio(string key, IReadOnlyList<CalSample> samples,
        string referenceLabel = "")
    {
        var pts = samples?.Where(s => s.LeakRate > 0 && !double.IsNaN(s.X)).ToList()
                  ?? new List<CalSample>();
        var fit = new RatioSensitivity
        {
            Key = key,
            ReferenceLabel = referenceLabel,
            PointCount = pts.Count,
            MaxCalibratedLeakRate = pts.Count > 0 ? pts.Max(p => p.LeakRate) : 0.0,
        };
        if (pts.Count == 0) return fit;

        // Weighted through-origin slope: s = Σ(w·Q·x) / Σ(w·Q²).
        double sQQ = 0, sQX = 0;
        foreach (var p in pts)
        {
            double w = p.Sigma > 0 ? 1.0 / (p.Sigma * p.Sigma) : 1.0;
            sQQ += w * p.LeakRate * p.LeakRate;
            sQX += w * p.LeakRate * p.X;
        }
        if (sQQ <= 0) return fit;
        double slope = sQX / sQQ;
        fit.Slope = slope;

        // Slope error: scale the formal covariance (1/ΣwQ²) by the reduced chi-square so a
        // poor fit widens the error. Falls back to an assumed relative error for a lone point.
        if (pts.Count >= 2)
        {
            double chi2 = 0;
            foreach (var p in pts)
            {
                double w = p.Sigma > 0 ? 1.0 / (p.Sigma * p.Sigma) : 1.0;
                double r = p.X - slope * p.LeakRate;
                chi2 += w * r * r;
            }
            double scale = chi2 / (pts.Count - 1);   // reduced χ², ~1 if σ is well-estimated
            fit.SlopeError = Math.Sqrt(Math.Max(scale, 0.0) / sQQ);
        }
        else
        {
            var p = pts[0];
            fit.SlopeError = p.Sigma > 0
                ? Math.Sqrt(1.0 / sQQ)               // == σ_x / Q for a single weighted point
                : Math.Abs(slope) * SinglePointRelError;
        }

        // Uncentered R² (through-origin convention): 1 − Σr² / Σx².
        double ssRes = 0, ssTot = 0;
        foreach (var p in pts)
        {
            double r = p.X - slope * p.LeakRate;
            ssRes += r * r;
            ssTot += p.X * p.X;
        }
        fit.RSquared = ssTot > 0 ? Math.Clamp(1.0 - ssRes / ssTot, 0.0, 1.0) : 1.0;
        return fit;
    }

    /// <summary>Fits every ratio present in <paramref name="points"/> and returns the sensitivities,
    /// stamping each with the reference label from <paramref name="referenceLabels"/> when supplied.</summary>
    public static List<RatioSensitivity> FitAll(IReadOnlyList<LeakCalPoint> points,
        IReadOnlyDictionary<string, string>? referenceLabels = null)
    {
        var byKey = new Dictionary<string, List<CalSample>>();
        foreach (var pt in points ?? Array.Empty<LeakCalPoint>())
            foreach (var m in pt.Measurements)
            {
                if (!byKey.TryGetValue(m.Key, out var list))
                    byKey[m.Key] = list = new List<CalSample>();
                list.Add(new CalSample(pt.LeakRate, m.X, m.Sigma));
            }

        var fits = new List<RatioSensitivity>();
        foreach (var (key, samples) in byKey)
        {
            string refLabel = referenceLabels != null && referenceLabels.TryGetValue(key, out var r) ? r : "";
            fits.Add(FitRatio(key, samples, refLabel));
        }
        return fits;
    }

    // ---- estimation --------------------------------------------------------

    /// <summary>One ratio's live reading fed into <see cref="Estimate"/>.</summary>
    public readonly record struct RatioReading(string Key, double X, double Sigma);

    /// <summary>
    /// Inverts each ratio's sensitivity to a leak rate and fuses them by inverse variance.
    /// <paramref name="readings"/> supplies the current fractional rise <c>x</c> and its noise
    /// <c>σ_x</c> per ratio; ratios without a usable fit (missing, insensitive, or whose reference
    /// line no longer matches the one fit against) are skipped with a reason.
    /// </summary>
    /// <param name="readings">(ratioKey, x, σ_x) for each ratio currently being monitored.</param>
    /// <param name="currentReferenceLabels">Optional current reference label per ratio; when given,
    /// a fit whose <see cref="RatioSensitivity.ReferenceLabel"/> differs is rejected.</param>
    public LeakRateEstimate Estimate(IReadOnlyList<RatioReading> readings,
        IReadOnlyDictionary<string, string>? currentReferenceLabels = null)
    {
        if (readings is null || readings.Count == 0) return LeakRateEstimate.None;

        var perRatio = new List<RatioLeakEstimate>(readings.Count);
        double sumW = 0, sumWQ = 0;
        bool anyOutOfRange = false;

        foreach (var r in readings)
        {
            var fit = _cal.FindFit(r.Key);
            string? skip = null;
            if (fit is null) skip = "no calibration fit";
            else if (Math.Abs(fit.Slope) <= MinUsableSlope) skip = "ratio insensitive to leak";
            else if (double.IsNaN(r.X)) skip = "no reading";
            else if (currentReferenceLabels != null &&
                     currentReferenceLabels.TryGetValue(r.Key, out var curRef) &&
                     !string.IsNullOrEmpty(fit.ReferenceLabel) &&
                     curRef != fit.ReferenceLabel)
                skip = "reference line changed since calibration";

            if (skip != null || fit is null)
            {
                perRatio.Add(new RatioLeakEstimate(r.Key, false, double.NaN, 0, false, skip));
                continue;
            }

            double s = fit.Slope;
            double q = r.X / s;
            // Var(Qᵢ) = (σ_x / s)² + (x · δs / s²)²   — measurement noise + slope uncertainty.
            double sigX = r.Sigma > 0 ? r.Sigma : 0;
            double varQ = sigX * sigX / (s * s)
                        + r.X * r.X * fit.SlopeError * fit.SlopeError / (s * s * s * s);
            if (varQ <= 0 || double.IsNaN(varQ) || double.IsInfinity(varQ))
            {
                perRatio.Add(new RatioLeakEstimate(r.Key, false, q, 0, false, "degenerate variance"));
                continue;
            }

            double w = 1.0 / varQ;
            bool outOfRange = q > fit.MaxCalibratedLeakRate * (1.0 + ExtrapolationMargin);
            anyOutOfRange |= outOfRange;
            sumW += w;
            sumWQ += w * q;
            perRatio.Add(new RatioLeakEstimate(r.Key, true, q, w, outOfRange, null));
        }

        if (sumW <= 0) return new LeakRateEstimate { HasEstimate = false, PerRatio = perRatio };

        double qHat = sumWQ / sumW;
        double sigmaQ = Math.Sqrt(1.0 / sumW);

        // Confidence from cross-ratio consistency: reduced χ² of the used Qᵢ about Q̂.
        var used = perRatio.Where(p => p.Used).ToList();
        double confidence;
        if (used.Count >= 2)
        {
            double chi2 = used.Sum(p => p.Weight * (p.LeakRate - qHat) * (p.LeakRate - qHat));
            double reduced = chi2 / (used.Count - 1);
            confidence = 1.0 / (1.0 + Math.Max(reduced, 0.0));
        }
        else
        {
            confidence = 0.5;   // a single ratio agrees with itself — no independent check
        }

        return new LeakRateEstimate
        {
            HasEstimate = true,
            LeakRate = Math.Max(0.0, qHat),   // a real leak can't be negative
            Sigma = sigmaQ,
            Confidence = confidence,
            OutOfCalibratedRange = anyOutOfRange || qHat > used.Min(p => MaxFor(p.Key)),
            PerRatio = perRatio,
        };
    }

    private double MaxFor(string key) => _cal.FindFit(key)?.MaxCalibratedLeakRate ?? double.PositiveInfinity;
}
