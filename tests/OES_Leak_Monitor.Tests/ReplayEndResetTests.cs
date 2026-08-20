using System;
using System.Collections.Generic;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Ending a replay drops the monitors' live state, latched alarms included — and says so in the
/// log.
///
/// <para>What this guards: the replay's last frames leave behind an EMA, a confirmation timer
/// and possibly a latched alarm. The first synthetic frame after teardown opens a recorder
/// session under the ordinary prefix and writes that state into it. A real run on 2026-08-20
/// produced <c>P_Ratio_0820153410.csv</c> — a production filename whose first row carries the
/// recording's own σ-scores and <c>OverallState=Alarm</c>, three quarters of a minute after the
/// recording had ended — and an audit trail showing an alarm nobody caused.</para>
///
/// <para>The other half is the audit rule it must not break: a latched alarm ends only where
/// something records that it did. Clearing without an acknowledgement is allowed here, but never
/// silently.</para>
/// </summary>
public class ReplayEndResetTests
{
    private const double LineNm = 350.0;
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    /// <summary>A flat 1000-count continuum with one line sitting on top of it at 350 nm.</summary>
    private static SpectrumSample Frame(double seconds, double lineLevel)
    {
        const int n = 1000;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            // A hair of structure so the baseline has a σ at all: a baseline with none makes the
            // threshold exactly the mean, which would trip on any rise whatsoever.
            inten[i] = 1000f + (i % 2 == 0 ? 1f : -1f);
            if (Math.Abs(wl[i] - LineNm) <= 0.5) inten[i] = (float)lineLevel;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds),
            Wavelengths = wl,
            Intensities = inten,
            IntegrationTime = 0,
            AverageCount = 0,
            SerialNumber = "TEST",
            IsTestMode = true,
        };
    }

    private sealed class Harness : IDisposable
    {
        public LeakMonitorEngine Engine { get; }
        public List<LeakAlarmLevel> Levels { get; } = new();

        public Harness()
        {
            var settings = new LeakMonitorSettings
            {
                Enabled = true,
                SuppressAlarmsInTestMode = false,   // the frames are replayed; alarms are the point
                RequireTwoForAlarm = false,         // one ratio here, so the composite can reach Alarm
                Ratios =
                {
                    new RatioDefinition
                    {
                        Key = "R_test",
                        DisplayName = "test line",
                        Enabled = true,
                        MonitorMode = MonitorMode.AbsoluteIntensity,
                        MinSnr = 0,
                        SigmaWarn = 3, SigmaAlarm = 6,
                        EmaTauSeconds = 0.1, ConfirmSeconds = 1.0,
                        Numerator = new LineRegion
                        {
                            Label = "X 350", CenterNm = LineNm, HalfWidthNm = 0.5,
                            BaselineGapNm = 1.0, BaselineWidthNm = 1.0,
                            Mode = LineExtractMode.RawMean,
                        },
                        Denominator = new LineRegion
                        {
                            Label = "X 500", CenterNm = 500.0, HalfWidthNm = 0.5,
                            BaselineGapNm = 1.0, BaselineWidthNm = 1.0,
                            Mode = LineExtractMode.RawMean,
                        },
                    },
                },
            };
            settings.Ratios.RemoveRange(0, settings.Ratios.Count - 1);   // drop the factory set

            Engine = new LeakMonitorEngine(settings);
            Engine.ConfigureTrigger(new LoggerSettings
            {
                TriggerMode = TriggerMode.SpectrumPercentile,
                TriggerPercentile = 99,
                SaveStartThresholdIntensity = 100,
            });
            Engine.AlarmStateChanged += (_, e) => Levels.Add(e.NewLevel);
        }

        /// <summary>Baseline from a quiet stretch, then a step up held long enough to latch.</summary>
        public void DriveToLatchedAlarm()
        {
            double t = 0;
            Engine.BeginGoldenRunCapture("base", seconds: 2);
            for (; t <= 2.5; t += 0.5) Engine.ProcessSample(Frame(t, 1000));
            for (; t <= 10.0; t += 0.5) Engine.ProcessSample(Frame(t, 2000));
            Assert.Equal(LeakAlarmLevel.Alarm, Levels[^1]);

            // Back to baseline: the alarm is latched, so it must still read Alarm.
            for (; t <= 14.0; t += 0.5) Engine.ProcessSample(Frame(t, 1000));
            Assert.Equal(LeakAlarmLevel.Alarm, Levels[^1]);
        }

        public void Dispose() => Engine.Dispose();
    }

    [Fact]
    public void EndingAReplay_ClearsTheLatchedAlarm()
    {
        using var h = new Harness();
        h.DriveToLatchedAlarm();

        h.Engine.ResetRuntimeState(clearAlarms: true, reason: "replay ended");

        Assert.NotEqual(LeakAlarmLevel.Alarm, h.Levels[^1]);
    }

    /// <summary>
    /// The Monitor tab's Reset is the other caller and must keep behaving as it did: a real,
    /// already-confirmed leak stays latched across a parameter change.
    /// </summary>
    [Fact]
    public void PlainReset_LeavesAConfirmedAlarmLatched()
    {
        using var h = new Harness();
        h.DriveToLatchedAlarm();
        int before = h.Levels.Count;

        h.Engine.ResetRuntimeState(clearAlarms: false);

        Assert.Equal(before, h.Levels.Count);      // no transition
        Assert.Equal(LeakAlarmLevel.Alarm, h.Levels[^1]);
    }
}
