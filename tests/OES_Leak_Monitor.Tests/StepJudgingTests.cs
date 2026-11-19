using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Step judging — the rolling-baseline path (#89, #90) — through the engine, on synthetic
/// spectra whose line heights set the ratio exactly.
///
/// <para>What it is for: on the lab machine the leak-free level after ignition decays by tens of
/// percent and then keeps drifting, by more than a DV=300 air leak moves the ratio, so a fixed
/// Golden Run either false-alarms or misses. A leak opened through a valve is a step; the drift
/// is slow. The step path compares the smoothed value with the median of the ratio's own last
/// few minutes, so it follows the drift and still sees the step.</para>
/// </summary>
public class StepJudgingTests
{
    private const double SignalNm = 350.0, ReferenceNm = 500.0, ReferenceHeight = 2000.0;
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    /// <summary>A frame on a flat 1000-count continuum: a peak of <c>ratio × 2000</c> at 350 nm
    /// over a 2000-count reference peak at 500 nm, so the ratio reads <paramref name="ratio"/>.
    /// <paramref name="dark"/> drops the whole spectrum below the save trigger (plasma off).</summary>
    private static SpectrumSample Frame(double seconds, double ratio, bool dark = false)
    {
        const int n = 1000;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            if (dark) { inten[i] = 5f; continue; }
            inten[i] = 1000f + (i % 2 == 0 ? 1f : -1f);
            if (Math.Abs(wl[i] - SignalNm) <= 0.5) inten[i] += (float)(ratio * ReferenceHeight);
            if (Math.Abs(wl[i] - ReferenceNm) <= 0.5) inten[i] += (float)ReferenceHeight;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds), Wavelengths = wl, Intensities = inten,
            IntegrationTime = 0, AverageCount = 0, SerialNumber = "T", IsSimulated = true,
        };
    }

    private static LineRegion Peak(string label, double nm) => new()
    {
        Label = label, CenterNm = nm, HalfWidthNm = 0.5,
        BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.PeakHeight,
    };

    /// <summary>A ratio judged only by pure factors on the Golden Run path (σ terms 0), so what
    /// each path does is arithmetic. Step rises are the factory 10 % / 15 %.</summary>
    private static RatioDefinition Def(string key, bool gr, bool step) => new()
    {
        Key = key, DisplayName = key, Enabled = true, Role = RatioRole.Alarm,
        MonitorMode = MonitorMode.Ratio, MinSnr = 0,
        WarnFactor = 1.1, AlarmFactor = 1.2, SigmaWarn = 0, SigmaAlarm = 0,
        EmaTauSeconds = 0.1, ConfirmSeconds = 2.0,
        GoldenRunJudging = gr, StepJudging = step,
        Numerator = Peak("X 350", SignalNm), Denominator = Peak("Y 500", ReferenceNm),
    };

    private sealed class Harness : IDisposable
    {
        private readonly string _logDir = Path.Combine(Path.GetTempPath(), "oes-step-" + Guid.NewGuid().ToString("N"));
        public LeakMonitorEngine Engine { get; }
        public LeakMonitorSettings Settings { get; }
        public SystemLogger Log { get; }
        public LeakMonitorSnapshot? Last { get; private set; }
        public double T;

        /// <param name="fast">Short windows (30 s lookback, 4 s gap, 4 s history: 8 s warm-up)
        /// for tests about behaviour, not about the factory timing.</param>
        public Harness(bool fast, bool requireTwo, params RatioDefinition[] defs)
        {
            Settings = new LeakMonitorSettings
            {
                Enabled = true, SuppressAlarmsInTestMode = false, RequireTwoForAlarm = requireTwo,
            };
            if (fast)
            {
                Settings.StepLookbackSeconds = 30;
                Settings.StepGapSeconds = 4;
                Settings.StepMinHistorySeconds = 4;
            }
            foreach (var d in defs) Settings.Ratios.Add(d);
            Log = new SystemLogger(_logDir);
            Engine = new LeakMonitorEngine(Settings, Log);
            Engine.ConfigureTrigger(new LoggerSettings
            {
                TriggerMode = TriggerMode.SpectrumPercentile,
                TriggerPercentile = 99, SaveStartThresholdIntensity = 100,
            });
            Engine.SampleProcessed += (_, s) => Last = s;
        }

        /// <summary>Frames every 0.5 s for <paramref name="seconds"/>; returns each frame's
        /// snapshot of <paramref name="key"/>.</summary>
        public List<RatioSnapshot> Run(double seconds, Func<double, double> ratio, string key = "R", bool dark = false)
        {
            var seen = new List<RatioSnapshot>();
            double end = T + seconds;
            for (; T < end - 1e-9; T += 0.5)
            {
                Engine.ProcessSample(Frame(T, ratio(T), dark));
                seen.Add(Ratio(key));
            }
            return seen;
        }

        public List<RatioSnapshot> Run(double seconds, double ratio, string key = "R", bool dark = false) =>
            Run(seconds, _ => ratio, key, dark);

        /// <summary>Captures a Golden Run at a steady <paramref name="ratio"/>.</summary>
        public void Capture(double ratio, double seconds = 2)
        {
            Engine.BeginGoldenRunCapture("gr", seconds);
            Run(seconds + 1.0, ratio);
        }

        public RatioSnapshot Ratio(string key = "R") => Last!.Ratios.First(r => r.Key == key);

        public IEnumerable<Aqst.OesApp.Core.LogRecord> Entries(string condition) =>
            Log.Snapshot().Where(e => e.Condition == condition);

        public void Dispose()
        {
            Engine.Dispose();
            Log.Dispose();
            try { Directory.Delete(_logDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ the step itself

    [Fact]
    public void A_step_warns_then_alarms_then_latches_without_any_Golden_Run()
    {
        using var h = new Harness(fast: true, requireTwo: false, Def("R", gr: true, step: true));
        h.Run(12, 0.50);                                    // past the 8 s warm-up
        Assert.Equal(RatioState.Normal, h.Ratio().State);  // judged by step: no baseline needed
        Assert.False(h.Ratio().HasBaseline);

        var stepped = h.Run(6, 0.65);                       // +30 %
        Assert.Contains(stepped, s => s.State == RatioState.Warning || s.State == RatioState.Alarm);
        var r = h.Ratio();
        Assert.Equal(RatioState.Alarm, r.State);
        Assert.Equal(JudgingPaths.Step, r.RaisedBy);
        Assert.InRange(r.StepRise, 0.28, 0.32);
        Assert.Equal(LeakAlarmLevel.Alarm, h.Last!.Overall);

        var after = h.Run(10, 0.50);                        // cause gone, latch stays
        Assert.All(after, s => Assert.Equal(RatioState.Alarm, s.State));
        var latched = Assert.Single(h.Entries("LeakMonitorRatioAlarmLatched"));
        Assert.Contains("Path=Step", latched.RelatedParameters);
    }

    [Fact]
    public void A_rise_inside_the_confirmation_time_does_not_escalate()
    {
        using var h = new Harness(fast: true, requireTwo: false, Def("R", gr: false, step: true));
        h.Run(12, 0.50);
        var blip = h.Run(1.5, 0.65);                        // shorter than the 2 s confirmation
        h.Run(5, 0.50);
        Assert.DoesNotContain(blip, s => s.State is RatioState.Warning or RatioState.Alarm);
        Assert.Equal(RatioState.Normal, h.Ratio().State);
    }

    [Fact]
    public void A_slow_drift_is_followed_and_never_judged_a_leak()
    {
        // Factory windows. +0.5 %/min for 20 minutes is a 10 % change overall — what a fixed
        // baseline would call a Warning — but the reference lags it by only about a minute and a
        // half, so the rise it sees stays near 1 %.
        using var h = new Harness(fast: false, requireTwo: false, Def("R", gr: false, step: true));
        var all = h.Run(20 * 60, t => 0.50 * (1 + 0.005 * t / 60));
        Assert.All(all, s => Assert.True(s.State is RatioState.Normal or RatioState.NoBaseline, $"{s.State}"));
        Assert.InRange(h.Ratio().StepRise, 0.0, 0.02);
    }

    // ------------------------------------------------------------------ the reference

    [Fact]
    public void Warm_up_counts_down_and_is_not_judged_or_counted_in_the_composite()
    {
        using var h = new Harness(fast: true, requireTwo: false, Def("R", gr: false, step: true));
        var first = h.Run(4, 0.50);
        Assert.All(first, s =>
        {
            Assert.Equal(RatioState.NoBaseline, s.State);
            Assert.True(s.StepWarmupSecondsRemaining > 0);
            Assert.True(double.IsNaN(s.StepRise));
        });
        Assert.True(first[^1].StepWarmupSecondsRemaining < first[0].StepWarmupSecondsRemaining);
        Assert.Equal(LeakAlarmLevel.Idle, h.Last!.Overall);
    }

    [Fact]
    public void A_leak_present_from_ignition_is_the_reference_and_is_not_seen()
    {
        // The blind spot by construction, and the reason the Golden Run path stays: with no
        // leak-free history there is nothing to rise above.
        using var h = new Harness(fast: true, requireTwo: false, Def("R", gr: false, step: true));
        h.Run(20, 0.65);
        Assert.Equal(0, h.Ratio().StepWarmupSecondsRemaining);
        Assert.Equal(RatioState.Normal, h.Ratio().State);
    }

    [Fact]
    public void A_step_longer_than_the_lookback_freezes_the_reference_instead_of_absorbing_it()
    {
        using var h = new Harness(fast: true, requireTwo: false, Def("R", gr: false, step: true));
        h.Run(12, 0.50);
        h.Run(90, 0.65);                                    // three lookbacks long
        var r = h.Ratio();
        Assert.True(r.StepReferenceFrozen);
        Assert.InRange(r.StepReference, 0.495, 0.505);
        Assert.InRange(r.StepRise, 0.28, 0.32);

        h.Engine.Acknowledge("tester");                     // still leaking: re-confirms
        h.Run(4, 0.65);
        Assert.Equal(RatioState.Alarm, h.Ratio().State);

        h.Engine.Acknowledge("tester");
        var over = h.Run(6, 0.50);                          // step over
        Assert.False(h.Ratio().StepReferenceFrozen);
        // Every pre-event reading aged out while frozen; the frozen reference carries on until a
        // fresh one exists, so the path never goes unjudged.
        Assert.All(over.Skip(2), s => Assert.Equal(RatioState.Normal, s.State));
    }

    [Theory]
    [InlineData("plasma")]
    [InlineData("reset")]
    [InlineData("disabled")]
    public void History_restarts_wherever_the_smoothing_restarts(string how)
    {
        using var h = new Harness(fast: true, requireTwo: false, Def("R", gr: false, step: true));
        h.Run(12, 0.50);
        Assert.Equal(0, h.Ratio().StepWarmupSecondsRemaining);

        switch (how)
        {
            case "plasma": h.Run(3, 0.50, dark: true); break;
            case "reset": h.Engine.ResetRuntimeState(clearAlarms: false); break;
            case "disabled":
                h.Engine.SetRatioEnabled("R", false);
                h.Run(1, 0.50);
                h.Engine.SetRatioEnabled("R", true);
                break;
        }
        h.Run(1, 0.50);
        var r = h.Ratio();
        Assert.True(r.StepWarmupSecondsRemaining > 6, $"warm-up {r.StepWarmupSecondsRemaining}");
        Assert.Equal(RatioState.NoBaseline, r.State);
    }

    // ------------------------------------------------------------------ two paths

    /// <summary>The two scenarios that tell the paths apart: a slow 30 % climb (Golden Run sees
    /// it, the rolling reference follows it) and a 20 % step after a 20 % slide (the rolling
    /// reference sees it, the Golden Run level is only regained).</summary>
    [Theory]
    [InlineData(true, false, RatioState.Alarm, RatioState.Normal)]
    [InlineData(false, true, RatioState.Normal, RatioState.Alarm)]
    [InlineData(true, true, RatioState.Alarm, RatioState.Alarm)]
    public void The_state_is_the_more_severe_of_the_enabled_paths(
        bool gr, bool step, RatioState afterClimb, RatioState afterStepOnSlide)
    {
        using (var h = new Harness(fast: true, requireTwo: false, Def("R", gr, step)))
        {
            h.Capture(0.50);
            h.Run(12, 0.50);
            h.Run(10 * 60, t => Math.Min(0.65, 0.50 + 0.15 * (h.T - 15) / 540));
            Assert.Equal(afterClimb, h.Ratio().State);
            if (afterClimb == RatioState.Alarm) Assert.Equal(JudgingPaths.GoldenRun, h.Ratio().RaisedBy);
        }
        using (var h = new Harness(fast: true, requireTwo: false, Def("R", gr, step)))
        {
            h.Capture(0.50);
            h.Run(5 * 60, t => Math.Max(0.40, 0.50 - 0.10 * (h.T - 3) / 240));
            h.Run(6, 0.48);                                 // +20 % over 0.40, still below 0.50
            Assert.Equal(afterStepOnSlide, h.Ratio().State);
            if (afterStepOnSlide == RatioState.Alarm) Assert.Equal(JudgingPaths.Step, h.Ratio().RaisedBy);
        }
    }

    [Fact]
    public void A_Golden_Run_capture_suppresses_the_Golden_Run_latch_but_not_the_step_latch()
    {
        using var h = new Harness(fast: true, requireTwo: false, Def("R", gr: true, step: true));
        h.Capture(0.50);
        h.Run(12, 0.50);

        h.Engine.BeginGoldenRunCapture("again", seconds: 8);
        h.Run(6, 0.70);                                     // a leak during the capture: +40 %
        h.Run(4, 0.70);

        var latched = Assert.Single(h.Entries("LeakMonitorRatioAlarmLatched"));
        Assert.Contains("Path=Step", latched.RelatedParameters);
        Assert.DoesNotContain("GR", latched.RelatedParameters);
    }

    [Fact]
    public void Acknowledge_clears_both_paths_and_the_log_names_each_one()
    {
        using var h = new Harness(fast: true, requireTwo: false, Def("R", gr: true, step: true));
        h.Capture(0.50);
        h.Run(12, 0.50);
        h.Run(6, 0.70);                                     // both paths alarm
        h.Run(4, 0.50);
        Assert.Equal(RatioState.Alarm, h.Ratio().State);

        h.Engine.Acknowledge("tester");
        h.Run(1, 0.50);
        Assert.Equal(RatioState.Normal, h.Ratio().State);
        var ack = Assert.Single(h.Entries("LeakMonitorAcknowledged"));
        Assert.Contains("R (GR+Step)", ack.Value);
    }

    [Fact]
    public void Under_require_two_one_stepping_ratio_is_not_a_composite_alarm()
    {
        using var h = new Harness(fast: true, requireTwo: true,
            Def("A", gr: false, step: true), Def("B", gr: false, step: true));
        h.Run(12, 0.50, key: "A");
        // Both entries read the same synthetic line, so B is taken out of the judgement: A is
        // then the only ratio in Alarm, which RequireTwoForAlarm does not let through.
        h.Engine.SetRatioEnabled("B", false);
        h.Run(6, 0.65, key: "A");
        Assert.Equal(RatioState.Alarm, h.Ratio("A").State);
        Assert.NotEqual(LeakAlarmLevel.Alarm, h.Last!.Overall);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void A_pedestal_ratio_ignores_step_judging_and_says_so_once()
    {
        var def = Def("R", gr: true, step: true);
        def.Numerator.Mode = LineExtractMode.RawMean;
        using var h = new Harness(fast: true, requireTwo: false, def);
        h.Run(2, 0.5);
        Assert.Equal(JudgingPaths.GoldenRun, h.Ratio().EnabledPaths);
        Assert.True(double.IsNaN(h.Ratio().StepWarmupSecondsRemaining));

        h.Engine.ReloadRatios();
        h.Engine.ReloadRatios();
        Assert.Single(h.Entries("LeakMonitorStepJudgingIgnored"));
    }

    [Fact]
    public void Out_of_range_step_settings_are_clamped_and_logged()
    {
        var def = Def("R", gr: false, step: true);
        def.StepWarnRise = 0.2;
        def.StepAlarmRise = 0.1;                           // below Warn
        using var h = new Harness(fast: true, requireTwo: false, def);
        // Harness built with a 4 s gap; break the window rules and reload.
        h.Settings.StepGapSeconds = 40;                    // not below the 30 s lookback
        h.Engine.ReloadRatios();

        Assert.Equal(0.2, h.Settings.Ratios[0].StepAlarmRise);
        Assert.True(h.Settings.StepGapSeconds < h.Settings.StepLookbackSeconds);
        Assert.True(h.Settings.StepMinHistorySeconds <= h.Settings.StepLookbackSeconds - h.Settings.StepGapSeconds);
        var entries = h.Entries("LeakMonitorStepSettingsClamped").ToList();
        Assert.Contains(entries, e => e.Description.Contains("stepAlarmRise"));
        Assert.Contains(entries, e => e.Description.Contains("stepGapSeconds"));
    }
}
