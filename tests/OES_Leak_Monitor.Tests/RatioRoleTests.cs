using System;
using System.Collections.Generic;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Entries that are recorded but never judged.
///
/// <para>What this guards: two of the three processes on the measured tool reach a cross-batch
/// mean/σ of 7.3 and 6.5, against the engine's own baseline-acceptance floor of 10, and a
/// full-spectrum sweep found nothing better. An alarm on those is one operators learn to ignore,
/// which costs more than the alarm is worth — but the value is still worth recording, because the
/// cross-batch trend can be read off it. The guard role is the same machinery for the opposite
/// reason: it is a control quantity whose job is to be read <em>beside</em> an alarm, and giving
/// it a vote would eventually let it veto a real leak.</para>
/// </summary>
public class RatioRoleTests
{
    private const double Line = 350.0;
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    private static SpectrumSample Frame(double seconds, double level)
    {
        const int n = 1000;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            inten[i] = 1000f + (i % 2 == 0 ? 1f : -1f);
            if (Math.Abs(wl[i] - Line) <= 0.5) inten[i] = (float)level;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds), Wavelengths = wl, Intensities = inten,
            IntegrationTime = 0, AverageCount = 0, SerialNumber = "T", IsTestMode = true,
        };
    }

    private static RatioDefinition Def(string key, RatioRole role) => new()
    {
        Key = key, DisplayName = key, Enabled = true, Role = role,
        MonitorMode = MonitorMode.AbsoluteIntensity, MinSnr = 0,
        SigmaWarn = 3, SigmaAlarm = 6, EmaTauSeconds = 0.1, ConfirmSeconds = 1.0,
        Numerator = new LineRegion
        {
            Label = "X", CenterNm = Line, HalfWidthNm = 0.5,
            BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
        },
        Denominator = new LineRegion
        {
            Label = "Y", CenterNm = 500.0, HalfWidthNm = 0.5,
            BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
        },
    };

    private sealed class Harness : IDisposable
    {
        public LeakMonitorEngine Engine { get; }
        public List<LeakAlarmEventArgs> Alarms { get; } = new();
        public LeakMonitorSnapshot? Last { get; private set; }

        public Harness(params RatioDefinition[] defs)
        {
            var settings = new LeakMonitorSettings
            {
                Enabled = true, SuppressAlarmsInTestMode = false, RequireTwoForAlarm = false,
            };
            foreach (var d in defs) settings.Ratios.Add(d);
            Engine = new LeakMonitorEngine(settings);
            Engine.ConfigureTrigger(new LoggerSettings
            {
                TriggerMode = TriggerMode.SpectrumPercentile,
                TriggerPercentile = 99, SaveStartThresholdIntensity = 100,
            });
            Engine.AlarmStateChanged += (_, e) => Alarms.Add(e);
            Engine.SampleProcessed += (_, s) => Last = s;
        }

        /// <summary>Baseline from a quiet stretch, then a large sustained rise.</summary>
        public void DriveBaselineThenRise()
        {
            double t = 0;
            Engine.BeginGoldenRunCapture("b", seconds: 2);
            for (; t <= 2.5; t += 0.5) Engine.ProcessSample(Frame(t, 1000));
            for (; t <= 12.0; t += 0.5) Engine.ProcessSample(Frame(t, 2000));
        }

        public RatioSnapshot Ratio(string key) => Last!.Ratios.First(r => r.Key == key);
        public void Dispose() => Engine.Dispose();
    }

    [Theory]
    [InlineData(RatioRole.TrendOnly)]
    [InlineData(RatioRole.Guard)]
    public void A_non_alarming_entry_records_but_never_escalates(RatioRole role)
    {
        using var h = new Harness(Def("R", role));
        h.DriveBaselineThenRise();

        var r = h.Ratio("R");
        Assert.Equal(RatioState.Observing, r.State);
        Assert.Equal(LeakAlarmLevel.Idle, h.Last!.Overall);   // nothing judged, so nothing to say
        Assert.Empty(h.Alarms);

        // ...but the value, the smoothing and the baseline are all there, which is what the
        // ratio CSV and the batch record write.
        Assert.True(r.HasBaseline);
        Assert.InRange(r.RawRatio, 1900, 2100);
        Assert.False(double.IsNaN(r.SmoothedRatio));
    }

    [Fact]
    public void An_alarming_entry_beside_it_still_alarms()
    {
        using var h = new Harness(Def("R_alarm", RatioRole.Alarm), Def("R_trend", RatioRole.TrendOnly));
        h.DriveBaselineThenRise();

        Assert.Equal(LeakAlarmLevel.Alarm, h.Last!.Overall);
        Assert.Equal(RatioState.Alarm, h.Ratio("R_alarm").State);
        Assert.Equal(RatioState.Observing, h.Ratio("R_trend").State);
    }

    /// <summary>
    /// A guard's reading rides on the alarm event, so whoever reads the alarm can read the
    /// control quantity beside it. Carried rather than looked up afterwards: the answer changes
    /// frame by frame and the question is asked about the moment the alarm fired.
    /// </summary>
    [Fact]
    public void The_alarm_carries_the_guard_readings()
    {
        using var h = new Harness(Def("R_alarm", RatioRole.Alarm), Def("R_guard", RatioRole.Guard));
        h.DriveBaselineThenRise();

        var alarm = h.Alarms.Last();
        var guard = Assert.Single(alarm.Guards);
        Assert.Equal("R_guard", guard.Key);
        Assert.Equal(RatioRole.Guard, guard.Role);
        Assert.False(double.IsNaN(guard.SmoothedRatio));
        // The alarming entry is not in there — it is the thing being explained, not the control.
        Assert.DoesNotContain(alarm.Guards, g => g.Key == "R_alarm");
    }

    /// <summary>
    /// Changing what an entry is for changes what its baseline and any latch refer to, so a
    /// stored one must not carry across the change.
    /// </summary>
    [Fact]
    public void Changing_the_role_changes_what_is_measured()
    {
        var alarming = Def("R", RatioRole.Alarm);
        var trending = alarming.Clone();
        trending.Role = RatioRole.TrendOnly;
        Assert.False(alarming.MeasuresSameAs(trending));
    }

    /// <summary>
    /// The Ratio Setup tab rebuilds every field it saves. A role set by hand and dropped on the
    /// first Save would turn a trend-only entry back into an alarming one, and the first anyone
    /// would know of it is an alarm nobody can act on.
    /// </summary>
    [Fact]
    public void RatioSetupEdit_does_not_drop_the_role()
    {
        var lines = SpectralLineCatalog.All.Select(l => new SpectralLineOption(l)).ToList();
        var original = Def("R", RatioRole.Guard);

        var edited = new RatioEditViewModel(original.Clone(), lines);
        Assert.Equal(RatioRole.Guard, edited.Role);

        edited.DisplayName = "renamed";
        Assert.Equal(RatioRole.Guard, edited.ToDefinition().Role);
    }
}
