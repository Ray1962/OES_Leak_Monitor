using System;
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
}
