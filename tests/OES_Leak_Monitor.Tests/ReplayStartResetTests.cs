using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// A replay that starts from its first frame starts a new run for the leak monitor (#139).
///
/// <para>What this guards: with no spectrometer, Connect → Start streams the SDK's synthetic
/// spectrum, whose frame p99 (≈ 1 500) clears a site trigger of 1 000. The engine opens a plasma
/// step on those frames, classifies it after <c>DecideAfterFrames</c> (as <c>Unknown</c> — the
/// synthetic spectrum matches no rule) and locks the verdict. Play then swaps the frames for the
/// recording's without the boundary gate ever closing, so the whole recording was judged as part
/// of that locked <c>Unknown</c> step: nothing class-scoped was judged, and the screen looked
/// exactly like a correct "not this process". Found on 2026-11-25 replaying the 11/25 precoat
/// and recipe recordings (<c>docs/replay-sop-20261125-zh-TW.md</c> §0).</para>
///
/// <para>The reset has to happen on the acquisition thread right before the first recorded
/// frames are fanned out — a reset in the UI-thread <c>ReplayStarting</c> handler leaves a gap
/// before <see cref="SpectrumReplaySource.Play"/> in which one more synthetic frame can open the
/// step again. So the source reports the tick that delivers recorded frame 0
/// (<see cref="ReplayTick.Started"/>), the way it already reports the last one.</para>
/// </summary>
public class ReplayStartResetTests : IDisposable
{
    private readonly string _csv;

    public ReplayStartResetTests()
    {
        // Five frames one recording-second apart; content does not matter to the transport.
        _csv = Path.Combine(Path.GetTempPath(), $"replay-start-{Guid.NewGuid():N}.csv");
        var sb = new StringBuilder("WaveLength,400.0,400.5,401.0\n");
        for (int i = 0; i < 5; i++) sb.Append($"10:00:0{i}.000,1,2,3\n");
        File.WriteAllText(_csv, sb.ToString());
    }

    public void Dispose() => File.Delete(_csv);

    private static SpectrumSample Synthetic() => new()
    {
        Timestamp = DateTime.Now,
        Wavelengths = new float[] { 400f, 400.5f, 401f },
        Intensities = new float[] { 1000f, 1500f, 1000f },
        SerialNumber = "TEST",
        IsSimulated = true,
    };

    private SpectrumReplaySource Loaded()
    {
        var src = new SpectrumReplaySource { Speed = SpectrumReplaySource.MaxSpeed };
        src.Load(_csv);
        return src;
    }

    /// <summary>Ticks until a tick delivers at least one frame (20× → one recorded second every 50 ms).</summary>
    private static ReplayTick NextDelivering(SpectrumReplaySource src)
    {
        for (int i = 0; i < 200; i++)
        {
            var tick = src.Advance(Synthetic());
            if (tick.Frames.Count > 0) return tick;
            Thread.Sleep(10);
        }
        throw new TimeoutException("the replay delivered nothing");
    }

    [Fact]
    public void The_tick_that_delivers_the_first_recorded_frame_is_marked_started()
    {
        var src = Loaded();
        src.Play();

        var first = NextDelivering(src);
        Assert.True(first.Started);

        var second = NextDelivering(src);
        Assert.False(second.Started);
    }

    [Fact]
    public void Resuming_after_a_pause_is_not_a_start()
    {
        var src = Loaded();
        src.Play();
        NextDelivering(src);
        NextDelivering(src);

        src.Pause();
        src.Play();
        Assert.False(NextDelivering(src).Started);
    }

    [Fact]
    public void Restart_and_playing_again_after_the_end_are_starts()
    {
        var src = Loaded();
        src.Play();
        NextDelivering(src);
        NextDelivering(src);

        src.Restart();
        Assert.True(NextDelivering(src).Started);

        ReplayTick tick;
        do tick = NextDelivering(src); while (!tick.Finished);
        src.Play();   // from Finished: plays again from the top
        Assert.True(NextDelivering(src).Started);
    }

    [Fact]
    public void A_tick_with_no_replay_running_is_not_a_start()
    {
        var src = Loaded();   // loaded, not playing
        Assert.False(src.Advance(Synthetic()).Started);
    }

    // ---------------------------------------------------------------- the host's half

    private static readonly DateTime Epoch = new(2026, 11, 25, 20, 0, 0, DateTimeKind.Local);

    /// <summary>
    /// A 1100-point frame from 300 to 849.5 nm: a flat continuum, "N2" at 336.7 and "Ar" at
    /// 750.8 as plateaus wide enough to carry the frame's 99th percentile.
    /// </summary>
    private static SpectrumSample Frame(double seconds, double continuum, double n2, double ar)
    {
        const int n = 1100;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            inten[i] = (float)(continuum + (i % 2 == 0 ? 1 : -1));
            if (Math.Abs(wl[i] - 336.7) <= 10) inten[i] = (float)n2;
            if (Math.Abs(wl[i] - 750.8) <= 10) inten[i] = (float)ar;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds), Wavelengths = wl, Intensities = inten,
            SerialNumber = "TEST", IsSimulated = true,
        };
    }

    /// <summary>What Connect → Start streams: p99 ≈ 1 500 and no rule matches (N2/Ar ≈ 1).</summary>
    private static SpectrumSample SyntheticFrame(double t) => Frame(t, continuum: 1000, n2: 1500, ar: 1500);
    /// <summary>A recorded recipe step: N2 / Ar = 0.025, as the 11/25 recordings read.</summary>
    private static SpectrumSample RecipeFrame(double t) => Frame(t, continuum: 100, n2: 500, ar: 20000);

    private static LineRegion Raw(string label, double nm) => new()
    {
        Label = label, CenterNm = nm, HalfWidthNm = 0.5,
        BaselineGapNm = 1.0, BaselineWidthNm = 1.0, Mode = LineExtractMode.RawMean,
    };

    /// <summary>The 11/25 rules (docs/replay-sop-20261125-zh-TW.md §1) and a site trigger of 1000.</summary>
    private static (LeakMonitorEngine engine, List<LeakMonitorSnapshot> snaps) Engine()
    {
        ProcessClassRule Rule(string cls, ComparisonOp op, double th) => new()
        {
            ClassName = cls, Numerator = Raw("N2 336.7", 336.7), Denominator = Raw("Ar 750.8", 750.8),
            Op = op, Threshold = th,
        };
        var settings = new LeakMonitorSettings
        {
            Enabled = true,
            SuppressAlarmsInTestMode = false,
            ProcessClassifier = new ProcessClassifierSettings
            {
                Enabled = true,
                DecideAfterFrames = 3,
                Rules = { Rule("Precoat", ComparisonOp.GreaterThan, 5), Rule("Recipe", ComparisonOp.LessThan, 0.5) },
            },
        };
        var engine = new LeakMonitorEngine(settings);
        engine.ConfigureTrigger(new LoggerSettings
        {
            TriggerMode = TriggerMode.SpectrumPercentile, TriggerPercentile = 99,
            SaveStartThresholdIntensity = 1000,
        });
        var snaps = new List<LeakMonitorSnapshot>();
        engine.SampleProcessed += (_, s) => snaps.Add(s);
        return (engine, snaps);
    }

    private static ReplayTick Tick(bool started, double from) => new()
    {
        Handled = true, Started = started,
        Frames = Enumerable.Range(0, 5).Select(i => RecipeFrame(from + 4 * i)).ToList(),
    };

    /// <summary>
    /// The bug as found: synthetic frames open a step and lock it Unknown; the recording that
    /// follows, delivered on a tick that starts the replay, is classified on its own frames.
    /// </summary>
    [Fact]
    public void A_started_tick_judges_the_recording_on_its_own_steps_not_the_synthetic_one()
    {
        var (engine, snaps) = Engine();
        using var _ = engine;
        for (int i = 0; i < 5; i++) engine.ProcessSample(SyntheticFrame(i * 0.2));
        Assert.Equal(ProcessClassifier.Unknown, snaps[^1].ProcessClass);

        ReplayDelivery.Deliver(Tick(started: true, from: 2), engine, engine.ProcessSample);

        Assert.Equal("Recipe", snaps[^1].ProcessClass);
    }

    /// <summary>A tick that is not a start (a resume, or the second tick of a run) leaves the
    /// step it is in alone — resetting there would re-judge the opening frames of every pause.</summary>
    [Fact]
    public void A_tick_that_is_not_a_start_leaves_the_running_step_alone()
    {
        var (engine, snaps) = Engine();
        using var _ = engine;
        for (int i = 0; i < 5; i++) engine.ProcessSample(SyntheticFrame(i * 0.2));

        ReplayDelivery.Deliver(Tick(started: false, from: 2), engine, engine.ProcessSample);

        Assert.Equal(ProcessClassifier.Unknown, snaps[^1].ProcessClass);
    }
}
