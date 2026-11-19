using System;
using System.Collections.Generic;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The Golden Run judging path, pinned through the engine: escalation after sustained
/// confirmation, the latch outliving the condition, Acknowledge ending it, and an interruption
/// not counting towards confirmation. Written before a second judging path (rolling baseline,
/// #89) was added beside it, so that adding one cannot quietly change how the first behaves.
/// </summary>
public class GoldenRunJudgingTests
{
    private const double Line = 350.0;
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    /// <summary>A frame whose monitored line reads <paramref name="level"/>; <paramref name="dark"/>
    /// puts the whole spectrum below the save trigger, i.e. plasma off.</summary>
    private static SpectrumSample Frame(double seconds, double level, bool dark = false)
    {
        const int n = 1000;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            inten[i] = dark ? 5f : 1000f + (i % 2 == 0 ? 1f : -1f);
            if (!dark && Math.Abs(wl[i] - Line) <= 0.5) inten[i] = (float)level;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds), Wavelengths = wl, Intensities = inten,
            IntegrationTime = 0, AverageCount = 0, SerialNumber = "T", IsSimulated = true,
        };
    }

    private static RatioDefinition Def(string key) => new()
    {
        Key = key, DisplayName = key, Enabled = true, Role = RatioRole.Alarm,
        MonitorMode = MonitorMode.AbsoluteIntensity, MinSnr = 0,
        SigmaWarn = 3, SigmaAlarm = 6, EmaTauSeconds = 0.1, ConfirmSeconds = 2.0,
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
        public LeakMonitorSnapshot? Last { get; private set; }
        public double T;

        public Harness()
        {
            var settings = new LeakMonitorSettings
            {
                Enabled = true, SuppressAlarmsInTestMode = false, RequireTwoForAlarm = false,
            };
            settings.Ratios.Add(Def("R"));
            Engine = new LeakMonitorEngine(settings);
            Engine.ConfigureTrigger(new LoggerSettings
            {
                TriggerMode = TriggerMode.SpectrumPercentile,
                TriggerPercentile = 99, SaveStartThresholdIntensity = 100,
            });
            Engine.SampleProcessed += (_, s) => Last = s;
            Engine.BeginGoldenRunCapture("b", seconds: 2);
            Run(3.0, 1000);   // frames 0–2.5 s: the 2 s capture finishes on the last one
        }

        /// <summary>Feeds frames every 0.5 s for <paramref name="seconds"/> and returns the
        /// state seen after each one.</summary>
        public List<RatioState> Run(double seconds, double level, bool dark = false)
        {
            var seen = new List<RatioState>();
            double end = T + seconds;
            for (; T < end; T += 0.5)
            {
                Engine.ProcessSample(Frame(T, level, dark));
                seen.Add(State);
            }
            return seen;
        }

        public RatioState State => Last!.Ratios.First(r => r.Key == "R").State;
        public void Dispose() => Engine.Dispose();
    }

    [Fact]
    public void A_sustained_rise_alarms_only_after_confirmation_and_then_latches()
    {
        using var h = new Harness();
        Assert.Equal(RatioState.Normal, h.State);

        var rising = h.Run(1.0, 2000);
        Assert.DoesNotContain(RatioState.Alarm, rising);       // inside the 2 s confirmation

        h.Run(3.0, 2000);
        Assert.Equal(RatioState.Alarm, h.State);

        var after = h.Run(5.0, 1000);                           // the cause is gone...
        Assert.All(after, s => Assert.Equal(RatioState.Alarm, s));  // ...the alarm is not
        Assert.Equal(LeakAlarmLevel.Alarm, h.Last!.Overall);
    }

    [Fact]
    public void Acknowledge_ends_the_latch_and_a_recovered_ratio_reads_normal()
    {
        using var h = new Harness();
        h.Run(4.0, 2000);
        h.Run(3.0, 1000);
        Assert.Equal(RatioState.Alarm, h.State);

        h.Engine.Acknowledge("tester");
        h.Run(1.0, 1000);
        Assert.Equal(RatioState.Normal, h.State);
    }

    [Fact]
    public void Time_above_threshold_before_a_plasma_gap_does_not_count_after_it()
    {
        using var h = new Harness();
        h.Run(1.5, 2000);                   // 1.5 s above — short of the 2 s confirmation
        h.Run(3.0, 0, dark: true);          // plasma off: timers restart
        var resumed = h.Run(1.5, 2000);     // another 1.5 s — would confirm if the first counted
        Assert.DoesNotContain(RatioState.Alarm, resumed);
    }

    [Fact]
    public void A_plasma_gap_keeps_a_latched_alarm()
    {
        using var h = new Harness();
        h.Run(4.0, 2000);
        Assert.Equal(RatioState.Alarm, h.State);

        h.Run(3.0, 0, dark: true);
        h.Run(2.0, 1000);
        Assert.Equal(RatioState.Alarm, h.State);
    }
}
