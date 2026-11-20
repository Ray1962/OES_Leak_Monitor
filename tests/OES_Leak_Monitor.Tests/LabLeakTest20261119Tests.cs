using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OES_Leak_Monitor;
using Xunit;
using Xunit.Abstractions;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Step judging on real spectra: the eleven recordings of the 2026-11-19 air-leak test on the lab
/// machine (#89), replayed through the engine with the configuration #90 recommends for it.
///
/// <para>Why these recordings: a dosing valve let air into an N₂ / Ar / H plasma for about a
/// minute per run, at DV = 0 / 200 / 300 / 400, in two sessions three hours apart. The leak
/// response repeated (DV=300: NO/Ar +30 %, OH/Ar +8 %), but the leak-free level did not —
/// after ignition both ratios started far above the afternoon's Golden Run and slid by more
/// than the leak moves them. A fixed baseline false-alarmed at ignition and missed DV=300 once
/// OH had drifted down; this is the test that the rolling baseline does neither.</para>
///
/// <para>Expected trip times come from the Python mirror of the step path
/// (<c>step_csharp_like.py</c> in the lab-test delivery), whose ports of the extractor and the
/// monitor matched the app's own Replay output frame for frame. The recordings are 3–12 MB each
/// and not committed: the test reads them from the data folder and <b>skips</b> without them.
/// Point <c>OES_TEST_LEAK_20261119</c> at a folder holding them to run it elsewhere.</para>
/// </summary>
public class LabLeakTest20261119Tests
{
    public const string FolderVariable = "OES_TEST_LEAK_20261119";
    public const string DefaultFolder = @"C:\DualOES\202611\19";

    private readonly ITestOutputHelper _out;
    public LabLeakTest20261119Tests(ITestOutputHelper output) => _out = output;

    private static string? Folder()
    {
        var configured = Environment.GetEnvironmentVariable(FolderVariable);
        var folder = string.IsNullOrWhiteSpace(configured) ? DefaultFolder : configured;
        return Directory.Exists(folder) ? folder : null;
    }

    /// <summary>The lab machine's two leak indicators as patched for #88, on step judging only.</summary>
    private static LeakMonitorSettings LabConfiguration()
    {
        var ar = new LineRegion
        {
            // Peak search off, as on the tool: the class default (1 nm) re-centres each window
            // on the strongest nearby pixel and reads a different quantity.
            Label = "Ar 750.4", CenterNm = 750.4, HalfWidthNm = 0.5,
            BaselineGapNm = 1, BaselineWidthNm = 1, Mode = LineExtractMode.RawMean, PeakSearchHalfWidthNm = 0,
        };
        RatioDefinition Ratio(string key, string name, LineRegion num, double warn, double alarm) => new()
        {
            Key = key, DisplayName = name, Enabled = true, MonitorMode = MonitorMode.Ratio,
            Numerator = num, Denominator = ar.Clone(),
            GoldenRunJudging = false, StepJudging = true,
            StepWarnRise = warn, StepAlarmRise = alarm,
        };
        var s = new LeakMonitorSettings
        {
            Enabled = true, SuppressAlarmsInTestMode = false, RequireTwoForAlarm = true,
        };
        s.Ratios.Add(Ratio("R_8d476877", "NO 235.6 / Ar 750.4", new LineRegion
        {
            Label = "uNO 235.6", CenterNm = 235.6, HalfWidthNm = 1.2,
            BaselineGapNm = 4, BaselineWidthNm = 2, Mode = LineExtractMode.Integral, PeakSearchHalfWidthNm = 0,
        }, 0.10, 0.15));
        s.Ratios.Add(Ratio("R_6017661d", "OH 308.8 / Ar 750.4", new LineRegion
        {
            Label = "uOH 308.8", CenterNm = 308.8, HalfWidthNm = 1.5,
            BaselineGapNm = 6.7, BaselineWidthNm = 2, Mode = LineExtractMode.Integral, PeakSearchHalfWidthNm = 0,
        }, 0.02, 0.03));
        s.WavelengthCorrections.Add(new WavelengthCorrection { Species = "Ar", WavelengthNm = 750.4, OffsetNm = -0.1 });
        return s;
    }

    /// <param name="file">Recording, in the order the test ran.</param>
    /// <param name="expected">Recording time of the composite Alarm, or null for a run with no leak.</param>
    /// <param name="until">Judge frames up to this time only (the DV=400 file runs on to 16:04).</param>
    [SkippableTheory]
    [InlineData("P_OES1_1119151003_1.csv", null, null)]           // DV=0, the Golden Run recording
    [InlineData("P_OES1_1119151842.csv", null, null)]             // DV=200
    [InlineData("P_OES1_1119152128.csv", "15:22:28.375", null)]   // DV=300
    [InlineData("P_OES1_1119152434.csv", "15:25:30.114", "15:30:00")] // DV=400
    [InlineData("P_OES1_1119174509.csv", null, null)]             // DV=0 at ignition: NO +40 %, OH +19 % over the afternoon
    [InlineData("P_OES1_1119175258.csv", null, null)]             // DV=200
    [InlineData("P_OES1_1119175752.csv", "17:59:06.916", null)]   // DV=300
    [InlineData("P_OES1_1119180025.csv", "18:01:21.144", null)]   // DV=400
    [InlineData("P_OES1_1119180900.csv", null, null)]             // DV=200
    [InlineData("P_OES1_1119181133.csv", "18:12:29.789", null)]   // DV=300, OH 8 % below the afternoon baseline
    [InlineData("P_OES1_1119181335.csv", "18:14:31.316", null)]   // DV=400
    public void Step_judging_calls_every_leak_and_nothing_else(string file, string? expected, string? until)
    {
        var folder = Folder();
        Skip.If(folder is null || !File.Exists(Path.Combine(folder, file)),
            $"recordings of 2026-11-19 not found — set {FolderVariable} to a folder holding {file}, or put it in {DefaultFolder}");
        var path = Path.Combine(folder!, file);

        var run = RecordedRun.Load(path);
        // The recording's first column is the time of day; ElapsedSec is relative to it.
        var firstStamp = File.ReadLines(path).Skip(1).First().Split(',')[0];
        var first = DateTime.ParseExact("2026-11-19 " + firstStamp, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var epoch = first.AddSeconds(-run.ElapsedSeconds(0));
        DateTime? stop = until is null ? null : DateTime.ParseExact("2026-11-19 " + until, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        using var engine = new LeakMonitorEngine(LabConfiguration());
        engine.ConfigureTrigger(new LoggerSettings
        {
            TriggerMode = TriggerMode.SpectrumPercentile, TriggerPercentile = 99,
            SaveStartThresholdIntensity = 1000,
        });
        LeakMonitorSnapshot? last = null;
        engine.SampleProcessed += (_, s) => last = s;

        DateTime? alarmAt = null;
        double maxNo = double.NegativeInfinity, maxOh = double.NegativeInfinity;
        for (int i = 0; i < run.FrameCount; i++)
        {
            var frame = run.Frame(i, epoch);
            if (stop is { } st && frame.Timestamp > st) break;
            engine.ProcessSample(frame);
            var no = last!.Ratios.First(r => r.Key == "R_8d476877");
            var oh = last.Ratios.First(r => r.Key == "R_6017661d");
            if (!double.IsNaN(no.StepRise)) maxNo = Math.Max(maxNo, no.StepRise);
            if (!double.IsNaN(oh.StepRise)) maxOh = Math.Max(maxOh, oh.StepRise);
            if (alarmAt is null && last.Overall == LeakAlarmLevel.Alarm) alarmAt = frame.Timestamp;
        }
        _out.WriteLine($"{file}: composite Alarm {alarmAt:HH:mm:ss.fff}; max rise NO {maxNo:P1}, OH {maxOh:P1}");

        if (expected is null)
        {
            Assert.Null(alarmAt);
            return;
        }
        Assert.NotNull(alarmAt);
        var want = DateTime.ParseExact("2026-11-19 " + expected, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        // Within one frame (≈ 0.7 s at 100 ms × 5).
        Assert.InRange((alarmAt!.Value - want).TotalSeconds, -1.0, 1.0);
    }

    // --- #125: old vs pixel-based windows ------------------------------------------------

    /// <summary>Frame-to-frame noise of a ratio series, relative to its mean: std of first
    /// differences / √2, so the afternoon's slow drift does not count as noise.</summary>
    private static double RelativeNoise(IReadOnlyList<double> r)
    {
        var d = Enumerable.Range(1, r.Count - 1).Select(i => r[i] - r[i - 1]).ToList();
        double m = d.Average();
        double sd = Math.Sqrt(d.Sum(x => (x - m) * (x - m)) / (d.Count - 1)) / Math.Sqrt(2);
        return sd / r.Average();
    }

    /// <summary>Leak step of a DV run: highest 10-frame mean over the median of the first
    /// fifth of the plasma frames (before the valve opens), relative.</summary>
    private static double RelativeStep(IReadOnlyList<double> r)
    {
        var head = r.Take(Math.Max(10, r.Count / 5)).OrderBy(x => x).ToList();
        double base0 = head[head.Count / 2];
        double peak = Enumerable.Range(0, r.Count - 9).Max(i => r.Skip(i).Take(10).Average());
        return (peak - base0) / base0;
    }

    private static List<double> RatioSeries(string path, LineRegion num, LineRegion den)
    {
        var run = RecordedRun.Load(path);
        var list = new List<double>();
        for (int i = 0; i < run.FrameCount; i++)
        {
            var y = run.Frame(i, DateTime.MinValue).Intensities;
            if (y.Max() < 1000) continue;   // plasma off
            var n = LineIntensityExtractor.Extract(run.Wavelengths, y, num);
            var d = LineIntensityExtractor.Extract(run.Wavelengths, y, den);
            if (n.HasValue && d.HasValue && d.Value > 0) list.Add(n.Value / d.Value);
        }
        return list;
    }

    /// <summary>
    /// The windows of #125 against the ones they replace, on the DV=0 Golden Run recording
    /// (noise) and the first DV=300 run (step). Pass: neither noise nor step SNR worse by more than 2 %.
    /// Each "new" region is the old one through <see cref="LineWindowAdvice.ApplySuggested"/>
    /// — what Apply suggested widths would do on the tool — and the factory ratios are the old
    /// and new <see cref="LeakMonitorSettings.CreateDefault"/> numerators over Ar 750.4.
    /// </summary>
    [SkippableFact]
    public void Pixel_based_windows_lower_noise_without_losing_the_leak_step()
    {
        const string quiet = "P_OES1_1119151003_1.csv", leak = "P_OES1_1119152128.csv";
        var folder = Folder();
        Skip.If(folder is null || !File.Exists(Path.Combine(folder, quiet)) || !File.Exists(Path.Combine(folder, leak)),
            $"recordings of 2026-11-19 not found — set {FolderVariable} to a folder holding {quiet} and {leak}");

        var lab = LabConfiguration();
        var arShift = -0.1;   // the lab's Ar 750.4 wavelength correction
        LineRegion Shift(LineRegion r) { var c = r.Clone(); if (c.Label.StartsWith("Ar")) c.CenterNm += arShift; return c; }

        var oldArPeak = new LineRegion { Label = "Ar 750.4", CenterNm = 750.4, HalfWidthNm = 0.5, BaselineGapNm = 1, BaselineWidthNm = 1, Mode = LineExtractMode.PeakHeight };
        var cases = new List<(string Name, LineRegion OldNum, LineRegion OldDen, LineRegion NewNum, LineRegion NewDen)>();
        foreach (var r in lab.Ratios)
            cases.Add((r.DisplayName, r.Numerator, r.Denominator,
                       LineWindowAdvice.ApplySuggested(r.Numerator, 0.6), LineWindowAdvice.ApplySuggested(r.Denominator, 0.6)));
        foreach (var r in lab.Ratios)
        {
            cases.Add((r.DisplayName + " [num only]", r.Numerator, r.Denominator, LineWindowAdvice.ApplySuggested(r.Numerator, 0.6), r.Denominator));
            cases.Add((r.DisplayName + " [Ar only]", r.Numerator, r.Denominator, r.Numerator, LineWindowAdvice.ApplySuggested(r.Denominator, 0.6)));
        }
        var factory = LeakMonitorSettings.CreateDefault().Ratios.Where(r => r.MonitorMode == MonitorMode.Ratio).ToList();
        var oldNums = new Dictionary<string, LineRegion>
        {
            ["R_N2Ar"] = new() { Label = "N₂ 337.1", CenterNm = 337.1, HalfWidthNm = 1.0, BaselineGapNm = 1, BaselineWidthNm = 1, Mode = LineExtractMode.RawMean },
            ["R_NOAr"] = new() { Label = "NO 237", CenterNm = 237.0, HalfWidthNm = 1.5, BaselineGapNm = 1, BaselineWidthNm = 1, Mode = LineExtractMode.RawMean },
            ["R_N2357Ar"] = new() { Label = "N₂ 357.7", CenterNm = 357.7, HalfWidthNm = 0.5, BaselineGapNm = 1, BaselineWidthNm = 1, Mode = LineExtractMode.PeakHeight },
        };
        foreach (var r in factory)
            cases.Add(("factory " + r.DisplayName, oldNums[r.Key], oldArPeak, r.Numerator, r.Denominator));

        var worse = new List<string>();
        _out.WriteLine("ratio | noise old → new | step old → new | step SNR old → new");
        foreach (var c in cases)
        {
            double nOld = RelativeNoise(RatioSeries(Path.Combine(folder!, quiet), Shift(c.OldNum), Shift(c.OldDen)));
            double nNew = RelativeNoise(RatioSeries(Path.Combine(folder!, quiet), Shift(c.NewNum), Shift(c.NewDen)));
            double sOld = RelativeStep(RatioSeries(Path.Combine(folder!, leak), Shift(c.OldNum), Shift(c.OldDen)));
            double sNew = RelativeStep(RatioSeries(Path.Combine(folder!, leak), Shift(c.NewNum), Shift(c.NewDen)));
            double snrOld = sOld / nOld, snrNew = sNew / nNew;
            _out.WriteLine($"{c.Name} | {nOld:P2} → {nNew:P2} | {sOld:P1} → {sNew:P1} | {snrOld:0.0} → {snrNew:0.0}");
            // 2 % slack: a window one pixel wider moves both numbers by about that much at random.
            if (nNew > nOld * 1.02 || snrNew < snrOld * 0.98) worse.Add(c.Name);
        }
        Assert.True(worse.Count == 0, "worse with the new windows: " + string.Join(", ", worse));
    }
}
