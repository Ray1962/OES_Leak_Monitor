using System;
using System.Collections.Generic;
using System.Linq;
using Aqst.OesSpectrometer.Models;
using OES_Leak_Monitor;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>
/// Process windows driven by the main tool's own events (S2F41 <c>TOOL_EVENT</c>), at the
/// engine seam.
///
/// <para>What this guards: PM3 interleaves Chamber Process (process A), Chamber Clean (B) and a
/// process nobody has placed yet, against one leak monitor. The spectrum can only infer which is
/// running; the tool knows, and the EAP forwards its ProcessStart / ProcessEnd CEIDs. A ratio
/// scoped to <c>A</c> must judge inside an A window and nowhere else — and an installation that
/// never configures any of this must not notice it exists.</para>
/// </summary>
public class ToolEventWindowTests
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);

    // PM3's rows of docs/EAP ceid-events.md.
    private const uint ProcessStart = 3100439529, ProcessEnd = 3100439530;
    private const uint CleanStart = 3099591660, CleanEnd = 3099591661;
    private const uint LeakCheckStart = 3100167145;

    private const double O777 = 777.4, Sig337 = 337.1;

    /// <summary>A flat continuum with the two lines the tests read placed on it.</summary>
    private static SpectrumSample Frame(double seconds, double o777, double sig337)
    {
        const int n = 1100;
        var wl = new float[n];
        var inten = new float[n];
        for (int i = 0; i < n; i++)
        {
            wl[i] = (float)(300.0 + i * 0.5);
            inten[i] = (float)(10 + (i % 2 == 0 ? 1 : -1));
            // Plateaus wider than the extraction window, so a RawMean reads the level exactly.
            if (Math.Abs(wl[i] - O777) <= 1.5) inten[i] = (float)o777;
            if (Math.Abs(wl[i] - Sig337) <= 1.5) inten[i] = (float)sig337;
        }
        return new SpectrumSample
        {
            Timestamp = Epoch.AddSeconds(seconds),
            Wavelengths = wl,
            Intensities = inten,
            IntegrationTime = 0,
            AverageCount = 0,
            SerialNumber = "TEST",
            IsSimulated = true,
        };
    }

    /// <summary>Plasma on: the trigger line (777 nm) at 800 against a save threshold of 500.</summary>
    private static SpectrumSample On(double t, double signal = 500) => Frame(t, o777: 800, sig337: signal);

    /// <summary>Plasma off: the trigger line at the continuum.</summary>
    private static SpectrumSample Off(double t) => Frame(t, o777: 10, sig337: 10);

    private static LineRegion Raw(string label, double nm) => new()
    {
        Label = label, CenterNm = nm, HalfWidthNm = 0.5,
        BaselineGapNm = 1.0, BaselineWidthNm = 1.0,
        Mode = LineExtractMode.RawMean,
    };

    private static RatioDefinition Ratio(string key, string processClass) => new()
    {
        Key = key,
        DisplayName = key,
        Enabled = true,
        ProcessClass = processClass,
        MonitorMode = MonitorMode.AbsoluteIntensity,
        MinSnr = 0,
        SigmaWarn = 3, SigmaAlarm = 6,
        EmaTauSeconds = 0.1, ConfirmSeconds = 1.0,
        Numerator = Raw("Sig 337", Sig337),
        Denominator = Raw("O 777", O777),
    };

    /// <summary>PM3's process and clean windows, plus a record-only row, as a site would map them.</summary>
    private static ToolEventSettings Pm3(double thresholdA = 0) => new()
    {
        Enabled = true,
        Mappings =
        {
            new ToolEventMapping { Ceid = ProcessStart, ClassName = "A", Edge = ToolEventEdge.Start,
                                   Name = "PM3 ProcessStart", PlasmaThreshold = thresholdA },
            new ToolEventMapping { Ceid = ProcessEnd, ClassName = "A", Edge = ToolEventEdge.End,
                                   Name = "PM3 ProcessEnd" },
            new ToolEventMapping { Ceid = CleanStart, ClassName = "B", Edge = ToolEventEdge.Start,
                                   Name = "PM3 Clean ProcessStart" },
            new ToolEventMapping { Ceid = CleanEnd, ClassName = "B", Edge = ToolEventEdge.End,
                                   Name = "PM3 Clean ProcessEnd" },
            new ToolEventMapping { Ceid = LeakCheckStart, ClassName = "", Edge = ToolEventEdge.Start,
                                   Name = "PM3 Auto Leak check" },
        },
    };

    private sealed class Harness : IDisposable
    {
        public LeakMonitorEngine Engine { get; }
        public List<LeakMonitorSnapshot> Snaps { get; } = new();

        public Harness(ProcessClassifierSettings? classifier, params RatioDefinition[] ratios)
            : this(log: null, classifier, ratios) { }

        public Harness(SystemLogger? log, ProcessClassifierSettings? classifier, params RatioDefinition[] ratios)
        {
            var settings = new LeakMonitorSettings
            {
                Enabled = true,
                SuppressAlarmsInTestMode = false,
                RequireTwoForAlarm = false,
            };
            foreach (var r in ratios) settings.Ratios.Add(r);
            if (classifier is not null) settings.ProcessClassifier = classifier;

            Engine = new LeakMonitorEngine(settings, log);
            Engine.ConfigureTrigger(new LoggerSettings
            {
                TriggerMode = TriggerMode.Wavelength,
                TriggerWavelength = (float)O777,
                WavelengthToleranceNm = 1f,
                SaveStartThresholdIntensity = 500,
            });
            Engine.SampleProcessed += (_, s) => Snaps.Add(s);
        }

        public LeakMonitorSnapshot Last => Snaps[^1];
        public RatioSnapshot Ratio(string key) => Last.Ratios.First(r => r.Key == key);

        /// <summary>Forwards a tool event the way the SECS bridge does.</summary>
        public ToolEventAck Event(uint ceid, double t) => Engine.ReceiveToolEvent(ceid, Epoch.AddSeconds(t));

        public void Dispose() => Engine.Dispose();
    }

    /// <summary>
    /// A real <see cref="SystemLogger"/> over a temporary folder, so a test can require that a
    /// warning was written — the warnings are the acceptance criterion for the event rules, since
    /// an EAP that drops an End is diagnosed from the log and nowhere else.
    /// </summary>
    private sealed class LogCapture : IDisposable
    {
        private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "oeslm-toolevent-" + Guid.NewGuid().ToString("N"));
        public SystemLogger Logger { get; }

        public LogCapture()
        {
            System.IO.Directory.CreateDirectory(_dir);
            Logger = new SystemLogger(_dir);
        }

        /// <summary>Every line written so far. Flushes by disposing, so call it once, last.</summary>
        public string[] Lines()
        {
            Logger.Dispose();
            return System.IO.Directory.GetFiles(_dir, "*.csv")
                .SelectMany(System.IO.File.ReadAllLines).ToArray();
        }

        public void Dispose()
        {
            Logger.Dispose();
            try { System.IO.Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------- off by default

    /// <summary>
    /// Never configured, the engine reports tool events as not in use, refuses them, and judges
    /// exactly as it always has: a class-scoped ratio applies to every step because nothing names
    /// one. This is what makes the feature safe to ship to an installation that has no EAP.
    /// </summary>
    [Fact]
    public void NotConfigured_ChangesNothing()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));

        Assert.Equal(ToolEventAck.Disabled, h.Event(ProcessStart, 0));
        for (double t = 0; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));

        Assert.Equal(ToolEventContext.Disabled, h.Last.ToolEventState);
        Assert.Equal("", h.Last.ToolWindowClass);
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_A").State);
    }

    // ---------------------------------------------------------------- the window

    /// <summary>
    /// Inside a Chamber Process window an A-scoped ratio judges; once the window closes it reads
    /// NotApplicable, while a ratio with no class keeps judging throughout — it made no claim
    /// about which process it measures.
    /// </summary>
    [Fact]
    public void AWindow_ScopesAClassRatio_AndAnUnscopedOneIsUnaffected()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"), Ratio("R_any", ""));
        h.Engine.ConfigureToolEvents(Pm3());

        double t = 0;
        Assert.Equal(ToolEventAck.Accepted, h.Event(ProcessStart, t));
        for (; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);
        Assert.Equal("A", h.Last.ToolWindowClass);
        Assert.Equal("A", h.Last.ProcessClass);
        Assert.Equal(RatioState.NoBaseline, h.Ratio("R_A").State);     // judged; no baseline yet
        Assert.Equal(RatioState.NoBaseline, h.Ratio("R_any").State);

        Assert.Equal(ToolEventAck.Accepted, h.Event(ProcessEnd, t));
        for (; t <= 4.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.OutsideWindow, h.Last.ToolEventState);
        Assert.Equal("", h.Last.ToolWindowClass);
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_A").State);
        Assert.Equal(RatioState.NoBaseline, h.Ratio("R_any").State);
    }

    /// <summary>
    /// The window decides from the next frame on, not after a step has been watched for a few
    /// frames: the tool's event marks the boundary exactly, so there is no ignition transient to
    /// wait out the way the spectral classifier has to.
    /// </summary>
    [Fact]
    public void TheWindowApplies_OnTheVeryNextFrame()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(CleanStart, 0);
        h.Engine.ProcessSample(On(0));
        Assert.Equal("B", h.Last.ToolWindowClass);
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_A").State);

        // The tool moves straight from one window to the next while the plasma stays lit.
        h.Event(CleanEnd, 0.5);
        h.Event(ProcessStart, 0.5);
        h.Engine.ProcessSample(On(0.5));
        Assert.Equal("A", h.Last.ToolWindowClass);
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_A").State);
    }

    /// <summary>
    /// A window says which process the tool is running, not that there is plasma: the seconds
    /// before ignition and after the plasma goes out are inside the window and still gated.
    /// </summary>
    [Fact]
    public void ThePlasmaGate_StillApplies_InsideAWindow()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(ProcessStart, 0);
        for (double t = 0; t <= 2.0; t += 0.5) h.Engine.ProcessSample(Off(t));

        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);
        Assert.False(h.Last.PlasmaPresent);
        Assert.Equal(RatioState.NoPlasma, h.Ratio("R_A").State);
    }

    /// <summary>
    /// A latched alarm survives its window closing, exactly as it survives leaving a class the
    /// classifier named: a confirmed leak does not end because the tool moved on.
    /// </summary>
    [Fact]
    public void ALatchedAlarm_SurvivesTheWindowClosing()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        double t = 0;
        h.Event(ProcessStart, t);
        h.Engine.BeginGoldenRunCapture("base", seconds: 2);
        for (; t <= 3.0; t += 0.5) h.Engine.ProcessSample(On(t, signal: 500));
        for (; t <= 12.0; t += 0.5) h.Engine.ProcessSample(On(t, signal: 2000));
        Assert.Equal(LeakAlarmLevel.Alarm, h.Last.Overall);

        h.Event(ProcessEnd, t);
        for (; t <= 14.0; t += 0.5) h.Engine.ProcessSample(On(t, signal: 500));
        Assert.Equal(ToolEventContext.OutsideWindow, h.Last.ToolEventState);
        Assert.Equal(RatioState.Alarm, h.Ratio("R_A").State);
        Assert.Equal(LeakAlarmLevel.Alarm, h.Last.Overall);
    }

    // ---------------------------------------------------------------- unknown context

    /// <summary>
    /// Switched on but before any window edge has arrived, the engine cannot say which process
    /// is running, and judges as it did without tool events rather than judging nothing — losing
    /// a real leak to an EAP that is late with its first event costs more than a false alarm,
    /// and the state says "unknown" so nobody mistakes it for a scoped judgement.
    /// </summary>
    [Fact]
    public void BeforeTheFirstEvent_TheEngineFallsBackToHowItJudgedWithoutThem()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        for (double t = 0; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));

        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_A").State);   // no classifier: unscoped
    }

    /// <summary>
    /// The fallback is the classifier when one is configured — and once a window is known, the
    /// tool's word replaces the spectrum's guess, so a ratio the classifier would have stood down
    /// judges inside the window the tool says is its own.
    /// </summary>
    [Fact]
    public void WithAClassifier_TheFallbackIsTheClassifier_AndAWindowOverridesIt()
    {
        // A one-rule classifier that names every lit step A.
        var classifier = new ProcessClassifierSettings
        {
            Enabled = true,
            DecideAfterFrames = 1,
            Rules =
            {
                new ProcessClassRule
                {
                    ClassName = "A", DisplayName = "O777/O777",
                    Numerator = Raw("O 777", O777), Denominator = Raw("O 777", O777),
                    Op = ComparisonOp.GreaterThan, Threshold = 0.5,
                },
            },
        };
        using var h = new Harness(classifier, Ratio("R_B", "B"));
        h.Engine.ConfigureToolEvents(Pm3());

        double t = 0;
        for (; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);
        Assert.Equal("A", h.Last.ProcessClass);                          // the classifier's verdict
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_B").State);

        h.Event(CleanStart, t);
        for (; t <= 4.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal("B", h.Last.ProcessClass);                          // the tool's word
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_B").State);
    }

    // ---------------------------------------------------------------- per-class plasma threshold

    /// <summary>
    /// Each class can run at its own plasma level. The mapping's threshold applies inside its
    /// window even with the spectral classifier switched off — the classifier used to be the only
    /// place a per-class level could come from, which would have left a window-supplied class
    /// gated at the base threshold with nothing to say so.
    /// </summary>
    [Fact]
    public void TheMappingsPlasmaThreshold_AppliesWithTheClassifierOff()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3(thresholdA: 900));

        h.Event(ProcessStart, 0);
        for (double t = 0; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));   // 800 < 900

        Assert.False(h.Last.PlasmaPresent);
        Assert.Equal(RatioState.NoPlasma, h.Ratio("R_A").State);
    }

    /// <summary>
    /// With no threshold on the mapping, the class's level in the classifier settings applies —
    /// whether or not the classifier is switched on, because that is where a site already wrote
    /// down what each process runs at.
    /// </summary>
    [Fact]
    public void WithoutOne_TheClassifierSettingsThresholdApplies_EvenWithTheClassifierOff()
    {
        var off = new ProcessClassifierSettings
        {
            Enabled = false,
            Classes = { new ProcessClassDefinition { Name = "A", PlasmaThreshold = 900 } },
        };
        using var h = new Harness(off, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3(thresholdA: 0));

        h.Event(ProcessStart, 0);
        for (double t = 0; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));

        Assert.False(h.Last.PlasmaPresent);
    }

    // ---------------------------------------------------------------- Golden Run capture

    /// <summary>
    /// A live capture that runs across a window's end learns an A-scoped ratio's baseline only
    /// from the A-window frames. The plasma after the window is lit and reads a different level;
    /// pooling it would put the mean between two processes, a value neither ever reads, with a σ
    /// wide enough to hide a leak.
    /// </summary>
    [Fact]
    public void ACapture_LearnsAnAScopedRatio_OnlyFromAWindowFrames()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        double t = 0;
        h.Event(ProcessStart, t);
        h.Engine.BeginGoldenRunCapture("window", seconds: 4);
        for (; t < 2.0; t += 0.5) h.Engine.ProcessSample(On(t, signal: 500));
        h.Event(ProcessEnd, t);
        for (; t <= 4.5; t += 0.5) h.Engine.ProcessSample(On(t, signal: 900));

        var run = h.Engine.Settings.FindGoldenRun("window");
        Assert.NotNull(run);
        var baseline = run!.Find("R_A");
        Assert.NotNull(baseline);
        Assert.Equal(500.0, baseline!.Mean, 6);
    }

    // ---------------------------------------------------------------- adoption

    /// <summary>
    /// What the host is answered with: an event the table knows is taken, one it does not is
    /// acknowledged and ignored (so the EAP can forward unfiltered), and a recorded-only row is
    /// taken without opening anything.
    /// </summary>
    [Fact]
    public void EventsAreTaken_IgnoredOrRecorded_ByTheTable()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        Assert.Equal(ToolEventAck.Ignored, h.Event(2697019492, 0));     // slit door, not mapped
        Assert.Equal(ToolEventAck.Accepted, h.Event(LeakCheckStart, 0)); // recorded only
        h.Engine.ProcessSample(On(0));

        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);    // no window opened
        Assert.Equal(LeakCheckStart, h.Last.LastToolCeid);
    }

    /// <summary>
    /// Adoption is where the engine takes a configuration, and it is staged: the host applies it
    /// on an acquisition restart. Re-applying the same table — every restart does — keeps the
    /// window it is in; a changed table starts from "unknown", because the window it remembers
    /// was named by a table that no longer exists. Switching off returns to "not in use".
    /// </summary>
    [Fact]
    public void ReapplyingTheSameTable_KeepsTheWindow_AndAChangedOneForgetsIt()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(ProcessStart, 0);
        h.Engine.ProcessSample(On(0));
        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);

        h.Engine.ConfigureToolEvents(Pm3());
        h.Engine.ProcessSample(On(0.5));
        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);

        h.Engine.ConfigureToolEvents(Pm3(thresholdA: 100));
        h.Engine.ProcessSample(On(1.0));
        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);

        h.Engine.ConfigureToolEvents(new ToolEventSettings { Enabled = false, Mappings = Pm3().Mappings });
        h.Engine.ProcessSample(On(1.5));
        Assert.Equal(ToolEventContext.Disabled, h.Last.ToolEventState);
        Assert.Equal(ToolEventAck.Disabled, h.Event(ProcessStart, 2.0));
    }

    // ---------------------------------------------------------------- status between frames

    /// <summary>
    /// The status the SECS tab and VIDs 030–032 report is current before any frame arrives. It
    /// used to be read off the last frame, so an applied table read "not in use" — and VID 030
    /// read 0 — until acquisition started; the 2026-10-23 smoke test caught exactly that. A host
    /// forwarding events while the spectrometer is stopped must still see them arrive (032), and
    /// see the window move as soon as a frame applies it.
    /// </summary>
    [Fact]
    public void TheStatus_IsCurrent_BetweenFrames()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        var seen = new List<ToolEventStatus>();
        h.Engine.ToolStatusChanged += seen.Add;

        Assert.Equal(ToolEventContext.Disabled, h.Engine.ToolStatus.State);

        h.Engine.ConfigureToolEvents(Pm3());
        Assert.Equal(ToolEventContext.Unknown, h.Engine.ToolStatus.State);
        Assert.Equal(ToolEventContext.Unknown, seen[^1].State);

        h.Event(ProcessStart, 0);
        Assert.Equal(ProcessStart, h.Engine.ToolStatus.LastCeid);        // arrived, no frame yet
        Assert.Equal(ToolEventContext.Unknown, h.Engine.ToolStatus.State); // applied by the next frame

        h.Engine.ProcessSample(On(0));
        Assert.Equal(ToolEventContext.InWindow, h.Engine.ToolStatus.State);
        Assert.Equal("A", h.Engine.ToolStatus.WindowClass);
        Assert.Equal(seen[^1], h.Engine.ToolStatus);
    }


    // ---------------------------------------------------------------- events that do not pair up (#29)

    /// <summary>
    /// A Start while a window is already open means the End was lost. The tool is plainly in the
    /// new process now, so the old window closes and the new one opens — and the log says the
    /// End never came, because an EAP that drops events is diagnosed from that line or not at all.
    /// </summary>
    [Fact]
    public void ASecondStart_ReplacesTheOpenWindow_AndSaysTheEndWasLost()
    {
        using var log = new LogCapture();
        using var h = new Harness(log.Logger, classifier: null, Ratio("R_A", "A"), Ratio("R_B", "B"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(ProcessStart, 0);
        h.Engine.ProcessSample(On(0));
        h.Event(CleanStart, 0.5);                       // no ProcessEnd in between
        h.Engine.ProcessSample(On(0.5));

        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);
        Assert.Equal("B", h.Last.ToolWindowClass);
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_A").State);
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_B").State);

        h.Engine.Dispose();
        Assert.Contains(log.Lines(), l => l.Contains("ToolEventWindowUnclosed") && l.Contains("warn"));
    }

    /// <summary>
    /// An End that matches no open window — the tool is outside any, or in a different one — is
    /// a stale or misrouted event. Acting on it could only close a window the tool is still in,
    /// so it changes nothing, and it is logged.
    /// </summary>
    [Fact]
    public void AnEndForNoOpenWindow_ChangesNothing_AndIsLogged()
    {
        using var log = new LogCapture();
        using var h = new Harness(log.Logger, classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(ProcessEnd, 0);                         // nothing is open: still Unknown
        h.Engine.ProcessSample(On(0));
        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);

        h.Event(ProcessStart, 0.5);
        h.Engine.ProcessSample(On(0.5));
        h.Event(CleanEnd, 1.0);                         // the open window is A, not B
        h.Engine.ProcessSample(On(1.0));
        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);
        Assert.Equal("A", h.Last.ToolWindowClass);

        h.Engine.Dispose();
        Assert.Equal(2, log.Lines().Count(l => l.Contains("ToolEventOrphanEnd") && l.Contains("warn")));
    }

    /// <summary>
    /// A window open longer than <c>MaxWindowSeconds</c> lost its End somewhere. Holding it open
    /// for ever would judge every later process as this one, so it closes — to Unknown, not to
    /// "outside", since nobody said the process ended: the engine falls back to how it judged
    /// without tool events, VID 030 reads 1, and the log says why. Measured on the frame clock,
    /// like every other duration the engine keeps.
    /// </summary>
    [Fact]
    public void AWindowThatOutlivesTheLimit_FallsBackToUnknown_AndIsLogged()
    {
        using var log = new LogCapture();
        using var h = new Harness(log.Logger, classifier: null, Ratio("R_A", "A"), Ratio("R_B", "B"));
        h.Engine.ConfigureToolEvents(Pm3());
        h.Engine.ConfigureToolEventsLive(new ToolEventSettings { MaxWindowSeconds = 10 });

        h.Event(CleanStart, 0);
        double t = 0;
        for (; t <= 9.5; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_A").State);

        for (; t <= 11.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);
        Assert.Equal(ToolEventContext.Unknown, h.Engine.ToolStatus.State);
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_A").State);   // no classifier: unscoped

        h.Engine.Dispose();
        Assert.Contains(log.Lines(), l => l.Contains("ToolEventWindowTimedOut") && l.Contains("warn"));
    }

    /// <summary>
    /// The limit is hot-applied: shortening it takes effect on the next frame, without waiting
    /// for an acquisition restart the way the table does — it names no class, so there is
    /// nothing for it to disagree with.
    /// </summary>
    [Fact]
    public void TheLimit_IsHotApplied()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(ProcessStart, 0);
        double t = 0;
        for (; t <= 20.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.InWindow, h.Last.ToolEventState);   // default limit 600 s

        h.Engine.ConfigureToolEventsLive(new ToolEventSettings { MaxWindowSeconds = 10 });
        h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);
    }

    /// <summary>
    /// After a timeout the fallback is the classifier when one is configured, exactly as before
    /// the first event — one meaning for Unknown, whichever way the engine came to it.
    /// </summary>
    [Fact]
    public void AfterATimeout_WithAClassifier_TheClassifierDecidesAgain()
    {
        var classifier = new ProcessClassifierSettings
        {
            Enabled = true,
            DecideAfterFrames = 1,
            Rules =
            {
                new ProcessClassRule
                {
                    ClassName = "A", DisplayName = "O777/O777",
                    Numerator = Raw("O 777", O777), Denominator = Raw("O 777", O777),
                    Op = ComparisonOp.GreaterThan, Threshold = 0.5,
                },
            },
        };
        using var h = new Harness(classifier, Ratio("R_A", "A"), Ratio("R_B", "B"));
        h.Engine.ConfigureToolEvents(Pm3());
        h.Engine.ConfigureToolEventsLive(new ToolEventSettings { MaxWindowSeconds = 5 });

        h.Event(CleanStart, 0);
        double t = 0;
        for (; t <= 2.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal("B", h.Last.ProcessClass);                          // the tool's word

        for (; t <= 7.0; t += 0.5) h.Engine.ProcessSample(On(t));
        Assert.Equal(ToolEventContext.Unknown, h.Last.ToolEventState);
        Assert.Equal("A", h.Last.ProcessClass);                          // the classifier's again
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_B").State);
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_A").State);
    }

    /// <summary>
    /// The event's own time — the tool's, when the EAP sends <c>TIME</c> — is what the status
    /// reports for it, not the moment it happened to arrive.
    /// </summary>
    [Fact]
    public void TheEventsOwnTime_IsWhatTheStatusReports()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        var stamped = new DateTime(2026, 10, 22, 14, 3, 0, 250, DateTimeKind.Local);
        h.Engine.ReceiveToolEvent(ProcessStart, stamped);

        Assert.Equal(stamped, h.Engine.ToolStatus.LastTime);
    }

    // ---------------------------------------------------------------- plasma outside any window (#30)

    /// <summary>A one-rule classifier that names every lit step A.</summary>
    private static ProcessClassifierSettings EverythingIsA() => new()
    {
        Enabled = true,
        DecideAfterFrames = 1,
        Rules =
        {
            new ProcessClassRule
            {
                ClassName = "A", DisplayName = "O777/O777",
                Numerator = Raw("O 777", O777), Denominator = Raw("O 777", O777),
                Op = ComparisonOp.GreaterThan, Threshold = 0.5,
            },
        },
    };

    /// <summary>
    /// Plasma between windows is a process the table does not map (process C, today). It is not
    /// judged as any class — the class is Unknown, a class-scoped ratio reads NotApplicable — and
    /// a ratio with no class carries on, since it made no claim about which process it measures.
    /// The host reads the same thing: 028 = Unclassified with 027 = "Unknown".
    /// </summary>
    [Fact]
    public void PlasmaOutsideAnyWindow_IsUnknown_AndOnlyUnscopedRatiosJudge()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"), Ratio("R_any", ""));
        h.Engine.ConfigureToolEvents(Pm3());

        double t = 0;
        h.Event(ProcessStart, t);
        for (; t <= 1.0; t += 0.5) h.Engine.ProcessSample(On(t));
        h.Event(ProcessEnd, t);
        for (; t <= 3.0; t += 0.5) h.Engine.ProcessSample(On(t));

        Assert.Equal(ToolEventContext.OutsideWindow, h.Last.ToolEventState);
        Assert.True(h.Last.PlasmaPresent);
        Assert.Equal(ProcessClassifier.Unknown, h.Last.ProcessClass);
        Assert.Equal(ProcessClassState.Unclassified, h.Last.ProcessClassState);
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_A").State);
        Assert.Equal(RatioState.NoBaseline, h.Ratio("R_any").State);       // judged; no baseline yet
    }

    /// <summary>
    /// Between windows with the plasma off nothing is running: no class, and 028 says there is
    /// no step rather than that one could not be classified.
    /// </summary>
    [Fact]
    public void OutsideAnyWindow_WithThePlasmaOff_IsNoStep()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(ProcessStart, 0);
        h.Engine.ProcessSample(On(0));
        h.Event(ProcessEnd, 0.5);
        for (double t = 0.5; t <= 3.0; t += 0.5) h.Engine.ProcessSample(Off(t));

        Assert.Equal(ToolEventContext.OutsideWindow, h.Last.ToolEventState);
        Assert.Equal("", h.Last.ProcessClass);
        Assert.Equal(ProcessClassState.NoStep, h.Last.ProcessClassState);
    }

    /// <summary>
    /// Inside a window the tool has named the class, so 028 reports it as classified even before
    /// the plasma lights — the host asks "which process", and the tool has answered.
    /// </summary>
    [Fact]
    public void InsideAWindow_TheClassIsReportedAsClassified()
    {
        using var h = new Harness(classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(ProcessStart, 0);
        h.Engine.ProcessSample(Off(0));
        Assert.Equal("A", h.Last.ProcessClass);
        Assert.Equal(ProcessClassState.Classified, h.Last.ProcessClassState);

        h.Engine.ProcessSample(On(0.5));
        Assert.Equal("A", h.Last.ProcessClass);
        Assert.Equal(ProcessClassState.Classified, h.Last.ProcessClassState);
    }

    /// <summary>
    /// Each stretch of plasma outside any window is written down when it ends — when it started,
    /// how long it ran, how bright it was — so that once someone knows which process it was, the
    /// fix is one row in the table and the evidence for it is already in the log.
    /// </summary>
    [Fact]
    public void EachStretchOfUnmappedPlasma_IsLoggedWithStartDurationAndBrightness()
    {
        using var log = new LogCapture();
        using var h = new Harness(log.Logger, classifier: null, Ratio("R_A", "A"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(ProcessStart, 0);
        h.Engine.ProcessSample(On(0));
        h.Event(ProcessEnd, 1);
        double t = 1;
        for (; t <= 6.0; t += 0.5) h.Engine.ProcessSample(On(t));          // 5 s lit, no window
        for (; t <= 9.0; t += 0.5) h.Engine.ProcessSample(Off(t));         // goes out: stretch ends

        h.Engine.Dispose();
        var lines = log.Lines().Where(l => l.Contains("ToolEventUnmappedPlasma")).ToArray();
        var line = Assert.Single(lines);
        Assert.Contains("00:00:01", line);       // started at 1 s past the epoch
        Assert.Contains("5 s", line);            // ran 5 s
        Assert.Contains("800", line);            // the trigger line's level while lit
    }

    /// <summary>
    /// With a classifier configured it keeps taking its per-step verdict, but only to be checked
    /// against the tool: a verdict that disagrees with the window it was taken in is logged once
    /// for the step, with both classes and the measured discriminants. It never changes which
    /// ratios judge.
    /// </summary>
    [Fact]
    public void AClassifierThatDisagreesWithTheWindow_IsLoggedOncePerStep_AndChangesNothing()
    {
        using var log = new LogCapture();
        using var h = new Harness(log.Logger, EverythingIsA(), Ratio("R_A", "A"), Ratio("R_B", "B"));
        h.Engine.ConfigureToolEvents(Pm3());

        double t = 0;
        h.Event(CleanStart, t);                                             // the tool says B
        for (; t <= 3.0; t += 0.5) h.Engine.ProcessSample(On(t));          // the spectrum says A
        Assert.Equal("B", h.Last.ProcessClass);
        Assert.Equal(RatioState.NotApplicable, h.Ratio("R_A").State);
        Assert.NotEqual(RatioState.NotApplicable, h.Ratio("R_B").State);

        h.Event(CleanEnd, t);
        for (; t <= 5.0; t += 0.5) h.Engine.ProcessSample(Off(t));
        h.Event(ProcessStart, t);                                           // agreement: no line
        for (; t <= 8.0; t += 0.5) h.Engine.ProcessSample(On(t));

        h.Engine.Dispose();
        var lines = log.Lines().Where(l => l.Contains("ToolEventClassMismatch")).ToArray();
        var line = Assert.Single(lines);
        Assert.Contains("warn", line);
        Assert.Contains("O777/O777", line);                                 // the discriminant
    }

    /// <summary>With no classifier there is nothing to cross-check, and nothing is logged.</summary>
    [Fact]
    public void WithoutAClassifier_NothingIsCrossChecked()
    {
        using var log = new LogCapture();
        using var h = new Harness(log.Logger, classifier: null, Ratio("R_B", "B"));
        h.Engine.ConfigureToolEvents(Pm3());

        h.Event(CleanStart, 0);
        for (double t = 0; t <= 3.0; t += 0.5) h.Engine.ProcessSample(On(t));

        h.Engine.Dispose();
        Assert.DoesNotContain(log.Lines(), l => l.Contains("ToolEventClassMismatch"));
    }
}
