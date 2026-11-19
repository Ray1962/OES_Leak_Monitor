using System;
using System.IO;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The ratio CSV and step judging (#96): a step-judged ratio's rise is recorded per frame, so a
/// run can be reviewed afterwards; a configuration without step judging writes exactly the file
/// it always did, so nothing downstream of an unchanged install sees a difference.
/// </summary>
public sealed class RatioCsvStepRiseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oes-steprise-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private static readonly DateTime Epoch = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

    private static SpectrumSample Frame(double seconds, double ratio)
    {
        const int n = 1000;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            inten[i] = 1000f + (i % 2 == 0 ? 1f : -1f);
            if (Math.Abs(wl[i] - 350.0) <= 0.5) inten[i] += (float)(ratio * 2000);
            if (Math.Abs(wl[i] - 500.0) <= 0.5) inten[i] += 2000f;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds), Wavelengths = wl, Intensities = inten,
            IntegrationTime = 0, AverageCount = 0, SerialNumber = "T", IsSimulated = true,
        };
    }

    private static RatioDefinition Def(string key, bool step) => new()
    {
        Key = key, DisplayName = key + " name", MonitorMode = MonitorMode.Ratio, MinSnr = 0,
        EmaTauSeconds = 0.1, ConfirmSeconds = 1, StepJudging = step,
        Numerator = new LineRegion { Label = "X 350", CenterNm = 350, HalfWidthNm = 0.5, BaselineGapNm = 1, BaselineWidthNm = 1, Mode = LineExtractMode.PeakHeight },
        Denominator = new LineRegion { Label = "Y 500", CenterNm = 500, HalfWidthNm = 0.5, BaselineGapNm = 1, BaselineWidthNm = 1, Mode = LineExtractMode.PeakHeight },
    };

    private string[] Write(params RatioDefinition[] defs)
    {
        var s = new LeakMonitorSettings
        {
            Enabled = true, SuppressAlarmsInTestMode = false,
            StepLookbackSeconds = 30, StepGapSeconds = 2, StepMinHistorySeconds = 2,   // 4 s warm-up
        };
        foreach (var d in defs) s.Ratios.Add(d);
        using var engine = new LeakMonitorEngine(s);
        engine.ConfigureTrigger(new LoggerSettings
        {
            TriggerMode = TriggerMode.SpectrumPercentile, TriggerPercentile = 99, SaveStartThresholdIntensity = 100,
        });
        string path;
        using (var logger = new RatioCsvLogger(engine, _root))
        {
            logger.Configure(new LoggerSettings { BaseDirectory = _root, FilePrefix = "T" });
            for (int i = 0; i < 20; i++) engine.ProcessSample(Frame(i * 0.5, i < 14 ? 0.5 : 0.6));
            path = logger.CurrentFile;
        }
        return File.ReadAllLines(path);
    }

    [Fact]
    public void Without_step_judging_the_header_is_exactly_the_old_one()
    {
        var lines = Write(Def("R_A", step: false));
        Assert.Equal("Timestamp,R_A name [R_A],R_A name [R_A]_pctBaseline,OverallState,LeakRate,LeakRateSigma", lines[0]);
        Assert.Empty(RatioCsvReader.Read(new StringReader(string.Join("\n", lines))).StepRise);
    }

    [Fact]
    public void A_step_judged_ratio_adds_one_rise_column_after_the_leak_rate_pair()
    {
        var lines = Write(Def("R_A", step: false), Def("R_B", step: true));
        Assert.EndsWith(",OverallState,LeakRate,LeakRateSigma,R_B name [R_B]_stepRise", lines[0]);

        var data = RatioCsvReader.Read(new StringReader(string.Join("\n", lines)));
        Assert.Equal(new[] { "R_A", "R_B" }, data.RatioKeys);   // the (ratio, %) pairs still parse
        var rise = data.StepRise["R_B"];
        Assert.Equal(data.RowCount, rise.Length);
        Assert.True(double.IsNaN(rise[0]), "a rise was written during warm-up");
        Assert.InRange(rise[^1], 0.18, 0.22);                     // 0.5 → 0.6
        Assert.False(data.StepRise.ContainsKey("R_A"));
    }
}
