using System.IO;
using Aqusen.Secs;
using OES_Leak_Monitor;
using Secs4Net;
using Xunit;
using Xunit.Abstractions;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// The leak monitor against a real plasma run, end to end: recorded spectra in, SECS alarms out.
///
/// <para>The recording is <c>P_OES1_0814220358.csv</c> — ten minutes of a chamber on
/// 2026-08-14, during which the tool went Normal → Warning → Alarm. Its ratio CSV
/// (<c>P_Ratio_0814220355.csv</c>) is the contemporaneous record of that:</para>
/// <code>
///   2026-08-14 22:10:52.983  Normal  -> Warning
///   2026-08-14 22:11:21.788  Warning -> Alarm
/// </code>
/// <para>These tests replay the same spectra through the <b>factory</b> ratio set with a
/// baseline captured from the head of the recording — deliberately not the configuration that
/// produced those transitions, which has since been retuned and cannot be recovered. Landing on
/// the same transitions anyway is the point: the leak in that data is well clear of the tuning.</para>
///
/// <para>See <see cref="RecordedRun"/> for why the recording is not committed and how to point
/// these tests at another one.</para>
/// </summary>
public class RecordedRunTests
{
    // What the tool recorded live on 2026-08-14, as offsets into the recording (which starts
    // at 22:03:58.952). Used as a tolerance target, not an exact expectation: a different ratio
    // set is being run against the same spectra.
    private static readonly TimeSpan RecordedWarning = TimeSpan.FromSeconds(414.0);   // 22:10:52.98
    private static readonly TimeSpan RecordedAlarm = TimeSpan.FromSeconds(442.8);     // 22:11:21.79

    /// <summary>How far a transition may drift from the recorded one before it is worth knowing.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(90);

    private static readonly DateTime Epoch = new(2026, 8, 14, 22, 3, 58, DateTimeKind.Local);

    private readonly ITestOutputHelper _out;

    public RecordedRunTests(ITestOutputHelper output) => _out = output;

    [SkippableFact]
    public void A_recorded_leak_drives_the_engine_from_normal_through_warning_to_alarm()
    {
        var replay = TryStart();
        _out.WriteLine(replay.Description);
        Assert.Equal(1904, replay.Recording.Wavelengths.Length);   // the spectrometer's own axis
        Assert.True(replay.Recording.FrameCount > 100, $"only {replay.Recording.FrameCount} frames");

        // A baseline captured from the head of the recording — what the manual tells an
        // engineer to do when validating against replayed data, since a live Golden Run was
        // taken under a different exposure.
        Assert.True(replay.CaptureAccepted, "the Golden Run capture was discarded: " + replay.CaptureReport);
        Assert.Equal(3, replay.BaselineCount);   // the fourth factory ratio ships disabled

        var levels = replay.Transitions.Select(t => t.Level).ToList();
        _out.WriteLine("transitions: " + string.Join(" -> ", replay.Transitions.Select(
            t => $"+{t.At.TotalSeconds:F0}s {t.Level}")));

        Assert.Contains(LeakAlarmLevel.Warning, levels);
        Assert.Contains(LeakAlarmLevel.Alarm, levels);

        // Order matters: an alarm that arrives without the warning ahead of it means the
        // two-level state machine was bypassed, not that the leak was sudden.
        var warning = replay.FirstAt(LeakAlarmLevel.Warning);
        var alarm = replay.FirstAt(LeakAlarmLevel.Alarm);
        Assert.True(warning < alarm, $"warning at +{warning.TotalSeconds:F0}s, alarm at +{alarm.TotalSeconds:F0}s");

        // And they land where the tool put them on the day.
        AssertNear(RecordedWarning, warning, "warning");
        AssertNear(RecordedAlarm, alarm, "alarm");
    }

    [SkippableFact]
    public void The_plasma_gate_stays_open_for_a_real_discharge()
    {
        var replay = TryStart();
        var final = replay.Final!;
        Assert.True(final.PlasmaGateAvailable, "the gate could not be evaluated on real spectra");
        Assert.True(final.PlasmaPresent, "the gate judged a lit plasma to be off");

        // Dropouts are single blank frames between good ones. This run had none; a change that
        // starts inventing them on clean data is worth hearing about.
        Assert.Equal(0, final.DropoutCount);
        _out.WriteLine($"plasma={final.PlasmaPresent} gate={final.PlasmaGateAvailable} " +
                       $"dropouts={final.DropoutCount}");
    }

    [SkippableFact]
    public async Task A_recorded_leak_reaches_a_SECS_host_as_S5F1()
    {
        // The whole path: recorded spectra → engine → bridge → socket → host, with the ids and
        // encoding a fab MES would see.
        var path = RecordedRun.ResolvePath();
        Skip.If(path is null, RecordedRun.SkipReason);

        var folder = Path.Combine(Path.GetTempPath(), "oes-secs-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var port = SecsTestPort.Free();

        using var bridge = new SecsBridge(null, folder, folder,
            () => new SecsBridge.AcquisitionInfo(0f, 0u));
        bridge.Configure(new SecsSettings
        {
            Enabled = true,
            ChamberCode = 2,
            IpAddress = "127.0.0.1",
            Port = port,
            // The frames are replayed, which is exactly the case this switch exists for.
            ReportInTestMode = true,
        });

        await using var host = new GemHost(
            new SecsGemOptions { IsActive = true, IpAddress = "127.0.0.1", Port = port },
            new GemOptions { ModelName = "TEST-HOST", SoftwareRevision = "1.0", EstablishRetryIntervalMs = 1000 });
        host.Start();
        host.Enable();
        Assert.True(await SecsTestPort.WaitAsync(
            () => host.CommunicationState == CommunicationState.Communicating, 20),
            "never reached Communicating");

        var alarms = new List<(string Alid, bool Set)>();
        host.AlarmReceived += m =>
        {
            lock (alarms) { alarms.Add((m.SecsItem![1].GetString(), (m.SecsItem[0].FirstValue<byte>() & 0x80) != 0)); }
        };

        var replay = Replay.Run(path, snapshot => bridge.OnSample(snapshot),
            level => bridge.OnLeakLevelChanged(level));
        _out.WriteLine(replay.Description);
        _out.WriteLine("transitions: " + string.Join(" -> ", replay.Transitions.Select(
            t => $"+{t.At.TotalSeconds:F0}s {t.Level}")));

        Assert.True(await SecsTestPort.WaitAsync(() => Count() >= 3, 15),
            $"expected at least three alarm messages, got {Count()}");
        await Task.Delay(300);

        List<(string Alid, bool Set)> received;
        lock (alarms) { received = alarms.ToList(); }
        _out.WriteLine("S5F1: " + string.Join(", ", received.Select(a => $"{a.Alid}{(a.Set ? "+" : "-")}")));

        // ALID 1 02 27 001 / 002 — chamber 2, ss=27, leak warning / leak alarm.
        Assert.Equal(("10227001", true), received[0]);
        Assert.Contains(("10227002", true), received);
        // The warning clears as it escalates, so the host is never left holding both.
        Assert.Contains(("10227001", false), received);

        // VID 007 tells a host that polls the same thing the alarms told one that listened.
        Assert.Equal(LeakAlarmLevel.Alarm, replay.Final!.Overall);

        try { Directory.Delete(folder, recursive: true); } catch { /* best effort */ }

        int Count() { lock (alarms) { return alarms.Count; } }
    }

    // ---- helpers -----------------------------------------------------------

    private Replay TryStart()
    {
        var path = RecordedRun.ResolvePath();
        Skip.If(path is null, RecordedRun.SkipReason);
        return Replay.Run(path!);
    }

    private void AssertNear(TimeSpan recorded, TimeSpan replayed, string what)
    {
        var drift = (replayed - recorded).Duration();
        _out.WriteLine($"{what}: recorded +{recorded.TotalSeconds:F0}s, replayed +{replayed.TotalSeconds:F0}s " +
                       $"(drift {drift.TotalSeconds:F0}s)");
        Assert.True(drift <= Tolerance,
            $"the {what} transition moved {drift.TotalSeconds:F0}s from where this recording " +
            $"produced it on 2026-08-14 (+{recorded.TotalSeconds:F0}s). Either detection changed, " +
            "or the factory ratio set was retuned — both are worth a look before this tolerance is widened.");
    }

    /// <summary>One replay of a recording through a fresh engine, with everything observed.</summary>
    private sealed class Replay
    {
        public required string Description { get; init; }
        public required RecordedRun.Loaded Recording { get; init; }
        public required IReadOnlyList<(TimeSpan At, LeakAlarmLevel Level)> Transitions { get; init; }
        public required LeakMonitorSnapshot? Final { get; init; }
        public required bool CaptureAccepted { get; init; }
        public required int BaselineCount { get; init; }
        public required string CaptureReport { get; init; }

        public TimeSpan FirstAt(LeakAlarmLevel level) =>
            Transitions.First(t => t.Level == level).At;

        /// <summary>
        /// Replays <paramref name="path"/> through a fresh engine on the factory ratio set,
        /// capturing a Golden Run from the first minute and reporting everything that followed.
        /// The optional hooks let a caller wire the SECS bridge to the same run.
        /// </summary>
        public static Replay Run(
            string path,
            Action<LeakMonitorSnapshot>? onSample = null,
            Action<LeakAlarmLevel>? onLevel = null)
        {
            var run = RecordedRun.Load(path);

            var settings = LeakMonitorSettings.CreateDefault();
            // The frames are replayed, and checking the alarm transitions is the exercise.
            settings.SuppressAlarmsInTestMode = false;

            using var engine = new LeakMonitorEngine(settings);
            // The plasma gate mirrors the intensity logger's save trigger, so it has to be told
            // what that is. Percentile mode tracks the discharge rather than one gas.
            engine.ConfigureTrigger(new LoggerSettings
            {
                TriggerMode = TriggerMode.SpectrumPercentile,
                TriggerPercentile = 99,
                SaveStartThresholdIntensity = 2000,
            });

            var transitions = new List<(TimeSpan, LeakAlarmLevel)>();
            engine.AlarmStateChanged += (_, e) =>
            {
                transitions.Add((e.Timestamp - Epoch, e.NewLevel));
                onLevel?.Invoke(e.NewLevel);
            };

            LeakMonitorSnapshot? last = null;
            engine.SampleProcessed += (_, s) => { last = s; onSample?.Invoke(s); };

            bool accepted = false;
            int baselines = 0;
            string report = "(capture never finished)";
            engine.GoldenRunCaptureFinished += (_, r) =>
            {
                accepted = r.Accepted;
                baselines = r.Run.Baselines.Count;
                report = $"accepted={r.Accepted}, {r.Run.Baselines.Count} baseline(s)" +
                         (r.Rejected.Count == 0
                             ? ""
                             : "; no baseline for " + string.Join("; ",
                                 r.Rejected.Select(x => $"{x.DisplayName} ({x.Reason})")));
            };

            engine.BeginGoldenRunCapture("ReplayBaseline", seconds: 60);
            for (var i = 0; i < run.FrameCount; i++)
            {
                engine.ProcessSample(run.Frame(i, Epoch));
            }

            return new Replay
            {
                Description =
                    $"{Path.GetFileName(path)}: {run.FrameCount} frames, {run.Wavelengths.Length} wavelengths " +
                    $"({run.Wavelengths[0]:F1}–{run.Wavelengths[^1]:F1} nm), {run.DurationSeconds:F0} s; " +
                    $"baseline {report}",
                Recording = run,
                Transitions = transitions,
                Final = last,
                CaptureAccepted = accepted,
                BaselineCount = baselines,
                CaptureReport = report,
            };
        }
    }
}
