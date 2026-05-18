using System;
using System.Collections.Generic;
using System.Linq;

namespace OES_Leak_Monitor;

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

    public RatioDefinition Clone() => new()
    {
        Key = Key, DisplayName = DisplayName, Enabled = Enabled,
        Numerator = Numerator.Clone(), Denominator = Denominator.Clone(),
        WarnFactor = WarnFactor, AlarmFactor = AlarmFactor,
        SigmaWarn = SigmaWarn, SigmaAlarm = SigmaAlarm,
        EmaTauSeconds = EmaTauSeconds, ConfirmSeconds = ConfirmSeconds,
    };
}

/// <summary>Baseline statistics for one ratio, captured during a Golden Run.</summary>
public sealed class GoldenRunRatioBaseline
{
    public string Key { get; set; } = "";
    public double Mean { get; set; }
    public double Sigma { get; set; }
    public int SampleCount { get; set; }

    /// <summary>Reference (denominator) line this baseline was captured against. A baseline
    /// only applies while the ratio's current reference still matches this label.</summary>
    public string ReferenceLabel { get; set; } = "";
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

    /// <summary>Minimum denominator (N2) intensity for the plasma-present gate.</summary>
    public double PlasmaPresentFloor { get; set; }

    public List<GoldenRunRatioBaseline> Baselines { get; set; } = new();

    public GoldenRunRatioBaseline? Find(string key) =>
        Baselines.FirstOrDefault(b => b.Key == key);
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

    public List<RatioDefinition> Ratios { get; set; } = new();
    public List<GoldenRun> GoldenRuns { get; set; } = new();

    public GoldenRun? FindGoldenRun(string? name) =>
        name is null ? null : GoldenRuns.FirstOrDefault(g => g.Name == name);

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
