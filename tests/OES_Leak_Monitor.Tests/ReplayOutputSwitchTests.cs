using System;
using System.IO;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Starting a replay must never leave the replay's ratio trend in a production-named file (#142).
///
/// <para>What this guards: <c>BeginReplayOutput</c> closed the recorders and only then switched
/// them to the <c>SIM</c> prefix. The acquisition thread keeps delivering test-mode frames all the
/// while, so one landing between the two reopened the ratio CSV under the ordinary prefix — and a
/// writer keeps the prefix its session opened with. A dev-machine replay on 2026-11-27 wrote the
/// whole 11/25 recording to <c>P_Ratio_1127111221.csv</c>; its log had <c>RatioCsvOpened</c>
/// one millisecond ahead of <c>ReplayStarted</c>.</para>
///
/// <para>The tests drive <see cref="ReplayOutputSwitch.Begin"/> with a real engine and a real
/// <see cref="RatioCsvLogger"/>, delivering a frame at the moment the race needs.</para>
/// </summary>
public sealed class ReplayOutputSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oes-replayswitch-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private static readonly DateTime Epoch = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

    private static SpectrumSample Frame(double seconds)
    {
        const int n = 1000;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            inten[i] = 1000f + (i % 2 == 0 ? 1f : -1f);
            if (Math.Abs(wl[i] - 350.0) <= 0.5) inten[i] += 1000f;
            if (Math.Abs(wl[i] - 500.0) <= 0.5) inten[i] += 2000f;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds), Wavelengths = wl, Intensities = inten,
            IntegrationTime = 0, AverageCount = 0, SerialNumber = "T", IsSimulated = true,
        };
    }

    private static LeakMonitorEngine Engine()
    {
        var s = new LeakMonitorSettings { Enabled = true, SuppressAlarmsInTestMode = false };
        s.Ratios.Add(new RatioDefinition
        {
            Key = "R_A", DisplayName = "A", MonitorMode = MonitorMode.Ratio, MinSnr = 0,
            Numerator = new LineRegion { Label = "X 350", CenterNm = 350, HalfWidthNm = 0.5, BaselineGapNm = 1, BaselineWidthNm = 1, Mode = LineExtractMode.PeakHeight },
            Denominator = new LineRegion { Label = "Y 500", CenterNm = 500, HalfWidthNm = 0.5, BaselineGapNm = 1, BaselineWidthNm = 1, Mode = LineExtractMode.PeakHeight },
        });
        var engine = new LeakMonitorEngine(s);
        engine.ConfigureTrigger(new LoggerSettings
        {
            TriggerMode = TriggerMode.SpectrumPercentile, TriggerPercentile = 99, SaveStartThresholdIntensity = 100,
        });
        return engine;
    }

    private LoggerSettings Output(string prefix) => new() { BaseDirectory = _root, FilePrefix = prefix };

    [Fact]
    public void A_frame_arriving_right_after_the_recorders_close_opens_a_replay_marked_file()
    {
        using var engine = Engine();
        using var logger = new RatioCsvLogger(engine, _root);
        logger.Configure(Output("P"));
        engine.ProcessSample(Frame(0));                          // live session before the replay
        Assert.StartsWith("P_Ratio_", Path.GetFileName(logger.CurrentFile));

        ReplayOutputSwitch.Begin(
            markOutput: () => logger.Configure(Output("SIMP")),
            closeRecorders: () =>
            {
                logger.Stop();
                engine.ProcessSample(Frame(1));                  // the acquisition thread's next frame
            });

        Assert.StartsWith("SIMP_Ratio_", Path.GetFileName(logger.CurrentFile));
    }

    [Fact]
    public void A_frame_arriving_while_the_output_is_being_marked_ends_up_in_a_closed_session()
    {
        using var engine = Engine();
        using var logger = new RatioCsvLogger(engine, _root);
        logger.Configure(Output("P"));
        engine.ProcessSample(Frame(0));

        ReplayOutputSwitch.Begin(
            markOutput: () =>
            {
                logger.Configure(Output("SIMP"));
                engine.ProcessSample(Frame(1));                  // writes to the live session, still open
            },
            closeRecorders: logger.Stop);

        Assert.Equal("", logger.CurrentFile);                    // nothing left open under "P"
        engine.ProcessSample(Frame(2));
        Assert.StartsWith("SIMP_Ratio_", Path.GetFileName(logger.CurrentFile));
    }
}
