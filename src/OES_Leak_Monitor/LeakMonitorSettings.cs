using System;
using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

/// <summary>What quantity a monitored entry tracks.</summary>
public enum MonitorMode
{
    /// <summary>Signal line ÷ reference line — actinometric ratio (normalizes out plasma drift).</summary>
    Ratio,
    /// <summary>
    /// The signal line's absolute baseline-subtracted intensity; the reference line is used only
    /// as a plasma-present gate, not divided in. For weak/near-noise lines where the ratio of two
    /// small numbers swings wildly: dropping the reference division removes one noise source, and a
    /// leak raises the line clearly above noise. Trade-off: absolute intensity is sensitive to
    /// plasma-condition drift (power / pressure / flow) that a ratio would cancel, so it needs a
    /// stable operating point and more frequent re-baselining.
    /// </summary>
    AbsoluteIntensity,
}

/// <summary>
/// One monitored actinometric ratio: an emission line divided by a reference line,
/// plus its alarm thresholds and smoothing. Serialized inside <see cref="LeakMonitorSettings"/>.
/// </summary>
public sealed class RatioDefinition
{
    /// <summary>Stable key used to pair the definition with its Golden Run baseline.</summary>
    public string Key { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Whether this entry tracks the signal/reference ratio or the signal line's
    /// absolute intensity (reference then only gates plasma-present). See <see cref="MonitorMode"/>.</summary>
    public MonitorMode MonitorMode { get; set; } = MonitorMode.Ratio;

    public LineRegion Numerator { get; set; } = new();
    public LineRegion Denominator { get; set; } = new();

    /// <summary>Warning trips at <c>max(WarnFactor·mean, mean + SigmaWarn·sigma)</c>.</summary>
    public double WarnFactor { get; set; } = 1.2;
    public double AlarmFactor { get; set; } = 1.5;
    public double SigmaWarn { get; set; } = 3.0;
    public double SigmaAlarm { get; set; } = 6.0;

    /// <summary>Exponential-moving-average time constant for the ratio, seconds.</summary>
    public double EmaTauSeconds { get; set; } = 5.0;

    /// <summary>The ratio must stay above a threshold this long before the level escalates.</summary>
    public double ConfirmSeconds { get; set; } = 15.0;

    /// <summary>
    /// Minimum signal-to-noise ratio each line must clear for the ratio to be trusted. When the
    /// signal or reference line falls below this (the emission is near the noise floor), the ratio
    /// is reported as <see cref="RatioState.LowSignal"/> and excluded from the alarm rather than
    /// allowed to swing wildly. The ratio of two near-noise lines is meaningless however well it
    /// is smoothed, so this guards against false alarms at low plasma intensity.
    /// </summary>
    public double MinSnr { get; set; } = 5.0;

    /// <summary>
    /// True when the monitored value carries the continuum pedestal — absolute-intensity mode
    /// reading a <see cref="LineExtractMode.RawMean"/> line. Everything that divides by the
    /// baseline mean has to know: with a mean of a few thousand counts and a σ of twenty, a
    /// real signal moves the ratio-to-baseline by fractions of a percent, so
    /// <list type="bullet">
    /// <item>the Warn/Alarm <em>factors</em> are meaningless (1.2× is tens of σ, never reached)
    /// and only the σ terms are used;</item>
    /// <item>the % -of-baseline display is σ-normalized rather than <c>value/mean·100</c>;</item>
    /// <item>the leak-rate calibration fits the absolute rise Δ, not the fractional one.</item>
    /// </list>
    /// A ratio, or an absolute reading of a baseline-subtracted line, has no pedestal: its mean
    /// <em>is</em> the signal, so the ordinary multiplicative forms apply.
    /// </summary>
    public bool ValueHasPedestal =>
        MonitorMode == MonitorMode.AbsoluteIntensity && Numerator.Mode == LineExtractMode.RawMean;

    public RatioDefinition Clone() => new()
    {
        Key = Key, DisplayName = DisplayName, Enabled = Enabled,
        MonitorMode = MonitorMode,
        Numerator = Numerator.Clone(), Denominator = Denominator.Clone(),
        WarnFactor = WarnFactor, AlarmFactor = AlarmFactor,
        SigmaWarn = SigmaWarn, SigmaAlarm = SigmaAlarm,
        EmaTauSeconds = EmaTauSeconds, ConfirmSeconds = ConfirmSeconds,
        MinSnr = MinSnr,
    };
}

/// <summary>Baseline statistics for one ratio, captured during a Golden Run.</summary>
public sealed class GoldenRunRatioBaseline
{
    public string Key { get; set; } = "";
    public double Mean { get; set; }
    public double Sigma { get; set; }
    public int SampleCount { get; set; }

    /// <summary>
    /// Reference (denominator) line this baseline was captured against. A ratio-mode baseline
    /// only applies while the ratio's current reference still matches this label. Empty for an
    /// absolute-intensity baseline, which doesn't involve the reference line at all.
    /// </summary>
    public string ReferenceLabel { get; set; } = "";

    /// <summary>
    /// Monitor mode the baseline was captured under. The mean is a mean of the monitored
    /// quantity, and a ratio and an absolute line intensity are not the same quantity — so a
    /// baseline only applies while the mode still matches. Defaults to
    /// <see cref="MonitorMode.Ratio"/> so a settings.json written before this field existed
    /// keeps its ratio-mode baselines; an absolute-mode baseline from such a file is rejected
    /// (with a log line) and must be re-captured.
    /// </summary>
    public MonitorMode Mode { get; set; } = MonitorMode.Ratio;

    /// <summary>
    /// Revision of the extraction maths the baseline was measured with. Bumped when a change
    /// alters the <em>units or scale</em> of an extracted value rather than just its accuracy —
    /// revision 1 made <see cref="LineExtractMode.Integral"/> an integral in counts·nm with
    /// fractional-pixel window edges, where it had been a plain pixel sum. A stored mean from an
    /// older revision is a number in a different unit, so it is rejected (with a log line) for
    /// any ratio whose reading involves an integral; <see cref="LineExtractMode.PeakHeight"/> and
    /// <see cref="LineExtractMode.RawMean"/> readings are unaffected and keep their baselines.
    /// </summary>
    public int ExtractionRevision { get; set; }
}

/// <summary>
/// The acquisition conditions a Golden Run was captured under. An absolute-intensity baseline is
/// a count, and counts scale with integration time, averaging and everything else in this list —
/// so a baseline captured at 50 ms means nothing at 100 ms, and the failure is silent: every
/// ratio simply reads high (or low) for ever. Recorded at capture and compared each frame; a
/// mismatch is reported rather than quietly tolerated. A ratio-mode baseline is far less
/// sensitive (most of this cancels in the division), but the warning is worth having there too.
/// </summary>
public sealed class AcquisitionFingerprint
{
    public double IntegrationTimeMs { get; set; }
    public long AverageCount { get; set; }
    public long BoxcarWidth { get; set; }
    public string AcquireMode { get; set; } = "";
    public string AverageMode { get; set; } = "";
    public bool BackgroundRemove { get; set; }
    public bool StraylightCorrection { get; set; }
    public bool LinearityCorrection { get; set; }

    /// <summary>Number of points on the wavelength axis the capture ran on (0 = not recorded).</summary>
    public int AxisLength { get; set; }
    public double AxisStartNm { get; set; }
    public double AxisEndNm { get; set; }

    public AcquisitionFingerprint Clone() => (AcquisitionFingerprint)MemberwiseClone();

    /// <summary>
    /// Lists the fields that differ from <paramref name="other"/>, or an empty string when they
    /// agree. The axis bounds are compared loosely (0.05 nm) — the SDK reports them as floats and
    /// a last-digit wobble is not a configuration change.
    /// </summary>
    public string Differences(AcquisitionFingerprint? other)
    {
        if (other is null) return "";
        var diffs = new List<string>();
        void Cmp(string name, object a, object b)
        {
            if (!Equals(a, b)) diffs.Add($"{name} {b} → {a}");
        }
        Cmp("integration", IntegrationTimeMs, other.IntegrationTimeMs);
        Cmp("average", AverageCount, other.AverageCount);
        Cmp("boxcar", BoxcarWidth, other.BoxcarWidth);
        Cmp("acquire mode", AcquireMode, other.AcquireMode);
        Cmp("average mode", AverageMode, other.AverageMode);
        Cmp("background removal", BackgroundRemove, other.BackgroundRemove);
        Cmp("stray-light correction", StraylightCorrection, other.StraylightCorrection);
        Cmp("linearity correction", LinearityCorrection, other.LinearityCorrection);
        // Axis fields are 0 on a fingerprint recorded before a frame was seen — don't report
        // "0 → 1891" as a change the operator made.
        if (AxisLength > 0 && other.AxisLength > 0)
        {
            Cmp("axis points", AxisLength, other.AxisLength);
            if (Math.Abs(AxisStartNm - other.AxisStartNm) > 0.05 ||
                Math.Abs(AxisEndNm - other.AxisEndNm) > 0.05)
                diffs.Add($"axis {other.AxisStartNm:0.#}–{other.AxisEndNm:0.#} → " +
                          $"{AxisStartNm:0.#}–{AxisEndNm:0.#} nm");
        }
        return string.Join(", ", diffs);
    }
}

/// <summary>
/// A leak-free reference capture for one recipe: per-ratio baseline mean/sigma plus the
/// minimum N2 reference intensity that counts as "plasma on" for that recipe.
/// </summary>
public sealed class GoldenRun
{
    public string Name { get; set; } = "";
    public DateTime CapturedUtc { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>Minimum denominator (N2) intensity for the plasma-present gate. Ratio-mode
    /// entries only — absolute-intensity ratios gate on the logger trigger (see
    /// <see cref="PlasmaGate"/>), so their reference never contributed to this floor.</summary>
    public double PlasmaPresentFloor { get; set; }

    /// <summary>
    /// Acquisition conditions at capture time, so a baseline taken at a different integration
    /// time / averaging / axis can be flagged instead of silently mis-scaling every reading.
    /// Null for a Golden Run captured before this was recorded — comparison is then skipped.
    /// </summary>
    public AcquisitionFingerprint? Acquisition { get; set; }

    public List<GoldenRunRatioBaseline> Baselines { get; set; } = new();

    public GoldenRunRatioBaseline? Find(string key) =>
        Baselines.FirstOrDefault(b => b.Key == key);
}

/// <summary>
/// One ratio's measured response at a single known leak rate, captured during a leak-rate
/// calibration. <see cref="X"/> is the mean rise relative to the Golden Run baseline over the
/// capture window — the same quantity the runtime estimator inverts. Its units depend on the
/// ratio's monitor mode: for <see cref="MonitorMode.Ratio"/> it is the <em>fractional</em> rise
/// (<c>smoothedRatio / baselineMean − 1</c>); for <see cref="MonitorMode.AbsoluteIntensity"/> it
/// is the <em>absolute</em> rise <c>Δ = value − baselineMean</c> (avoiding a division by the
/// near-noise baseline mean). The fit's <see cref="RatioSensitivity.Absolute"/> flag records which.
/// </summary>
public sealed class RatioCalMeasurement
{
    public string Key { get; set; } = "";

    /// <summary>Mean rise over the capture window (0 at the leak-free baseline). Fractional in
    /// ratio mode, absolute (Δ intensity) in absolute-intensity mode — see the class summary.</summary>
    public double X { get; set; }

    /// <summary>Standard deviation of <see cref="X"/> over the capture window.</summary>
    public double Sigma { get; set; }

    public int SampleCount { get; set; }
}

/// <summary>
/// One calibration point: the per-ratio response measured while a known leak of
/// <see cref="LeakRate"/> (mbar·L/s) was applied. The leak-free state is the implicit
/// origin (Q = 0, x = 0) and is not stored as a point.
/// </summary>
public sealed class LeakCalPoint
{
    /// <summary>Known leak rate for this point, temperature-corrected, mbar·L/s.</summary>
    public double LeakRate { get; set; }

    /// <summary>Free-text label, e.g. the calibrated-leak element's id.</summary>
    public string Label { get; set; } = "";

    public DateTime CapturedUtc { get; set; }

    public List<RatioCalMeasurement> Measurements { get; set; } = new();

    public RatioCalMeasurement? Find(string key) =>
        Measurements.FirstOrDefault(m => m.Key == key);
}

/// <summary>
/// Fitted through-origin sensitivity for one ratio: <c>x ≈ Slope · Q</c>, i.e. the fractional
/// rise produced per mbar·L/s of leak. The runtime estimator inverts this (<c>Q = x / Slope</c>)
/// and weights each ratio by the inverse variance derived from <see cref="SlopeError"/>.
/// </summary>
public sealed class RatioSensitivity
{
    public string Key { get; set; } = "";

    /// <summary>Sensitivity sᵢ — fractional rise per mbar·L/s.</summary>
    public double Slope { get; set; }

    /// <summary>Standard error of <see cref="Slope"/> (δsᵢ).</summary>
    public double SlopeError { get; set; }

    /// <summary>Through-origin coefficient of determination (uncentered), 0..1.</summary>
    public double RSquared { get; set; }

    /// <summary>Whether this ratio was calibrated in absolute-intensity mode. When true the
    /// slope maps mbar·L/s → absolute rise <c>Δ = value − baselineMean</c>; when false it maps
    /// to the fractional rise. The runtime estimator must feed a reading in the matching unit,
    /// so a fit is rejected if the ratio's current monitor mode no longer agrees with this flag
    /// (same spirit as <see cref="ReferenceLabel"/>). Defaults to false so pre-existing
    /// (ratio-mode) calibrations keep their fractional meaning.</summary>
    public bool Absolute { get; set; }

    /// <summary>Largest leak rate used in the fit — readings above this are extrapolated.</summary>
    public double MaxCalibratedLeakRate { get; set; }

    public int PointCount { get; set; }

    /// <summary>Reference (denominator) line this sensitivity was fit against. The fit only
    /// applies while the ratio's current reference still matches this label — same rule as
    /// <see cref="GoldenRunRatioBaseline.ReferenceLabel"/>.</summary>
    public string ReferenceLabel { get; set; } = "";
}

/// <summary>
/// A leak-rate calibration for one recipe / operating point: the captured calibration points
/// plus the per-ratio sensitivities fit from them. Bound to a <see cref="GoldenRun"/> baseline;
/// it only applies while that baseline is active and the ratio reference lines still match.
/// Persisted inside <see cref="LeakMonitorSettings"/> (same <c>settings.json</c> as everything else).
/// </summary>
public sealed class LeakCalibration
{
    public string Name { get; set; } = "";

    /// <summary>The Golden Run this calibration's fractional rises were measured against.</summary>
    public string GoldenRunName { get; set; } = "";

    public string LeakRateUnit { get; set; } = "mbar·L/s";

    public DateTime CapturedUtc { get; set; }

    public List<LeakCalPoint> Points { get; set; } = new();

    public List<RatioSensitivity> Fits { get; set; } = new();

    public RatioSensitivity? FindFit(string key) =>
        Fits.FirstOrDefault(f => f.Key == key);
}

/// <summary>Persisted configuration for the actinometry leak-monitoring model.</summary>
public sealed class LeakMonitorSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Name of the <see cref="GoldenRun"/> currently used as the baseline.</summary>
    public string? ActiveGoldenRun { get; set; }

    /// <summary>How long a Golden Run capture averages the ratios, seconds.</summary>
    public double GoldenRunCaptureSeconds { get; set; } = 60;

    /// <summary>Require at least two ratios in Alarm before the overall state is Alarm.</summary>
    public bool RequireTwoForAlarm { get; set; } = true;

    /// <summary>In Test Mode the ratios are still shown but alarms are not raised.</summary>
    public bool SuppressAlarmsInTestMode { get; set; } = true;

    /// <summary>Write the ratio trend to a CSV alongside each intensity-logger save session.</summary>
    public bool RatioCsvEnabled { get; set; } = true;

    /// <summary>Name of the <see cref="LeakCalibration"/> currently used for leak-rate estimation,
    /// or null when no calibration is active (estimation off, alarms still run).</summary>
    public string? ActiveCalibration { get; set; }

    /// <summary>How long each leak-rate calibration point averages the ratios, seconds.</summary>
    public double CalibrationPointCaptureSeconds { get; set; } = 30;

    public List<RatioDefinition> Ratios { get; set; } = new();
    public List<GoldenRun> GoldenRuns { get; set; } = new();
    public List<LeakCalibration> Calibrations { get; set; } = new();

    /// <summary>Catalog-level wavelength-drift corrections (a sparse <c>(species, wavelength)</c> →
    /// offset overlay). Applied to every ratio line that matches, at monitor-build time. Empty by
    /// default. See <see cref="WavelengthCorrection"/> / <see cref="WavelengthCalibration"/>.</summary>
    public List<WavelengthCorrection> WavelengthCorrections { get; set; } = new();

    public GoldenRun? FindGoldenRun(string? name) =>
        name is null ? null : GoldenRuns.FirstOrDefault(g => g.Name == name);

    public LeakCalibration? FindCalibration(string? name) =>
        name is null ? null : Calibrations.FirstOrDefault(c => c.Name == name);

    /// <summary>Factory defaults: the four v1 ratios, all referenced to N2 337.1 nm.</summary>
    public static LeakMonitorSettings CreateDefault() => new()
    {
        Ratios = new List<RatioDefinition>
        {
            new()
            {
                Key = "R_O", DisplayName = "O 777 / N₂ 337",
                Numerator = new LineRegion
                {
                    Label = "O 777.2", CenterNm = 777.2, HalfWidthNm = 0.5,
                    BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.PeakHeight,
                },
                Denominator = DefaultN2Reference(),
            },
            new()
            {
                Key = "R_OH", DisplayName = "OH 309 / N₂ 337",
                Numerator = new LineRegion
                {
                    Label = "OH 308.9", CenterNm = 308.9, HalfWidthNm = 1.0,
                    BaselineGapNm = 0.6, BaselineWidthNm = 0.6, Mode = LineExtractMode.Integral,
                },
                Denominator = DefaultN2Reference(),
            },
            new()
            {
                Key = "R_NO", DisplayName = "NO 237 / N₂ 337",
                Numerator = new LineRegion
                {
                    Label = "NO 237", CenterNm = 237.0, HalfWidthNm = 1.5,
                    BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.Integral,
                },
                Denominator = DefaultN2Reference(),
            },
            new()
            {
                Key = "R_Ar", DisplayName = "Ar 750 / N₂ 337",
                Numerator = new LineRegion
                {
                    Label = "Ar 750.4", CenterNm = 750.4, HalfWidthNm = 0.5,
                    BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.PeakHeight,
                },
                Denominator = DefaultN2Reference(),
            },
        },
    };

    // The default reference is N₂ 337.1, sourced from the shared catalog so its Label
    // matches the names the reference-line picker offers.
    private static LineRegion DefaultN2Reference() =>
        ReferenceLineCatalog.FindByName("N₂ 337.1")!.CreateRegion();
}
