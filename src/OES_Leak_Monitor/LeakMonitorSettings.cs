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
    [System.Text.Json.Serialization.JsonIgnore]   // derived; see LineRegion.MeasurementKey
    public bool ValueHasPedestal =>
        MonitorMode == MonitorMode.AbsoluteIntensity && Numerator.Mode == LineExtractMode.RawMean;

    /// <summary>
    /// True when <paramref name="other"/> monitors the same quantity — same mode, same monitored
    /// line, and (in ratio mode, where it is divided in) the same reference line. Thresholds and
    /// smoothing are deliberately *not* compared: retuning a threshold does not change what is
    /// being measured, so an alarm raised against the old one still refers to the same thing.
    /// </summary>
    public bool MeasuresSameAs(RatioDefinition? other) =>
        other is not null &&
        Key == other.Key &&
        MonitorMode == other.MonitorMode &&
        Numerator.MeasuresSameAs(other.Numerator) &&
        (MonitorMode == MonitorMode.AbsoluteIntensity ||
         Denominator.MeasuresSameAs(other.Denominator));

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

/// <summary>Minimum intensity of one reference line that counts as "plasma on", measured from
/// the leak-free capture. Keyed by <see cref="LineRegion.MeasurementKey"/> so ratios that read
/// the same line the same way share one floor and ratios that don't never see each other's.
/// <see cref="ReferenceLabel"/> carries no meaning to the code — it is there so the entry is
/// readable in <c>settings.json</c> and in the log.</summary>
public sealed class PlasmaFloorEntry
{
    public string ReferenceKey { get; set; } = "";
    public string ReferenceLabel { get; set; } = "";
    public double Floor { get; set; }
}

/// <summary>
/// A leak-free reference capture for one recipe: per-ratio baseline mean/sigma plus the
/// minimum reference intensity that counts as "plasma on" for that recipe, per reference line.
/// </summary>
public sealed class GoldenRun
{
    public string Name { get; set; } = "";
    public DateTime CapturedUtc { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>
    /// Superseded by <see cref="PlasmaFloors"/> and no longer written. It held one floor for the
    /// whole run — 20 % of the mean of <em>every</em> ratio-mode denominator pooled together —
    /// which is only meaningful while every ratio shares one reference line. Mix an Ar 811.5
    /// reference (bright) with an N₂ 662.4 one (faint) and the pooled floor lands above the faint
    /// line's normal level: that ratio reads "plasma off" in every frame and goes silently dark,
    /// while the bright one gets a floor at 10 % of its own level and is effectively ungated.
    /// Kept as the fallback for runs captured before the per-reference floors existed, so an
    /// upgrade changes nothing until the next capture.
    /// </summary>
    public double PlasmaPresentFloor { get; set; }

    /// <summary>Per-reference-line plasma floors, ratio-mode entries only — absolute-intensity
    /// ratios gate on the logger trigger (see <see cref="PlasmaGate"/>), so their reference
    /// never contributes one. Empty for a run captured before this existed.</summary>
    public List<PlasmaFloorEntry> PlasmaFloors { get; set; } = new();

    /// <summary>The floor for one reference line, or 0 (ungated) when this run never measured
    /// it. Falls back to the run-level <see cref="PlasmaPresentFloor"/> for a legacy run.</summary>
    public double FindFloor(string referenceKey) =>
        PlasmaFloors is null || PlasmaFloors.Count == 0
            ? PlasmaPresentFloor
            : PlasmaFloors.FirstOrDefault(f => f.ReferenceKey == referenceKey)?.Floor ?? 0.0;

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

    /// <summary>
    /// Emission lines the site added to the fixed catalog, each already carrying the "u" species
    /// marker. Stored here rather than in a file of their own because a ratio can reference one:
    /// a settings.json without them would carry a ratio pointing at a line that does not exist,
    /// and the manual promises this single file is a machine's whole configuration.
    /// </summary>
    public List<UserSpectralLine> UserSpectralLines { get; set; } = new();

    /// <summary>Name of the <see cref="GoldenRun"/> currently used as the baseline.</summary>
    public string? ActiveGoldenRun { get; set; }

    /// <summary>How long a Golden Run capture averages the ratios, seconds. Editable in the
    /// Configuration tab; the useful range is bounded because a capture shorter than a few
    /// seconds averages too few frames to give a meaningful σ, and one longer than ten minutes
    /// is asking the process to hold steady for longer than most recipes run.</summary>
    public double GoldenRunCaptureSeconds { get; set; } = DefaultGoldenRunCaptureSeconds;

    public const double DefaultGoldenRunCaptureSeconds = 60;
    public const double MinGoldenRunCaptureSeconds = 5;
    public const double MaxGoldenRunCaptureSeconds = 600;

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

    /// <summary>
    /// Factory defaults: three nitrogen-family indicators divided by Ar 750.4, plus one
    /// absolute-intensity diagnostic that ships disabled.
    /// <para>The set this replaced — O 777, OH 309, NO 237 and Ar 750, all over N₂ 337.1 — came
    /// from the textbook assumption that an air leak raises O, OH and NO. Measured against a
    /// known 100-unit air leak on a real Ar plasma, three of those four numerators did not
    /// respond (O 777.2 moved 0.7 σ, OH 308.9 moved 0.1 σ) while N₂ 337.1 — the line that set
    /// was dividing <em>by</em> — moved 33.8 σ. Numerator and denominator were the wrong way
    /// round. What ships now is what that measurement actually selected: N₂ 337.1 (33.8 σ) as
    /// the primary, NO 237 (18.3 σ) as an independent second opinion from another species, and
    /// N₂ 357.7 (16.2 σ) so a single ratio dropping to LowSignal cannot make
    /// <see cref="RequireTwoForAlarm"/> unreachable.</para>
    /// <para>This is still one machine's answer, and §1.1 of the manual says so: which lines
    /// respond is a measurement, and it has to be repeated per tool. The Warn/Alarm factors
    /// (1.05 / 1.12) belong to that machine's 2 % baseline scatter — the class defaults stay at
    /// 1.2 / 1.5 for hand-added ratios, whose scatter nobody has measured yet.</para>
    /// </summary>
    public static LeakMonitorSettings CreateDefault() => new()
    {
        Ratios = new List<RatioDefinition>
        {
            new()
            {
                Key = "R_N2Ar", DisplayName = "N₂ 337 / Ar 750",
                // RawMean: 337.1 sits in dense band structure on a sloping continuum, where a
                // side-window baseline subtracts more error than signal. The pedestal it carries
                // is largely divided out by the reference.
                Numerator = new LineRegion
                {
                    Label = "N₂ 337.1", CenterNm = 337.1, HalfWidthNm = 1.0,
                    BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
                },
                Denominator = DefaultArReference(),
                WarnFactor = 1.05, AlarmFactor = 1.12,
            },
            new()
            {
                Key = "R_NOAr", DisplayName = "NO 237 / Ar 750",
                Numerator = new LineRegion
                {
                    Label = "NO 237", CenterNm = 237.0, HalfWidthNm = 1.5,
                    BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
                },
                Denominator = DefaultArReference(),
                WarnFactor = 1.05, AlarmFactor = 1.12,
            },
            new()
            {
                Key = "R_N2357Ar", DisplayName = "N₂ 358 / Ar 750",
                Numerator = new LineRegion
                {
                    Label = "N₂ 357.7", CenterNm = 357.7, HalfWidthNm = 0.5,
                    BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.PeakHeight,
                },
                Denominator = DefaultArReference(),
                WarnFactor = 1.05, AlarmFactor = 1.12,
            },
            new()
            {
                // Same line as the entry above, undivided: the pair reads out whether a rise is
                // chemistry or the whole spectrum brightening, which is the one row of the
                // inspection plan's diagnostic matrix that a single ratio cannot settle.
                // Disabled by factory: absolute intensity does not return to baseline after a
                // leak is closed (measured: +7.6 % still, with the Ar-normalized ratio back to
                // +1 %) and drifts with window fouling, so left armed it would sit in Warning
                // after every event on a tool nobody has tuned yet. Arm it deliberately.
                // Its Warn/Alarm factors are ignored — absolute + RawMean carries the continuum
                // pedestal (ValueHasPedestal), so only the σ thresholds apply.
                Key = "R_N2357Abs", DisplayName = "N₂ 358 absolute", Enabled = false,
                MonitorMode = MonitorMode.AbsoluteIntensity,
                Numerator = new LineRegion
                {
                    Label = "N₂ 357.7", CenterNm = 357.7, HalfWidthNm = 1.0,
                    BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
                },
                Denominator = DefaultArReference(),
            },
        },
    };

    // The default reference is Ar 750.4, sourced from the shared catalog so its Label
    // matches the names the reference-line picker offers.
    private static LineRegion DefaultArReference() =>
        ReferenceLineCatalog.FindByName("Ar 750.4")!.CreateRegion();
}
